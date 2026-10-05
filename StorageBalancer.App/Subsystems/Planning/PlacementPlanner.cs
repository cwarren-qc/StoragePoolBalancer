using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using StorageBalancer.App.Configuration;
using StorageBalancer.App.Domain;

namespace StorageBalancer.App.Subsystems.Planning;

public sealed class PlacementPlanner
{
    public PlacementPlan CreatePlan(
        PoolSnapshot snapshot,
        IReadOnlyList<FilePlacementRuleConfig> rules,
        IReadOnlyList<string>? excludedPathPatterns = null)
    {
        rules ??= Array.Empty<FilePlacementRuleConfig>();
        var exclusionPatterns = CompileExclusionPatterns(excludedPathPatterns);
        if (snapshot.SchemaVersion != 1)
            throw new InvalidDataException($"Snapshot schema version {snapshot.SchemaVersion} is not supported.");
        if (snapshot.AllocationUnitSize <= 0)
            throw new InvalidDataException("Snapshot allocation unit size must be greater than zero.");

        var warnings = ImmutableArray.CreateBuilder<PlanningWarning>();
        var unplacedItems = new Dictionary<string, UnplacedItem>(StringComparer.OrdinalIgnoreCase);
        var targets = new List<PlanningTarget>();
        var virtualRoot = new MutablePlanningFolder(string.Empty, string.Empty);
        var assignedDescendantPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var diskIndex = 0; diskIndex < snapshot.Disks.Count; diskIndex++)
        {
            var disk = snapshot.Disks[diskIndex];
            for (var volumeIndex = 0; volumeIndex < disk.Volumes.Count; volumeIndex++)
            {
                var volume = disk.Volumes[volumeIndex];
                var alias = string.IsNullOrWhiteSpace(volume.Alias) ? $"D{diskIndex + 1}-V{volumeIndex + 1}" : volume.Alias;
                if (!volume.IsComplete)
                {
                    warnings.Add(new PlanningWarning($"Excluded incomplete volume {alias}."));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(volume.RootFolderPath) || volume.Folders is null || volume.Folders.Count == 0)
                {
                    warnings.Add(new PlanningWarning($"Excluded volume {alias}: its scan has no root folder data."));
                    continue;
                }

                if (volume.Capacity < 0 || volume.OtherItemsSizeOnDisk < 0)
                {
                    warnings.Add(new PlanningWarning($"Excluded volume {alias}: capacity or used-space data is invalid."));
                    continue;
                }

                try
                {
                    var volumeRoot = BuildVolumeTree(disk, volume);
                    var volumeUsed = AddSaturated(volume.OtherItemsSizeOnDisk, CalculateVolumeFolderSize(volumeRoot, snapshot.AllocationUnitSize));
                    ValidateMergeCompatibility(virtualRoot, volumeRoot);
                    MergeVolumeTree(virtualRoot, volumeRoot, disk, volume, alias);
                    targets.Add(new PlanningTarget(
                        disk.Id,
                        disk.HardwareName,
                        volume.Id,
                        alias,
                        volume.MountPoint,
                        volume.RootFolderPath,
                        volume.Capacity,
                        Math.Max(0, volume.Capacity - Math.Min(volume.Capacity, volumeUsed))));
                }
                catch (InvalidDataException exception)
                {
                    warnings.Add(new PlanningWarning($"Excluded volume {alias}: {exception.Message}"));
                }
            }
        }

        if (targets.Count == 0)
            warnings.Add(new PlanningWarning("No complete, usable volumes were found in the snapshot."));

        var root = Freeze(virtualRoot, snapshot.AllocationUnitSize, warnings);
        var excludedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var foldersWithExcludedDescendants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excludedItemCount = CollectExcludedPaths(root, exclusionPatterns, excludedPaths, foldersWithExcludedDescendants);
        if (excludedItemCount > 0)
            warnings.Add(new PlanningWarning($"{excludedItemCount:N0} files and folders matched never-move patterns and were left in place."));

