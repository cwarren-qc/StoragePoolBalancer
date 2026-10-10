using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using StorageBalancer.App.Configuration;
using StorageBalancer.App.Domain;
using StorageBalancer.App.Subsystems.Logging;

namespace StorageBalancer.App.Subsystems.Execution;

public static class ProcessSummaryReporter
{
    public static string WriteReport(
        string snapshotsFolder,
        AppConfig config,
        PoolSnapshot? snapshot,
        string snapshotName,
        PlacementPlan? plan,
        ExecutionPhase phase,
        DateTime? startedAt,
        DateTime? completedAt,
        long transferredFiles,
        long totalFiles,
        long transferredBytes,
        long totalBytes,
        string? error,
        IReadOnlyList<TransferredFileRecord> completedTransfers,
        IReadOnlyList<TransferErrorItem> transferErrors,
        IReadOnlyList<CleanedFolderRecord> cleanedFolders,
        IReadOnlyCollection<VolumeExecutionProgress> volumeProgresses,
        IAppEventLogger? logger = null)
    {
        try
        {
            if (!Directory.Exists(snapshotsFolder))
            {
                Directory.CreateDirectory(snapshotsFolder);
            }

            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            string reportFileName = $"balancing-summary-{timestamp}.txt";
            string reportFilePath = Path.Combine(snapshotsFolder, reportFileName);

            var sb = new StringBuilder();

            DateTime now = DateTime.UtcNow;
            DateTime start = startedAt ?? now;
            DateTime end = completedAt ?? now;
            TimeSpan execDuration = end >= start ? end - start : TimeSpan.Zero;

            string statusText = phase switch
            {
                ExecutionPhase.Completed => "COMPLETED SUCCESSFULLY",
                ExecutionPhase.Cancelled => "CANCELLED BY USER",
                ExecutionPhase.Failed => $"FAILED: {error ?? "Execution stopped with error"}",
                _ => phase.ToString().ToUpperInvariant()
            };

            // ========================================================================
            // HEADER
            // ========================================================================
            sb.AppendLine("================================================================================");
            sb.AppendLine("                    STORAGE POOL BALANCER - PROCESS SUMMARY");
            sb.AppendLine("================================================================================");
            sb.AppendLine($"Generated:           {now:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"Process Status:      {statusText}");
            sb.AppendLine($"Execution Duration:  {FormatDuration(execDuration)}");
            sb.AppendLine($"Snapshot Target:     {snapshotName}");
            if (!string.IsNullOrWhiteSpace(error) && phase != ExecutionPhase.Cancelled)
            {
                sb.AppendLine($"Error Message:       {error}");
            }
            sb.AppendLine();

            // ========================================================================
            // STAGE 1: STATE SCAN
            // ========================================================================
            sb.AppendLine("================================================================================");
            sb.AppendLine("STAGE 1: STATE SCAN");
            sb.AppendLine("================================================================================");
            if (snapshot != null)
            {
                long totalScanFiles = snapshot.Volumes.Sum(v => (long)v.Folders.Values.Sum(f => f.Count));
                long totalScanFolders = snapshot.Volumes.Sum(v => (long)v.Folders.Count);
                long totalScanBytes = snapshot.Volumes.Sum(v => v.Folders.Values.Sum(f => f.Sum(file => file.SizeOnDisk)));

                sb.AppendLine($"Snapshot Name:       {snapshotName}");
                sb.AppendLine($"Scanned At:          {snapshot.ScannedAt:yyyy-MM-dd HH:mm:ss} UTC");
                sb.AppendLine($"Total Volumes:       {snapshot.Volumes.Count}");
                sb.AppendLine($"Total Files:         {totalScanFiles:N0}");
                sb.AppendLine($"Total Folders:       {totalScanFolders:N0}");
                sb.AppendLine($"Total Scanned Data:  {FormatBytes(totalScanBytes)} ({totalScanBytes:N0} bytes)");
                sb.AppendLine();

                sb.AppendLine("Volume Scan Breakdown:");
                foreach (var vol in snapshot.Volumes.OrderBy(v => v.Alias, StringComparer.OrdinalIgnoreCase))
                {
                    string disk = !string.IsNullOrEmpty(vol.Disk) ? $" (Disk: {vol.Disk})" : string.Empty;
                    long volUsed = vol.Folders.Values.Sum(f => f.Sum(file => file.SizeOnDisk));
                    int volFiles = vol.Folders.Values.Sum(f => f.Count);
                    int volFolders = vol.Folders.Count;

                    sb.AppendLine($"  - Volume {vol.Alias}{disk}:");
                    sb.AppendLine($"      Capacity: {FormatBytes(vol.Capacity)} | Used: {FormatBytes(volUsed)} | Free: {FormatBytes(Math.Max(0, vol.Capacity - volUsed))}");
                    sb.AppendLine($"      Files: {volFiles:N0} | Folders: {volFolders:N0} | Root: {vol.RootFolderPath ?? vol.MountPoint}");
                }
                sb.AppendLine();

                var allIssues = snapshot.Volumes.SelectMany(v => v.Issues.Select(iss => (Volume: v.Alias, Issue: iss))).ToList();

                sb.AppendLine(">>> START LIST: SCAN ISSUES <<<");
                if (allIssues.Count > 0)
                {
                    foreach (var (volume, issue) in allIssues)
                    {
                        sb.AppendLine($"  - Volume {volume} ({issue.Path}): {issue.Message}");
                    }
                }
                else
                {
                    sb.AppendLine("  (No scan issues or warnings recorded)");
                }
                sb.AppendLine("<<< END LIST: SCAN ISSUES >>>");
            }
            else
            {
                sb.AppendLine("Scan details: Snapshot object was not supplied or was run in legacy mode.");
            }
            sb.AppendLine();

            // ========================================================================
            // STAGE 2: BALANCING & PLACEMENT PLANNING
            // ========================================================================
            sb.AppendLine("================================================================================");
            sb.AppendLine("STAGE 2: PLACEMENT BALANCING (PLAN)");
            sb.AppendLine("================================================================================");
            if (plan != null)
            {
                long totalMoveBytes = plan.Placements.Sum(p => p.SizeMoved);
                long totalFilesMoved = plan.Volumes.Sum(v => v.FilesMovedIn);
                long totalPlanBytes = plan.Placements.Sum(p => p.SizeOnDisk);
                int totalPlanFiles = plan.Placements.Sum(p => p.Files?.Length ?? 0);

                sb.AppendLine($"Snapshot Scanned At: {plan.SnapshotScannedAt:yyyy-MM-dd HH:mm:ss} UTC");
                sb.AppendLine($"Placement Rules:     {config.FilePlacementRules?.Count ?? 0} configured rules");
                sb.AppendLine($"Duplicates Policy:   Consolidate = {config.Duplicates?.Consolidate ?? false}");
                sb.AppendLine($"Filler Mode:         {(config.Filler != null ? "Enabled" : "Disabled")}");
                sb.AppendLine();

                sb.AppendLine("Balancing Overview:");
                sb.AppendLine($"  - Total Placements:   {plan.Placements.Length:N0}");
                sb.AppendLine($"  - Total Files:        {totalPlanFiles:N0}");
                sb.AppendLine($"  - Total Plan Data:    {FormatBytes(totalPlanBytes)} ({totalPlanBytes:N0} bytes)");
                sb.AppendLine($"  - Data Moving:        {FormatBytes(totalMoveBytes)} ({totalMoveBytes:N0} bytes)");
                sb.AppendLine($"  - Files Moving:       {totalFilesMoved:N0}");
                sb.AppendLine();

                if (!plan.Volumes.IsEmpty)
                {
                    var movesOut = new Dictionary<string, Dictionary<string, (long Files, long Bytes)>>(StringComparer.OrdinalIgnoreCase);
                    var movesIn = new Dictionary<string, Dictionary<string, (long Files, long Bytes)>>(StringComparer.OrdinalIgnoreCase);

                    if (!plan.Placements.IsEmpty)
                    {
                        foreach (var p in plan.Placements)
                        {
                            if (p.Files != null)
                            {
                                foreach (var f in p.Files)
                                {
                                    if (!string.Equals(f.OriginalVolumeAlias, f.DestinationVolumeAlias, StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (!movesOut.TryGetValue(f.OriginalVolumeAlias, out var outMap))
                                        {
                                            outMap = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
                                            movesOut[f.OriginalVolumeAlias] = outMap;
                                        }
                                        var prevOut = outMap.GetValueOrDefault(f.DestinationVolumeAlias);
                                        outMap[f.DestinationVolumeAlias] = (prevOut.Item1 + 1, prevOut.Item2 + f.SizeOnDisk);

                                        if (!movesIn.TryGetValue(f.DestinationVolumeAlias, out var inMap))
                                        {
                                            inMap = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
                                            movesIn[f.DestinationVolumeAlias] = inMap;
                                        }
                                        var prevIn = inMap.GetValueOrDefault(f.OriginalVolumeAlias);
                                        inMap[f.OriginalVolumeAlias] = (prevIn.Item1 + 1, prevIn.Item2 + f.SizeOnDisk);
                                    }
                                }
                            }
                        }
                    }

                    sb.AppendLine("Volume Storage Projections:");
                    foreach (var v in plan.Volumes.OrderBy(v => v.Alias, StringComparer.OrdinalIgnoreCase))
                    {
                        long stayedBytes = v.Provenance.FirstOrDefault(pv => string.Equals(pv.Alias, v.Alias, StringComparison.OrdinalIgnoreCase))?.Size
                            ?? Math.Max(0, v.FinalSize - (movesIn.TryGetValue(v.Alias, out var im) ? im.Values.Sum(x => x.Bytes) : 0));

                        var outTargets = movesOut.TryGetValue(v.Alias, out var outMap)
                            ? outMap.OrderByDescending(kv => kv.Value.Bytes).ThenBy(kv => kv.Key).Select(kv => $"{kv.Key}: {kv.Value.Files:N0} / {FormatBytes(kv.Value.Bytes)}").ToList()
                            : new List<string>();

                        var inSources = movesIn.TryGetValue(v.Alias, out var inMap)
                            ? inMap.OrderByDescending(kv => kv.Value.Bytes).ThenBy(kv => kv.Key).Select(kv => $"{kv.Key}: {kv.Value.Files:N0} / {FormatBytes(kv.Value.Bytes)}").ToList()
                            : new List<string>();

                        // Fallback if placement.Files was not populated
                        if (outTargets.Count == 0 && inSources.Count == 0 && v.Provenance.Length > 0)
                        {
                            var fallbackIn = v.Provenance.Where(pv => !string.Equals(pv.Alias, v.Alias, StringComparison.OrdinalIgnoreCase))
                                .Select(pv => $"{pv.Alias}: {FormatBytes(pv.Size)}").ToList();
                            if (fallbackIn.Count > 0) inSources.AddRange(fallbackIn);

                            foreach (var other in plan.Volumes)
                            {
                                if (string.Equals(other.Alias, v.Alias, StringComparison.OrdinalIgnoreCase)) continue;
                                var outProv = other.Provenance.FirstOrDefault(pv => string.Equals(pv.Alias, v.Alias, StringComparison.OrdinalIgnoreCase));
                                if (outProv != null && outProv.Size > 0)
                                {
                                    outTargets.Add($"{other.Alias}: {FormatBytes(outProv.Size)}");
                                }
                            }
                        }

                        string outStr = outTargets.Count > 0 ? $"[{string.Join(", ", outTargets)}]" : "None";
                        string inStr = inSources.Count > 0 ? $"[{string.Join(", ", inSources)}]" : "None";

                        sb.AppendLine($"  - Volume {v.Alias}: Final Size {FormatBytes(v.FinalSize)} / Capacity {FormatBytes(v.Capacity)} (Status: {v.Status})");
                        sb.AppendLine($"       Staying in Place: {FormatBytes(stayedBytes)}");
                        sb.AppendLine($"       Moving Out To:    {outStr}");
                        sb.AppendLine($"       Moving In From:   {inStr}");
                    }
                    sb.AppendLine();
                }

                if (plan.FolderCleanup != null)
                {
                    sb.AppendLine("Folder Cleanup Plan:");
                    sb.AppendLine($"  - Preserving Unique:           {plan.FolderCleanup.PreservedUniqueCount:N0}");
                    sb.AppendLine($"  - Keeping with Data:           {plan.FolderCleanup.KeptWithDataCount:N0}");
                    sb.AppendLine($"  - Scheduled for Cleaning:      {plan.FolderCleanup.CleanedCount:N0}");
                    sb.AppendLine();
                }

                sb.AppendLine(">>> START LIST: PLANNED PATH PLACEMENTS <<<");
                if (!plan.Placements.IsEmpty)
                {
                    foreach (var p in plan.Placements)
                    {
                        string targets = string.Join(", ", p.Targets.Select(t => $"{t.Alias}: {FormatBytes(t.Size)}"));
                        string sources = string.Join(", ", p.Sources.Select(s => $"{s.Alias}: {FormatBytes(s.Size)}"));
                        int fileCount = p.Files?.Length ?? 0;
                        sb.AppendLine($"  - [{p.LogicApplied}] \"{p.RelativePath}\"");
                        sb.AppendLine($"      Targets: [{targets}] | Sources: [{sources}] | Files: {fileCount:N0} | Size: {FormatBytes(p.SizeOnDisk)} | Moving: {FormatBytes(p.SizeMoved)}");
                    }
                }
                else
                {
                    sb.AppendLine("  (No placement actions planned)");
                }
                sb.AppendLine("<<< END LIST: PLANNED PATH PLACEMENTS >>>");
                sb.AppendLine();

                sb.AppendLine(">>> START LIST: FOLDER CLEANUP ACTIONS PLANNED <<<");
                if (plan.FolderCleanup?.Actions != null && plan.FolderCleanup.Actions.Count > 0)
                {
                    foreach (var act in plan.FolderCleanup.Actions)
                    {
                        sb.AppendLine($"  - [{act.Status}] Volume: {act.VolumeAlias} | Path: \"{act.RelativePath}\" | Primary: {act.PrimaryVolumeAlias} | Reason: {act.Reason}");
                    }
                }
                else
                {
                    sb.AppendLine("  (No folder cleanup actions planned)");
                }
                sb.AppendLine("<<< END LIST: FOLDER CLEANUP ACTIONS PLANNED >>>");
            }
            else
            {
                sb.AppendLine("Plan details: Placement plan was not generated or not available.");
            }
            sb.AppendLine();

            // ========================================================================
            // STAGE 3: PLAN EXECUTION
            // ========================================================================
            sb.AppendLine("================================================================================");
            sb.AppendLine("STAGE 3: PLAN EXECUTION");
            sb.AppendLine("================================================================================");
            sb.AppendLine($"Execution Result:    {statusText}");
            sb.AppendLine($"Started At:          {start:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"Completed At:        {end:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"Duration:            {FormatDuration(execDuration)}");
            sb.AppendLine();

            double filesPct = totalFiles > 0 ? ((double)transferredFiles / totalFiles) * 100.0 : 0.0;
            double bytesPct = totalBytes > 0 ? ((double)transferredBytes / totalBytes) * 100.0 : 0.0;
            double avgBps = execDuration.TotalSeconds > 0 ? transferredBytes / execDuration.TotalSeconds : 0.0;

            sb.AppendLine("Transfer Performance & Metrics:");
            sb.AppendLine($"  - Files Transferred:   {transferredFiles:N0} / {totalFiles:N0} planned ({filesPct:F1}%)");
            sb.AppendLine($"  - Data Transferred:    {FormatBytes(transferredBytes)} / {FormatBytes(totalBytes)} planned ({bytesPct:F1}%)");
            sb.AppendLine($"  - Average Speed:       {(avgBps / (1024 * 1024)):F1} MB/s");
            sb.AppendLine($"  - Transfer Errors:     {transferErrors.Count:N0}");
            sb.AppendLine($"  - Folders Cleaned:     {cleanedFolders.Count:N0}");
            sb.AppendLine();

            if (volumeProgresses != null && volumeProgresses.Count > 0)
            {
                sb.AppendLine("Volume Execution States:");
                foreach (var vp in volumeProgresses.OrderBy(v => v.Alias, StringComparer.OrdinalIgnoreCase))
                {
                    long remOut = Math.Max(0, vp.MovedOutTotalBytes - vp.MovedOutBytes);
                    long remIn = Math.Max(0, vp.MovedInTotalBytes - vp.MovedInBytes);
                    sb.AppendLine($"  - Volume {vp.Alias}: Status: {vp.Status} | Activity: {vp.CurrentActivity}");
                    sb.AppendLine($"      Moved Out: {FormatBytes(vp.MovedOutBytes)} / {FormatBytes(vp.MovedOutTotalBytes)} ({vp.MovedOutFiles:N0}/{vp.MovedOutTotalFiles:N0} files) | Remaining Out: {FormatBytes(remOut)}");
                    sb.AppendLine($"      Moved In:  {FormatBytes(vp.MovedInBytes)} / {FormatBytes(vp.MovedInTotalBytes)} ({vp.MovedInFiles:N0}/{vp.MovedInTotalFiles:N0} files) | Remaining In:  {FormatBytes(remIn)}");
                }
                sb.AppendLine();
            }

            // LIST: TRANSFERRED FILES
            sb.AppendLine(">>> START LIST: TRANSFERRED FILES <<<");
            if (completedTransfers != null && completedTransfers.Count > 0)
            {
                foreach (var rec in completedTransfers)
                {
                    sb.AppendLine($"  - [{rec.TimestampUtc:HH:mm:ss.fff UTC}] {rec.SourceVolume} -> {rec.TargetVolume} | {FormatBytes(rec.SizeOnDisk),9} | \"{rec.RelativePath}\" | Duration: {rec.Duration.TotalSeconds:F1}s");
                }
            }
            else
            {
                sb.AppendLine("  (No files were transferred)");
            }
            sb.AppendLine("<<< END LIST: TRANSFERRED FILES >>>");
            sb.AppendLine();

            // LIST: TRANSFER ERRORS
            sb.AppendLine(">>> START LIST: TRANSFER ERRORS AND SKIPPED FILES <<<");
            if (transferErrors != null && transferErrors.Count > 0)
            {
                foreach (var errItem in transferErrors)
                {
                    sb.AppendLine($"  - [{errItem.TimestampUtc:HH:mm:ss.fff UTC}] {errItem.SourceVolume} -> {errItem.TargetVolume} | {FormatBytes(errItem.SizeOnDisk),9} | \"{errItem.RelativePath}\"");
                    sb.AppendLine($"      Reason: {errItem.ErrorMessage}");
                }
            }
            else
            {
                sb.AppendLine("  (Zero transfer errors or skipped files)");
            }
            sb.AppendLine("<<< END LIST: TRANSFER ERRORS AND SKIPPED FILES >>>");
            sb.AppendLine();

            // LIST: CLEANED FOLDERS
            sb.AppendLine(">>> START LIST: EXECUTED FOLDER CLEANUPS <<<");
            if (cleanedFolders != null && cleanedFolders.Count > 0)
            {
                foreach (var clean in cleanedFolders)
                {
                    sb.AppendLine($"  - [{clean.TimestampUtc:HH:mm:ss.fff UTC}] Volume: {clean.VolumeAlias} | \"{clean.RelativePath}\" | Primary: {clean.PrimaryVolumeAlias}");
                }
            }
            else
            {
                sb.AppendLine("  (No folders were deleted during this execution)");
            }
            sb.AppendLine("<<< END LIST: EXECUTED FOLDER CLEANUPS >>>");
            sb.AppendLine();

            sb.AppendLine("================================================================================");
            sb.AppendLine("                              END OF SUMMARY");
            sb.AppendLine("================================================================================");

            File.WriteAllText(reportFilePath, sb.ToString(), Encoding.UTF8);

            logger?.LogInfo("Plan Execution", $"Detailed process summary report generated: '{reportFilePath}'");
            return reportFilePath;
        }
        catch (Exception ex)
        {
            logger?.LogError("Plan Execution", $"Failed to generate process summary report: {ex.Message}", ex);
            return string.Empty;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        int i = Math.Min((int)Math.Floor(Math.Log(bytes) / Math.Log(1024)), units.Length - 1);
        return $"{bytes / Math.Pow(1024, i):F1} {units[i]}";
    }

    private static string FormatDuration(TimeSpan span)
    {
        return $"{(int)span.TotalHours:D2}:{span.Minutes:D2}:{span.Seconds:D2}";
    }
}
