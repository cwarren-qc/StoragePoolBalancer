using System;
using System.Collections.Immutable;

namespace StorageBalancer.App.Domain;

public abstract record PlanningNode(string RelativePath, long Size, long SizeOnDisk);

public sealed record PlanningFile(
    string Name,
    string RelativePath,
    long Size,
    long SizeOnDisk,
    ImmutableArray<PlanningFileCopy> Copies
) : PlanningNode(RelativePath, Size, SizeOnDisk);

public sealed record PlanningFolder(
    string Name,
    string RelativePath,
    long Size,
    long SizeOnDisk,
    ImmutableArray<PlanningFolderCopy> Copies,
    ImmutableArray<PlanningNode> Children
) : PlanningNode(RelativePath, Size, SizeOnDisk);

public sealed record PlanningFileCopy(
    string DiskId,
    string DiskName,
    string VolumeId,
    string VolumeAlias,
    string MountPoint,
    string RootFolderPath,
    string RelativePath,
    long Size,
    long SizeOnDisk)
{
    public string FullPath => System.IO.Path.Combine(RootFolderPath, RelativePath.Replace('\\', System.IO.Path.DirectorySeparatorChar));
}

public sealed record PlanningFolderCopy(
    string DiskId,
    string DiskName,
    string VolumeId,
    string VolumeAlias,
    string MountPoint,
    string RootFolderPath,
    long SizeOnDisk
);

public sealed record PlannedPlacement(
    int Order,
    string RelativePath,
    string RuleId,
    string LogicApplied,
    ImmutableArray<VolumeProvenance> Targets,
    long SizeOnDisk,
    long SizeMoved,
    ImmutableArray<VolumeProvenance> Sources
);

public sealed record VolumeProvenance(string VolumeId, string Alias, long Size);

public sealed record VolumeTransferGroup(
    string RelativePath,
    string OtherVolumeId,
    string OtherAlias,
    int FolderCount,
    int FileCount,
    long Size
);

public sealed record VolumePlanSummary(
    string VolumeId,
    string Alias,
    string DiskName,
    string MountPoint,
    long Capacity,
    long FinalSize,
    bool IsEligible,
    string Status,
    ImmutableArray<VolumeProvenance> Provenance,
    ImmutableArray<VolumeTransferGroup> Incoming,
    ImmutableArray<VolumeTransferGroup> Outgoing
);

public sealed record PlanningWarning(string Message);

public sealed record PlacementPlan(
    DateTime SnapshotScannedAt,
    ImmutableArray<PlannedPlacement> Placements,
    ImmutableArray<PlanningWarning> Warnings,
    ImmutableArray<VolumePlanSummary> Volumes
);