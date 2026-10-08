using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace StorageBalancer.App.Subsystems.Execution;

public sealed class PhysicalDiskLockManager
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    private SemaphoreSlim GetSemaphore(string diskName) =>
        _locks.GetOrAdd(string.IsNullOrWhiteSpace(diskName) ? "DefaultDisk" : diskName.Trim(), _ => new SemaphoreSlim(1, 1));

    public bool TryAcquire(string sourceDisk, string targetDisk, out IDisposable? releaser)
    {
        var s = string.IsNullOrWhiteSpace(sourceDisk) ? "DefaultDisk" : sourceDisk.Trim();
        var t = string.IsNullOrWhiteSpace(targetDisk) ? "DefaultDisk" : targetDisk.Trim();

        if (string.Equals(s, t, StringComparison.OrdinalIgnoreCase))
        {
            var sem = GetSemaphore(s);
            if (sem.Wait(0))
            {
                releaser = new SingleReleaser(sem);
                return true;
            }
            releaser = null;
            return false;
        }

        var (firstKey, secondKey) = StringComparer.OrdinalIgnoreCase.Compare(s, t) < 0 ? (s, t) : (t, s);
        var semFirst = GetSemaphore(firstKey);
        var semSecond = GetSemaphore(secondKey);

        if (semFirst.Wait(0))
        {
            if (semSecond.Wait(0))
            {
                releaser = new DualReleaser(semFirst, semSecond);
                return true;
            }
            semFirst.Release();
        }

        releaser = null;
        return false;
    }

    public async Task<IDisposable> AcquireAsync(string sourceDisk, string targetDisk, CancellationToken ct)
    {
        var s = string.IsNullOrWhiteSpace(sourceDisk) ? "DefaultDisk" : sourceDisk.Trim();
        var t = string.IsNullOrWhiteSpace(targetDisk) ? "DefaultDisk" : targetDisk.Trim();

        if (string.Equals(s, t, StringComparison.OrdinalIgnoreCase))
        {
            var sem = GetSemaphore(s);
            await sem.WaitAsync(ct).ConfigureAwait(false);
            return new SingleReleaser(sem);
        }

        var (firstKey, secondKey) = StringComparer.OrdinalIgnoreCase.Compare(s, t) < 0 ? (s, t) : (t, s);
        var semFirst = GetSemaphore(firstKey);
        var semSecond = GetSemaphore(secondKey);

        await semFirst.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await semSecond.WaitAsync(ct).ConfigureAwait(false);
            return new DualReleaser(semFirst, semSecond);
        }
        catch
        {
            semFirst.Release();
            throw;
        }
    }

    private sealed class SingleReleaser(SemaphoreSlim sem) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                sem.Release();
        }
    }

    private sealed class DualReleaser(SemaphoreSlim sem1, SemaphoreSlim sem2) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                sem2.Release();
                sem1.Release();
            }
        }
    }
}

