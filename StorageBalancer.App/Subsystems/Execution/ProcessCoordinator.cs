using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using StorageBalancer.App.Configuration;
using StorageBalancer.App.Domain;
using StorageBalancer.App.Subsystems.Logging;
using StorageBalancer.App.Subsystems.Planning;
using StorageBalancer.App.Subsystems.Scanner;

namespace StorageBalancer.App.Subsystems.Execution;

public class ProcessCoordinator
{
    private readonly ConfigManager _configManager;
    private readonly StateScanner _scanner;
    private readonly PlacementPlanner _planner;
    private readonly PlanExecutor _executor;
    private readonly IWebHostEnvironment _environment;
    private readonly IAppEventLogger? _logger;

    private readonly object _lock = new();
    private bool _isRunning;
    private int _currentStep;
    private ProcessPipelinePhase _phase = ProcessPipelinePhase.Idle;
    private DateTime? _startedAt;
    private DateTime? _completedAt;
    private string? _error;
    private int? _maxFilesToCopy;
    private CancellationTokenSource? _cts;
    private PlacementPlan? _currentPlan;

    public ProcessCoordinator(
        ConfigManager configManager,
        StateScanner scanner,
        PlacementPlanner planner,
        PlanExecutor executor,
        IWebHostEnvironment environment,
        IAppEventLogger? logger = null)
    {
        _configManager = configManager;
        _scanner = scanner;
        _planner = planner;
        _executor = executor;
        _environment = environment;
        _logger = logger;
    }

    public ProcessPipelineStatus GetStatus()
    {
        lock (_lock)
        {
            var scanStatus = _scanner.GetStatus();
            var execStatus = _executor.GetStatus();

            bool running = _isRunning;
            int step = _currentStep;
            var phase = _phase;

            if (!_isRunning)
            {
                if (scanStatus.IsScanning)
                {
                    running = true;
                    step = 1;
                    phase = ProcessPipelinePhase.Scanning;
                }
                else if (execStatus.IsRunning)
                {
                    running = true;
                    step = 3;
                    phase = ProcessPipelinePhase.Executing;
                }
                else if (phase == ProcessPipelinePhase.Idle && execStatus.CompletedAt.HasValue && !execStatus.IsSimulation)
                {
                    step = 3;
                    phase = execStatus.Phase == ExecutionPhase.Cancelled
                        ? ProcessPipelinePhase.Cancelled
                        : (execStatus.Phase == ExecutionPhase.Failed ? ProcessPipelinePhase.Failed : ProcessPipelinePhase.Completed);
                }
            }

            return new ProcessPipelineStatus(
                IsRunning: running,
                IsCancellationRequested: (_cts?.IsCancellationRequested ?? false) || scanStatus.IsCancellationRequested || execStatus.IsCancellationRequested,
                CurrentStep: step,
                Phase: phase,
                StartedAt: _startedAt ?? scanStatus.StartedAt ?? execStatus.StartedAt,
                CompletedAt: _completedAt ?? execStatus.CompletedAt,
                Error: _error ?? execStatus.Error ?? scanStatus.Error,
                MaxFilesToCopy: _maxFilesToCopy,
                ScanStatus: scanStatus,
                ExecutionStatus: execStatus,
                HasPlan: _currentPlan != null
            );
        }
    }

    public PlacementPlan? GetCurrentPlan()
    {
        lock (_lock)
        {
            return _currentPlan;
        }
    }

    public bool TryStartProcess(int? maxFilesToCopy)
    {
        lock (_lock)
        {
            if (_isRunning || _scanner.GetStatus().IsScanning || _executor.GetStatus().IsRunning)
                return false;

            _isRunning = true;
            _currentStep = 1;
            _phase = ProcessPipelinePhase.Scanning;
            _startedAt = DateTime.UtcNow;
            _completedAt = null;
            _error = null;
            _maxFilesToCopy = maxFilesToCopy;
            _currentPlan = null;
            _cts = new CancellationTokenSource();

            var token = _cts.Token;
            Task.Run(() => RunPipelineAsync(maxFilesToCopy, token), token);
            return true;
        }
    }

    public bool TryCancelProcess()
    {
        lock (_lock)
        {
            if (!_isRunning)
            {
                bool cancelledAny = false;
                if (_scanner.GetStatus().IsScanning) cancelledAny |= _scanner.TryCancelScan();
                if (_executor.GetStatus().IsRunning) cancelledAny |= _executor.TryCancel();
                return cancelledAny;
            }

            _cts?.Cancel();

            if (_currentStep == 1)
            {
                _scanner.TryCancelScan();
            }
            else if (_currentStep == 3)
            {
                _executor.TryCancel();
            }

            return true;
        }
    }

