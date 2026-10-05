using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StorageBalancer.App.Configuration;

public class VolumeConfig
{
    public string Id { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
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

public class FilePlacementRuleConfig
{
    public string Id { get; set; } = string.Empty;
    public string FullRelativePath { get; set; } = string.Empty;
    public int StartingDepth { get; set; } = 1;
    public List<string> AllowedVolumeIds { get; set; } = new();

    [JsonPropertyName("AllowedDiskIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? LegacyAllowedDiskIds { get; set; }
}

public class AppConfig
{
    public bool DrivePoolMode { get; set; }
    public string? SnapshotsFolder { get; set; }
    public List<PhysicalDiskConfig> Disks { get; set; } = new();
    public List<FilePlacementRuleConfig> FilePlacementRules { get; set; } = new();
    public List<string> ExcludedPathPatterns { get; set; } = new();
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
        var config = JsonSerializer.Deserialize<AppConfig>(json, _options) ?? new AppConfig();
        config.FilePlacementRules ??= new List<FilePlacementRuleConfig>();

        foreach (var rule in config.FilePlacementRules)
        {
            rule.AllowedVolumeIds ??= new List<string>();
            if (rule.AllowedVolumeIds.Count > 0 || rule.LegacyAllowedDiskIds is not { Count: > 0 })
                continue;

            var legacyDiskIds = new HashSet<string>(rule.LegacyAllowedDiskIds, StringComparer.OrdinalIgnoreCase);
            rule.AllowedVolumeIds = config.Disks
                .Where(disk => legacyDiskIds.Contains(disk.Id))
                .SelectMany(disk => disk.Volumes)
                .Select(volume => volume.Id)
                .ToList();
            rule.LegacyAllowedDiskIds = null;
        }

        return config;
    }

    public void Save(AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        var json = JsonSerializer.Serialize(config, _options);
        File.WriteAllText(_configPath, json);
    }
}