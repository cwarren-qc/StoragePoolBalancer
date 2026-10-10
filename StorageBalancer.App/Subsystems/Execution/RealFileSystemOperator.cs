using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using StorageBalancer.App.Domain;

namespace StorageBalancer.App.Subsystems.Execution;

public class RealFileSystemOperator : IFileSystemOperator
{
    public static readonly DateTime OrphanMarkerTimestampUtc = new DateTime(2006, 7, 8, 9, 10, 11, DateTimeKind.Utc);
    public const string TempFileExtension = ".spb-tmp";
    public const string BackupFileExtension = ".spb-old";
    private const int BufferSize = 1024 * 1024; // 1 MiB stream buffer

    private readonly bool _verifyCopies;

    public RealFileSystemOperator(bool verifyCopies = true)
    {
        _verifyCopies = verifyCopies;
    }

    public async Task TransferFileAsync(
        FileMoveTask task,
        Action<long> onBytesTransferred,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(task.SourceFilePath))
            throw new ArgumentException("Source file path is not specified on move task.", nameof(task));
        if (string.IsNullOrWhiteSpace(task.TargetFilePath))
            throw new ArgumentException("Target file path is not specified on move task.", nameof(task));

        var sourceInfo = new FileInfo(task.SourceFilePath);
        if (!sourceInfo.Exists)
            throw new FileNotFoundException($"Source file not found: '{task.SourceFilePath}'");

        if (File.Exists(task.TargetFilePath))
            throw new IOException($"Target file already exists: '{task.TargetFilePath}'. Move aborted to prevent data overwrite.");

