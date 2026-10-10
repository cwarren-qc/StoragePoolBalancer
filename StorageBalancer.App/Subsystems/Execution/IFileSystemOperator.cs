using System;
using System.Threading;
using System.Threading.Tasks;
using StorageBalancer.App.Domain;

namespace StorageBalancer.App.Subsystems.Execution;

public interface IFileSystemOperator
{
    /// <summary>
    /// Transfers a single file from source to target.
    /// The <paramref name="onBytesTransferred"/> callback receives the incremental chunk of bytes transferred in each step.
    /// </summary>
    Task TransferFileAsync(
        FileMoveTask task,
        Action<long> onBytesTransferred,
        CancellationToken ct);

    Task EnsureFolderExistsAsync(
        string folderPath,
        CancellationToken ct);

    Task DeleteFolderAsync(
        FolderCleanupTask task,
        CancellationToken ct);
}
