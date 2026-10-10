using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StorageBalancer.App.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FolderCleanupStatus
{
    PreservingUnique,
    KeepingWithData,
    Cleaning
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProcessPipelinePhase
{
    Idle,
    Scanning,
    Planning,
    Executing,
    Completed,
    Cancelled,
    Failed
}

public record ProcessStartRequest(
    int? MaxFilesToCopy = null
);

public record ProcessPipelineStatus(
    bool IsRunning,
    bool IsCancellationRequested,
    int CurrentStep,
    ProcessPipelinePhase Phase,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? Error,
    int? MaxFilesToCopy,
    StorageBalancer.App.Subsystems.Scanner.ScanStatus? ScanStatus,
    ExecutionStatus? ExecutionStatus,
    bool HasPlan
);

[JsonConverter(typeof(JsonStringEnumConverter))]
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

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VolumeActivityStatus
{
    Idle,
    Reading,
    Writing,
    CleaningFolders,
    Complete
}

public record ExecutionStartRequest(
    string? SnapshotName,
    bool IsSimulation = true,
    int SimulationDurationSeconds = 120,
    int? MaxThreads = null,
    bool? VerifyCopies = null,
    int? MaxFilesToCopy = 10
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

public record TransferErrorItem(
    string SourceVolume,
    string TargetVolume,
    string RelativePath,
    string FileName,
    long SizeOnDisk,
    string ErrorMessage,
    DateTime TimestampUtc
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
    FolderCleanupSummary? FolderCleanup = null,
    ImmutableList<TransferErrorItem>? TransferErrors = null,
    string? SnapshotName = null
);

public record TransferredFileRecord(
    DateTime TimestampUtc,
    string SourceVolume,
    string TargetVolume,
    string RelativePath,
    string FileName,
    long SizeOnDisk,
    TimeSpan Duration
);

public record CleanedFolderRecord(
    DateTime TimestampUtc,
    string VolumeAlias,
    string RelativePath,
    string PrimaryVolumeAlias
);

