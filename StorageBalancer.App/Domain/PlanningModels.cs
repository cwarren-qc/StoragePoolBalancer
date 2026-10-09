using System;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

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

[JsonConverter(typeof(PlacementLogicJsonConverter))]
public enum PlacementLogic
{
    Staying,
    Moving,
    Splitting,
    Consolidating,
    MovingConsolidating
}

public sealed class PlacementLogicJsonConverter : JsonConverter<PlacementLogic>
{
    public override PlacementLogic Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString();
        return str switch
        {
            "Staying" or "Stayed intact" or "Staying intact" => PlacementLogic.Staying,
            "Moving" or "Moved" => PlacementLogic.Moving,
            "Splitting" or "Split" => PlacementLogic.Splitting,
            "Consolidating" or "Consolidated" => PlacementLogic.Consolidating,
            "Moving/Consolidating" or "Moved/Consolidated" => PlacementLogic.MovingConsolidating,
            _ => Enum.TryParse<PlacementLogic>(str, true, out var val) ? val : PlacementLogic.Staying
        };
    }

    public override void Write(Utf8JsonWriter writer, PlacementLogic value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            PlacementLogic.Staying => "Staying",
            PlacementLogic.Moving => "Moving",
            PlacementLogic.Splitting => "Splitting",
            PlacementLogic.Consolidating => "Consolidating",
            PlacementLogic.MovingConsolidating => "Moving/Consolidating",
            _ => value.ToString()
        });
    }
}

[JsonConverter(typeof(VolumeEligibilityStatusJsonConverter))]
public enum VolumeEligibilityStatus
{
    Included,
    Unavailable,
    IncompleteScan
}

public sealed class VolumeEligibilityStatusJsonConverter : JsonConverter<VolumeEligibilityStatus>
{
    public override VolumeEligibilityStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString();
        return str switch
        {
            "Incomplete scan" => VolumeEligibilityStatus.IncompleteScan,
            _ => Enum.TryParse<VolumeEligibilityStatus>(str, true, out var val) ? val : VolumeEligibilityStatus.Included
        };
    }

    public override void Write(Utf8JsonWriter writer, VolumeEligibilityStatus value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            VolumeEligibilityStatus.IncompleteScan => "Incomplete scan",
            _ => value.ToString()
        });
    }
}

public enum DeferredCopyReason
{
    None,
    DeferredDuplicate,
    FillerPlacement
}

public sealed record PlannedPlacement(
    int Order,
    string RelativePath,
    PlacementLogic LogicApplied,
    ImmutableArray<VolumeProvenance> Targets,
    long SizeOnDisk,
    long SizeMoved,
    ImmutableArray<VolumeProvenance> Sources,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ImmutableArray<PlannedFileItem>? Files = null,
    ImmutableArray<VolumeProvenance>? MovedSources = null
);

public sealed record VolumeProvenance(string Alias, long Size);

public sealed record VolumePlanSummary(
    string Alias,
    string DiskName,
    string MountPoint,
    long Capacity,
    long FinalSize,
    bool IsEligible,
    VolumeEligibilityStatus Status,
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
    ImmutableArray<VolumePlanSummary> Volumes,
    FolderCleanupSummary? FolderCleanup = null
);