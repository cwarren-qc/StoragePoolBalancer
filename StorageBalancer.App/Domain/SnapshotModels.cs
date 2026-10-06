using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace StorageBalancer.App.Domain;

public record SnapshotFile(string Name, long Size, long SizeOnDisk);

public record SnapshotIssue(string Path, string Message);

public record SnapshotVolume(
    string Alias,
    string Disk,
    string MountPoint,
    long Capacity,
    long OtherItemsSizeOnDisk,
    string ConfiguredRootFolder,
    string? RootFolderPath,
    bool IsComplete,
    ImmutableList<SnapshotIssue> Issues,
    SortedDictionary<string, List<SnapshotFile>> Folders
);

public record PoolSnapshot(
    int SchemaVersion,
    DateTime ScannedAt,
    bool DrivePoolMode,
    int AllocationUnitSize,
    ImmutableList<SnapshotVolume> Volumes
);