using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace StorageBalancer.App.Domain;

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
    string Status,
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
    string Status, // "Cleaned", "PreservedUnique", "KeptWithData"
    string Reason
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
    string Phase = "Idle",
    FolderCleanupSummary? FolderCleanup = null
);

