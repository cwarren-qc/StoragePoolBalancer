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
using StorageBalancer.App.Subsystems.Planning;
using StorageBalancer.App.Subsystems.Storage;

namespace StorageBalancer.App.Subsystems.Execution;

public class PlanExecutor
{
    private readonly PlacementPlanner _planner;
    private readonly JsonStateRepository _stateRepository;

    private readonly object _stateLock = new();
    private bool _isRunning;
    private bool _isSimulation;
    private CancellationTokenSource? _cts;
    private DateTime? _startedAt;
    private DateTime? _completedAt;
    private string? _error;

    private long _totalBytes;
    private long _transferredBytes;
    private long _totalFiles;
    private long _transferredFiles;
    private double _throughputBps;
    private int _activeWorkers;
    private string _phase = "Idle";

    private readonly ConcurrentDictionary<string, VolumeState> _volumeStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ActiveTransferInfo> _activeTransfers = new();

    public PlanExecutor(PlacementPlanner planner, JsonStateRepository stateRepository)
    {
        _planner = planner;
        _stateRepository = stateRepository;
    }

    public ExecutionStatus GetStatus()
    {
        lock (_stateLock)
        {
            var volumes = _volumeStates.Values
                .OrderBy(v => v.Alias)
                .Select(v => v.ToProgress())
                .ToImmutableList();

            var activeTransfers = _activeTransfers.Values
                .OrderBy(a => a.WorkerId)
                .ToImmutableList();

            double progressPct = _totalBytes > 0
                ? Math.Min(100.0, ((double)_transferredBytes / _totalBytes) * 100.0)
                : (_totalFiles > 0 ? ((double)_transferredFiles / _totalFiles) * 100.0 : 0.0);

            if (!_isRunning && _completedAt != null && _error == null)
            {
                progressPct = 100.0;
            }

            return new ExecutionStatus(
                _isRunning,
                _isSimulation,
                _cts?.IsCancellationRequested ?? false,
                _startedAt,
                _completedAt,
                Math.Round(progressPct, 1),
                _transferredBytes,
                _totalBytes,
                _transferredFiles,
                _totalFiles,
                _throughputBps,
                _activeWorkers,
                _error,
                volumes,
                activeTransfers,
                _phase
            );
        }
    }

