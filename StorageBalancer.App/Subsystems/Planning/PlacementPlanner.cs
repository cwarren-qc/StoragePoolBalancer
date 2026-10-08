using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using StorageBalancer.App.Configuration;
using StorageBalancer.App.Domain;

namespace StorageBalancer.App.Subsystems.Planning;

public sealed class PlacementPlanner
{
    public PlacementPlan CreatePlan(PoolSnapshot snapshot, IReadOnlyList<FilePlacementRuleConfig> rules, bool includeFiles = false) =>
        CreatePlan(snapshot, rules, null, null, null, includeFiles);

    public PlacementPlan CreatePlan(
        PoolSnapshot snapshot,
        IReadOnlyList<FilePlacementRuleConfig> rules,
        SpecialRuleConfig? duplicates,
        SpecialRuleConfig? unmatched,
        bool includeFiles = false) =>
        CreatePlan(snapshot, rules, duplicates, null, unmatched, includeFiles);

    public PlacementPlan CreatePlan(
        PoolSnapshot snapshot,
        IReadOnlyList<FilePlacementRuleConfig> rules,
        SpecialRuleConfig? duplicates,
        SpecialRuleConfig? filler,
        SpecialRuleConfig? unmatched,
        bool includeFiles = false)
    {
        rules ??= Array.Empty<FilePlacementRuleConfig>();
        if (snapshot.SchemaVersion != 1)
            throw new InvalidDataException($"Snapshot schema version {snapshot.SchemaVersion} is not supported.");
        if (snapshot.AllocationUnitSize <= 0)
            throw new InvalidDataException("Snapshot allocation unit size must be greater than zero.");

        var warnings = ImmutableArray.CreateBuilder<PlanningWarning>();
        var targets = new List<PlanningTarget>();
        var virtualRoot = new MutablePlanningFolder(string.Empty, string.Empty);

        foreach (var volume in snapshot.Volumes)
        {
            if (!volume.IsComplete)
            {
                warnings.Add(new PlanningWarning($"Excluded incomplete volume {volume.Alias}."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(volume.RootFolderPath) || volume.Folders is null || volume.Folders.Count == 0)
            {
                warnings.Add(new PlanningWarning($"Excluded volume {volume.Alias}: its scan has no root folder data."));
                continue;
            }

            if (volume.Capacity < 0 || volume.OtherItemsSizeOnDisk < 0)
            {
                warnings.Add(new PlanningWarning($"Excluded volume {volume.Alias}: capacity or used-space data is invalid."));
                continue;
            }

            try
            {
                var volumeRoot = BuildVolumeTree(volume);
                ValidateMergeCompatibility(virtualRoot, volumeRoot);
                MergeVolumeTree(virtualRoot, volumeRoot, volume);

                targets.Add(new PlanningTarget(
                    volume.Disk, volume.Alias, volume.MountPoint, volume.RootFolderPath,
                    volume.Capacity, Math.Max(0, volume.Capacity - volume.OtherItemsSizeOnDisk), volume.OtherItemsSizeOnDisk));
            }
            catch (InvalidDataException exception)
            {
                warnings.Add(new PlanningWarning($"Excluded volume {volume.Alias}: {exception.Message}"));
            }
        }

        if (targets.Count == 0)
            warnings.Add(new PlanningWarning("No complete, usable volumes were found in the snapshot."));

        var root = Freeze(virtualRoot, snapshot.AllocationUnitSize, warnings);
        var ctx = new PlanContext(targets);
        var duplicateRecord = new MatchRecord("** Duplicate", -1);
        var unmatchedRecord = new MatchRecord("** Unmatched", -1);

        PopulateUnassigned(root, ctx);

        foreach (var rule in rules)
        {
            if (rule is null || rule.StartingDepth < 1) continue;
            if (!rule.DeferToFiller && (rule.AllowedVolumeAliases is null || rule.AllowedVolumeAliases.Count == 0)) continue;

            string rulePath = NormalizeRulePath(rule.FullRelativePath);
            ApplyRuleRecursive(root, rule, rulePath, null, ctx, duplicateRecord);

            if (rule.StartingDepth > 1)
            {
                var ruleFolder = FindFolder(root, rulePath);
                if (ruleFolder != null)
                {
                    var leftovers = GetUnassignedFilesInSubtree(ruleFolder, ctx).ToList();
                    if (leftovers.Count > 0)
                    {
                        string chunkName = string.IsNullOrEmpty(ruleFolder.RelativePath) ? "<Pool Root>" : ruleFolder.RelativePath;
                        AssignOrSplitChunk(leftovers, chunkName, rule, ctx, duplicateRecord);
                    }
                }
            }
        }

        foreach (var kvp in ctx.Unassigned)
        {
            bool first = true;
            int dupIdx = 1;
            string rootChunk = CleanRootChunkName(null, kvp.Key.RelativePath);
            foreach (var copy in kvp.Value)
            {
                ctx.Deferred.Add(new DeferredCopy(kvp.Key, copy, "None", first ? unmatchedRecord : duplicateRecord, rootChunk, first ? 0 : dupIdx++));
                first = false;
            }
        }
        ctx.Unassigned.Clear();
        ctx.MatchRecords.Add(duplicateRecord);
        ctx.MatchRecords.Add(unmatchedRecord);

        // 1. Separate deferred copies by category
        var fillerDeferred = ctx.Deferred.Where(d => d.CopyIndex == 0 && d.Reason == "Filler Placement").ToList();
        var unmatchedDeferred = ctx.Deferred.Where(d => d.CopyIndex == 0 && d.Reason == "None").ToList();
        var duplicateDeferred = ctx.Deferred.Where(d => d.CopyIndex > 0).ToList();

        var fillerAllowedTargets = (filler?.AllowedVolumeAliases != null && filler.AllowedVolumeAliases.Count > 0 && !filler.AllowedVolumeAliases.Contains("*"))
            ? ctx.Targets.Where(t => filler.AllowedVolumeAliases.Contains(t.Alias, StringComparer.OrdinalIgnoreCase)).ToList()
            : null;

        var duplicateAllowedTargets = (duplicates?.AllowedVolumeAliases != null && duplicates.AllowedVolumeAliases.Count > 0 && !duplicates.AllowedVolumeAliases.Contains("*"))
            ? ctx.Targets.Where(t => duplicates.AllowedVolumeAliases.Contains(t.Alias, StringComparer.OrdinalIgnoreCase)).ToList()
            : null;

        var unmatchedAllowedTargets = (unmatched?.AllowedVolumeAliases != null && unmatched.AllowedVolumeAliases.Count > 0 && !unmatched.AllowedVolumeAliases.Contains("*"))
            ? ctx.Targets.Where(t => unmatched.AllowedVolumeAliases.Contains(t.Alias, StringComparer.OrdinalIgnoreCase)).ToList()
            : null;

        // 1. Process duplicates
        if (duplicates?.Consolidate == true)
        {
            ProcessConsolidatedDuplicates(duplicateDeferred, ctx, duplicateAllowedTargets, duplicateRecord);
        }
        else
        {
            ctx.Deferred = duplicateDeferred;
            ProcessDeferredStayPut(ctx, duplicateAllowedTargets);
            ProcessDeferredEmptiest(ctx, duplicateAllowedTargets);
            if (duplicateRecord.TotalSize > 0)
            {
                duplicateRecord.Order = ctx.DecisionCounter++;
            }
        }

        // 2. Process filler rules (primary copies of paths with Defer to filler)
        ctx.Deferred = fillerDeferred;
        ProcessDeferredStayPut(ctx, fillerAllowedTargets);
        ProcessDeferredEmptiest(ctx, fillerAllowedTargets);

        foreach (var fillerRecord in ctx.MatchRecords.Where(m => m.Order < 0 && m.TotalSize > 0))
        {
            fillerRecord.Order = ctx.DecisionCounter++;
        }

        // 3. Process unmatched catch-all at the very end
        ctx.Deferred = unmatchedDeferred;
        ProcessDeferredStayPut(ctx, unmatchedAllowedTargets);
        ProcessDeferredEmptiest(ctx, unmatchedAllowedTargets);
        if (unmatchedRecord.TotalSize > 0)
        {
            unmatchedRecord.Order = ctx.DecisionCounter++;
        }

        foreach (var kvp in ctx.DeferredDueToSpace.Where(x => x.Value > 0))
        {
            warnings.Add(new PlanningWarning($"Warning: {FormatBytes(kvp.Value)} of '{DisplayPath(kvp.Key)}' was deferred because allowed volumes lacked available space."));
        }

        var volumeSummaries = BuildVolumeSummaries(snapshot, ctx);

        var finalPlacements = ctx.MatchRecords
            .Where(m => m.TotalSize > 0)
            .Select(m => {
                string logic = "Stayed intact";
                if (m.MovedSize > 0)
                {
                    if (m.Targets.Count > 1) logic = "Split";
                    else if (m.Sources.Count == 1 && m.Targets.Count == 1) logic = "Moved";
                    else if (m.Sources.Count > 1 && m.Targets.Count == 1)
                    {
                        var target = m.Targets.Keys.First();
                        var biggestSource = m.Sources.OrderByDescending(kv => kv.Value).FirstOrDefault().Key;
                        logic = string.Equals(target, biggestSource, StringComparison.OrdinalIgnoreCase) ? "Consolidated" : "Moved/Consolidated";
                    }
                }

                var targetsArray = m.Targets.Select(kvp => new VolumeProvenance(kvp.Key, kvp.Value))
                    .OrderByDescending(x => x.Size).ToImmutableArray();

                var movedSourcesArray = m.MovedSources.Select(kvp => new VolumeProvenance(kvp.Key, kvp.Value))
                    .OrderByDescending(x => x.Size).ToImmutableArray();

                return new PlannedPlacement(
                    m.Order,
                    DisplayPath(m.Path),
                    logic,
                    targetsArray,
                    m.TotalSize,
                    m.MovedSize,
                    movedSourcesArray,
                    includeFiles ? m.Files.ToImmutableArray() : null
                );
            })
            .OrderBy(p => p.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        return new PlacementPlan(snapshot.ScannedAt, finalPlacements, warnings.ToImmutable(), volumeSummaries);
    }

    private static void PopulateUnassigned(PlanningFolder folder, PlanContext ctx)
    {
        foreach (var child in folder.Children)
        {
            if (child is PlanningFile file) ctx.Unassigned[file] = file.Copies.ToList();
            else if (child is PlanningFolder childFolder) PopulateUnassigned(childFolder, ctx);
        }
    }

    private static void ApplyRuleRecursive(PlanningFolder folder, FilePlacementRuleConfig rule, string rulePath, MatchRecord? inheritedRecord, PlanContext ctx, MatchRecord duplicateRecord)
    {
        var unassignedFiles = GetUnassignedFilesInSubtree(folder, ctx).ToList();
        if (unassignedFiles.Count == 0) return;

        bool withinRule = IsSameOrDescendant(folder.RelativePath, rulePath);
        bool canReachRule = IsSameOrDescendant(rulePath, folder.RelativePath);

        if (!withinRule && !canReachRule) return;

        int depth = GetRuleDepth(folder.RelativePath);

        if (withinRule && depth >= rule.StartingDepth)
        {
            MatchRecord? currentRecord = inheritedRecord;

            if (rule.DeferToFiller)
            {
                if (currentRecord == null && !string.IsNullOrEmpty(folder.RelativePath))
                {
                    currentRecord = new MatchRecord($"{folder.RelativePath} [filler]", -1);
                    ctx.MatchRecords.Add(currentRecord);
                }
                else if (currentRecord == null && string.IsNullOrEmpty(folder.RelativePath))
                {
                    var loose = folder.Children.OfType<PlanningFile>().Where(f => ctx.Unassigned.ContainsKey(f)).ToList();
                    if (loose.Count > 0)
                    {
                        currentRecord = new MatchRecord("<Pool Root> (Loose files) [filler]", -1);
                        ctx.MatchRecords.Add(currentRecord);
                    }
                }

                if (currentRecord != null)
                {
                    foreach (var file in unassignedFiles) DeferFile(file, currentRecord, duplicateRecord, ctx);
                }
                return;
            }

            if (currentRecord == null && !string.IsNullOrEmpty(folder.RelativePath))
            {
                currentRecord = new MatchRecord(folder.RelativePath, ctx.DecisionCounter++);
                ctx.MatchRecords.Add(currentRecord);
            }

            var allowedTargets = rule.AllowedVolumeAliases.Contains("*")
                ? ctx.Targets.ToList()
                : ctx.Targets.Where(t => rule.AllowedVolumeAliases.Contains(t.Alias, StringComparer.OrdinalIgnoreCase)).ToList();

            if (string.IsNullOrEmpty(folder.RelativePath))
            {
                ProcessSplit(folder, rule, allowedTargets, currentRecord, ctx, duplicateRecord);
                return;
            }

            long logicalSize = unassignedFiles.Sum(f => f.SizeOnDisk);
            var currentAffinity = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in unassignedFiles)
            {
                foreach (var copy in ctx.Unassigned[file])
                    currentAffinity[copy.VolumeAlias] = currentAffinity.GetValueOrDefault(copy.VolumeAlias) + copy.SizeOnDisk;
            }

            var bestTarget = allowedTargets
                .Where(t => t.RemainingSpace >= logicalSize)
                .OrderByDescending(t => currentAffinity.GetValueOrDefault(t.Alias))
                .ThenByDescending(t => t.RemainingSpace)
                .FirstOrDefault();

            if (bestTarget != null)
            {
                foreach (var file in unassignedFiles) AssignPrimaryCopy(file, bestTarget, currentRecord, duplicateRecord, ctx);
                return;
            }

            ProcessSplit(folder, rule, allowedTargets, currentRecord, ctx, duplicateRecord);
            return;
        }

        if (canReachRule || withinRule)
        {
            foreach (var child in folder.Children.OfType<PlanningFolder>())
                ApplyRuleRecursive(child, rule, rulePath, inheritedRecord, ctx, duplicateRecord);
        }
    }

    private static void ProcessSplit(PlanningFolder folder, FilePlacementRuleConfig rule, List<PlanningTarget> allowedTargets, MatchRecord? inheritedRecord, PlanContext ctx, MatchRecord duplicateRecord)
    {
        var looseFiles = folder.Children.OfType<PlanningFile>().Where(f => ctx.Unassigned.ContainsKey(f)).ToList();

        if (looseFiles.Count > 0)
        {
            MatchRecord looseRecord = inheritedRecord!;
            if (looseRecord == null)
            {
                looseRecord = new MatchRecord(string.IsNullOrEmpty(folder.RelativePath) ? "<Pool Root> (Loose files)" : folder.RelativePath + "\\* (Loose files)", ctx.DecisionCounter++);
                ctx.MatchRecords.Add(looseRecord);
            }

            long looseSize = looseFiles.Sum(f => f.SizeOnDisk);
            var currentAffinity = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in looseFiles)
            {
                foreach (var copy in ctx.Unassigned[file])
                    currentAffinity[copy.VolumeAlias] = currentAffinity.GetValueOrDefault(copy.VolumeAlias) + copy.SizeOnDisk;
            }

            var bestTarget = allowedTargets
                .Where(t => t.RemainingSpace >= looseSize)
                .OrderByDescending(t => currentAffinity.GetValueOrDefault(t.Alias))
                .ThenByDescending(t => t.RemainingSpace)
                .FirstOrDefault();

            if (bestTarget != null)
            {
                foreach (var file in looseFiles) AssignPrimaryCopy(file, bestTarget, looseRecord, duplicateRecord, ctx);
            }
            else
            {
                foreach (var file in looseFiles)
                {
                    var fileTarget = allowedTargets
                        .Where(t => t.RemainingSpace >= file.SizeOnDisk)
                        .OrderByDescending(t => ctx.Unassigned[file].Any(c => string.Equals(c.VolumeAlias, t.Alias, StringComparison.OrdinalIgnoreCase)) ? 1 : 0)
                        .ThenByDescending(t => t.RemainingSpace)
                        .FirstOrDefault();

                    if (fileTarget != null) AssignPrimaryCopy(file, fileTarget, looseRecord, duplicateRecord, ctx);
                    else
                    {
                        ctx.DeferredDueToSpace[looseRecord.Path] = ctx.DeferredDueToSpace.GetValueOrDefault(looseRecord.Path) + file.SizeOnDisk;
                        DeferFile(file, looseRecord, duplicateRecord, ctx);
                    }
                }
            }
        }

        foreach (var child in folder.Children.OfType<PlanningFolder>())
            ApplyRuleRecursive(child, rule, NormalizeRulePath(rule.FullRelativePath), inheritedRecord, ctx, duplicateRecord);
    }

    private static void AssignOrSplitChunk(List<PlanningFile> files, string chunkName, FilePlacementRuleConfig rule, PlanContext ctx, MatchRecord duplicateRecord)
    {
        string recordName = rule.DeferToFiller ? $"{chunkName} [filler]" : chunkName;
        int order = rule.DeferToFiller ? -1 : ctx.DecisionCounter++;
        var currentRecord = new MatchRecord(recordName, order);
        ctx.MatchRecords.Add(currentRecord);

        if (rule.DeferToFiller)
        {
            foreach (var file in files) DeferFile(file, currentRecord, duplicateRecord, ctx);
            return;
        }

        var allowedTargets = rule.AllowedVolumeAliases.Contains("*")
            ? ctx.Targets.ToList()
            : ctx.Targets.Where(t => rule.AllowedVolumeAliases.Contains(t.Alias, StringComparer.OrdinalIgnoreCase)).ToList();

        long logicalSize = files.Sum(f => f.SizeOnDisk);
        var currentAffinity = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            foreach (var copy in ctx.Unassigned[file])
                currentAffinity[copy.VolumeAlias] = currentAffinity.GetValueOrDefault(copy.VolumeAlias) + copy.SizeOnDisk;
        }

        var bestTarget = allowedTargets
            .Where(t => t.RemainingSpace >= logicalSize)
            .OrderByDescending(t => currentAffinity.GetValueOrDefault(t.Alias))
            .ThenByDescending(t => t.RemainingSpace)
            .FirstOrDefault();

        if (bestTarget != null)
        {
            foreach (var file in files) AssignPrimaryCopy(file, bestTarget, currentRecord, duplicateRecord, ctx);
        }
        else
        {
            foreach (var file in files)
            {
                var fileTarget = allowedTargets
                    .Where(t => t.RemainingSpace >= file.SizeOnDisk)
                    .OrderByDescending(t => ctx.Unassigned[file].Any(c => string.Equals(c.VolumeAlias, t.Alias, StringComparison.OrdinalIgnoreCase)) ? 1 : 0)
                    .ThenByDescending(t => t.RemainingSpace)
                    .FirstOrDefault();

                if (fileTarget != null) AssignPrimaryCopy(file, fileTarget, currentRecord, duplicateRecord, ctx);
                else
                {
                    ctx.DeferredDueToSpace[currentRecord.Path] = ctx.DeferredDueToSpace.GetValueOrDefault(currentRecord.Path) + file.SizeOnDisk;
                    DeferFile(file, currentRecord, duplicateRecord, ctx);
                }
            }
        }
    }

