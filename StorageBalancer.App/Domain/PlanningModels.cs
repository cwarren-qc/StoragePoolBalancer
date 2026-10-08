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
    string DiskName,
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
    string DiskName,
    string VolumeAlias,
    string MountPoint,
    string RootFolderPath,
    long SizeOnDisk
);

public sealed record PlannedFileItem(
    string Name,
    string RelativePath,
    string OriginalVolumeAlias,
    string OriginalMountPoint,
    string OriginalRootFolderPath,
    string OriginalFullPath,
    string DestinationVolumeAlias,
    long Size,
    long SizeOnDisk
);

public sealed record PlannedPlacement(
    int Order,
    string RelativePath,
    string LogicApplied,
    ImmutableArray<VolumeProvenance> Targets,
    long SizeOnDisk,
    long SizeMoved,
    ImmutableArray<VolumeProvenance> Sources,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ImmutableArray<PlannedFileItem>? Files = null
);

public sealed record VolumeProvenance(string Alias, long Size);

public sealed record VolumePlanSummary(
    string Alias,
    string DiskName,
    string MountPoint,
    long Capacity,
    long FinalSize,
    bool IsEligible,
    string Status,
    ImmutableArray<VolumeProvenance> Provenance,
    long OtherItemsSizeOnDisk = 0,
    long FilesMovedOut = 0,
    long FilesMovedIn = 0
);

public sealed record PlanningWarning(string Message);

public sealed record PlacementPlan(
    DateTime SnapshotScannedAt,
    ImmutableArray<PlannedPlacement> Placements,
    ImmutableArray<PlanningWarning> Warnings,
    ImmutableArray<VolumePlanSummary> Volumes
);