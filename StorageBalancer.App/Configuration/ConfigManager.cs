using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace StorageBalancer.App.Configuration;

public class AppConfig
{
    public bool DrivePoolMode { get; set; }
    public string? SnapshotsFolder { get; set; }
    public List<VolumeConfig> Volumes { get; set; } = new();
    public List<FilePlacementRuleConfig> FilePlacementRules { get; set; } = new();
    public SpecialRuleConfig Duplicates { get; set; } = new();
    public SpecialRuleConfig Filler { get; set; } = new();
    public SpecialRuleConfig Unmatched { get; set; } = new();
    public int MaxExecutionThreads { get; set; } = 2;
    public bool VerifyCopies { get; set; } = true;
    public int DefaultSimulationDurationSeconds { get; set; } = 120;
}

public class VolumeConfig
{
    public string Alias { get; set; } = string.Empty;
    public string MountPoint { get; set; } = string.Empty;
    public string? RootFolderRelativePath { get; set; } = string.Empty;
    public long Capacity { get; set; }
    public string? Disk { get; set; }
}

public class FilePlacementRuleConfig
{
    public string FullRelativePath { get; set; } = string.Empty;
    public int StartingDepth { get; set; } = 1;
    public bool DeferToFiller { get; set; }
    public List<string> AllowedVolumeAliases { get; set; } = new();
}

public class SpecialRuleConfig
{
    public bool Consolidate { get; set; }
    public List<string> AllowedVolumeAliases { get; set; } = new();
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
            return new AppConfig();

        var json = File.ReadAllText(_configPath);
        var config = JsonSerializer.Deserialize<AppConfig>(json, _options) ?? new AppConfig();

        config.Volumes ??= new List<VolumeConfig>();
        config.FilePlacementRules ??= new List<FilePlacementRuleConfig>();
        config.Duplicates ??= new SpecialRuleConfig();
        config.Duplicates.AllowedVolumeAliases ??= new List<string>();
        config.Filler ??= new SpecialRuleConfig();
        config.Filler.AllowedVolumeAliases ??= new List<string>();
        config.Unmatched ??= new SpecialRuleConfig();
        config.Unmatched.AllowedVolumeAliases ??= new List<string>();

        foreach (var rule in config.FilePlacementRules)
        {
            rule.AllowedVolumeAliases ??= new List<string>();
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