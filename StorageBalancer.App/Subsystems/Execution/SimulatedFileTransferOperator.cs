using System;
using System.Threading;
using System.Threading.Tasks;
using StorageBalancer.App.Domain;

namespace StorageBalancer.App.Subsystems.Execution;

public class SimulatedFileTransferOperator : IFileTransferOperator
{
    private readonly int _durationSeconds;
    private readonly long _totalBytes;
    private readonly int _maxThreads;

    public SimulatedFileTransferOperator(int durationSeconds, long totalBytes, int maxThreads)
    {
        _durationSeconds = durationSeconds;
        _totalBytes = totalBytes;
        _maxThreads = Math.Max(1, maxThreads);
    }

    public async Task TransferFileAsync(
        FileMoveTask task,
        Action<long> onBytesTransferred,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_durationSeconds <= 0)
        {
            // Instant mode: immediately report full file transfer and return
            onBytesTransferred(task.SizeOnDisk);
            return;
        }

        // Paced simulation mode
        double targetRateBps = _durationSeconds > 0 ? (double)_totalBytes / _durationSeconds : double.MaxValue;
        double workerRateBps = Math.Max(1024 * 1024, targetRateBps / _maxThreads);

        long bytesRemaining = task.SizeOnDisk;
        if (bytesRemaining <= 0)
        {
            onBytesTransferred(0);
            return;
        }

        long chunkSize = Math.Max(64 * 1024, (long)(workerRateBps * 0.05)); // ~50ms slices

        while (bytesRemaining > 0)
        {
            ct.ThrowIfCancellationRequested();

            long advance = Math.Min(bytesRemaining, chunkSize);
            bytesRemaining -= advance;
            onBytesTransferred(advance);

            if (bytesRemaining > 0)
            {
                double sliceSec = (double)advance / workerRateBps;
                int delayMs = (int)(sliceSec * 1000.0);
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
            }
        }
    }
}

