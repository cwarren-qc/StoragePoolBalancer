using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StorageBalancer.App.Configuration;
using StorageBalancer.App.Domain;
using StorageBalancer.App.Subsystems.Storage;

namespace StorageBalancer.App.Subsystems.Scanner;

public class StateScanner
{
    private readonly int _blockSize;
    private readonly JsonStateRepository _stateRepository;
    private readonly ConcurrentDictionary<string, VolumeScanProgress> _volumeProgress = new();
    private readonly object _statusLock = new();
    private bool _isScanning;
    private CancellationTokenSource? _scanCancellation;
    private DateTime? _scannedAt;
    private string? _error;
    private string? _snapshotName;
    private string? _snapshotPath;

    public StateScanner(JsonStateRepository stateRepository, int blockSize = 4096)
    {
        _stateRepository = stateRepository;
        _blockSize = blockSize;
    }

    public ScanStatus GetStatus()
    {
        lock (_statusLock)
        {
            return new ScanStatus(
                _isScanning,
                _scanCancellation?.IsCancellationRequested ?? false,
                _scannedAt,
                _error,
                _snapshotName,
                _snapshotPath,
                _volumeProgress.Values.OrderBy(v => v.Alias).ToArray());
        }
    }

    public bool TryStartScan(AppConfig config, string snapshotName, string snapshotPath)
    {
        lock (_statusLock)
        {
            if (_isScanning)
                return false;

            _isScanning = true;
            _scanCancellation = new CancellationTokenSource();
            _scannedAt = null;
            _error = null;
            _snapshotName = snapshotName;
            _snapshotPath = snapshotPath;
            _volumeProgress.Clear();

            foreach (var vol in config.Volumes)
            {
                _volumeProgress[vol.Alias] = new VolumeScanProgress(
                    vol.Alias,
                    vol.Disk ?? string.Empty,
                    "Queued",
                    string.Empty,
                    0, 0, 0, null,
                    ImmutableList<ScanIssue>.Empty);
            }
        }

        _ = RunScanAsync(config, _scanCancellation, snapshotName, snapshotPath);
        return true;
    }

    public bool TryCancelScan()
    {
        lock (_statusLock)
        {
            if (!_isScanning || _scanCancellation is null)
                return false;

            _scanCancellation.Cancel();
            return true;
        }
    }

    private async Task RunScanAsync(AppConfig config, CancellationTokenSource cancellation, string snapshotName, string snapshotPath)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            // Group volumes by the Disk field. If Disk is missing/empty, use the Alias as a fallback group
            var scanGroups = config.Volumes.GroupBy(v =>
                string.IsNullOrWhiteSpace(v.Disk) ? v.Alias : v.Disk.Trim(),
                StringComparer.OrdinalIgnoreCase);

            var scanTasks = scanGroups.Select(group => Task.Run(
                () => ScanVolumeGroup(group.Key, group.ToList(), config.DrivePoolMode, cancellationToken), cancellationToken));

            var scannedGroups = await Task.WhenAll(scanTasks);
            var allScannedVolumes = scannedGroups.SelectMany(g => g).ToImmutableList();

            var snapshot = new PoolSnapshot(
                1,
                DateTime.UtcNow,
                config.DrivePoolMode,
                _blockSize,
                allScannedVolumes);

            _stateRepository.SaveSnapshot(snapshot, snapshotPath);

