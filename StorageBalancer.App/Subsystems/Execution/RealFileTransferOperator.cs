using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using StorageBalancer.App.Domain;

namespace StorageBalancer.App.Subsystems.Execution;

public class RealFileTransferOperator : IFileTransferOperator
{
    public static readonly DateTime OrphanMarkerTimestampUtc = new DateTime(2006, 7, 8, 9, 10, 11, DateTimeKind.Utc);
    public const string TempFileExtension = ".spb-tmp";
    private const int BufferSize = 1024 * 1024; // 1 MiB stream buffer

    private readonly bool _verifyCopies;

    public RealFileTransferOperator(bool verifyCopies = true)
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

        // Clean up any stale temp file with this name before starting
        if (File.Exists(tempFilePath))
        {
            try { File.Delete(tempFilePath); } catch { /* Ignore cleanup of old temp file */ }
        }

        byte[] buffer = new byte[BufferSize];
        byte[]? sourceHash = null;

        try
        {
            // 1. Copy streaming with on-the-fly checksumming
            using (var sourceHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var sourceOptions = new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
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

            // 2. Size verification
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

            // 5. Atomic commit: Move temp file to final target path
            File.Move(tempFilePath, task.TargetFilePath);

            // 6. Delete original source file only after successful atomic commit
            File.Delete(task.SourceFilePath);
        }
        catch
        {
            // Rollback: Safely delete partial/failed temporary file on target volume
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
}