    public bool TryStartExecution(
        AppConfig config,
        PoolSnapshot snapshot,
        ExecutionStartRequest request)
    {
        lock (_stateLock)
        {
            if (_isRunning) return false;

            _isRunning = true;
            _isSimulation = request.IsSimulation;
            _cts = new CancellationTokenSource();
            _startedAt = DateTime.UtcNow;
            _completedAt = null;
            _error = null;
            _phase = "Preparing";

            _transferredBytes = 0;
            _transferredFiles = 0;
            _throughputBps = 0;
            _activeWorkers = 0;
            _volumeStates.Clear();
            _activeTransfers.Clear();

            int maxThreads = request.MaxThreads.GetValueOrDefault(config.MaxExecutionThreads);
            if (maxThreads < 1) maxThreads = 1;
            if (maxThreads > 16) maxThreads = 16;

            int durationSeconds = request.SimulationDurationSeconds > 0
                ? request.SimulationDurationSeconds
                : config.DefaultSimulationDurationSeconds;
            if (durationSeconds < 5) durationSeconds = 5;

            var token = _cts.Token;
            string snapName = request.SnapshotName ?? string.Empty;

            Task.Run(async () =>
            {
                try
                {
                    await RunPlanAsync(config, snapshot, snapName, request.IsSimulation, durationSeconds, maxThreads, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    lock (_stateLock)
                    {
                        _error = "Execution was cancelled by the user.";
                        _completedAt = DateTime.UtcNow;
                        _isRunning = false;
                        _phase = "Cancelled";
                    }
                }
                catch (Exception ex)
                {
                    lock (_stateLock)
                    {
                        _error = ex.Message;
                        _completedAt = DateTime.UtcNow;
                        _isRunning = false;
                        _phase = "Failed";
                    }
                }
                finally
                {
                    lock (_stateLock)
                    {
                        _isRunning = false;
                        _activeWorkers = 0;
                        _activeTransfers.Clear();
                        if (_completedAt == null) _completedAt = DateTime.UtcNow;
                    }
                }
            }, token);

            return true;
        }
    }

    public bool TryCancel()
    {
        lock (_stateLock)
        {
            if (!_isRunning || _cts == null) return false;
            _cts.Cancel();
            return true;
        }
    }

    private CachedPlan? _cachedPlan;

    private sealed record CachedPlan(
        string SnapshotName,
        DateTime ScannedAt,
        string ConfigFingerprint,
        PlacementPlan Plan
    );

    public void CachePlan(string snapshotName, DateTime scannedAt, AppConfig config, PlacementPlan plan)
    {
        lock (_stateLock)
        {
            string fingerprint = GetConfigFingerprint(config);
            _cachedPlan = new CachedPlan(snapshotName, scannedAt, fingerprint, plan);
        }
    }

    private static string GetConfigFingerprint(AppConfig config)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var v in config.Volumes)
        {
            sb.Append(v.Alias).Append(':').Append(v.Capacity).Append(';');
        }
        foreach (var r in config.FilePlacementRules)
        {
            sb.Append(r.FullRelativePath).Append(':').Append(r.StartingDepth).Append(':').Append(r.DeferToFiller).Append(':').Append(string.Join(",", r.AllowedVolumeAliases)).Append(';');
        }
        if (config.Duplicates != null)
        {
            sb.Append("dup:").Append(config.Duplicates.Consolidate).Append(':').Append(string.Join(",", config.Duplicates.AllowedVolumeAliases)).Append(';');
        }
        if (config.Filler != null)
        {
            sb.Append("fil:").Append(string.Join(",", config.Filler.AllowedVolumeAliases)).Append(';');
        }
        if (config.Unmatched != null)
        {
            sb.Append("unm:").Append(string.Join(",", config.Unmatched.AllowedVolumeAliases)).Append(';');
        }
        return sb.ToString();
    }

    private async Task RunPlanAsync(
        AppConfig config,
        PoolSnapshot snapshot,
        string snapshotName,
        bool isSimulation,
        int durationSeconds,
        int maxThreads,
        CancellationToken ct)
    {
        PlacementPlan plan;
        string fingerprint = GetConfigFingerprint(config);

        lock (_stateLock)
        {
            if (_cachedPlan != null &&
                string.Equals(_cachedPlan.SnapshotName, snapshotName, StringComparison.OrdinalIgnoreCase) &&
                _cachedPlan.ScannedAt == snapshot.ScannedAt &&
                _cachedPlan.ConfigFingerprint == fingerprint)
            {
                plan = _cachedPlan.Plan;
            }
            else
            {
                plan = _planner.CreatePlan(
                    snapshot,
                    config.FilePlacementRules,
                    config.Duplicates,
                    config.Filler,
                    config.Unmatched,
                    includeFiles: true
                );
                _cachedPlan = new CachedPlan(snapshotName, snapshot.ScannedAt, fingerprint, plan);
            }
        }

        var diskMap = config.Volumes.ToDictionary(v => v.Alias, v => string.IsNullOrWhiteSpace(v.Disk) ? v.Alias : v.Disk, StringComparer.OrdinalIgnoreCase);

        var moveItems = new List<FileMoveTask>();
        foreach (var placement in plan.Placements)
        {
            if (placement.Files == null) continue;
            foreach (var f in placement.Files)
            {
                if (!string.Equals(f.OriginalVolumeAlias, f.DestinationVolumeAlias, StringComparison.OrdinalIgnoreCase))
                {
                    string srcDisk = diskMap.GetValueOrDefault(f.OriginalVolumeAlias, f.OriginalVolumeAlias);
                    string tgtDisk = diskMap.GetValueOrDefault(f.DestinationVolumeAlias, f.DestinationVolumeAlias);
                    moveItems.Add(new FileMoveTask(
                        f.Name,
                        f.RelativePath,
                        f.OriginalVolumeAlias,
                        f.DestinationVolumeAlias,
                        srcDisk,
                        tgtDisk,
                        f.Size,
                        f.SizeOnDisk
                    ));
                }
            }
        }

        // Sort largest files first
        moveItems.Sort((a, b) => b.SizeOnDisk.CompareTo(a.SizeOnDisk));

        lock (_stateLock)
        {
            _totalBytes = moveItems.Sum(m => m.SizeOnDisk);
            _totalFiles = moveItems.Count;
            _phase = "Transferring";

            var volumeSummaries = plan.Volumes.ToDictionary(v => v.Alias, StringComparer.OrdinalIgnoreCase);

            foreach (var vol in snapshot.Volumes)
            {
                long capacity = vol.Capacity;
                long otherItems = vol.OtherItemsSizeOnDisk;
                string disk = diskMap.GetValueOrDefault(vol.Alias, vol.Alias);

                var summary = volumeSummaries.GetValueOrDefault(vol.Alias);
                long finalSize = summary?.FinalSize ?? vol.Capacity;

                long selfStayed = 0;
                var outgoingTotals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                var incomingTotals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

                if (summary != null)
                {
                    foreach (var prov in summary.Provenance)
                    {
                        if (string.Equals(prov.Alias, vol.Alias, StringComparison.OrdinalIgnoreCase))
                        {
                            selfStayed = Math.Max(0, prov.Size - otherItems);
                        }
                        else
                        {
                            incomingTotals[prov.Alias] = prov.Size;
                        }
                    }
                }

                foreach (var otherSummary in plan.Volumes)
                {
                    if (string.Equals(otherSummary.Alias, vol.Alias, StringComparison.OrdinalIgnoreCase)) continue;
                    var outProv = otherSummary.Provenance.FirstOrDefault(p => string.Equals(p.Alias, vol.Alias, StringComparison.OrdinalIgnoreCase));
                    if (outProv != null && outProv.Size > 0)
                    {
                        outgoingTotals[otherSummary.Alias] = outProv.Size;
                    }
                }

                long initialTotalMovedOutBytes = moveItems.Where(m => string.Equals(m.SourceVolume, vol.Alias, StringComparison.OrdinalIgnoreCase)).Sum(m => m.SizeOnDisk);
                long initialTotalMovedOutFiles = moveItems.Count(m => string.Equals(m.SourceVolume, vol.Alias, StringComparison.OrdinalIgnoreCase));

                long initialTotalMovedInBytes = moveItems.Where(m => string.Equals(m.TargetVolume, vol.Alias, StringComparison.OrdinalIgnoreCase)).Sum(m => m.SizeOnDisk);
                long initialTotalMovedInFiles = moveItems.Count(m => string.Equals(m.TargetVolume, vol.Alias, StringComparison.OrdinalIgnoreCase));

                long initialCurrentSize = otherItems + selfStayed + initialTotalMovedOutBytes;

                var state = new VolumeState(
                    vol.Alias,
                    disk,
                    capacity,
                    initialCurrentSize,
                    finalSize,
                    selfStayed,
                    otherItems,
                    initialTotalMovedOutBytes,
                    initialTotalMovedOutFiles,
                    initialTotalMovedInBytes,
                    initialTotalMovedInFiles,
                    outgoingTotals,
                    incomingTotals
                );

                _volumeStates[vol.Alias] = state;
            }
        }

        if (_totalFiles == 0)
        {
            lock (_stateLock)
            {
                _completedAt = DateTime.UtcNow;
                _isRunning = false;
            }
            return;
        }

        if (isSimulation)
        {
            await RunSimulationAsync(moveItems, durationSeconds, maxThreads, ct).ConfigureAwait(false);
        }
        else
        {
            // Real execution will be plugged in via SafeFileMover in Phase 4
            throw new NotSupportedException("Real filesystem execution is not enabled in this pass. Please select Simulation mode.");
        }
    }

    private async Task RunSimulationAsync(
        List<FileMoveTask> tasks,
        int durationSeconds,
        int maxThreads,
        CancellationToken ct)
    {
        if (durationSeconds <= 0) durationSeconds = 120;
        double targetRateBps = (double)_totalBytes / durationSeconds;
        double targetFilesPerSec = (double)_totalFiles / durationSeconds;

        var lockManager = new PhysicalDiskLockManager();

        // Group tasks by physical spindle pair, retaining largest-first order in each queue
        var pairQueues = tasks
            .GroupBy(t => (t.SourceDisk, t.TargetDisk))
            .Select(g => new PairQueue(g.Key.SourceDisk, g.Key.TargetDisk, g))
            .ToList();

        var activeWorkers = new List<SimWorker>();
        for (int i = 0; i < maxThreads; i++)
        {
            activeWorkers.Add(new SimWorker(i + 1));
        }

        var lastTickTime = DateTime.UtcNow;

        try
        {
            while (pairQueues.Any(pq => pq.Tasks.Count > 0) || activeWorkers.Any(w => w.CurrentTask != null))
            {
                ct.ThrowIfCancellationRequested();

                var now = DateTime.UtcNow;
                double elapsedDeltaSec = (now - lastTickTime).TotalSeconds;
                if (elapsedDeltaSec <= 0) elapsedDeltaSec = 0.001;
                lastTickTime = now;

                // 1. Assign work to idle workers (prioritize draining full source disks first)
                var sortedQueues = pairQueues
                    .Where(pq => pq.Tasks.Count > 0)
                    .OrderByDescending(GetQueuePriority)
                    .ToList();

                foreach (var worker in activeWorkers.Where(w => w.CurrentTask == null))
                {
                    // Pass 1: Try assigning tasks while respecting headroom reserve on target volumes
                    bool assigned = TryAssignTask(worker, sortedQueues, respectHeadroomReserve: true, lockManager, targetRateBps, maxThreads);
                    if (!assigned)
                    {
                        // Pass 2 fallback: If no task fits with reserve, allow using reserve space so nothing stalls
                        TryAssignTask(worker, sortedQueues, respectHeadroomReserve: false, lockManager, targetRateBps, maxThreads);
                    }
                }

                // 2. Deadlock & Insufficient Space Guard:
                // Check if all workers are idle and NO remaining task in ANY queue can fit on its destination volume.
                int runningCount = activeWorkers.Count(w => w.CurrentTask != null);
                if (runningCount == 0 && pairQueues.Any(pq => pq.Tasks.Count > 0))
                {
                    bool hasAnyFittingTask = pairQueues.Any(pq => pq.Tasks.Any(t =>
                    {
                        if (!_volumeStates.TryGetValue(t.TargetVolume, out var vs)) return false;
                        long free = Math.Max(0, vs.Capacity - vs.CurrentSize);
                        return free >= t.SizeOnDisk;
                    }));

                    if (!hasAnyFittingTask)
                    {
                        int remainingCount = pairQueues.Sum(pq => pq.Tasks.Count);
                        long remainingBytes = pairQueues.Sum(pq => pq.Tasks.Sum(t => t.SizeOnDisk));

                        var blockedSummary = pairQueues
                            .SelectMany(pq => pq.Tasks)
                            .GroupBy(t => t.TargetVolume)
                            .Where(g =>
                            {
                                _volumeStates.TryGetValue(g.Key, out var vs);
                                long free = vs != null ? Math.Max(0, vs.Capacity - vs.CurrentSize) : 0;
                                return free < g.Min(x => x.SizeOnDisk);
                            })
                            .Select(g =>
                            {
                                _volumeStates.TryGetValue(g.Key, out var vs);
                                long free = vs != null ? Math.Max(0, vs.Capacity - vs.CurrentSize) : 0;
                                return $"{g.Key} ({g.Count():N0} files waiting, smallest is {FormatBytes(g.Min(x => x.SizeOnDisk))}, but only {FormatBytes(free)} free)";
                            });

                        throw new InvalidOperationException(
                            $"Execution halted: {remainingCount:N0} remaining files ({FormatBytes(remainingBytes)}) cannot move because destination volumes have insufficient free space: {string.Join("; ", blockedSummary)}."
                        );
                    }
                }

                // 3. Advance bytes & files on active workers
                lock (_stateLock)
            {
                _activeWorkers = runningCount;
                _throughputBps = runningCount > 0 ? targetRateBps : 0;
            }

            double tickBytesBudget = runningCount > 0 ? (targetRateBps * elapsedDeltaSec) / runningCount : 0;
            double tickFilesBudget = runningCount > 0 ? (targetFilesPerSec * elapsedDeltaSec) / runningCount : 0;

            foreach (var worker in activeWorkers.Where(w => w.CurrentTask != null))
            {
                double budgetBytes = tickBytesBudget;
                double budgetFiles = tickFilesBudget;
                int filesCompletedThisTick = 0;

                while (worker.CurrentTask != null && (budgetBytes > 0 || budgetFiles > 0) && filesCompletedThisTick < 10000)
                {
                    var task = worker.CurrentTask;
                    long bytesRemainingOnTask = task.SizeOnDisk - worker.BytesCopied;

                    if (budgetBytes >= bytesRemainingOnTask || (budgetFiles >= 1 && bytesRemainingOnTask <= 0))
                    {
                        // Task completes in this tick
                        long advance = bytesRemainingOnTask > 0 ? bytesRemainingOnTask : 0;
                        worker.BytesCopied += advance;
                        Interlocked.Add(ref _transferredBytes, advance);
                        Interlocked.Increment(ref _transferredFiles);

                        budgetBytes = Math.Max(0, budgetBytes - advance);
                        budgetFiles = Math.Max(0, budgetFiles - 1);
                        filesCompletedThisTick++;

                        if (_volumeStates.TryGetValue(task.SourceVolume, out var srcState))
                            srcState.CompleteMovedOut(task.TargetVolume, task.SizeOnDisk);
                        if (_volumeStates.TryGetValue(task.TargetVolume, out var tgtState))
                            tgtState.CompleteMovedIn(task.SourceVolume, task.SizeOnDisk);

                        // Try to take next task from the same queue while holding the disk lock
                        if (worker.CurrentQueue != null && worker.CurrentQueue.Tasks.Count > 0)
                        {
                            var nextNode = FindCandidate(worker.CurrentQueue, respectHeadroomReserve: true)
                                        ?? FindCandidate(worker.CurrentQueue, respectHeadroomReserve: false);

                            if (nextNode != null)
                            {
                                var nextTask = nextNode.Value;
                                worker.CurrentQueue.Tasks.Remove(nextNode);
                                worker.SetNextTask(nextTask);

                                if (_volumeStates.TryGetValue(nextTask.SourceVolume, out var sVol))
                                    sVol.SetActivity($"Reading: \"{nextTask.FileName}\"", "Reading");
                                if (_volumeStates.TryGetValue(nextTask.TargetVolume, out var tVol))
                                    tVol.SetActivity($"Writing: \"{nextTask.FileName}\"", "Writing");

                                _activeTransfers[worker.WorkerId] = new ActiveTransferInfo(
                                    worker.WorkerId,
                                    nextTask.FileName,
                                    nextTask.SourceVolume,
                                    nextTask.TargetVolume,
                                    nextTask.SourceDisk,
                                    nextTask.TargetDisk,
                                    nextTask.SizeOnDisk,
                                    0,
                                    targetRateBps / maxThreads
                                );
                                continue;
                            }
                        }

                        // No more immediate tasks on this queue or target is full; worker completes
                        if (_volumeStates.TryGetValue(task.SourceVolume, out var sVolDone))
                            sVolDone.SetActivity("Idle", "Idle");
                        if (_volumeStates.TryGetValue(task.TargetVolume, out var tVolDone))
                            tVolDone.SetActivity("Idle", "Idle");

                        worker.Complete();
                        _activeTransfers.TryRemove(worker.WorkerId, out _);
                        break;
                    }
                    else
                    {
                        // File takes longer than budget, partially advance
                        long advance = (long)budgetBytes;
                        if (advance <= 0 && bytesRemainingOnTask > 0) advance = bytesRemainingOnTask;

                        worker.BytesCopied += advance;
                        Interlocked.Add(ref _transferredBytes, advance);
                        budgetBytes = 0;
                        budgetFiles = 0;

                        _activeTransfers[worker.WorkerId] = new ActiveTransferInfo(
                            worker.WorkerId,
                            task.FileName,
                            task.SourceVolume,
                            task.TargetVolume,
                            task.SourceDisk,
                            task.TargetDisk,
                            task.SizeOnDisk,
                            worker.BytesCopied,
                            targetRateBps / maxThreads
                        );
                        break;
                    }
                }
            }

            await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var w in activeWorkers)
            {
                w.Complete();
            }
        }

        lock (_stateLock)
        {
            _completedAt = DateTime.UtcNow;
            _isRunning = false;
            _activeWorkers = 0;
            _throughputBps = 0;
            _phase = "Completed";
            _activeTransfers.Clear();

            foreach (var v in _volumeStates.Values)
            {
                v.SetActivity("Complete", "Complete");
            }
        }
    }

    private LinkedListNode<FileMoveTask>? FindCandidate(PairQueue pq, bool respectHeadroomReserve)
    {
        for (var node = pq.Tasks.First; node != null; node = node.Next)
        {
            var candidate = node.Value;
            if (!_volumeStates.TryGetValue(candidate.TargetVolume, out var tgtState)) continue;
            long tgtFree = Math.Max(0, tgtState.Capacity - tgtState.CurrentSize);

            if (tgtFree < candidate.SizeOnDisk) continue;

            if (respectHeadroomReserve)
            {
                long tgtPendingOut = Math.Max(0, tgtState.MovedOutTotalBytes - tgtState.MovedOutBytes);
                if (tgtPendingOut > 0)
                {
                    // Keep a headroom buffer (up to 20 GiB or 5% of capacity) so the target volume doesn't choke its outgoing transfers
                    long reserve = Math.Min(20L * 1024 * 1024 * 1024, tgtState.Capacity / 20);
                    if (tgtFree - candidate.SizeOnDisk < reserve)
                    {
                        continue;
                    }
                }
            }

            return node;
        }

        return null;
    }

    private double GetQueuePriority(PairQueue pq)
    {
        var firstTask = pq.Tasks.First?.Value;
        if (firstTask == null) return 0;

        double score = 0;
        if (_volumeStates.TryGetValue(firstTask.SourceVolume, out var src))
        {
            // Highest priority to fullest source volumes (drain crowded disks first)
            double srcFullness = src.Capacity > 0 ? (double)src.CurrentSize / src.Capacity : 0;
            score += srcFullness * 100.0;

            long srcPendingOut = src.MovedOutTotalBytes - src.MovedOutBytes;
            if (srcPendingOut > 0)
                score += 50.0;
        }

        if (_volumeStates.TryGetValue(firstTask.TargetVolume, out var tgt))
        {
            // Depenalize targeting a nearly-full volume that hasn't finished draining its own outgoing data
            long tgtPendingOut = tgt.MovedOutTotalBytes - tgt.MovedOutBytes;
            if (tgtPendingOut > 0)
            {
                double tgtFullness = tgt.Capacity > 0 ? (double)tgt.CurrentSize / tgt.Capacity : 0;
                score -= tgtFullness * 60.0;
            }
        }

        return score;
    }

    private bool TryAssignTask(
        SimWorker worker,
        List<PairQueue> queues,
        bool respectHeadroomReserve,
        PhysicalDiskLockManager lockManager,
        double targetRateBps,
        int maxThreads)
    {
        foreach (var pq in queues.Where(pq => pq.Tasks.Count > 0))
        {
            var candidateNode = FindCandidate(pq, respectHeadroomReserve);
            if (candidateNode == null) continue;

            if (lockManager.TryAcquire(pq.SourceDisk, pq.TargetDisk, out var releaser) && releaser != null)
            {
                var task = candidateNode.Value;
                pq.Tasks.Remove(candidateNode);
                worker.StartTask(task, releaser, pq);

                if (_volumeStates.TryGetValue(task.SourceVolume, out var srcVol))
                    srcVol.SetActivity($"Reading: \"{task.FileName}\"", "Reading");
                if (_volumeStates.TryGetValue(task.TargetVolume, out var tgtVol))
                    tgtVol.SetActivity($"Writing: \"{task.FileName}\"", "Writing");

                _activeTransfers[worker.WorkerId] = new ActiveTransferInfo(
                    worker.WorkerId,
                    task.FileName,
                    task.SourceVolume,
                    task.TargetVolume,
                    task.SourceDisk,
                    task.TargetDisk,
                    task.SizeOnDisk,
                    0,
                    targetRateBps / maxThreads
                );
                return true;
            }
        }

        return false;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        int i = Math.Min((int)Math.Floor(Math.Log(bytes) / Math.Log(1024)), units.Length - 1);
        return $"{bytes / Math.Pow(1024, i):F1} {units[i]}";
    }

    private sealed class PairQueue(string sourceDisk, string targetDisk, IEnumerable<FileMoveTask> tasks)
    {
        public string SourceDisk { get; } = sourceDisk;
        public string TargetDisk { get; } = targetDisk;
        public LinkedList<FileMoveTask> Tasks { get; } = new(tasks);
    }

    private sealed class FileMoveTask(
        string fileName,
        string relativePath,
        string sourceVolume,
        string targetVolume,
        string sourceDisk,
        string targetDisk,
        long size,
        long sizeOnDisk)
    {
        public string FileName { get; } = fileName;
        public string RelativePath { get; } = relativePath;
        public string SourceVolume { get; } = sourceVolume;
        public string TargetVolume { get; } = targetVolume;
        public string SourceDisk { get; } = sourceDisk;
        public string TargetDisk { get; } = targetDisk;
        public long Size { get; } = size;
        public long SizeOnDisk { get; } = sizeOnDisk;
    }

    private sealed class SimWorker(int workerId)
    {
        public int WorkerId { get; } = workerId;
        public FileMoveTask? CurrentTask { get; private set; }
        public PairQueue? CurrentQueue { get; private set; }
        public IDisposable? LockReleaser { get; private set; }
        public long BytesCopied { get; set; }

        public void StartTask(FileMoveTask task, IDisposable releaser, PairQueue queue)
        {
            CurrentTask = task;
            LockReleaser = releaser;
            CurrentQueue = queue;
            BytesCopied = 0;
        }

        public void SetNextTask(FileMoveTask task)
        {
            CurrentTask = task;
            BytesCopied = 0;
        }

        public void Complete()
        {
            LockReleaser?.Dispose();
            LockReleaser = null;
            CurrentQueue = null;
            CurrentTask = null;
            BytesCopied = 0;
        }
    }

    private sealed class VolumeState
    {
        private readonly object _lock = new();

        public string Alias { get; }
        public string DiskName { get; }
        public long Capacity { get; }
        public long CurrentSize { get; private set; }
        public long FinalSize { get; }
        public long StayedSize { get; private set; }
        public long OtherItemsSizeOnDisk { get; }

        public long MovedOutTotalBytes { get; }
        public long MovedOutTotalFiles { get; }
        public long MovedInTotalBytes { get; }
        public long MovedInTotalFiles { get; }

        public long MovedOutBytes { get; private set; }
        public long MovedOutFiles { get; private set; }
        public long MovedInBytes { get; private set; }
        public long MovedInFiles { get; private set; }

        public string Status { get; private set; } = "Idle";
        public string CurrentActivity { get; private set; } = "Idle";

        private readonly Dictionary<string, long> _outgoingRemaining;
        private readonly Dictionary<string, long> _outgoingTotals;
        private readonly Dictionary<string, long> _incomingRemaining;
        private readonly Dictionary<string, long> _incomingTotals;

        public VolumeState(
            string alias,
            string diskName,
            long capacity,
            long currentSize,
            long finalSize,
            long stayedSize,
            long otherItemsSizeOnDisk,
            long movedOutTotalBytes,
            long movedOutTotalFiles,
            long movedInTotalBytes,
            long movedInTotalFiles,
            Dictionary<string, long> outgoingTotals,
            Dictionary<string, long> incomingTotals)
        {
            Alias = alias;
            DiskName = diskName;
            Capacity = capacity;
            CurrentSize = currentSize;
            FinalSize = finalSize;
            StayedSize = stayedSize;
            OtherItemsSizeOnDisk = otherItemsSizeOnDisk;

            MovedOutTotalBytes = movedOutTotalBytes;
            MovedOutTotalFiles = movedOutTotalFiles;
            MovedInTotalBytes = movedInTotalBytes;
            MovedInTotalFiles = movedInTotalFiles;

            _outgoingTotals = new Dictionary<string, long>(outgoingTotals, StringComparer.OrdinalIgnoreCase);
            _outgoingRemaining = new Dictionary<string, long>(outgoingTotals, StringComparer.OrdinalIgnoreCase);

            _incomingTotals = new Dictionary<string, long>(incomingTotals, StringComparer.OrdinalIgnoreCase);
            _incomingRemaining = new Dictionary<string, long>(incomingTotals, StringComparer.OrdinalIgnoreCase);
        }

        public void SetActivity(string activity, string status)
        {
            lock (_lock)
            {
                CurrentActivity = activity;
                Status = status;
            }
        }

        public void CompleteMovedOut(string targetVolume, long size)
        {
            lock (_lock)
            {
                MovedOutBytes += size;
                MovedOutFiles += 1;
                CurrentSize = Math.Max(0, CurrentSize - size);

                if (_outgoingRemaining.TryGetValue(targetVolume, out var rem))
                {
                    _outgoingRemaining[targetVolume] = Math.Max(0, rem - size);
                }
            }
        }

        public void CompleteMovedIn(string sourceVolume, long size)
        {
            lock (_lock)
            {
                MovedInBytes += size;
                MovedInFiles += 1;
                CurrentSize += size;
                StayedSize += size; // Absorbs into stayed data!

                if (_incomingRemaining.TryGetValue(sourceVolume, out var rem))
                {
                    _incomingRemaining[sourceVolume] = Math.Max(0, rem - size);
                }
            }
        }

        public VolumeExecutionProgress ToProgress()
        {
            lock (_lock)
            {
                var outSegments = _outgoingRemaining
                    .Select(kv => new TransferSegment(kv.Key, kv.Value, _outgoingTotals.GetValueOrDefault(kv.Key, kv.Value)))
                    .ToImmutableList();

                var inSegments = _incomingRemaining
                    .Select(kv => new TransferSegment(kv.Key, kv.Value, _incomingTotals.GetValueOrDefault(kv.Key, kv.Value)))
                    .ToImmutableList();

                return new VolumeExecutionProgress(
                    Alias,
                    DiskName,
                    Capacity,
                    CurrentSize,
                    FinalSize,
                    StayedSize,
                    OtherItemsSizeOnDisk,
                    outSegments,
                    inSegments,
                    MovedOutBytes,
                    MovedOutTotalBytes,
                    MovedOutFiles,
                    MovedOutTotalFiles,
                    MovedInBytes,
                    MovedInTotalBytes,
                    MovedInFiles,
                    MovedInTotalFiles,
                    Status,
                    CurrentActivity
                );
            }
        }
    }
}

