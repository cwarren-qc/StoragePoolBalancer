using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StorageBalancer.App.Domain;

[JsonConverter(typeof(FolderCleanupStatusJsonConverter))]
public enum FolderCleanupStatus
{
    Cleaning,
    Preserving,
    Keeping
}

public sealed class FolderCleanupStatusJsonConverter : JsonConverter<FolderCleanupStatus>
{
    public override FolderCleanupStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString();
        return str switch
        {
            "Cleaning" or "Cleaned" => FolderCleanupStatus.Cleaning,
            "Preserving" or "Preserved" or "PreservedUnique" => FolderCleanupStatus.Preserving,
            "Keeping" or "Kept" or "KeptWithData" => FolderCleanupStatus.Keeping,
            _ => Enum.TryParse<FolderCleanupStatus>(str, true, out var val) ? val : FolderCleanupStatus.Cleaning
        };
    }

    public override void Write(Utf8JsonWriter writer, FolderCleanupStatus value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            FolderCleanupStatus.Cleaning => "Cleaning",
            FolderCleanupStatus.Preserving => "Preserving",
            FolderCleanupStatus.Keeping => "Keeping",
            _ => value.ToString()
        });
    }
}

[JsonConverter(typeof(ExecutionPhaseJsonConverter))]
public enum ExecutionPhase
{
    Idle,
    Preparing,
    Transferring,
    CleaningFolders,
    Completed,
    Cancelled,
    Failed
}

public sealed class ExecutionPhaseJsonConverter : JsonConverter<ExecutionPhase>
{
    public override ExecutionPhase Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString();
        return str switch
        {
            "Cleaning Folders" => ExecutionPhase.CleaningFolders,
            _ => Enum.TryParse<ExecutionPhase>(str, true, out var val) ? val : ExecutionPhase.Idle
        };
    }

    public override void Write(Utf8JsonWriter writer, ExecutionPhase value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            ExecutionPhase.CleaningFolders => "Cleaning Folders",
            _ => value.ToString()
        });
    }
}

[JsonConverter(typeof(VolumeActivityStatusJsonConverter))]
public enum VolumeActivityStatus
{
    Idle,
    Reading,
    Writing,
    CleaningFolders,
    Complete
}

public sealed class VolumeActivityStatusJsonConverter : JsonConverter<VolumeActivityStatus>
{
    public override VolumeActivityStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString();
        return str switch
        {
            "Cleaning Folders" => VolumeActivityStatus.CleaningFolders,
            _ => Enum.TryParse<VolumeActivityStatus>(str, true, out var val) ? val : VolumeActivityStatus.Idle
        };
    }

    public override void Write(Utf8JsonWriter writer, VolumeActivityStatus value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            VolumeActivityStatus.CleaningFolders => "Cleaning Folders",
            _ => value.ToString()
        });
    }
}

public record ExecutionStartRequest(
    string? SnapshotName,
    bool IsSimulation = true,
    int SimulationDurationSeconds = 120,
    int? MaxThreads = null,
    bool? VerifyCopies = null
);

public record TransferSegment(
    string Alias,
    long RemainingBytes,
    long TotalBytes
);

public record VolumeExecutionProgress(
    string Alias,
    string DiskName,
    long Capacity,
    long CurrentSize,
    long FinalSize,
    long StayedSize,
    long OtherItemsSizeOnDisk,
    ImmutableList<TransferSegment> OutgoingRemaining,
    ImmutableList<TransferSegment> IncomingRemaining,
    long MovedOutBytes,
    long MovedOutTotalBytes,
    long MovedOutFiles,
    long MovedOutTotalFiles,
    long MovedInBytes,
    long MovedInTotalBytes,
    long MovedInFiles,
    long MovedInTotalFiles,
    VolumeActivityStatus Status,
    string CurrentActivity
);

public record FileMoveTask(
    string FileName,
    string RelativePath,
    string SourceVolume,
    string TargetVolume,
    string SourceDisk,
    string TargetDisk,
    long Size,
    long SizeOnDisk,
    string SourceFilePath = "",
    string TargetFilePath = ""
);

public record ActiveTransferInfo(
    int WorkerId,
    string FileName,
    string SourceVolume,
    string TargetVolume,
    string SourceDisk,
    string TargetDisk,
    long FileSize,
    long BytesCopied,
    double ThroughputBps
);

public record FolderCleanupAction(
    string RelativePath,
    string VolumeAlias,
    string PrimaryVolumeAlias,
    FolderCleanupStatus Status,
    string Reason
);

public record FolderCleanupTask(
    string RelativePath,
    string VolumeAlias,
    string PrimaryVolumeAlias,
    string TargetFolderPath,
    string PrimaryFolderPath
);

public record FolderCleanupSummary(
    int TotalFoldersEvaluated,
    int TotalFolderInstances,
    int CleanedCount,
    int PreservedUniqueCount,
    int KeptWithDataCount,
    ImmutableList<FolderCleanupAction> Actions
);

public record ExecutionStatus(
    bool IsRunning,
    bool IsSimulation,
    bool IsCancellationRequested,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    double ProgressPercent,
    long TransferredBytes,
    long TotalBytes,
    long TransferredFiles,
    long TotalFiles,
    double ThroughputBps,
    int ActiveWorkers,
    string? Error,
    ImmutableList<VolumeExecutionProgress> Volumes,
    ImmutableList<ActiveTransferInfo> ActiveTransfers,
    ExecutionPhase Phase = ExecutionPhase.Idle,
    FolderCleanupSummary? FolderCleanup = null
);

