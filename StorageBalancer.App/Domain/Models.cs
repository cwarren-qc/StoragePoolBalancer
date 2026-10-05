using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace StorageBalancer.App.Domain;

public record SnapshotFile(string Name, long Size, long SizeOnDisk);

public record SnapshotIssue(string Path, string Message);

public record SnapshotVolume(
    string Id,
    string MountPoint,
    long Capacity,
    long OtherItemsSizeOnDisk,
    string ConfiguredRootFolder,
    string? RootFolderPath,
    bool IsComplete,
    ImmutableList<SnapshotIssue> Issues,
    SortedDictionary<string, List<SnapshotFile>> Folders
);

public record SnapshotDisk(
    string Id,
    string HardwareName,
    string Description,
    ImmutableList<SnapshotVolume> Volumes
);

public record PoolSnapshot(
    int SchemaVersion,
    DateTime ScannedAt,
    bool DrivePoolMode,
    int AllocationUnitSize,
    ImmutableList<SnapshotDisk> Disks
);