            lock (_statusLock)
            {
                _isScanning = false;
                _scannedAt = snapshot.ScannedAt;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_statusLock)
            {
                _isScanning = false;
                foreach (var alias in _volumeProgress.Keys)
                {
                    UpdateProgress(alias, progress => progress.Status is "Queued" or "Scanning"
                        ? progress with { Status = "Cancelled", CurrentPath = string.Empty }
                        : progress);
                }
            }
        }
        catch (Exception exception)
        {
            lock (_statusLock)
            {
                _isScanning = false;
                _error = exception.Message;

                foreach (var alias in _volumeProgress.Keys)
                {
                    UpdateProgress(alias, progress => progress.Status == "Scanning"
                        ? progress with { Status = "Failed", Error = exception.Message }
                        : progress);
                }
            }
        }
        finally
        {
            lock (_statusLock)
            {
                if (ReferenceEquals(_scanCancellation, cancellation))
                    _scanCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private List<SnapshotVolume> ScanVolumeGroup(string diskName, List<VolumeConfig> volumesInGroup, bool drivePoolMode, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[Thread {Environment.CurrentManagedThreadId}] Starting Disk Group: {diskName}");

        var scannedVolumes = new List<SnapshotVolume>();

        // Scan volumes SEQUENTIALLY to prevent disk head thrashing
        foreach (var volConfig in volumesInGroup)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateProgress(volConfig.Alias, progress => progress with { Status = "Scanning", CurrentPath = volConfig.MountPoint });

            var issues = new List<SnapshotIssue>();
            DirectoryInfo? scanRoot = null;
            var folders = new SortedDictionary<string, List<SnapshotFile>>(StringComparer.Ordinal);
            long otherItemsSizeOnDisk = 0;
            var rootWasFound = false;

            try
            {
                var volumeRoot = new DirectoryInfo(volConfig.MountPoint);
                scanRoot = drivePoolMode
                    ? FindPoolPartRoot(volConfig.Alias, volumeRoot, issues, cancellationToken)
                    : ResolveConfiguredRoot(volConfig.Alias, volumeRoot, volConfig.RootFolderRelativePath, issues);

                Console.WriteLine($"  -> Scanning Volume: {volumeRoot.FullName}");
                otherItemsSizeOnDisk = ScanDirectory(
                    volConfig.Alias,
                    volumeRoot,
                    scanRoot,
                    folders,
                    issues,
                    cancellationToken,
                    ref rootWasFound);

                if (scanRoot is not null && !rootWasFound)
                    AddIssue(volConfig.Alias, issues, scanRoot.FullName, "Configured root folder was not found during the volume scan.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                AddIssue(volConfig.Alias, issues, volConfig.MountPoint, exception.Message);
            }

            scannedVolumes.Add(new SnapshotVolume(
                volConfig.Alias,
                diskName,
                volConfig.MountPoint,
                volConfig.Capacity,
                otherItemsSizeOnDisk,
                volConfig.RootFolderRelativePath ?? string.Empty,
                scanRoot?.FullName,
                scanRoot is not null && rootWasFound && issues.Count == 0,
                issues.ToImmutableList(),
                folders));

            UpdateProgress(volConfig.Alias, progress => progress with { Status = "Complete", CurrentPath = string.Empty });
        }

        Console.WriteLine($"[Thread {Environment.CurrentManagedThreadId}] Finished Disk Group: {diskName}");
        return scannedVolumes;
    }

    private DirectoryInfo? FindPoolPartRoot(
        string alias,
        DirectoryInfo volumeRoot,
        List<SnapshotIssue> issues,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = volumeRoot.EnumerateDirectories("PoolPart.*", SearchOption.TopDirectoryOnly).Take(2).ToArray();
            if (matches.Length == 1)
                return matches[0];

            var message = matches.Length == 0
                ? "No PoolPart.* directory was found."
                : "More than one PoolPart.* directory was found; select a volume with exactly one pool folder.";
            AddIssue(alias, issues, volumeRoot.FullName, message);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            AddIssue(alias, issues, volumeRoot.FullName, exception.Message);
            return null;
        }
    }

    private DirectoryInfo? ResolveConfiguredRoot(
        string alias,
        DirectoryInfo volumeRoot,
        string? configuredRoot,
        List<SnapshotIssue> issues)
    {
        try
        {
            var fullVolumePath = Path.GetFullPath(volumeRoot.FullName);
            var fullRootPath = Path.GetFullPath(Path.Combine(fullVolumePath, configuredRoot ?? string.Empty));
            var relativePath = Path.GetRelativePath(fullVolumePath, fullRootPath);
            if (Path.IsPathRooted(relativePath) || relativePath == ".." || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                AddIssue(alias, issues, fullRootPath, "Configured root folder must be inside its volume.");
                return null;
            }

            return new DirectoryInfo(fullRootPath);
        }
        catch (Exception exception)
        {
            AddIssue(alias, issues, volumeRoot.FullName, exception.Message);
            return null;
        }
    }

    private long ScanDirectory(
        string alias,
        DirectoryInfo dirInfo,
        DirectoryInfo? selectedRoot,
        SortedDictionary<string, List<SnapshotFile>> folders,
        List<SnapshotIssue> issues,
        CancellationToken cancellationToken,
        ref bool rootWasFound)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var insideSelectedRoot = selectedRoot is not null && IsSameOrDescendant(dirInfo.FullName, selectedRoot.FullName);
        var isSelectedRoot = selectedRoot is not null && IsSamePath(dirInfo.FullName, selectedRoot.FullName);
        var files = insideSelectedRoot ? new List<SnapshotFile>() : null;
        if (files is not null)
        {
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(selectedRoot!.FullName, dirInfo.FullName));
            folders.Add(relativePath, files);
            if (isSelectedRoot)
                rootWasFound = true;
        }

        UpdateProgress(alias, progress => progress with
        {
            CurrentPath = dirInfo.FullName,
            FoldersScanned = progress.FoldersScanned + 1
        });
        long otherSizeOnDisk = 0;

        try
        {
            if (!dirInfo.Exists)
            {
                AddIssue(alias, issues, dirInfo.FullName, "Folder does not exist or is not accessible.");
                return 0;
            }

            otherSizeOnDisk = files is null ? GetFolderSizeOnDisk() : 0;
            var entries = dirInfo.GetFileSystemInfos();
            Array.Sort(entries, CompareFileSystemInfo);
            foreach (var fileSystemInfo in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if ((fileSystemInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        if (fileSystemInfo is DirectoryInfo reparseDirectory &&
                            selectedRoot is not null &&
                            IsSamePath(reparseDirectory.FullName, selectedRoot.FullName))
                        {
                            otherSizeOnDisk += ScanDirectory(
                                alias,
                                reparseDirectory,
                                selectedRoot,
                                folders,
                                issues,
                                cancellationToken,
                                ref rootWasFound);
                            continue;
                        }

                        if (files is null && fileSystemInfo is DirectoryInfo)
                            otherSizeOnDisk += GetFolderSizeOnDisk();
                        AddIssue(alias, issues, fileSystemInfo.FullName, "Reparse point was skipped.");
                        continue;
                    }

                    if (fileSystemInfo is FileInfo fileInfo)
                    {
                        var size = fileInfo.Length;
                        var sizeOnDisk = GetFileSizeOnDisk(size);
                        if (files is not null)
                            files.Add(new SnapshotFile(fileInfo.Name, size, sizeOnDisk));
                        else
                            otherSizeOnDisk += sizeOnDisk;

                        UpdateProgress(alias, progress => progress with
                        {
                            CurrentPath = fileInfo.FullName,
                            FilesScanned = progress.FilesScanned + 1,
                            BytesScanned = progress.BytesScanned + size
                        });
                    }
                    else if (fileSystemInfo is DirectoryInfo subDirInfo)
                    {
                        otherSizeOnDisk += ScanDirectory(
                            alias,
                            subDirInfo,
                            selectedRoot,
                            folders,
                            issues,
                            cancellationToken,
                            ref rootWasFound);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    AddIssue(alias, issues, fileSystemInfo.FullName, exception.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            AddIssue(alias, issues, dirInfo.FullName, exception.Message);
        }

        files?.Sort(CompareSnapshotFile);
        return otherSizeOnDisk;
    }

    private long GetFileSizeOnDisk(long size)
    {
        return size == 0 ? 0 : (size % _blockSize == 0 ? size : (size / _blockSize + 1) * _blockSize);
    }

    private long GetFolderSizeOnDisk()
    {
        return _blockSize;
    }

    private static bool IsSameOrDescendant(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.Equals(fullPath, fullRoot, comparison))
            return true;

        var rootPrefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootPrefix, comparison);
    }

    private static bool IsSamePath(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }

    private static string NormalizeRelativePath(string path)
    {
        return path == "." ? "." : path.Replace(Path.DirectorySeparatorChar, '\\').Replace(Path.AltDirectorySeparatorChar, '\\');
    }

    private static int CompareFileSystemInfo(FileSystemInfo left, FileSystemInfo right)
    {
        var comparison = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Name, right.Name);
    }

    private static int CompareSnapshotFile(SnapshotFile left, SnapshotFile right)
    {
        var comparison = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Name, right.Name);
    }

    private void AddIssue(string alias, List<SnapshotIssue> issues, string path, string message)
    {
        issues.Add(new SnapshotIssue(path, message));
        UpdateProgress(alias, progress => progress with
        {
            CurrentPath = path,
            Error = string.IsNullOrEmpty(progress.Error) ? message : $"{progress.Error}; {message}",
            Issues = progress.Issues.Add(new ScanIssue(path, message))
        });
    }

    private void UpdateProgress(string alias, Func<VolumeScanProgress, VolumeScanProgress> update)
    {
        _volumeProgress.AddOrUpdate(
            alias,
            _ => throw new InvalidOperationException("Volume progress was not initialized."),
            (_, current) => update(current));
    }
}

public record ScanStatus(
    bool IsScanning,
    bool IsCancellationRequested,
    DateTime? ScannedAt,
    string? Error,
    string? SnapshotName,
    string? SnapshotPath,
    IReadOnlyCollection<VolumeScanProgress> Volumes);

public record VolumeScanProgress(
    string Alias,
    string DiskName,
    string Status,
    string CurrentPath,
    long FilesScanned,
    long FoldersScanned,
    long BytesScanned,
    string? Error,
    ImmutableList<ScanIssue> Issues);

public record ScanIssue(string Path, string Message);