    private async Task RunPipelineAsync(int? maxFilesToCopy, CancellationToken ct)
    {
        var config = _configManager.Load();
        string snapshotName = "In-Memory Scan";

        try
        {
            // ----------------------------------------------------
            // STEP 1: STATE SCAN
            // ----------------------------------------------------
            ct.ThrowIfCancellationRequested();

            if (!_scanner.TryStartScan(config, snapshotName))
            {
                throw new InvalidOperationException("Failed to initiate state scan.");
            }

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var scanStatus = _scanner.GetStatus();
                if (!scanStatus.IsScanning)
                {
                    if (scanStatus.IsCancellationRequested)
                    {
                        throw new OperationCanceledException("State scan was cancelled by user.");
                    }
                    if (!string.IsNullOrEmpty(scanStatus.Error))
                    {
                        throw new InvalidOperationException($"State scan failed: {scanStatus.Error}");
                    }
                    break;
                }
                await Task.Delay(300, ct).ConfigureAwait(false);
            }

            // ----------------------------------------------------
            // STEP 2: PLACEMENT PLANNING
            // ----------------------------------------------------
            ct.ThrowIfCancellationRequested();
            lock (_lock)
            {
                _currentStep = 2;
                _phase = ProcessPipelinePhase.Planning;
            }

            var snapshot = _scanner.GetLastSnapshot();
            if (snapshot == null)
            {
                throw new InvalidOperationException("No in-memory snapshot was produced by the state scan.");
            }

            _logger?.LogInfo("Balancing", $"Generating placement plan for snapshot '{snapshotName}'...");

            PlacementPlan plan = await Task.Run(() => _planner.CreatePlan(
                snapshot,
                config.FilePlacementRules,
                config.Duplicates,
                config.Filler,
                config.Unmatched,
                includeFiles: true), ct).ConfigureAwait(false);

            _executor.CachePlan(snapshotName, snapshot.ScannedAt, config, plan);

            lock (_lock)
            {
                _currentPlan = plan;
            }

            long totalMoveBytes = plan.Placements.Sum(p => p.SizeMoved);
            long totalFilesMoved = plan.Volumes.Sum(v => v.FilesMovedIn);
            _logger?.LogInfo("Balancing", $"Generated plan for snapshot '{snapshotName}'. Total placements: {plan.Placements.Length}, files moving: {totalFilesMoved:N0}, data moving: {totalMoveBytes:N0} bytes.");

            // ----------------------------------------------------
            // STEP 3: PLAN EXECUTION
            // ----------------------------------------------------
            ct.ThrowIfCancellationRequested();
            lock (_lock)
            {
                _currentStep = 3;
                _phase = ProcessPipelinePhase.Executing;
            }

            string? snapshotsFolder = null;
            if (!string.IsNullOrWhiteSpace(config.SnapshotsFolder))
            {
                try { snapshotsFolder = ResolveSnapshotsFolder(config, _environment); } catch { }
            }

            var execRequest = new ExecutionStartRequest(
                SnapshotName: snapshotName,
                IsSimulation: false,
                MaxFilesToCopy: maxFilesToCopy
            );

            if (!_executor.TryStartExecution(config, snapshot, execRequest, snapshotsFolder))
            {
                throw new InvalidOperationException("Failed to start plan execution.");
            }

            while (true)
            {
                var execStatus = _executor.GetStatus();
                if (!execStatus.IsRunning)
                {
                    if (execStatus.Phase == ExecutionPhase.Cancelled || execStatus.IsCancellationRequested)
                    {
                        throw new OperationCanceledException("Plan execution was cancelled by user.");
                    }
                    if (!string.IsNullOrEmpty(execStatus.Error) && execStatus.Phase == ExecutionPhase.Failed)
                    {
                        throw new InvalidOperationException($"Plan execution failed: {execStatus.Error}");
                    }
                    break;
                }
                await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);
            }

            lock (_lock)
            {
                _isRunning = false;
                _phase = ProcessPipelinePhase.Completed;
                _completedAt = DateTime.UtcNow;
            }
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                _isRunning = false;
                _phase = ProcessPipelinePhase.Cancelled;
                _completedAt = DateTime.UtcNow;
                _error = "Process was cancelled by the user.";
            }

            if (_currentStep < 3)
            {
                string snapshotsFolder = ResolveSnapshotsFolder(config, _environment);
                var snap = _scanner.GetLastSnapshot();
                ProcessSummaryReporter.WriteReport(
                    snapshotsFolder,
                    config,
                    snap,
                    snapshotName,
                    _currentPlan,
                    ExecutionPhase.Cancelled,
                    _startedAt,
                    _completedAt,
                    0, 0, 0, 0,
                    _error,
                    Array.Empty<TransferredFileRecord>(),
                    Array.Empty<TransferErrorItem>(),
                    Array.Empty<CleanedFolderRecord>(),
                    Array.Empty<VolumeExecutionProgress>(),
                    _logger
                );
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _isRunning = false;
                _phase = ProcessPipelinePhase.Failed;
                _completedAt = DateTime.UtcNow;
                _error = ex.Message;
            }

            if (_currentStep < 3)
            {
                string snapshotsFolder = ResolveSnapshotsFolder(config, _environment);
                var snap = _scanner.GetLastSnapshot();
                ProcessSummaryReporter.WriteReport(
                    snapshotsFolder,
                    config,
                    snap,
                    snapshotName,
                    _currentPlan,
                    ExecutionPhase.Failed,
                    _startedAt,
                    _completedAt,
                    0, 0, 0, 0,
                    _error,
                    Array.Empty<TransferredFileRecord>(),
                    Array.Empty<TransferErrorItem>(),
                    Array.Empty<CleanedFolderRecord>(),
                    Array.Empty<VolumeExecutionProgress>(),
                    _logger
                );
            }
        }
    }

    private static string ResolveSnapshotsFolder(AppConfig config, IWebHostEnvironment environment)
    {
        var configuredFolder = config.SnapshotsFolder?.Trim() ?? "snapshots";
        var snapshotsFolder = Path.IsPathRooted(configuredFolder)
            ? configuredFolder
            : Path.Combine(environment.ContentRootPath, configuredFolder);
        return Path.GetFullPath(snapshotsFolder);
    }
}
