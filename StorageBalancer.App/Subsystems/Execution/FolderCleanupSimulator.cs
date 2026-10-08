using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using StorageBalancer.App.Domain;

namespace StorageBalancer.App.Subsystems.Execution;

public static class FolderCleanupSimulator
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
        return path.Trim().Replace('/', '\\').Trim('\\');
    }

    private static string GetParentPath(string path)
    {
        int lastSlash = path.LastIndexOf('\\');
        return lastSlash >= 0 ? path.Substring(0, lastSlash) : string.Empty;
    }

    public static FolderCleanupSummary Simulate(PoolSnapshot snapshot, IReadOnlyList<FileMoveTask> completedTasks)
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

        // 2. Apply simulated file moves
        foreach (var task in completedTasks)
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
            .Where(p => !string.IsNullOrEmpty(p))
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

            // Rule 1: Singleton folder in pool -> NEVER delete!
            if (volumesWithFolder.Count == 1)
            {
                string singleVol = volumesWithFolder[0];
                preservedUniqueCount++;
                actions.Add(new FolderCleanupAction(
                    folderPath,
                    singleVol,
                    singleVol,
                    "PreservedUnique",
                    "Preserved: only copy in the entire pool (prevents removing logical folder from DrivePool view)"
                ));
                continue;
            }

            // Rule 2: Multi-volume folder.
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
                // All sibling volumes are empty! Choose the first volume as Primary and KEEP it
                // so the user's empty folder remains intact in the merged view.
                primaryVolume = volumesWithFolder.First();
                preservedUniqueCount++;
                actions.Add(new FolderCleanupAction(
                    folderPath,
                    primaryVolume,
                    primaryVolume,
                    "PreservedUnique",
                    "Designated Primary: kept so logical empty folder remains visible in merged view"
                ));
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
                            "KeptWithData",
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
                        "KeptWithData",
                        "Kept: volume still contains active files or subdirectories"
                    ));
                }
                else
                {
                    // Redundant empty folder! Sibling volume has 0 files and 0 subfolders.
                    // Verified: Primary still exists in volumeTrees[primaryVolume]
                    cleanedCount++;
                    actions.Add(new FolderCleanupAction(
                        folderPath,
                        vol,
                        primaryVolume,
                        "Cleaned",
                        $"Cleaned: redundant empty folder safely removed; verified primary copy on {primaryVolume}"
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
