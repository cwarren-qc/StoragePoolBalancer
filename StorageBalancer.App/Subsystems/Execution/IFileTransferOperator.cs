using System;
using System.Threading;
using System.Threading.Tasks;
using StorageBalancer.App.Domain;

namespace StorageBalancer.App.Subsystems.Execution;

public interface IFileTransferOperator
{
    Task TransferFileAsync(
        FileMoveTask task,
        Action<long> onBytesTransferred,
        CancellationToken ct);
}