    private static void AssignPrimaryCopy(PlanningFile file, PlanningTarget target, MatchRecord? record, MatchRecord duplicateRecord, PlanContext ctx)
    {
        var copies = ctx.Unassigned[file];
        ctx.Unassigned.Remove(file);

        var primaryCopy = copies.FirstOrDefault(c => string.Equals(c.VolumeAlias, target.Alias, StringComparison.OrdinalIgnoreCase)) ?? copies.First();
        ctx.CopyDestinations[primaryCopy] = target.Alias;

        if (record != null)
        {
            record.TotalSize += primaryCopy.SizeOnDisk;
            record.Targets[target.Alias] = record.Targets.GetValueOrDefault(target.Alias) + primaryCopy.SizeOnDisk;
            record.Sources[primaryCopy.VolumeAlias] = record.Sources.GetValueOrDefault(primaryCopy.VolumeAlias) + primaryCopy.SizeOnDisk;

            if (!string.Equals(primaryCopy.VolumeAlias, target.Alias, StringComparison.OrdinalIgnoreCase))
            {
                record.MovedSize += primaryCopy.SizeOnDisk;
                record.MovedSources[primaryCopy.VolumeAlias] = record.MovedSources.GetValueOrDefault(primaryCopy.VolumeAlias) + primaryCopy.SizeOnDisk;
            }

            record.Files.Add(new PlannedFileItem(
                file.Name,
                file.RelativePath,
                primaryCopy.VolumeAlias,
                primaryCopy.MountPoint,
                primaryCopy.RootFolderPath,
                primaryCopy.FullPath,
                target.Alias,
                primaryCopy.Size,
                primaryCopy.SizeOnDisk));
        }

        if (!ctx.AssignedVolumes.TryGetValue(file, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ctx.AssignedVolumes[file] = set;
        }
        set.Add(target.Alias);
        target.RemainingSpace -= primaryCopy.SizeOnDisk;

        int dupIdx = 1;
        string rootChunk = CleanRootChunkName(record?.Path, file.RelativePath);
        foreach (var copy in copies)
        {
            if (copy == primaryCopy) continue;
            ctx.Deferred.Add(new DeferredCopy(file, copy, "Deferred Duplicate", duplicateRecord, rootChunk, dupIdx++));
        }
    }

    private static void DeferFile(PlanningFile file, MatchRecord record, MatchRecord duplicateRecord, PlanContext ctx)
    {
        var copies = ctx.Unassigned[file];
        ctx.Unassigned.Remove(file);

        string rootChunk = CleanRootChunkName(record?.Path, file.RelativePath);
        bool isPrimary = true;
        int dupIdx = 1;
        foreach (var copy in copies)
        {
            ctx.Deferred.Add(new DeferredCopy(file, copy, "Filler Placement", isPrimary ? record : duplicateRecord, rootChunk, isPrimary ? 0 : dupIdx++));
            isPrimary = false;
        }
    }

    private static void ProcessDeferredStayPut(PlanContext ctx, List<PlanningTarget>? allowedTargetsOverride = null)
    {
        var remainingDeferred = new List<DeferredCopy>();
        var allowedTargets = allowedTargetsOverride ?? ctx.Targets;

        foreach (var def in ctx.Deferred)
        {
            var target = allowedTargets.FirstOrDefault(t => string.Equals(t.Alias, def.Copy.VolumeAlias, StringComparison.OrdinalIgnoreCase));
            var assigned = ctx.AssignedVolumes.TryGetValue(def.File, out var set) ? set : null;

            if (target != null && target.RemainingSpace >= def.Copy.SizeOnDisk && (assigned == null || !assigned.Contains(target.Alias)))
            {
                ctx.CopyDestinations[def.Copy] = target.Alias;
                target.RemainingSpace -= def.Copy.SizeOnDisk;

                if (assigned == null)
                {
                    assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    ctx.AssignedVolumes[def.File] = assigned;
                }
                assigned.Add(target.Alias);

                if (def.Record != null)
                {
                    def.Record.TotalSize += def.Copy.SizeOnDisk;
                    def.Record.Targets[target.Alias] = def.Record.Targets.GetValueOrDefault(target.Alias) + def.Copy.SizeOnDisk;
                    def.Record.Sources[def.Copy.VolumeAlias] = def.Record.Sources.GetValueOrDefault(def.Copy.VolumeAlias) + def.Copy.SizeOnDisk;

                    def.Record.Files.Add(new PlannedFileItem(
                        def.File.Name,
                        def.File.RelativePath,
                        def.Copy.VolumeAlias,
                        def.Copy.MountPoint,
                        def.Copy.RootFolderPath,
                        def.Copy.FullPath,
                        target.Alias,
                        def.Copy.Size,
                        def.Copy.SizeOnDisk));
                }
            }
            else
            {
                remainingDeferred.Add(def);
            }
        }
        ctx.Deferred = remainingDeferred;
    }

    private static void ProcessDeferredEmptiest(PlanContext ctx, List<PlanningTarget>? allowedTargetsOverride = null)
    {
        var remainingDeferred = new List<DeferredCopy>();
        var allowedTargets = allowedTargetsOverride ?? ctx.Targets;

        foreach (var def in ctx.Deferred)
        {
            var assigned = ctx.AssignedVolumes.TryGetValue(def.File, out var set) ? set : null;
            var target = allowedTargets
                .Where(t => t.RemainingSpace >= def.Copy.SizeOnDisk && (assigned == null || !assigned.Contains(t.Alias)))
                .OrderByDescending(t => t.RemainingSpace)
                .FirstOrDefault();

            if (target == null && allowedTargetsOverride != null)
            {
                // Fallback: if configured targets lack space, ignore restriction and check all pool targets
                target = ctx.Targets
                    .Where(t => t.RemainingSpace >= def.Copy.SizeOnDisk && (assigned == null || !assigned.Contains(t.Alias)))
                    .OrderByDescending(t => t.RemainingSpace)
                    .FirstOrDefault();
            }

            if (target != null)
            {
                ctx.CopyDestinations[def.Copy] = target.Alias;
                target.RemainingSpace -= def.Copy.SizeOnDisk;

                if (assigned == null)
                {
                    assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    ctx.AssignedVolumes[def.File] = assigned;
                }
                assigned.Add(target.Alias);

                if (def.Record != null)
                {
                    def.Record.TotalSize += def.Copy.SizeOnDisk;
                    def.Record.Targets[target.Alias] = def.Record.Targets.GetValueOrDefault(target.Alias) + def.Copy.SizeOnDisk;
                    def.Record.Sources[def.Copy.VolumeAlias] = def.Record.Sources.GetValueOrDefault(def.Copy.VolumeAlias) + def.Copy.SizeOnDisk;

                    if (!string.Equals(def.Copy.VolumeAlias, target.Alias, StringComparison.OrdinalIgnoreCase))
                    {
                        def.Record.MovedSize += def.Copy.SizeOnDisk;
                        def.Record.MovedSources[def.Copy.VolumeAlias] = def.Record.MovedSources.GetValueOrDefault(def.Copy.VolumeAlias) + def.Copy.SizeOnDisk;
                    }

                    def.Record.Files.Add(new PlannedFileItem(
                        def.File.Name,
                        def.File.RelativePath,
                        def.Copy.VolumeAlias,
                        def.Copy.MountPoint,
                        def.Copy.RootFolderPath,
                        def.Copy.FullPath,
                        target.Alias,
                        def.Copy.Size,
                        def.Copy.SizeOnDisk));
                }
            }
            else
            {
                remainingDeferred.Add(def);
            }
        }
        ctx.Deferred = remainingDeferred;

        foreach (var def in ctx.Deferred)
        {
            ctx.CopyDestinations[def.Copy] = def.Copy.VolumeAlias;
            if (def.Record != null)
            {
                ctx.DeferredDueToSpace[def.Record.Path] = ctx.DeferredDueToSpace.GetValueOrDefault(def.Record.Path) + def.Copy.SizeOnDisk;
                def.Record.TotalSize += def.Copy.SizeOnDisk;
                def.Record.Targets[def.Copy.VolumeAlias] = def.Record.Targets.GetValueOrDefault(def.Copy.VolumeAlias) + def.Copy.SizeOnDisk;
                def.Record.Sources[def.Copy.VolumeAlias] = def.Record.Sources.GetValueOrDefault(def.Copy.VolumeAlias) + def.Copy.SizeOnDisk;

                def.Record.Files.Add(new PlannedFileItem(
                    def.File.Name,
                    def.File.RelativePath,
                    def.Copy.VolumeAlias,
                    def.Copy.MountPoint,
                    def.Copy.RootFolderPath,
                    def.Copy.FullPath,
                    def.Copy.VolumeAlias,
                    def.Copy.Size,
                    def.Copy.SizeOnDisk));
            }
        }
    }

    private static void ProcessConsolidatedDuplicates(
        List<DeferredCopy> duplicates,
        PlanContext ctx,
        List<PlanningTarget>? allowedTargetsOverride,
        MatchRecord fallbackDuplicateRecord)
    {
        var duplicateGroups = duplicates
            .GroupBy(d => d.CopyIndex == 1
                ? $"{d.RootChunkName} [duplicate]"
                : $"{d.RootChunkName} [duplicate #{d.CopyIndex}]")
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var allowedTargets = allowedTargetsOverride ?? ctx.Targets;

        foreach (var group in duplicateGroups)
        {
            var groupRecord = new MatchRecord(group.Key, ctx.DecisionCounter++);
            ctx.MatchRecords.Add(groupRecord);

            long groupSize = group.Sum(d => d.Copy.SizeOnDisk);

            // An eligible target volume must not already hold any file in this group
            var eligibleTargets = allowedTargets
                .Where(t => group.All(def => !ctx.AssignedVolumes.TryGetValue(def.File, out var assigned) || !assigned.Contains(t.Alias)))
                .ToList();

            var currentAffinity = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var def in group)
            {
                currentAffinity[def.Copy.VolumeAlias] = currentAffinity.GetValueOrDefault(def.Copy.VolumeAlias) + def.Copy.SizeOnDisk;
            }

            var bestTarget = eligibleTargets
                .Where(t => t.RemainingSpace >= groupSize)
                .OrderByDescending(t => currentAffinity.GetValueOrDefault(t.Alias))
                .ThenByDescending(t => t.RemainingSpace)
                .FirstOrDefault();

            if (bestTarget != null)
            {
                // Consolidate all duplicate files in this group onto bestTarget
                foreach (var def in group)
                {
                    ctx.CopyDestinations[def.Copy] = bestTarget.Alias;
                    bestTarget.RemainingSpace -= def.Copy.SizeOnDisk;

                    if (!ctx.AssignedVolumes.TryGetValue(def.File, out var assigned))
                    {
                        assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        ctx.AssignedVolumes[def.File] = assigned;
                    }
                    assigned.Add(bestTarget.Alias);

                    groupRecord.TotalSize += def.Copy.SizeOnDisk;
                    groupRecord.Targets[bestTarget.Alias] = groupRecord.Targets.GetValueOrDefault(bestTarget.Alias) + def.Copy.SizeOnDisk;
                    groupRecord.Sources[def.Copy.VolumeAlias] = groupRecord.Sources.GetValueOrDefault(def.Copy.VolumeAlias) + def.Copy.SizeOnDisk;

                    if (!string.Equals(def.Copy.VolumeAlias, bestTarget.Alias, StringComparison.OrdinalIgnoreCase))
                    {
                        groupRecord.MovedSize += def.Copy.SizeOnDisk;
                        groupRecord.MovedSources[def.Copy.VolumeAlias] = groupRecord.MovedSources.GetValueOrDefault(def.Copy.VolumeAlias) + def.Copy.SizeOnDisk;
                    }

                    groupRecord.Files.Add(new PlannedFileItem(
                        def.File.Name,
                        def.File.RelativePath,
                        def.Copy.VolumeAlias,
                        def.Copy.MountPoint,
                        def.Copy.RootFolderPath,
                        def.Copy.FullPath,
                        bestTarget.Alias,
                        def.Copy.Size,
                        def.Copy.SizeOnDisk));
                }
            }
            else
            {
                // Group exceeds capacity of single target; distribute individually across eligible targets (split)
                foreach (var def in group)
                {
                    var fileTarget = allowedTargets
                        .Where(t => t.RemainingSpace >= def.Copy.SizeOnDisk &&
                                    (!ctx.AssignedVolumes.TryGetValue(def.File, out var assigned) || !assigned.Contains(t.Alias)))
                        .OrderByDescending(t => string.Equals(t.Alias, def.Copy.VolumeAlias, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                        .ThenByDescending(t => t.RemainingSpace)
                        .FirstOrDefault();

                    // If file cannot fit on allowed targets, ignore eligible volume restriction and find ANY pool volume that fits respecting copy exclusivity
                    if (fileTarget == null && allowedTargetsOverride != null)
                    {
                        fileTarget = ctx.Targets
                            .Where(t => t.RemainingSpace >= def.Copy.SizeOnDisk &&
                                        (!ctx.AssignedVolumes.TryGetValue(def.File, out var assigned) || !assigned.Contains(t.Alias)))
                            .OrderByDescending(t => string.Equals(t.Alias, def.Copy.VolumeAlias, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                            .ThenByDescending(t => t.RemainingSpace)
                            .FirstOrDefault();
                    }

                    var chosenTarget = fileTarget;
                    if (chosenTarget != null)
                    {
                        ctx.CopyDestinations[def.Copy] = chosenTarget.Alias;
                        chosenTarget.RemainingSpace -= def.Copy.SizeOnDisk;

                        if (!ctx.AssignedVolumes.TryGetValue(def.File, out var assigned))
                        {
                            assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            ctx.AssignedVolumes[def.File] = assigned;
                        }
                        assigned.Add(chosenTarget.Alias);

                        groupRecord.TotalSize += def.Copy.SizeOnDisk;
                        groupRecord.Targets[chosenTarget.Alias] = groupRecord.Targets.GetValueOrDefault(chosenTarget.Alias) + def.Copy.SizeOnDisk;
                        groupRecord.Sources[def.Copy.VolumeAlias] = groupRecord.Sources.GetValueOrDefault(def.Copy.VolumeAlias) + def.Copy.SizeOnDisk;

                        if (!string.Equals(def.Copy.VolumeAlias, chosenTarget.Alias, StringComparison.OrdinalIgnoreCase))
                        {
                            groupRecord.MovedSize += def.Copy.SizeOnDisk;
                            groupRecord.MovedSources[def.Copy.VolumeAlias] = groupRecord.MovedSources.GetValueOrDefault(def.Copy.VolumeAlias) + def.Copy.SizeOnDisk;
                        }

                        groupRecord.Files.Add(new PlannedFileItem(
                            def.File.Name,
                            def.File.RelativePath,
                            def.Copy.VolumeAlias,
                            def.Copy.MountPoint,
                            def.Copy.RootFolderPath,
                            def.Copy.FullPath,
                            chosenTarget.Alias,
                            def.Copy.Size,
                            def.Copy.SizeOnDisk));
                    }
                    else
                    {
                        // Fallback: entire pool has no room; leave in place and record space deficit
                        ctx.DeferredDueToSpace[groupRecord.Path] = ctx.DeferredDueToSpace.GetValueOrDefault(groupRecord.Path) + def.Copy.SizeOnDisk;
                        ctx.CopyDestinations[def.Copy] = def.Copy.VolumeAlias;
                        groupRecord.TotalSize += def.Copy.SizeOnDisk;
                        groupRecord.Targets[def.Copy.VolumeAlias] = groupRecord.Targets.GetValueOrDefault(def.Copy.VolumeAlias) + def.Copy.SizeOnDisk;
                        groupRecord.Sources[def.Copy.VolumeAlias] = groupRecord.Sources.GetValueOrDefault(def.Copy.VolumeAlias) + def.Copy.SizeOnDisk;

                        groupRecord.Files.Add(new PlannedFileItem(
                            def.File.Name,
                            def.File.RelativePath,
                            def.Copy.VolumeAlias,
                            def.Copy.MountPoint,
                            def.Copy.RootFolderPath,
                            def.Copy.FullPath,
                            def.Copy.VolumeAlias,
                            def.Copy.Size,
                            def.Copy.SizeOnDisk));
                    }
                }
            }
        }
    }

    private static string CleanRootChunkName(string? path, string fileRelativePath)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (path.EndsWith(" [filler]", StringComparison.OrdinalIgnoreCase))
                path = path[..^" [filler]".Length];
            if (path.StartsWith("<Pool Root>", StringComparison.OrdinalIgnoreCase))
                return "<Pool Root>";
            if (path.EndsWith("\\* (Loose files)", StringComparison.OrdinalIgnoreCase))
                return path[..^"\\* (Loose files)".Length];
            return path;
        }

        if (string.IsNullOrWhiteSpace(fileRelativePath)) return "<Pool Root>";
        var parts = fileRelativePath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[0] : "<Pool Root>";
    }

    private static ImmutableArray<VolumePlanSummary> BuildVolumeSummaries(PoolSnapshot snapshot, PlanContext ctx)
    {
        var targetsByAlias = ctx.Targets.ToDictionary(target => target.Alias, StringComparer.OrdinalIgnoreCase);
        var finalSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var provenanceSizes = new Dictionary<(string Target, string Source), long>();

        foreach (var target in ctx.Targets)
        {
            finalSizes[target.Alias] = target.OtherItemsSizeOnDisk;
            provenanceSizes[(target.Alias, target.Alias)] = target.OtherItemsSizeOnDisk;
        }

        var filesMovedOut = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var filesMovedIn = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in ctx.CopyDestinations)
        {
            var targetAlias = kvp.Value;
            var sourceAlias = kvp.Key.VolumeAlias;
            finalSizes[targetAlias] = AddSaturated(finalSizes.GetValueOrDefault(targetAlias), kvp.Key.SizeOnDisk);

            var provKey = (targetAlias, sourceAlias);
            provenanceSizes[provKey] = AddSaturated(provenanceSizes.GetValueOrDefault(provKey), kvp.Key.SizeOnDisk);

            if (!string.Equals(targetAlias, sourceAlias, StringComparison.OrdinalIgnoreCase))
            {
                filesMovedOut[sourceAlias] = filesMovedOut.GetValueOrDefault(sourceAlias) + 1;
                filesMovedIn[targetAlias] = filesMovedIn.GetValueOrDefault(targetAlias) + 1;
            }
        }

        var summaries = ImmutableArray.CreateBuilder<VolumePlanSummary>();
        foreach (var volume in snapshot.Volumes)
        {
            var isEligible = targetsByAlias.ContainsKey(volume.Alias);

            var provenance = provenanceSizes
                .Where(pair => string.Equals(pair.Key.Target, volume.Alias, StringComparison.OrdinalIgnoreCase) && pair.Value > 0)
                .Select(pair => new VolumeProvenance(pair.Key.Source, pair.Value))
                .OrderByDescending(item => item.Size).ToImmutableArray();

            summaries.Add(new VolumePlanSummary(
                volume.Alias, volume.Disk, volume.MountPoint, volume.Capacity, finalSizes.GetValueOrDefault(volume.Alias),
                isEligible, isEligible ? "Included" : volume.IsComplete ? "Unavailable" : "Incomplete scan", provenance,
                volume.OtherItemsSizeOnDisk,
                filesMovedOut.GetValueOrDefault(volume.Alias),
                filesMovedIn.GetValueOrDefault(volume.Alias)));
        }

        return summaries.ToImmutable();
    }

    private static IEnumerable<PlanningFile> GetUnassignedFilesInSubtree(PlanningFolder folder, PlanContext ctx)
    {
        foreach (var child in folder.Children)
        {
            if (child is PlanningFile file && ctx.Unassigned.ContainsKey(file)) yield return file;
            else if (child is PlanningFolder childFolder)
                foreach (var f in GetUnassignedFilesInSubtree(childFolder, ctx)) yield return f;
        }
    }

    private static PlanningFolder? FindFolder(PlanningFolder root, string path)
    {
        if (string.IsNullOrEmpty(path)) return root;
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        PlanningFolder? current = root;
        foreach (var part in parts)
        {
            current = current.Children.OfType<PlanningFolder>().FirstOrDefault(f => string.Equals(f.Name, part, StringComparison.OrdinalIgnoreCase));
            if (current == null) return null;
        }
        return current;
    }

    private static MutableVolumeFolder BuildVolumeTree(SnapshotVolume volume)
    {
        var root = new MutableVolumeFolder(string.Empty, string.Empty);
        foreach (var entry in volume.Folders)
        {
            var relativePath = NormalizeSnapshotPath(entry.Key);
            var folder = GetOrAddVolumeFolder(root, relativePath);
            if (entry.Value is null) throw new InvalidDataException($"Folder entry '{entry.Key}' has no file list.");

            foreach (var file in entry.Value)
            {
                if (file is null || string.IsNullOrWhiteSpace(file.Name) || file.Name is "." or ".." ||
                    file.Name.Contains('\\') || file.Name.Contains('/') || file.Size < 0 || file.SizeOnDisk < 0)
                {
                    throw new InvalidDataException($"Folder entry '{entry.Key}' contains an invalid file record.");
                }
                folder.Files.Add(new MutableVolumeFile(file.Name, file.Size, file.SizeOnDisk));
            }
        }
        return root;
    }

    private static string NormalizeSnapshotPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == ".") return string.Empty;
        var normalized = path.Replace('/', '\\');
        if (normalized.StartsWith('\\') || Path.IsPathRooted(path))
            throw new InvalidDataException($"Folder path '{path}' is rooted instead of relative.");
        var parts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or ".."))
            throw new InvalidDataException($"Folder path '{path}' contains a traversal segment.");
        return string.Join('\\', parts);
    }

    private static MutableVolumeFolder GetOrAddVolumeFolder(MutableVolumeFolder root, string relativePath)
    {
        var current = root;
        var path = string.Empty;
        foreach (var segment in relativePath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            path = string.IsNullOrEmpty(path) ? segment : $"{path}\\{segment}";
            if (!current.ChildFolders.TryGetValue(segment, out var child))
            {
                child = new MutableVolumeFolder(segment, path);
                current.ChildFolders.Add(segment, child);
            }
            current = child;
        }
        return current;
    }

    private static void MergeVolumeTree(
        MutablePlanningFolder target, MutableVolumeFolder source, SnapshotVolume volume)
    {
        target.Copies.Add(new PlanningFolderCopy(volume.Disk, volume.Alias, volume.MountPoint, volume.RootFolderPath!, source.SizeOnDisk));

        foreach (var file in source.Files)
        {
            if (target.ChildFolders.ContainsKey(file.Name))
                throw new InvalidDataException($"'{file.Name}' is both a file and a folder in the merged snapshot.");

            if (!target.Files.TryGetValue(file.Name, out var mergedFile))
            {
                var relativePath = JoinRelativePath(source.RelativePath, file.Name);
                mergedFile = new MutablePlanningFile(file.Name, relativePath);
                target.Files.Add(file.Name, mergedFile);
            }

            var fileRelativePath = JoinRelativePath(source.RelativePath, file.Name);
            mergedFile.Copies.Add(new PlanningFileCopy(volume.Disk, volume.Alias, volume.MountPoint, volume.RootFolderPath!, fileRelativePath, file.Size, file.SizeOnDisk));
        }

        foreach (var child in source.ChildFolders.Values)
        {
            if (target.Files.ContainsKey(child.Name))
                throw new InvalidDataException($"'{child.Name}' is both a file and a folder in the merged snapshot.");

            if (!target.ChildFolders.TryGetValue(child.Name, out var mergedChild))
            {
                mergedChild = new MutablePlanningFolder(child.Name, child.RelativePath);
                target.ChildFolders.Add(child.Name, mergedChild);
            }

            MergeVolumeTree(mergedChild, child, volume);
        }
    }

    private static void ValidateMergeCompatibility(MutablePlanningFolder target, MutableVolumeFolder source)
    {
        foreach (var file in source.Files)
            if (target.ChildFolders.ContainsKey(file.Name))
                throw new InvalidDataException($"'{file.Name}' is both a file and a folder in the merged snapshot.");

        foreach (var child in source.ChildFolders.Values)
        {
            if (target.Files.ContainsKey(child.Name))
                throw new InvalidDataException($"'{child.Name}' is both a file and a folder in the merged snapshot.");

            if (target.ChildFolders.TryGetValue(child.Name, out var existingChild))
                ValidateMergeCompatibility(existingChild, child);
        }
    }

    private static PlanningFolder Freeze(MutablePlanningFolder folder, int allocationUnitSize, ImmutableArray<PlanningWarning>.Builder warnings)
    {
        var files = folder.Files.Values
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file.Name, StringComparer.Ordinal)
            .Select(file =>
            {
                var copies = file.Copies.ToImmutableArray();
                if (copies.Select(copy => copy.Size).Distinct().Skip(1).Any())
                    warnings.Add(new PlanningWarning($"Copies of '{file.RelativePath}' have different sizes; planning uses the largest copy."));

                return new PlanningFile(file.Name, file.RelativePath, copies.Max(copy => copy.Size), copies.Max(copy => copy.SizeOnDisk), copies);
            })
            .ToImmutableArray();

        var childFolders = folder.ChildFolders.Values
            .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(child => child.Name, StringComparer.Ordinal)
            .Select(child => Freeze(child, allocationUnitSize, warnings))
            .ToArray();

        var children = files.Cast<PlanningNode>()
            .Concat(childFolders)
            .OrderByDescending(child => child.SizeOnDisk)
            .ThenBy(child => child.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        var size = files.Aggregate(0L, (total, file) => AddSaturated(total, file.Size));
        var sizeOnDisk = (long)allocationUnitSize;
        foreach (var file in files) sizeOnDisk = AddSaturated(sizeOnDisk, file.SizeOnDisk);
        foreach (var child in childFolders) sizeOnDisk = AddSaturated(sizeOnDisk, child.SizeOnDisk);

        return new PlanningFolder(folder.Name, folder.RelativePath, size, sizeOnDisk, folder.Copies.ToImmutableArray(), children);
    }

    private static bool IsSameOrDescendant(string path, string parent)
    {
        if (string.IsNullOrEmpty(parent)) return true;
        return string.Equals(path, parent, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetRuleDepth(string path)
    {
        return string.IsNullOrEmpty(path) ? 1 : path.Count(character => character == '\\') + 1;
    }

    private static string NormalizeRulePath(string path)
    {
        return string.Join('\\', path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string JoinRelativePath(string parent, string child)
    {
        return string.IsNullOrEmpty(parent) ? child : $"{parent}\\{child}";
    }

    private static string DisplayPath(string path)
    {
        return string.IsNullOrEmpty(path) ? "<Pool Root>" : path;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes == 0) return "0 B";
        string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
        int index = (int)Math.Min(units.Length - 1, Math.Floor(Math.Log(bytes) / Math.Log(1024)));
        double value = bytes / Math.Pow(1024, index);
        return $"{value.ToString(index > 1 ? "0.0" : "0")} {units[index]}";
    }

    private static long AddSaturated(long left, long right)
    {
        if (right <= 0) return left;
        return left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    private sealed class MutableVolumeFolder(string name, string relativePath)
    {
        public string Name { get; } = name;
        public string RelativePath { get; } = relativePath;
        public List<MutableVolumeFile> Files { get; } = new();
        public Dictionary<string, MutableVolumeFolder> ChildFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public long SizeOnDisk { get; set; }
    }

    private sealed record MutableVolumeFile(string Name, long Size, long SizeOnDisk);

    private sealed class MutablePlanningFolder(string name, string relativePath)
    {
        public string Name { get; } = name;
        public string RelativePath { get; } = relativePath;
        public Dictionary<string, MutablePlanningFile> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, MutablePlanningFolder> ChildFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<PlanningFolderCopy> Copies { get; } = new();
    }

    private sealed class MutablePlanningFile(string name, string relativePath)
    {
        public string Name { get; } = name;
        public string RelativePath { get; } = relativePath;
        public List<PlanningFileCopy> Copies { get; } = new();
    }

    private sealed class PlanContext(List<PlanningTarget> targets)
    {
        public int DecisionCounter { get; set; } = 1;
        public List<PlanningTarget> Targets { get; } = targets;
        public Dictionary<PlanningFile, List<PlanningFileCopy>> Unassigned { get; } = new();
        public List<DeferredCopy> Deferred { get; set; } = new();
        public Dictionary<PlanningFile, HashSet<string>> AssignedVolumes { get; } = new();
        public Dictionary<PlanningFileCopy, string> CopyDestinations { get; } = new();
        public List<MatchRecord> MatchRecords { get; } = new();
        public Dictionary<string, long> DeferredDueToSpace { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class MatchRecord(string path, int order)
    {
        public string Path { get; } = path;
        public int Order { get; set; } = order;
        public bool IsDeferred { get; set; }
        public long TotalSize { get; set; }
        public long MovedSize { get; set; }
        public Dictionary<string, long> Targets { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long> MovedSources { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<PlannedFileItem> Files { get; } = new();
    }

    private sealed class PlanningTarget(
        string diskName, string alias, string mountPoint, string rootFolderPath, long capacity, long remainingSpace, long otherItemsSizeOnDisk)
    {
        public string DiskName { get; } = diskName;
        public string Alias { get; } = alias;
        public string MountPoint { get; } = mountPoint;
        public string RootFolderPath { get; } = rootFolderPath;
        public long Capacity { get; } = capacity;
        public long RemainingSpace { get; set; } = remainingSpace;
        public long OtherItemsSizeOnDisk { get; } = otherItemsSizeOnDisk;
    }

    private sealed record DeferredCopy(PlanningFile File, PlanningFileCopy Copy, string Reason, MatchRecord? Record, string RootChunkName = "", int CopyIndex = 0);
}