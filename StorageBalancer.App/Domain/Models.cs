using System;
using System.Collections.Immutable;

namespace StorageBalancer.App.Domain;

// The base abstraction for any file system item
public abstract record FileSystemNode(string Name, string RelativePath, long Size, long SizeOnDisk);

// Represents a single file
public record FileNode(
    string Name,
    string RelativePath,
    long Size,
    long SizeOnDisk
) : FileSystemNode(Name, RelativePath, Size, SizeOnDisk);

// Represents a folder, containing an immutable list of children (Files and sub-folders)
public record FolderNode(
    string Name,
    string RelativePath,
    long Size,
    long SizeOnDisk,
    ImmutableList<FileSystemNode> Children
) : FileSystemNode(Name, RelativePath, Size, SizeOnDisk);

// Represents a logical partition/volume
public record Volume(
    string Id,
    string MountPoint,
    long Capacity,
    FolderNode RootFolder
);

// Represents the actual hardware
public record PhysicalDisk(
    string Id,
    string HardwareName,
    ImmutableList<Volume> Volumes
);

// The immutable snapshot of the entire system
public record PoolSnapshot(
    DateTime ScannedAt,
    ImmutableList<PhysicalDisk> Disks
);