        var folderAssignments = new Dictionary<string, PlacementAssignment>(StringComparer.OrdinalIgnoreCase);
        var fileAssignments = new Dictionary<string, PlacementAssignment>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules)
        {
            if (rule is null || rule.StartingDepth < 1 || rule.AllowedVolumeIds is null || rule.AllowedVolumeIds.Count == 0)
            {
                warnings.Add(new PlanningWarning("Skipped an invalid placement rule."));
                continue;
            }

            ApplyRule(
                root,
                rule,
                targets,
                folderAssignments,
                fileAssignments,
                assignedDescendantPaths,
                excludedPaths,
                foldersWithExcludedDescendants,
                unplacedItems);
        }

        if (rules.Count == 0)
            warnings.Add(new PlanningWarning("No placement rules are configured."));

        var placements = folderAssignments
            .Select(pair => CreatePlacement(pair.Key, "Folder", pair.Value))
            .Concat(fileAssignments.Select(pair => CreatePlacement(pair.Key, "File", pair.Value)))
            .OrderBy(placement => placement.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(placement => placement.ItemType, StringComparer.Ordinal)
            .ToImmutableArray();

        var moves = ImmutableArray.CreateBuilder<PlannedMove>();
        BuildMoves(root, null, folderAssignments, fileAssignments, moves);
        var plannedMoves = moves.OrderBy(move => move.RelativePath, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        var volumeSummaries = BuildVolumeSummaries(
            snapshot,
            targets,
            root,
            folderAssignments,
            fileAssignments,
            plannedMoves);

        return new PlacementPlan(
            snapshot.ScannedAt,
            placements,
            plannedMoves,
            unplacedItems.Values.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToImmutableArray(),
            warnings.ToImmutable(),
            volumeSummaries);
    }

    private static ImmutableArray<VolumePlanSummary> BuildVolumeSummaries(
        PoolSnapshot snapshot,
        List<PlanningTarget> targets,
        PlanningFolder root,
        Dictionary<string, PlacementAssignment> folderAssignments,
        Dictionary<string, PlacementAssignment> fileAssignments,
        ImmutableArray<PlannedMove> moves)
    {
        var targetsByVolume = targets.ToDictionary(target => target.VolumeId, StringComparer.OrdinalIgnoreCase);
        var finalSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var provenanceSizes = new Dictionary<(string Target, string Source), long>();
        CollectExistingCopies(root, finalSizes, provenanceSizes);
        foreach (var move in moves)
        {
            finalSizes[move.SourceVolumeId] = Math.Max(0, finalSizes.GetValueOrDefault(move.SourceVolumeId) - move.Size);
            var sourceKey = (move.SourceVolumeId, move.SourceVolumeId);
            provenanceSizes[sourceKey] = Math.Max(0, provenanceSizes.GetValueOrDefault(sourceKey) - move.Size);
            finalSizes[move.TargetVolumeId] = AddSaturated(finalSizes.GetValueOrDefault(move.TargetVolumeId), move.Size);
            var targetKey = (move.TargetVolumeId, move.SourceVolumeId);
            provenanceSizes[targetKey] = AddSaturated(provenanceSizes.GetValueOrDefault(targetKey), move.Size);
        }

        var transfers = moves
            .GroupBy(move => new
            {
                move.SourceVolumeId,
                move.TargetVolumeId,
                GroupPath = GetTransferGroupPath(move.PlacementPath, move.RelativePath)
            })
            .Select(group => new AggregatedTransfer(
                group.Key.SourceVolumeId,
                group.Key.TargetVolumeId,
                group.Key.GroupPath,
                group.First().SourceAlias,
                group.First().TargetAlias,
                group.Count(),
                CountMovedFolders(group, group.Key.GroupPath),
                group.Aggregate(0L, (total, move) => AddSaturated(total, move.Size))))
            .ToArray();

        var summaries = ImmutableArray.CreateBuilder<VolumePlanSummary>();
        for (var diskIndex = 0; diskIndex < snapshot.Disks.Count; diskIndex++)
        {
            var disk = snapshot.Disks[diskIndex];
            for (var volumeIndex = 0; volumeIndex < disk.Volumes.Count; volumeIndex++)
            {
                var volume = disk.Volumes[volumeIndex];
                var alias = string.IsNullOrWhiteSpace(volume.Alias) ? $"D{diskIndex + 1}-V{volumeIndex + 1}" : volume.Alias;
                var isEligible = targetsByVolume.ContainsKey(volume.Id);
                var provenance = provenanceSizes
                    .Where(pair => string.Equals(pair.Key.Target, volume.Id, StringComparison.OrdinalIgnoreCase) && pair.Value > 0)
                    .Select(pair => new VolumeProvenance(
                        pair.Key.Source,
                        FindVolumeAlias(snapshot, pair.Key.Source),
                        pair.Value))
                    .OrderByDescending(item => item.Size)
                    .ToImmutableArray();
                var incoming = transfers
                    .Where(transfer => string.Equals(transfer.TargetVolumeId, volume.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(transfer => new VolumeTransferGroup(
                        DisplayPath(transfer.RelativePath),
                        transfer.SourceVolumeId,
                        transfer.SourceAlias,
                        transfer.FolderCount,
                        transfer.FileCount,
                        transfer.Size))
                    .OrderByDescending(item => item.Size)
                    .ToImmutableArray();
                var outgoing = transfers
                    .Where(transfer => string.Equals(transfer.SourceVolumeId, volume.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(transfer => new VolumeTransferGroup(
                        DisplayPath(transfer.RelativePath),
                        transfer.TargetVolumeId,
                        transfer.TargetAlias,
                        transfer.FolderCount,
                        transfer.FileCount,
                        transfer.Size))
                    .OrderByDescending(item => item.Size)
                    .ToImmutableArray();

                summaries.Add(new VolumePlanSummary(
                    volume.Id,
                    alias,
                    disk.HardwareName,
                    volume.MountPoint,
                    volume.Capacity,
                    finalSizes.GetValueOrDefault(volume.Id),
                    isEligible,
                    isEligible ? "Included" : volume.IsComplete ? "Unavailable" : "Incomplete scan",
                    provenance,
                    incoming,
                    outgoing));
            }
        }

        return summaries.ToImmutable();
    }

    private static void CollectExistingCopies(
        PlanningFolder folder,
        Dictionary<string, long> finalSizes,
        Dictionary<(string Target, string Source), long> provenanceSizes)
    {
        foreach (var child in folder.Children)
        {
            if (child is PlanningFolder childFolder)
            {
                CollectExistingCopies(childFolder, finalSizes, provenanceSizes);
                continue;
            }

            if (child is not PlanningFile file)
                continue;

            foreach (var copy in file.Copies)
            {
                finalSizes[copy.VolumeId] = AddSaturated(finalSizes.GetValueOrDefault(copy.VolumeId), copy.Size);
                var provenanceKey = (copy.VolumeId, copy.VolumeId);
                provenanceSizes[provenanceKey] = AddSaturated(provenanceSizes.GetValueOrDefault(provenanceKey), copy.Size);
            }
        }
    }

    private static string GetTransferGroupPath(string placementPath, string filePath)
    {
        if (!string.Equals(placementPath, filePath, StringComparison.OrdinalIgnoreCase))
            return placementPath;

        var separatorIndex = filePath.LastIndexOf('\\');
        return separatorIndex < 0 ? string.Empty : filePath[..separatorIndex];
    }

    private static int CountMovedFolders(IEnumerable<PlannedMove> moves, string groupPath)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var move in moves)
        {
            var separatorIndex = move.RelativePath.LastIndexOf('\\');
            var folderPath = separatorIndex < 0 ? string.Empty : move.RelativePath[..separatorIndex];
            while (!string.IsNullOrEmpty(folderPath))
            {
                if (IsSameOrDescendant(folderPath, groupPath))
                    folders.Add(folderPath);
                if (string.Equals(folderPath, groupPath, StringComparison.OrdinalIgnoreCase))
                    break;
                separatorIndex = folderPath.LastIndexOf('\\');
                folderPath = separatorIndex < 0 ? string.Empty : folderPath[..separatorIndex];
            }
        }

        return folders.Count;
    }

    private static string FindVolumeAlias(PoolSnapshot snapshot, string volumeId)
    {
        for (var diskIndex = 0; diskIndex < snapshot.Disks.Count; diskIndex++)
        {
            var disk = snapshot.Disks[diskIndex];
            for (var volumeIndex = 0; volumeIndex < disk.Volumes.Count; volumeIndex++)
            {
                var volume = disk.Volumes[volumeIndex];
                if (!string.Equals(volume.Id, volumeId, StringComparison.OrdinalIgnoreCase))
                    continue;

                return string.IsNullOrWhiteSpace(volume.Alias) ? $"D{diskIndex + 1}-V{volumeIndex + 1}" : volume.Alias;
            }
        }

        return volumeId;
    }

    private sealed record AggregatedTransfer(
        string SourceVolumeId,
        string TargetVolumeId,
        string RelativePath,
        string SourceAlias,
        string TargetAlias,
        int FileCount,
        int FolderCount,
        long Size);

    private static ImmutableArray<Regex> CompileExclusionPatterns(IReadOnlyList<string>? patterns)
    {
        if (patterns is null)
            return ImmutableArray<Regex>.Empty;

        try
        {
            return patterns
                .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
                .Select(pattern => new Regex(
                    pattern.Trim(),
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(250)))
                .ToImmutableArray();
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"A never-move path pattern is invalid: {exception.Message}", exception);
        }
    }

    private static int CollectExcludedPaths(
        PlanningFolder folder,
        ImmutableArray<Regex> patterns,
        HashSet<string> excludedPaths,
        HashSet<string> foldersWithExcludedDescendants)
    {
        if (MatchesExclusion(folder.RelativePath, true, patterns))
        {
            MarkFolderAndAncestors(folder.RelativePath, foldersWithExcludedDescendants);
            return AddExcludedSubtree(folder, excludedPaths);
        }

        var excludedCount = 0;
        foreach (var child in folder.Children)
        {
            if (child is PlanningFile file)
            {
                if (!MatchesExclusion(file.RelativePath, false, patterns))
                    continue;

                excludedPaths.Add(file.RelativePath);
                MarkFolderAndAncestors(folder.RelativePath, foldersWithExcludedDescendants);
                excludedCount++;
            }
            else if (child is PlanningFolder childFolder)
            {
                var childExcludedCount = CollectExcludedPaths(childFolder, patterns, excludedPaths, foldersWithExcludedDescendants);
                if (childExcludedCount > 0)
                {
                    foldersWithExcludedDescendants.Add(folder.RelativePath);
                    excludedCount += childExcludedCount;
                }
            }
        }

        return excludedCount;
    }

    private static int AddExcludedSubtree(PlanningFolder folder, HashSet<string> excludedPaths)
    {
        excludedPaths.Add(folder.RelativePath);
        var count = 1;
        foreach (var child in folder.Children)
        {
            if (child is PlanningFile file)
            {
                excludedPaths.Add(file.RelativePath);
                count++;
            }
            else if (child is PlanningFolder childFolder)
            {
                count += AddExcludedSubtree(childFolder, excludedPaths);
            }
        }

        return count;
    }

    private static bool MatchesExclusion(string relativePath, bool isFolder, ImmutableArray<Regex> patterns)
    {
        foreach (var pattern in patterns)
        {
            try
            {
                if (pattern.IsMatch(relativePath) || (isFolder && pattern.IsMatch(relativePath + "\\")))
                    return true;
            }
            catch (RegexMatchTimeoutException exception)
            {
                throw new InvalidDataException("A never-move path pattern exceeded the matching time limit.", exception);
            }
        }

        return false;
    }

    private static void MarkFolderAndAncestors(string relativePath, HashSet<string> foldersWithExcludedDescendants)
    {
        foldersWithExcludedDescendants.Add(relativePath);
        var separatorIndex = relativePath.LastIndexOf('\\');
        while (separatorIndex >= 0)
        {
            relativePath = relativePath[..separatorIndex];
            foldersWithExcludedDescendants.Add(relativePath);
            separatorIndex = relativePath.LastIndexOf('\\');
        }
    }

    private static MutableVolumeFolder BuildVolumeTree(SnapshotDisk disk, SnapshotVolume volume)
    {
        var root = new MutableVolumeFolder(string.Empty, string.Empty);
        foreach (var entry in volume.Folders)
        {
            var relativePath = NormalizeSnapshotPath(entry.Key);
            var folder = GetOrAddVolumeFolder(root, relativePath);
            if (entry.Value is null)
                throw new InvalidDataException($"Folder entry '{entry.Key}' has no file list.");

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
        if (string.IsNullOrWhiteSpace(path) || path == ".")
            return string.Empty;

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

    private static long CalculateVolumeFolderSize(MutableVolumeFolder folder, int allocationUnitSize)
    {
        var size = (long)allocationUnitSize;
        foreach (var file in folder.Files)
            size = AddSaturated(size, file.SizeOnDisk);
        foreach (var child in folder.ChildFolders.Values)
            size = AddSaturated(size, CalculateVolumeFolderSize(child, allocationUnitSize));
        folder.SizeOnDisk = size;
        return size;
    }

    private static void MergeVolumeTree(
        MutablePlanningFolder target,
        MutableVolumeFolder source,
        SnapshotDisk disk,
        SnapshotVolume volume,
        string alias)
    {
        target.Copies.Add(new PlanningFolderCopy(
            disk.Id,
            disk.HardwareName,
            volume.Id,
            alias,
            volume.MountPoint,
            volume.RootFolderPath!,
            source.SizeOnDisk));

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
            mergedFile.Copies.Add(new PlanningFileCopy(
                disk.Id,
                disk.HardwareName,
                volume.Id,
                alias,
                volume.MountPoint,
                volume.RootFolderPath!,
                fileRelativePath,
                file.Size,
                file.SizeOnDisk));
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

            MergeVolumeTree(mergedChild, child, disk, volume, alias);
        }
    }

    private static void ValidateMergeCompatibility(MutablePlanningFolder target, MutableVolumeFolder source)
    {
        foreach (var file in source.Files)
        {
            if (target.ChildFolders.ContainsKey(file.Name))
                throw new InvalidDataException($"'{file.Name}' is both a file and a folder in the merged snapshot.");
        }

        foreach (var child in source.ChildFolders.Values)
        {
            if (target.Files.ContainsKey(child.Name))
                throw new InvalidDataException($"'{child.Name}' is both a file and a folder in the merged snapshot.");

            if (target.ChildFolders.TryGetValue(child.Name, out var existingChild))
                ValidateMergeCompatibility(existingChild, child);
        }
    }

    private static PlanningFolder Freeze(
        MutablePlanningFolder folder,
        int allocationUnitSize,
        ImmutableArray<PlanningWarning>.Builder warnings)
    {
        var files = folder.Files.Values
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file.Name, StringComparer.Ordinal)
            .Select(file =>
            {
                var copies = file.Copies.ToImmutableArray();
                const string duplicateCopiesWarning = "Multiple copies of a file were found; extra copies are left untouched by the preview.";
                if (copies.Length > 1 && !warnings.Any(warning => warning.Message == duplicateCopiesWarning))
                    warnings.Add(new PlanningWarning(duplicateCopiesWarning));
                if (copies.Select(copy => copy.Size).Distinct().Skip(1).Any())
                    warnings.Add(new PlanningWarning($"Copies of '{file.RelativePath}' have different sizes; planning uses the largest copy."));

                return new PlanningFile(
                    file.Name,
                    file.RelativePath,
                    copies.Max(copy => copy.Size),
                    copies.Max(copy => copy.SizeOnDisk),
                    copies);
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
        foreach (var file in files)
            sizeOnDisk = AddSaturated(sizeOnDisk, file.SizeOnDisk);
        foreach (var child in childFolders)
            sizeOnDisk = AddSaturated(sizeOnDisk, child.SizeOnDisk);

        return new PlanningFolder(
            folder.Name,
            folder.RelativePath,
            size,
            sizeOnDisk,
            folder.Copies.ToImmutableArray(),
            children);
    }

    private static void ApplyRule(
        PlanningFolder folder,
        FilePlacementRuleConfig rule,
        List<PlanningTarget> targets,
        Dictionary<string, PlacementAssignment> folderAssignments,
        Dictionary<string, PlacementAssignment> fileAssignments,
        HashSet<string> assignedDescendantPaths,
        HashSet<string> excludedPaths,
        HashSet<string> foldersWithExcludedDescendants,
        Dictionary<string, UnplacedItem> unplacedItems)
    {
        if (excludedPaths.Contains(folder.RelativePath))
            return;
        if (IsCovered(folder.RelativePath, folderAssignments, fileAssignments))
            return;

        var rulePath = NormalizeRulePath(rule.FullRelativePath);
        var withinRule = IsSameOrDescendant(folder.RelativePath, rulePath);
        var canReachRule = IsSameOrDescendant(rulePath, folder.RelativePath);
        if (!withinRule && !canReachRule)
            return;

        var depth = GetRuleDepth(folder.RelativePath);
        if (withinRule && depth >= rule.StartingDepth &&
            !HasPlacedDescendant(folder.RelativePath, assignedDescendantPaths) &&
            !foldersWithExcludedDescendants.Contains(folder.RelativePath) &&
            TryPlaceFolder(folder, rule, targets, folderAssignments, assignedDescendantPaths))
        {
            RemoveUnplacedSubtree(folder.RelativePath, unplacedItems);
            return;
        }

        if (withinRule && depth >= rule.StartingDepth)
        {
            var children = folder.Children
                .OrderByDescending(child => child.SizeOnDisk)
                .ThenBy(child => child.RelativePath, StringComparer.OrdinalIgnoreCase);
            foreach (var child in children)
            {
                if (child is PlanningFolder childFolder)
                {
                    ApplyRule(childFolder, rule, targets, folderAssignments, fileAssignments, assignedDescendantPaths, excludedPaths, foldersWithExcludedDescendants, unplacedItems);
                }
                else if (child is PlanningFile file && !excludedPaths.Contains(file.RelativePath) && !IsCovered(file.RelativePath, folderAssignments, fileAssignments))
                {
                    if (TryPlaceFile(file, rule, targets, fileAssignments, assignedDescendantPaths))
                    {
                        unplacedItems.Remove(file.RelativePath);
                    }
                    else
                    {
                        unplacedItems[file.RelativePath] = new UnplacedItem(
                            file.RelativePath,
                            "File",
                            rule.Id,
                            "No allowed volume has enough space for this file.",
                            file.SizeOnDisk);
                    }
                }
            }

            if (folder.Children.IsEmpty && !folderAssignments.ContainsKey(folder.RelativePath))
            {
                unplacedItems[folder.RelativePath] = new UnplacedItem(
                    DisplayPath(folder.RelativePath),
                    "Folder",
                    rule.Id,
                    "No allowed volume has enough space for this folder.",
                    folder.SizeOnDisk);
            }

            return;
        }

        if (canReachRule || withinRule)
        {
            foreach (var childFolder in folder.Children.OfType<PlanningFolder>().OrderBy(child => child.SizeOnDisk))
                ApplyRule(childFolder, rule, targets, folderAssignments, fileAssignments, assignedDescendantPaths, excludedPaths, foldersWithExcludedDescendants, unplacedItems);
        }
    }

    private static void RemoveUnplacedSubtree(string relativePath, Dictionary<string, UnplacedItem> unplacedItems)
    {
        var prefix = string.IsNullOrEmpty(relativePath) ? string.Empty : relativePath + "\\";
        foreach (var path in unplacedItems.Keys.Where(path =>
            string.Equals(path, relativePath, StringComparison.OrdinalIgnoreCase) ||
            (prefix.Length == 0 ? !string.IsNullOrEmpty(path) : path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToArray())
        {
            unplacedItems.Remove(path);
        }
    }

    private static bool TryPlaceFolder(
        PlanningFolder folder,
        FilePlacementRuleConfig rule,
        List<PlanningTarget> targets,
        Dictionary<string, PlacementAssignment> assignments,
        HashSet<string> assignedDescendantPaths)
    {
        var allowedVolumeOrder = GetAllowedVolumeOrder(rule);
        foreach (var target in OrderTargets(targets, allowedVolumeOrder, candidate => ExistingSize(folder.Copies, candidate.VolumeId)))
        {
            var existingSize = ExistingSize(folder.Copies, target.VolumeId);
            var additionalSize = Math.Max(0, folder.SizeOnDisk - existingSize);
            if (additionalSize > target.RemainingSpace)
                continue;

            target.RemainingSpace -= additionalSize;
            assignments.Add(folder.RelativePath, new PlacementAssignment(target, rule.Id, folder.SizeOnDisk, folder.RelativePath));
            MarkAncestorFolders(folder.RelativePath, assignedDescendantPaths);
            return true;
        }

        return false;
    }

    private static bool TryPlaceFile(
        PlanningFile file,
        FilePlacementRuleConfig rule,
        List<PlanningTarget> targets,
        Dictionary<string, PlacementAssignment> assignments,
        HashSet<string> assignedDescendantPaths)
    {
        var allowedVolumeOrder = GetAllowedVolumeOrder(rule);
        foreach (var target in OrderTargets(targets, allowedVolumeOrder, candidate => ExistingSize(file.Copies, candidate.VolumeId)))
        {
            var existingSize = ExistingSize(file.Copies, target.VolumeId);
            var additionalSize = Math.Max(0, file.SizeOnDisk - existingSize);
            if (additionalSize > target.RemainingSpace)
                continue;

            target.RemainingSpace -= additionalSize;
            assignments.Add(file.RelativePath, new PlacementAssignment(target, rule.Id, file.SizeOnDisk, file.RelativePath));
            MarkAncestorFolders(file.RelativePath, assignedDescendantPaths);
            return true;
        }

        return false;
    }

    private static Dictionary<string, int> GetAllowedVolumeOrder(FilePlacementRuleConfig rule)
    {
        return rule.AllowedVolumeIds
            .Select((volumeId, index) => new { VolumeId = volumeId, Index = index })
            .GroupBy(item => item.VolumeId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<PlanningTarget> OrderTargets(
        IEnumerable<PlanningTarget> targets,
        Dictionary<string, int> allowedVolumeOrder,
        Func<PlanningTarget, long> existingSize)
    {
        return targets
            .Where(target => allowedVolumeOrder.ContainsKey(target.VolumeId))
            .OrderByDescending(existingSize)
            .ThenBy(target => allowedVolumeOrder[target.VolumeId])
            .ThenByDescending(target => target.RemainingSpace)
            .ThenBy(target => target.VolumeId, StringComparer.OrdinalIgnoreCase);
    }

    private static long ExistingSize(ImmutableArray<PlanningFolderCopy> copies, string volumeId)
    {
        return copies.FirstOrDefault(copy => string.Equals(copy.VolumeId, volumeId, StringComparison.OrdinalIgnoreCase))?.SizeOnDisk ?? 0;
    }

    private static long ExistingSize(ImmutableArray<PlanningFileCopy> copies, string volumeId)
    {
        return copies.Where(copy => string.Equals(copy.VolumeId, volumeId, StringComparison.OrdinalIgnoreCase))
            .Select(copy => copy.SizeOnDisk)
            .DefaultIfEmpty(0)
            .Max();
    }

    private static bool IsCovered(
        string relativePath,
        Dictionary<string, PlacementAssignment> folderAssignments,
        Dictionary<string, PlacementAssignment> fileAssignments)
    {
        if (fileAssignments.ContainsKey(relativePath))
            return true;

        var path = relativePath;
        while (true)
        {
            if (folderAssignments.ContainsKey(path))
                return true;
            var separatorIndex = path.LastIndexOf('\\');
            if (separatorIndex < 0)
                return false;
            path = path[..separatorIndex];
        }
    }

    private static bool HasPlacedDescendant(string relativePath, HashSet<string> assignedDescendantPaths)
    {
        return assignedDescendantPaths.Contains(relativePath);
    }

    private static void MarkAncestorFolders(string relativePath, HashSet<string> assignedDescendantPaths)
    {
        var separatorIndex = relativePath.LastIndexOf('\\');
        while (separatorIndex >= 0)
        {
            relativePath = relativePath[..separatorIndex];
            assignedDescendantPaths.Add(relativePath);
            separatorIndex = relativePath.LastIndexOf('\\');
        }

        assignedDescendantPaths.Add(string.Empty);
    }

    private static bool IsSameOrDescendant(string path, string parent)
    {
        if (string.IsNullOrEmpty(parent))
            return true;
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
        return string.IsNullOrEmpty(path) ? "." : path;
    }

    private static PlannedPlacement CreatePlacement(string relativePath, string itemType, PlacementAssignment assignment)
    {
        return new PlannedPlacement(
            DisplayPath(relativePath),
            itemType,
            assignment.RuleId,
            assignment.Target.DiskId,
            assignment.Target.DiskName,
            assignment.Target.Alias,
            assignment.Target.VolumeId,
            assignment.Target.MountPoint,
            assignment.SizeOnDisk);
    }

    private static void BuildMoves(
        PlanningFolder folder,
        PlacementAssignment? inheritedAssignment,
        Dictionary<string, PlacementAssignment> folderAssignments,
        Dictionary<string, PlacementAssignment> fileAssignments,
        ImmutableArray<PlannedMove>.Builder moves)
    {
        if (folderAssignments.TryGetValue(folder.RelativePath, out var explicitFolderAssignment))
            inheritedAssignment = explicitFolderAssignment;

        foreach (var child in folder.Children)
        {
            if (child is PlanningFolder childFolder)
            {
                BuildMoves(childFolder, inheritedAssignment, folderAssignments, fileAssignments, moves);
                continue;
            }

            if (child is not PlanningFile file)
                continue;

            var assignment = fileAssignments.TryGetValue(file.RelativePath, out var explicitFileAssignment)
                ? explicitFileAssignment
                : inheritedAssignment;
            if (assignment is null || file.Copies.IsEmpty || file.Copies.Any(copy =>
                string.Equals(copy.VolumeId, assignment.Target.VolumeId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var source = file.Copies
                .OrderByDescending(copy => copy.SizeOnDisk)
                .ThenBy(copy => copy.DiskId, StringComparer.OrdinalIgnoreCase)
                .First();
            var destination = Path.Combine(
                assignment.Target.RootFolderPath,
                file.RelativePath.Replace('\\', Path.DirectorySeparatorChar));
            moves.Add(new PlannedMove(
                file.RelativePath,
                assignment.PlacementPath,
                source.DiskId,
                source.DiskName,
                source.VolumeAlias,
                source.VolumeId,
                source.FullPath,
                assignment.Target.DiskId,
                assignment.Target.DiskName,
                assignment.Target.Alias,
                assignment.Target.VolumeId,
                destination,
                file.Size));
        }
    }

    private static long AddSaturated(long left, long right)
    {
        if (right <= 0)
            return left;
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

    private sealed class PlanningTarget(
        string diskId,
        string diskName,
        string volumeId,
        string alias,
        string mountPoint,
        string rootFolderPath,
        long capacity,
        long remainingSpace)
    {
        public string DiskId { get; } = diskId;
        public string DiskName { get; } = diskName;
        public string VolumeId { get; } = volumeId;
        public string Alias { get; } = alias;
        public string MountPoint { get; } = mountPoint;
        public string RootFolderPath { get; } = rootFolderPath;
        public long Capacity { get; } = capacity;
        public long RemainingSpace { get; set; } = remainingSpace;
    }

    private sealed record PlacementAssignment(PlanningTarget Target, string RuleId, long SizeOnDisk, string PlacementPath);
}