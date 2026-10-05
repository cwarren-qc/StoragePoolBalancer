using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace StorageBalancer.App.Configuration;

public class VolumeConfig
{
    public string Id { get; set; } = string.Empty;
    public string MountPoint { get; set; } = string.Empty;
    public long Capacity { get; set; }
    public string RootFolderRelativePath { get; set; } = string.Empty;
}

public class PhysicalDiskConfig
{
    public string Id { get; set; } = string.Empty;
    public string HardwareName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<VolumeConfig> Volumes { get; set; } = new();
}

public class AppConfig
{
    public bool DrivePoolMode { get; set; }
    public string? SnapshotsFolder { get; set; }
    public List<PhysicalDiskConfig> Disks { get; set; } = new();
    // We will add rules here later
}

public class ConfigManager
{
    private readonly string _configPath;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    public ConfigManager()
    {
        var configuredFolder = Environment.GetEnvironmentVariable("CONFIG_FOLDER");
        _configPath = string.IsNullOrWhiteSpace(configuredFolder)
            ? Path.GetFullPath("config.json")
            : Path.Combine(Path.GetFullPath(configuredFolder), "config.json");
    }

    public AppConfig Load()
    {
        if (!File.Exists(_configPath))
            return new AppConfig(); // Return default empty config

        var json = File.ReadAllText(_configPath);
        return JsonSerializer.Deserialize<AppConfig>(json, _options) ?? new AppConfig();
    }

    public void Save(AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        var json = JsonSerializer.Serialize(config, _options);
        File.WriteAllText(_configPath, json);
    }
}