using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
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
    private ExecutionPhase _phase = ExecutionPhase.Idle;
    private FolderCleanupSummary? _folderCleanup;
    private ImmutableList<FolderCleanupAction> _allFolderCleanupActions = ImmutableList<FolderCleanupAction>.Empty;

    private readonly ConcurrentDictionary<string, VolumeState> _volumeStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ActiveTransferInfo> _activeTransfers = new();
    private int? _maxFilesToCopy;
    private long _lastDiagnosticLogTicks;
    private readonly List<TransferErrorItem> _transferErrors = new();

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
                _isRunning ? _activeTransfers.Values.Sum(a => a.ThroughputBps) : 0,
                _isRunning ? _activeTransfers.Count : 0,
                _error,
                volumes,
                activeTransfers,
                _phase,
                _folderCleanup,
                _transferErrors.ToImmutableList()
            );
        }
    }

    public (int TotalMatching, ImmutableList<FolderCleanupAction> Items) QueryFolderCleanup(string? status, string? search, int limit = 500, int offset = 0)
    {
        lock (_stateLock)
        {
            if (_allFolderCleanupActions.IsEmpty)
                return (0, ImmutableList<FolderCleanupAction>.Empty);

            IEnumerable<FolderCleanupAction> query = _allFolderCleanupActions;

            if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "All", StringComparison.OrdinalIgnoreCase))
            {
                if (Enum.TryParse<FolderCleanupStatus>(status, true, out var targetStatus))
                {
                    query = query.Where(a => a.Status == targetStatus);
                }
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                string s = search.Trim();
                query = query.Where(a =>
                    a.RelativePath.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                    a.VolumeAlias.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                    a.PrimaryVolumeAlias.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                    a.Reason.Contains(s, StringComparison.OrdinalIgnoreCase));
            }

            var matchingList = query.ToList();
            int total = matchingList.Count;

            if (offset < 0) offset = 0;
            if (limit <= 0) limit = 500;
            if (limit > 2000) limit = 2000;

            var items = matchingList
                .Skip(offset)
                .Take(limit)
                .ToImmutableList();

            return (total, items);
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
            _phase = ExecutionPhase.Preparing;
            _folderCleanup = null;
            _allFolderCleanupActions = ImmutableList<FolderCleanupAction>.Empty;

            _transferredBytes = 0;
            _transferredFiles = 0;
            _maxFilesToCopy = request.MaxFilesToCopy;
            _volumeStates.Clear();
            _activeTransfers.Clear();
            _transferErrors.Clear();

            int maxThreads = request.MaxThreads.GetValueOrDefault(config.MaxExecutionThreads);
            if (maxThreads < 1) maxThreads = 1;
            if (maxThreads > 16) maxThreads = 16;

            int durationSeconds;
            if (request.SimulationDurationSeconds == 0)
            {
                durationSeconds = 0;
            }
            else if (request.SimulationDurationSeconds > 0)
            {
                durationSeconds = Math.Max(5, request.SimulationDurationSeconds);
            }
            else
            {
                durationSeconds = Math.Max(5, config.DefaultSimulationDurationSeconds);
            }

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
                        _phase = ExecutionPhase.Cancelled;
                    }
                }
                catch (Exception ex)
                {
                    lock (_stateLock)
                    {
                        _error = ex.Message;
                        _completedAt = DateTime.UtcNow;
                        _isRunning = false;
                        _phase = ExecutionPhase.Failed;
                    }
                }
                finally
                {
                    lock (_stateLock)
                    {
                        _isRunning = false;
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
            if (!_isRunning)
            {
                _folderCleanup = plan.FolderCleanup;
                _allFolderCleanupActions = plan.FolderCleanup?.Actions ?? ImmutableList<FolderCleanupAction>.Empty;
            }
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

        var volumeRootMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in snapshot.Volumes)
        {
            if (!string.IsNullOrWhiteSpace(v.RootFolderPath))
            {
                volumeRootMap[v.Alias] = v.RootFolderPath;
            }
        }
        foreach (var vc in config.Volumes)
        {
            if (!volumeRootMap.ContainsKey(vc.Alias))
            {
                string root = Path.Combine(vc.MountPoint ?? "", vc.RootFolderRelativePath ?? "");
                volumeRootMap[vc.Alias] = root;
            }
        }

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
                    string srcRoot = volumeRootMap.GetValueOrDefault(f.OriginalVolumeAlias, "");
                    string tgtRoot = volumeRootMap.GetValueOrDefault(f.DestinationVolumeAlias, "");
                    string cleanRelativePath = f.RelativePath.TrimStart('\\', '/');
                    string srcPath = !string.IsNullOrEmpty(f.OriginalFullPath)
                        ? f.OriginalFullPath
                        : (!string.IsNullOrEmpty(srcRoot) ? Path.Combine(srcRoot, cleanRelativePath) : "");
                    string tgtPath = !string.IsNullOrEmpty(tgtRoot) ? Path.Combine(tgtRoot, cleanRelativePath) : "";

                    moveItems.Add(new FileMoveTask(
                        f.Name,
                        f.RelativePath,
                        f.OriginalVolumeAlias,
                        f.DestinationVolumeAlias,
                        srcDisk,
                        tgtDisk,
                        f.Size,
                        f.SizeOnDisk,
                        srcPath,
                        tgtPath
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
            _phase = ExecutionPhase.Transferring;

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

                long initialCurrentSize = selfStayed + initialTotalMovedOutBytes;

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

        if (_totalFiles == 0 && (plan.FolderCleanup == null || plan.FolderCleanup.CleanedCount == 0))
        {
            lock (_stateLock)
            {
                _completedAt = DateTime.UtcNow;
                _isRunning = false;
            }
            return;
        }

        IFileSystemOperator fsOperator = isSimulation
            ? new SimulatedFileSystemOperator(durationSeconds, _totalBytes, maxThreads)
            : new RealFileSystemOperator(config.VerifyCopies);

        await ExecutePlanAsync(fsOperator, moveItems, plan.FolderCleanup, volumeRootMap, durationSeconds, maxThreads, ct).ConfigureAwait(false);
    }

    private async Task ExecutePlanAsync(
        IFileSystemOperator fsOperator,
        List<FileMoveTask> tasks,
        FolderCleanupSummary? folderCleanup,
        Dictionary<string, string> volumeRootMap,
        int durationSeconds,
        int maxThreads,
        CancellationToken ct)
    {
        // 1. Ensure all designated primary folders exist before starting file transfers.
        // This ensures migrated empty folders and targets exist up-front. If a user deletes
        // a folder during the file copy process (which may take hours), we avoid re-creating it later.
        if (folderCleanup != null)
        {
            foreach (var action in folderCleanup.Actions.Where(a => a.Status == FolderCleanupStatus.Cleaning || a.Status == FolderCleanupStatus.PreservingUnique))
            {
                ct.ThrowIfCancellationRequested();

                string primaryRoot = volumeRootMap.GetValueOrDefault(action.PrimaryVolumeAlias, "");
                string cleanRelPath = action.RelativePath.TrimStart('\\', '/');
                string primaryFolderPath = !string.IsNullOrEmpty(primaryRoot) ? Path.Combine(primaryRoot, cleanRelPath) : "";

                if (!string.IsNullOrEmpty(primaryFolderPath))
                {
                    await fsOperator.EnsureFolderExistsAsync(primaryFolderPath, ct).ConfigureAwait(false);
                }
            }
        }

        var lockManager = new PhysicalDiskLockManager();
        var queueLock = new object();

        // Group tasks by physical spindle pair, retaining largest-first order in each queue
        var pairQueues = tasks
            .GroupBy(t => (t.SourceDisk, t.TargetDisk))
            .Select(g => new PairQueue(g.Key.SourceDisk, g.Key.TargetDisk, g))
            .ToList();

        var workerTasks = new List<Task>();
        for (int i = 0; i < maxThreads; i++)
        {
            int workerId = i + 1;
            workerTasks.Add(Task.Run(() => RunWorkerLoopAsync(workerId, fsOperator, pairQueues, lockManager, queueLock, ct), ct));
        }

        try
        {
            await Task.WhenAll(workerTasks).ConfigureAwait(false);
        }
        finally
        {
            _activeTransfers.Clear();
        }

        lock (_stateLock)
        {
            _phase = ExecutionPhase.CleaningFolders;
            _activeTransfers.Clear();
            foreach (var v in _volumeStates.Values)
            {
                v.SetActivity("Cleaning empty folders", VolumeActivityStatus.CleaningFolders);
            }
        }

        if (folderCleanup != null)
        {
            foreach (var action in folderCleanup.Actions.Where(a => a.Status == FolderCleanupStatus.Cleaning))
            {
                ct.ThrowIfCancellationRequested();

                string volRoot = volumeRootMap.GetValueOrDefault(action.VolumeAlias, "");
                string primaryRoot = volumeRootMap.GetValueOrDefault(action.PrimaryVolumeAlias, "");
                string cleanRelPath = action.RelativePath.TrimStart('\\', '/');

                string targetFolderPath = !string.IsNullOrEmpty(volRoot) ? Path.Combine(volRoot, cleanRelPath) : "";
                string primaryFolderPath = !string.IsNullOrEmpty(primaryRoot) ? Path.Combine(primaryRoot, cleanRelPath) : "";

                var cleanupTask = new FolderCleanupTask(
                    action.RelativePath,
                    action.VolumeAlias,
                    action.PrimaryVolumeAlias,
                    targetFolderPath,
                    primaryFolderPath
                );

                await fsOperator.DeleteFolderAsync(cleanupTask, ct).ConfigureAwait(false);
            }
        }

        lock (_stateLock)
        {
            _allFolderCleanupActions = folderCleanup?.Actions ?? ImmutableList<FolderCleanupAction>.Empty;
            _folderCleanup = folderCleanup != null ? folderCleanup with { Actions = ImmutableList<FolderCleanupAction>.Empty } : null;
            _completedAt = DateTime.UtcNow;
            _isRunning = false;
            _phase = ExecutionPhase.Completed;
            _activeTransfers.Clear();

            foreach (var v in _volumeStates.Values)
            {
                v.SetActivity("Complete", VolumeActivityStatus.Complete);
            }
        }
    }

    private async Task RunWorkerLoopAsync(
        int workerId,
        IFileSystemOperator fsOperator,
        List<PairQueue> pairQueues,
        PhysicalDiskLockManager lockManager,
        object queueLock,
        CancellationToken ct)
    {
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

            PairQueue? activeQueue = null;
            IDisposable? lockReleaser = null;
            FileMoveTask? currentTask = null;

            lock (queueLock)
            {
                if (_maxFilesToCopy.HasValue && _transferredFiles >= _maxFilesToCopy.Value)
                {
                    return;
                }

                // Check if all work across all queues is done
                if (pairQueues.All(pq => pq.Tasks.Count == 0))
                {
                    return;
                }

                // Prioritize draining fullest source disks first
                var sortedQueues = pairQueues
                    .Where(pq => pq.Tasks.Count > 0)
                    .OrderByDescending(GetQueuePriority)
                    .ToList();

                foreach (var pq in sortedQueues)
                {
                    // Pass 1: candidate respecting target volume headroom reserve
                    // Pass 2: fallback to any fitting candidate so progress does not stall
                    var candidateNode = FindCandidate(pq, respectHeadroomReserve: true)
                                     ?? FindCandidate(pq, respectHeadroomReserve: false);

                    if (candidateNode != null)
                    {
                        if (lockManager.TryAcquire(pq.SourceDisk, pq.TargetDisk, out var releaser) && releaser != null)
                        {
                            activeQueue = pq;
                            lockReleaser = releaser;
                            currentTask = candidateNode.Value;
                            pq.Tasks.Remove(candidateNode);
                            break;
                        }
                    }
                }

                // Deadlock check: all workers idle, queues have tasks, but no remaining task fits in destination free space
                if (currentTask == null && _activeTransfers.IsEmpty && pairQueues.Any(pq => pq.Tasks.Count > 0))
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
            }

            if (currentTask == null)
            {
                long now = Environment.TickCount64;
                if (now - Interlocked.Read(ref _lastDiagnosticLogTicks) > 3000)
                {
                    Interlocked.Exchange(ref _lastDiagnosticLogTicks, now);
                    LogQueueBlockedDiagnostics(workerId, pairQueues, lockManager);
                }

                // Disks currently locked by other workers or waiting on headroom space; wait briefly and retry
                await Task.Delay(25, ct).ConfigureAwait(false);
                continue;
            }

            // We hold the physical spindle locks for this queue.
            // Run tasks continuously on this spindle pair to maximize sequential I/O throughput.
            try
            {
                while (currentTask != null)
                {
                    ct.ThrowIfCancellationRequested();

                    if (_volumeStates.TryGetValue(currentTask.SourceVolume, out var sVol))
                        sVol.SetActivity($"Reading: \"{currentTask.FileName}\"", VolumeActivityStatus.Reading);
                    if (_volumeStates.TryGetValue(currentTask.TargetVolume, out var tVol))
                        tVol.SetActivity($"Writing: \"{currentTask.FileName}\"", VolumeActivityStatus.Writing);

                    Console.WriteLine($"COPYING: {currentTask.SourceVolume} to {currentTask.TargetVolume}, {currentTask.RelativePath} ({FormatBytes(currentTask.SizeOnDisk)})");

                    _activeTransfers[workerId] = new ActiveTransferInfo(
                        workerId,
                        currentTask.FileName,
                        currentTask.SourceVolume,
                        currentTask.TargetVolume,
                        currentTask.SourceDisk,
                        currentTask.TargetDisk,
                        currentTask.SizeOnDisk,
                        0,
                        0
                    );

                    long taskBytesTransferred = 0;
                    var sw = Stopwatch.StartNew();

                    bool copySucceeded = false;
                    try
                    {
                        await fsOperator.TransferFileAsync(currentTask, chunkBytes =>
                        {
                            if (chunkBytes > 0)
                            {
                                taskBytesTransferred += chunkBytes;
                                Interlocked.Add(ref _transferredBytes, chunkBytes);
                            }

                            double bps = sw.Elapsed.TotalSeconds > 0 ? (double)taskBytesTransferred / sw.Elapsed.TotalSeconds : 0;
                            _activeTransfers[workerId] = new ActiveTransferInfo(
                                workerId,
                                currentTask.FileName,
                                currentTask.SourceVolume,
                                currentTask.TargetVolume,
                                currentTask.SourceDisk,
                                currentTask.TargetDisk,
                                currentTask.SizeOnDisk,
                                taskBytesTransferred,
                                bps
                            );
                        }, ct).ConfigureAwait(false);

                        Console.WriteLine($"COPIED: {currentTask.SourceVolume} to {currentTask.TargetVolume}, {currentTask.FileName} ({FormatBytes(currentTask.SizeOnDisk)}) in {sw.Elapsed.TotalSeconds:F1}s");
                        copySucceeded = true;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"\n[Thread {workerId}] FAILED COPYING: {currentTask.SourceVolume} to {currentTask.TargetVolume}, {currentTask.RelativePath}\n  -> Reason: {ex.GetType().Name} - {ex.Message}\n");

                        var errorItem = new TransferErrorItem(
                            currentTask.SourceVolume,
                            currentTask.TargetVolume,
                            currentTask.RelativePath,
                            currentTask.FileName,
                            currentTask.SizeOnDisk,
                            $"{ex.GetType().Name}: {ex.Message}",
                            DateTime.UtcNow
                        );

                        lock (_stateLock)
                        {
                            _transferErrors.Add(errorItem);
                            if (taskBytesTransferred > 0)
                            {
                                Interlocked.Add(ref _transferredBytes, -taskBytesTransferred);
                            }
                            Interlocked.Add(ref _totalBytes, -currentTask.SizeOnDisk);
                            Interlocked.Decrement(ref _totalFiles);
                        }

                        if (_volumeStates.TryGetValue(currentTask.SourceVolume, out var srcFailState))
                            srcFailState.RecordFailedMovedOut(currentTask.TargetVolume, currentTask.SizeOnDisk);
                        if (_volumeStates.TryGetValue(currentTask.TargetVolume, out var tgtFailState))
                            tgtFailState.RecordFailedMovedIn(currentTask.SourceVolume, currentTask.SizeOnDisk);
                    }

                    if (copySucceeded)
                    {
                        // Successfully completed this file
                        long completedCount = Interlocked.Increment(ref _transferredFiles);
                        if (_volumeStates.TryGetValue(currentTask.SourceVolume, out var srcState))
                            srcState.CompleteMovedOut(currentTask.TargetVolume, currentTask.SizeOnDisk);
                        if (_volumeStates.TryGetValue(currentTask.TargetVolume, out var tgtState))
                            tgtState.CompleteMovedIn(currentTask.SourceVolume, currentTask.SizeOnDisk);

                        if (_maxFilesToCopy.HasValue && completedCount >= _maxFilesToCopy.Value)
                        {
                            throw new InvalidOperationException($"Test limit reached: {completedCount} file(s) copied (limit was {_maxFilesToCopy.Value}). Execution aborted for test.");
                        }
                    }

                    // Check if another task can immediately be processed on this same spindle pair
                    lock (queueLock)
                    {
                        if (_maxFilesToCopy.HasValue && _transferredFiles >= _maxFilesToCopy.Value)
                        {
                            currentTask = null;
                            break;
                        }

                        var nextNode = FindCandidate(activeQueue!, respectHeadroomReserve: true)
                                    ?? FindCandidate(activeQueue!, respectHeadroomReserve: false);

                        if (nextNode != null)
                        {
                            currentTask = nextNode.Value;
                            activeQueue!.Tasks.Remove(nextNode);
                        }
                        else
                        {
                            currentTask = null;
                        }
                    }
                }
            }
            finally
            {
                _activeTransfers.TryRemove(workerId, out _);
                lockReleaser?.Dispose();
                ResetVolumeActivityIfIdle();
            }
        }
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Worker cancelled
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[Thread {workerId}] WORKER TERMINATED UNEXPECTEDLY: {ex.GetType().Name} - {ex.Message}\n");
            throw;
        }
    }

    private void ResetVolumeActivityIfIdle()
    {
        foreach (var (volName, vs) in _volumeStates)
        {
            bool isStillActive = _activeTransfers.Values.Any(t => t.SourceVolume == volName || t.TargetVolume == volName);
            if (!isStillActive && (vs.Status == VolumeActivityStatus.Reading || vs.Status == VolumeActivityStatus.Writing))
            {
                vs.SetActivity("Idle", VolumeActivityStatus.Idle);
            }
        }
    }

    private void LogQueueBlockedDiagnostics(int workerId, List<PairQueue> pairQueues, PhysicalDiskLockManager lockManager)
    {
        var activeQueues = pairQueues.Where(pq => pq.Tasks.Count > 0).ToList();
        if (activeQueues.Count == 0) return;

        var lockedDisks = activeQueues
            .SelectMany(pq => new[] { pq.SourceDisk, pq.TargetDisk })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(lockManager.IsLocked)
            .ToList();

        Console.WriteLine($"\n[Worker {workerId}] WAITING - All remaining tasks blocked. Active queues: {activeQueues.Count}");
        if (lockedDisks.Count > 0)
        {
            Console.WriteLine($"  [Physical Spindle Locks]: Currently busy disks: {string.Join(", ", lockedDisks)}");
        }
        else
        {
            Console.WriteLine("  [Physical Spindle Locks]: No physical disks are currently locked.");
        }

        foreach (var pq in activeQueues.Take(15))
        {
            bool srcLocked = lockManager.IsLocked(pq.SourceDisk);
            bool tgtLocked = lockManager.IsLocked(pq.TargetDisk);

            var firstTask = pq.Tasks.First?.Value;
            if (firstTask == null) continue;

            _volumeStates.TryGetValue(firstTask.TargetVolume, out var tgtState);
            long tgtFree = tgtState != null ? Math.Max(0, tgtState.Capacity - tgtState.CurrentSize) : 0;
            long tgtCap = tgtState?.Capacity ?? 0;
            long tgtCur = tgtState?.CurrentSize ?? 0;
            long reserve = tgtState != null ? Math.Min(20L * 1024 * 1024 * 1024, tgtState.Capacity / 20) : 0;
            long tgtPendingOut = tgtState != null ? Math.Max(0, tgtState.MovedOutTotalBytes - tgtState.MovedOutBytes) : 0;

            long minSize = pq.Tasks.Min(t => t.SizeOnDisk);
            long maxSize = pq.Tasks.Max(t => t.SizeOnDisk);

            var pass1 = FindCandidate(pq, respectHeadroomReserve: true);
            var pass2 = FindCandidate(pq, respectHeadroomReserve: false);

            string blockReason;
            if (srcLocked || tgtLocked)
            {
                var busy = new List<string>();
                if (srcLocked) busy.Add($"Source '{pq.SourceDisk}'");
                if (tgtLocked) busy.Add($"Target '{pq.TargetDisk}'");
                blockReason = $"SPINDLE BUSY: {string.Join(", ", busy)} locked by another worker.";
            }
            else if (pass1 == null && pass2 == null)
            {
                blockReason = $"INSUFFICIENT SPACE: Target '{firstTask.TargetVolume}' has {FormatBytes(tgtFree)} free (Cap: {FormatBytes(tgtCap)}, CurUsed: {FormatBytes(tgtCur)}), but smallest waiting file is {FormatBytes(minSize)}.";
            }
            else if (pass1 == null && pass2 != null)
            {
                blockReason = $"HEADROOM RESERVE: Target '{firstTask.TargetVolume}' has {FormatBytes(tgtFree)} free, needs {FormatBytes(reserve)} reserve (Pending out: {FormatBytes(tgtPendingOut)}). Fallback Pass 2 has candidate '{pass2.Value.FileName}' ({FormatBytes(pass2.Value.SizeOnDisk)}).";
            }
            else
            {
                blockReason = $"READY FOR PICKUP: Next candidate is '{pass1!.Value.FileName}' ({FormatBytes(pass1.Value.SizeOnDisk)}).";
            }

            Console.WriteLine($"  * Queue {pq.SourceDisk} -> {pq.TargetDisk} ({pq.Tasks.Count:N0} files, {FormatBytes(minSize)} to {FormatBytes(maxSize)}): {blockReason}");
        }

        if (activeQueues.Count > 15)
        {
            Console.WriteLine($"  * ... plus {activeQueues.Count - 15} more active queues.");
        }
        Console.WriteLine();
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

        public long MovedOutTotalBytes { get; private set; }
        public long MovedOutTotalFiles { get; private set; }
        public long MovedInTotalBytes { get; private set; }
        public long MovedInTotalFiles { get; private set; }

        public long MovedOutBytes { get; private set; }
        public long MovedOutFiles { get; private set; }
        public long MovedInBytes { get; private set; }
        public long MovedInFiles { get; private set; }

        public VolumeActivityStatus Status { get; private set; } = VolumeActivityStatus.Idle;
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

        public void SetActivity(string activity, VolumeActivityStatus status)
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

        public void RecordFailedMovedOut(string targetVolume, long size)
        {
            lock (_lock)
            {
                MovedOutTotalBytes = Math.Max(0, MovedOutTotalBytes - size);
                MovedOutTotalFiles = Math.Max(0, MovedOutTotalFiles - 1);
                StayedSize += size;
                if (_outgoingRemaining.TryGetValue(targetVolume, out var rem))
                {
                    _outgoingRemaining[targetVolume] = Math.Max(0, rem - size);
                }
            }
        }

        public void RecordFailedMovedIn(string sourceVolume, long size)
        {
            lock (_lock)
            {
                MovedInTotalBytes = Math.Max(0, MovedInTotalBytes - size);
                MovedInTotalFiles = Math.Max(0, MovedInTotalFiles - 1);
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