        string targetDirectory = Path.GetDirectoryName(task.TargetFilePath)!;
        if (!Directory.Exists(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        string tempFilePath = task.TargetFilePath + TempFileExtension;
        string sourceOldFilePath = task.SourceFilePath + BackupFileExtension;

        // Clean up any stale temp or old backup files before starting
        if (File.Exists(tempFilePath))
        {
            try { File.Delete(tempFilePath); } catch { /* Ignore cleanup of stale temp file */ }
        }
        if (File.Exists(sourceOldFilePath))
        {
            try { File.Delete(sourceOldFilePath); } catch { /* Ignore cleanup of stale backup file */ }
        }

        byte[] buffer = new byte[BufferSize];
        byte[]? sourceHash = null;
        bool sourceRenamedToOld = false;

        try
        {
            // Open source with FileShare.Read | FileShare.Delete:
            // - FileShare.Read: Hard lock against writers. Any process attempting to write/modify is blocked,
            //   and if another process currently holds write access, this Open fails immediately.
            // - FileShare.Delete: Allows our process to rename the open source file to ".spb-old" while holding the lock.
            var sourceOptions = new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = BufferSize
            };

            var targetOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.ReadWrite,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
                BufferSize = BufferSize
            };

            await using (var sourceStream = new FileStream(task.SourceFilePath, sourceOptions))
            {
                // 1. Copy streaming to temporary file with on-the-fly checksumming while source is locked
                using (var sourceHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    await using (var targetStream = new FileStream(tempFilePath, targetOptions))
                    {
                        // Tag with custom orphan marker timestamp so it can be identified if interrupted
                        try
                        {
                            File.SetCreationTimeUtc(tempFilePath, OrphanMarkerTimestampUtc);
                            File.SetLastWriteTimeUtc(tempFilePath, OrphanMarkerTimestampUtc);
                        }
                        catch
                        {
                            // Timestamp tagging is best-effort while handle is open
                        }

                        int bytesRead;
                        while ((bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                        {
                            sourceHasher.AppendData(buffer, 0, bytesRead);
                            await targetStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                            onBytesTransferred(bytesRead);
                        }

                        await targetStream.FlushAsync(ct).ConfigureAwait(false);
                    }

                    sourceHash = sourceHasher.GetHashAndReset();
                }

                // Ensure orphan marker timestamp is set on closed temp file
                try
                {
                    File.SetCreationTimeUtc(tempFilePath, OrphanMarkerTimestampUtc);
                    File.SetLastWriteTimeUtc(tempFilePath, OrphanMarkerTimestampUtc);
                }
                catch
                {
                    // Best-effort
                }

                // 2. Size verification while source lock is still held
                var tempInfo = new FileInfo(tempFilePath);
                if (!tempInfo.Exists)
                    throw new FileNotFoundException($"Written temporary file was not found: '{tempFilePath}'");

                if (tempInfo.Length != sourceInfo.Length)
                    throw new IOException($"Target file size mismatch for '{task.FileName}': expected {sourceInfo.Length} bytes, but wrote {tempInfo.Length} bytes.");

                // 3. Verification pass: Read back non-cached stream and verify checksum
                if (_verifyCopies && sourceHash != null)
                {
                    using var targetHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var verifyOptions = new FileStreamOptions
                    {
                        Mode = FileMode.Open,
                        Access = FileAccess.Read,
                        Share = FileShare.Read,
                        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                        BufferSize = BufferSize
                    };

                    await using (var verifyStream = new FileStream(tempFilePath, verifyOptions))
                    {
                        int bytesRead;
                        while ((bytesRead = await verifyStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                        {
                            targetHasher.AppendData(buffer, 0, bytesRead);
                        }
                    }

                    byte[] targetHash = targetHasher.GetHashAndReset();
                    if (!CryptographicOperations.FixedTimeEquals(sourceHash, targetHash))
                    {
                        throw new InvalidDataException(
                            $"Checksum verification failed for '{task.FileName}'. Destination checksum does not match source checksum."
                        );
                    }
                }

                // 4. Preserve original metadata
                try
                {
                    File.SetCreationTimeUtc(tempFilePath, sourceInfo.CreationTimeUtc);
                    File.SetLastWriteTimeUtc(tempFilePath, sourceInfo.LastWriteTimeUtc);
                    File.SetLastAccessTimeUtc(tempFilePath, sourceInfo.LastAccessTimeUtc);
                    File.SetAttributes(tempFilePath, sourceInfo.Attributes);
                }
                catch
                {
                    // Attribute setting best effort
                }

                // 5. Rename source file to .spb-old WHILE STILL HOLDING THE SOURCE LOCK
                File.Move(task.SourceFilePath, sourceOldFilePath);
                sourceRenamedToOld = true;

                // 6. Rename copied file from .spb-tmp to final destination file path
                File.Move(tempFilePath, task.TargetFilePath);

                // Source lock handle will now automatically close when exiting this block
            }

            // 7. Delete the old source file (.spb-old) now that destination is active and source lock is released
            File.Delete(sourceOldFilePath);
        }
        catch
        {
            // Rollback:
            // A. If source file was already renamed to .spb-old, attempt to rename it back to its original name
            if (sourceRenamedToOld)
            {
                try
                {
                    if (File.Exists(sourceOldFilePath) && !File.Exists(task.SourceFilePath))
                    {
                        File.Move(sourceOldFilePath, task.SourceFilePath);
                    }
                }
                catch
                {
                    // Best-effort rollback
                }
            }

            // B. Safely delete partial/failed temporary file on target volume
            try
            {
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }
            }
            catch
            {
                // Suppress rollback cleanup error to allow root exception to surface
            }

            throw;
        }
    }

    public Task EnsureFolderExistsAsync(string folderPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(folderPath) && !Directory.Exists(folderPath))
        {
            try
            {
                Directory.CreateDirectory(folderPath);
            }
            catch
            {
                // Best-effort directory creation
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteFolderAsync(FolderCleanupTask task, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(task.TargetFolderPath))
            return Task.CompletedTask;

        // Safety verification: Ensure the designated Primary volume contains this folder on disk
        // before deleting the extra/redundant folder instance.
        if (string.IsNullOrWhiteSpace(task.PrimaryFolderPath) || !Directory.Exists(task.PrimaryFolderPath))
        {
            return Task.CompletedTask;
        }

        if (!Directory.Exists(task.TargetFolderPath))
            return Task.CompletedTask;

        // Safety verification: Ensure the folder is truly empty (no files and no subdirectories)
        if (Directory.EnumerateFileSystemEntries(task.TargetFolderPath).Any())
            return Task.CompletedTask;

        try
        {
            Directory.Delete(task.TargetFolderPath);
        }
        catch
        {
            // Best effort deletion
        }

        return Task.CompletedTask;
    }
}
