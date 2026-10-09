using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using StorageBalancer.App.Configuration;
using StorageBalancer.App.Domain;

namespace StorageBalancer.App.Subsystems.Planning;

public static class FolderCleanupPlanner
{
    private sealed class MutableFolderInfo
    {
        public string RelativePath { get; }
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Subfolders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public MutableFolderInfo(string relativePath)
        {
            RelativePath = relativePath;
        }

        public bool HasContents => Files.Count > 0 || Subfolders.Count > 0;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var trimmed = path.Trim().Replace('/', '\\').Trim('\\');
        return trimmed == "." ? string.Empty : trimmed;
    }

    private static string GetParentPath(string path)
    {
        int lastSlash = path.LastIndexOf('\\');
        return lastSlash >= 0 ? path.Substring(0, lastSlash) : string.Empty;
    }

    private static bool IsSameOrDescendant(string path, string parent)
    {
        if (string.IsNullOrEmpty(parent)) return true;
        return string.Equals(path, parent, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetRuleDepth(string path)
    {
        return string.IsNullOrEmpty(path) ? 1 : path.Count(c => c == '\\') + 1;
    }

    private static string? ResolveTargetVolumeForFolder(
        string folderPath,
        IReadOnlyList<FilePlacementRuleConfig>? rules,
        Dictionary<string, Dictionary<string, MutableFolderInfo>> volumeTrees)
    {
        if (string.IsNullOrEmpty(folderPath)) return null;

        // 1. Check matching File Placement Rules
        if (rules != null && rules.Count > 0)
        {
            FilePlacementRuleConfig? bestRule = null;
            int bestRuleMatchDepth = -1;

            foreach (var rule in rules)
            {
                if (rule.AllowedVolumeAliases == null || rule.AllowedVolumeAliases.Count == 0)
                    continue;

                string normRulePath = NormalizePath(rule.FullRelativePath);
                if (IsSameOrDescendant(folderPath, normRulePath))
                {
                    int depth = GetRuleDepth(folderPath);
                    if (depth >= rule.StartingDepth)
                    {
                        int ruleDepth = string.IsNullOrEmpty(normRulePath) ? 0 : normRulePath.Split('\\').Length;
                        if (ruleDepth > bestRuleMatchDepth)
                        {
                            bestRuleMatchDepth = ruleDepth;
                            bestRule = rule;
                        }
                    }
                }
            }

            if (bestRule != null)
            {
                var allowed = bestRule.AllowedVolumeAliases.Contains("*")
                    ? volumeTrees.Keys.ToList()
                    : bestRule.AllowedVolumeAliases
                        .Where(a => volumeTrees.ContainsKey(a))
                        .ToList();

                if (allowed.Count == 1)
                {
                    return allowed[0];
                }
                else if (allowed.Count > 1)
                {
                    // Check if an ancestor has active contents on one of these allowed volumes
                    string curr = GetParentPath(folderPath);
                    while (!string.IsNullOrEmpty(curr))
                    {
                        var match = allowed.FirstOrDefault(a =>
                            volumeTrees[a].TryGetValue(curr, out var info) && info.HasContents);
                        if (match != null) return match;
                        curr = GetParentPath(curr);
                    }

                    return allowed[0];
                }
            }
        }

        // 2. Check parent/ancestor consolidation: find volume where ancestor has active contents
        string ancestor = GetParentPath(folderPath);
        while (!string.IsNullOrEmpty(ancestor))
        {
            var volsWithData = volumeTrees
                .Where(kv => kv.Value.TryGetValue(ancestor, out var info) && info.HasContents)
                .OrderByDescending(kv => kv.Value[ancestor].Files.Count)
                .Select(kv => kv.Key)
                .ToList();

            if (volsWithData.Count > 0)
            {
                return volsWithData[0];
            }

            ancestor = GetParentPath(ancestor);
        }

        return null;
    }

    public static FolderCleanupSummary Calculate(
        PoolSnapshot snapshot,
        IReadOnlyList<FileMoveTask> plannedMoves,
        IReadOnlyList<FilePlacementRuleConfig>? rules)
    {
        // 1. Build initial folder structure per volume from snapshot
        var volumeTrees = new Dictionary<string, Dictionary<string, MutableFolderInfo>>(StringComparer.OrdinalIgnoreCase);

        foreach (var vol in snapshot.Volumes)
        {
            var tree = new Dictionary<string, MutableFolderInfo>(StringComparer.OrdinalIgnoreCase);
            volumeTrees[vol.Alias] = tree;

            // Ensure root folder exists
            tree[string.Empty] = new MutableFolderInfo(string.Empty);

            if (vol.Folders != null)
            {
                foreach (var (rawFolder, files) in vol.Folders)
                {
                    string normFolder = NormalizePath(rawFolder);
                    EnsureFolderChain(tree, normFolder);

                    var info = tree[normFolder];
                    if (files != null)
                    {
                        foreach (var f in files)
                        {
                            info.Files.Add(f.Name);
                        }
                    }
                }
            }
        }

        // 2. Apply planned file moves
        foreach (var task in plannedMoves)
        {
            string normFilePath = NormalizePath(task.RelativePath);
            string folderPath = GetParentPath(normFilePath);

            // Remove file from source volume
            if (volumeTrees.TryGetValue(task.SourceVolume, out var srcTree))
            {
                if (srcTree.TryGetValue(folderPath, out var srcFolder))
                {
                    srcFolder.Files.Remove(task.FileName);
                }
            }

            // Add file to target volume (and ensure folder chain exists)
            if (volumeTrees.TryGetValue(task.TargetVolume, out var tgtTree))
            {
                EnsureFolderChain(tgtTree, folderPath);
                tgtTree[folderPath].Files.Add(task.FileName);
            }
        }

        // 3. Collect all unique non-root relative folder paths across all volumes
        var allUniqueFolders = volumeTrees.Values
            .SelectMany(tree => tree.Keys)
            .Where(p => !string.IsNullOrEmpty(p) && p != ".")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            // Sort bottom-up: deepest folders first (by directory depth descending, then length descending, then path)
            .OrderByDescending(p => p.Count(c => c == '\\'))
            .ThenByDescending(p => p.Length)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var actions = ImmutableList.CreateBuilder<FolderCleanupAction>();
        int cleanedCount = 0;
        int preservedUniqueCount = 0;
        int keptWithDataCount = 0;

        foreach (var folderPath in allUniqueFolders)
        {
            // Find all volumes that currently contain this folder
            var volumesWithFolder = volumeTrees
                .Where(kv => kv.Value.ContainsKey(folderPath))
                .Select(kv => kv.Key)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (volumesWithFolder.Count == 0)
                continue;

            // Categorize which volumes have real contents (files or subfolders)
            var volumesWithData = volumesWithFolder
                .Where(v => volumeTrees[v][folderPath].HasContents)
                .ToList();

            string primaryVolume;

            if (volumesWithData.Count > 0)
            {
                // Choose volume with data as Primary (prefer the one with the most files)
                primaryVolume = volumesWithData
                    .OrderByDescending(v => volumeTrees[v][folderPath].Files.Count)
                    .ThenBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .First();
            }
            else
            {
                // All current volume instances of this folder are completely empty!
                // Check if placement rules or parent consolidation dictates a target volume:
                string? targetVol = ResolveTargetVolumeForFolder(folderPath, rules, volumeTrees);

                if (targetVol != null && volumeTrees.ContainsKey(targetVol))
                {
                    primaryVolume = targetVol;
                    if (!volumesWithFolder.Contains(targetVol, StringComparer.OrdinalIgnoreCase))
                    {
                        EnsureFolderChain(volumeTrees[targetVol], folderPath);
                        volumesWithFolder.Add(targetVol);
                    }

                    preservedUniqueCount++;
                    actions.Add(new FolderCleanupAction(
                        folderPath,
                        primaryVolume,
                        primaryVolume,
                        FolderCleanupStatus.PreservingUnique,
                        $"Designated Primary: empty folder migrated to {primaryVolume} per placement rule/consolidation"
                    ));
                }
                else
                {
                    primaryVolume = volumesWithFolder.First();
                    preservedUniqueCount++;
                    actions.Add(new FolderCleanupAction(
                        folderPath,
                        primaryVolume,
                        primaryVolume,
                        FolderCleanupStatus.PreservingUnique,
                        volumesWithFolder.Count == 1
                            ? "Preserving Unique: only copy in the entire pool (prevents removing logical folder from DrivePool view)"
                            : "Designated Primary: kept so logical empty folder remains visible in merged view"
                    ));
                }
            }

            // If it was a singleton folder that stayed on its original volume (not relocated):
            if (volumesWithFolder.Count == 1)
            {
                continue;
            }

            // Now evaluate sibling volumes against Primary
            foreach (var vol in volumesWithFolder)
            {
                if (string.Equals(vol, primaryVolume, StringComparison.OrdinalIgnoreCase))
                {
                    // Primary volume is preserved
                    if (volumesWithData.Contains(vol))
                    {
                        keptWithDataCount++;
                        actions.Add(new FolderCleanupAction(
                            folderPath,
                            vol,
                            primaryVolume,
                            FolderCleanupStatus.KeepingWithData,
                            "Primary copy: active files or subdirectories remain on this volume"
                        ));
                    }
                    continue;
                }

                var folderInfo = volumeTrees[vol][folderPath];
                if (folderInfo.HasContents)
                {
                    keptWithDataCount++;
                    actions.Add(new FolderCleanupAction(
                        folderPath,
                        vol,
                        primaryVolume,
                        FolderCleanupStatus.KeepingWithData,
                        "Keeping with data: volume still contains active files or subdirectories"
                    ));
                }
                else
                {
                    // Redundant empty folder! Sibling volume has 0 files and 0 subfolders.
                    cleanedCount++;
                    actions.Add(new FolderCleanupAction(
                        folderPath,
                        vol,
                        primaryVolume,
                        FolderCleanupStatus.Cleaning,
                        $"Cleaning: redundant empty folder safely removed; verified primary copy on {primaryVolume}"
                    ));

                    // Remove this folder from volume `vol`
                    volumeTrees[vol].Remove(folderPath);

                    // Also remove this folder from parent folder's Subfolders on volume `vol`
                    string parent = GetParentPath(folderPath);
                    if (volumeTrees[vol].TryGetValue(parent, out var parentInfo))
                    {
                        parentInfo.Subfolders.Remove(folderPath);
                    }
                }
            }
        }

        return new FolderCleanupSummary(
            TotalFoldersEvaluated: allUniqueFolders.Count,
            TotalFolderInstances: actions.Count,
            CleanedCount: cleanedCount,
            PreservedUniqueCount: preservedUniqueCount,
            KeptWithDataCount: keptWithDataCount,
            Actions: actions.ToImmutable()
        );
    }

    private static void EnsureFolderChain(Dictionary<string, MutableFolderInfo> tree, string folderPath)
    {
        if (tree.ContainsKey(folderPath)) return;

        tree[folderPath] = new MutableFolderInfo(folderPath);

        string parent = GetParentPath(folderPath);
        EnsureFolderChain(tree, parent);
        tree[parent].Subfolders.Add(folderPath);
    }
}
