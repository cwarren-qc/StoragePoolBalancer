using System;
using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, DiskScanProgress> _diskProgress = new();
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
                _diskProgress.Values.OrderBy(disk => disk.HardwareName).ToArray());
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
            _diskProgress.Clear();

            foreach (var disk in config.Disks)
            {
                _diskProgress[disk.Id] = new DiskScanProgress(
                    disk.Id, disk.HardwareName, "Queued", string.Empty, 0, 0, 0, null);
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
            var diskScanTasks = config.Disks.Select(diskConfig => Task.Run(
                () => ScanPhysicalDisk(diskConfig, config.DrivePoolMode, cancellationToken), cancellationToken));
            var scannedDisks = await Task.WhenAll(diskScanTasks);
            var snapshot = new PoolSnapshot(DateTime.UtcNow, scannedDisks.ToImmutableList());
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
                foreach (var diskId in _diskProgress.Keys)
                {
                    UpdateProgress(diskId, progress => progress.Status is "Queued" or "Scanning"
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

                foreach (var diskId in _diskProgress.Keys)
                {
                    UpdateProgress(diskId, progress => progress.Status == "Scanning"
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

    private PhysicalDisk ScanPhysicalDisk(PhysicalDiskConfig diskConfig, bool drivePoolMode, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[Thread {Environment.CurrentManagedThreadId}] Starting Disk: {diskConfig.HardwareName}");
        UpdateProgress(diskConfig.Id, progress => progress with { Status = "Scanning" });

        var volumes = ImmutableList.CreateBuilder<Volume>();

        // Scan volumes SEQUENTIALLY to prevent disk head thrashing
        foreach (var volConfig in diskConfig.Volumes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var volumeRoot = new DirectoryInfo(volConfig.MountPoint);
            var scanRoot = drivePoolMode ? FindPoolPartRoot(diskConfig.Id, volumeRoot, cancellationToken) : volumeRoot;
            if (scanRoot is null)
                continue;

            Console.WriteLine($"  -> Scanning Volume: {scanRoot.FullName}");
            var rootFolder = ScanDirectory(diskConfig.Id, scanRoot, "", cancellationToken);
            volumes.Add(new Volume(volConfig.Id, volConfig.MountPoint, volConfig.Capacity, rootFolder));
        }

        Console.WriteLine($"[Thread {Environment.CurrentManagedThreadId}] Finished Disk: {diskConfig.HardwareName}");
        UpdateProgress(diskConfig.Id, progress => progress with { Status = "Complete", CurrentPath = string.Empty });
        return new PhysicalDisk(diskConfig.Id, diskConfig.HardwareName, volumes.ToImmutable());
    }

    private DirectoryInfo? FindPoolPartRoot(string diskId, DirectoryInfo volumeRoot, CancellationToken cancellationToken)
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
            UpdateProgress(diskId, progress => progress with
            {
                CurrentPath = volumeRoot.FullName,
                Error = string.IsNullOrEmpty(progress.Error) ? message : $"{progress.Error}; {message}"
            });
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            UpdateProgress(diskId, progress => progress with
            {
                CurrentPath = volumeRoot.FullName,
                Error = string.IsNullOrEmpty(progress.Error) ? exception.Message : $"{progress.Error}; {exception.Message}"
            });
            return null;
        }
    }

    private FolderNode ScanDirectory(string diskId, DirectoryInfo dirInfo, string relativePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var children = ImmutableList.CreateBuilder<FileSystemNode>();
        long totalSize = 0;
        long totalSizeOnDisk = 0;
        UpdateProgress(diskId, progress => progress with
        {
            CurrentPath = dirInfo.FullName,
            FoldersScanned = progress.FoldersScanned + 1
        });

        try
        {
            if (dirInfo.Exists)
            {
                foreach (var fileSystemInfo in dirInfo.EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string childRelativePath = string.IsNullOrEmpty(relativePath) ? fileSystemInfo.Name : Path.Combine(relativePath, fileSystemInfo.Name);

                    if (fileSystemInfo is FileInfo fileInfo)
                    {
                        long sizeOnDisk = fileInfo.Length == 0 ? 0 : (fileInfo.Length % _blockSize == 0 ? fileInfo.Length : (fileInfo.Length / _blockSize + 1) * _blockSize);
                        children.Add(new FileNode(fileInfo.Name, childRelativePath, fileInfo.Length, sizeOnDisk));
                        totalSize += fileInfo.Length;
                        totalSizeOnDisk += sizeOnDisk;
                        UpdateProgress(diskId, progress => progress with
                        {
                            CurrentPath = fileInfo.FullName,
                            FilesScanned = progress.FilesScanned + 1,
                            BytesScanned = progress.BytesScanned + fileInfo.Length
                        });
                    }
                    else if (fileSystemInfo is DirectoryInfo subDirInfo)
                    {
                        if ((subDirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                            continue;

                        var subFolder = ScanDirectory(diskId, subDirInfo, childRelativePath, cancellationToken);
                        children.Add(subFolder);
                        totalSize += subFolder.Size;
                        totalSizeOnDisk += subFolder.SizeOnDisk;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            UpdateProgress(diskId, progress => progress with
            {
                CurrentPath = dirInfo.FullName,
                Error = string.IsNullOrEmpty(progress.Error) ? exception.Message : $"{progress.Error}; {exception.Message}"
            });
        }

        return new FolderNode(dirInfo.Name, relativePath, totalSize, totalSizeOnDisk, children.ToImmutable());
    }

    private void UpdateProgress(string diskId, Func<DiskScanProgress, DiskScanProgress> update)
    {
        _diskProgress.AddOrUpdate(
            diskId,
            _ => throw new InvalidOperationException("Disk progress was not initialized."),
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
    IReadOnlyCollection<DiskScanProgress> Disks);

public record DiskScanProgress(
    string Id,
    string HardwareName,
    string Status,
    string CurrentPath,
    long FilesScanned,
    long FoldersScanned,
    long BytesScanned,
    string? Error);