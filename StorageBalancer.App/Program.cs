using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.RegularExpressions;
using StorageBalancer.App.Subsystems.Storage;
using StorageBalancer.App.Configuration;
using StorageBalancer.App.Subsystems.Scanner;
using StorageBalancer.App.Subsystems.Planning;

var builder = WebApplication.CreateBuilder(args);

// Register our services as Singletons (they live for the lifetime of the app)
builder.Services.AddSingleton<ConfigManager>();
builder.Services.AddSingleton<JsonStateRepository>();
builder.Services.AddSingleton<StateScanner>();
builder.Services.AddSingleton<PlacementPlanner>();

// Add support for serving a Web UI (HTML/JS)
builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles(); // Allows serving index.html from wwwroot folder

// --- API ENDPOINTS FOR THE WEB UI ---

// GET: /api/config -> Returns the current configuration
app.MapGet("/api/config", (ConfigManager configManager) =>
{
    return configManager.Load();
});

// POST: /api/config -> Saves configuration from the UI
app.MapPost("/api/config", (AppConfig newConfig, ConfigManager configManager) =>
{
    if (string.IsNullOrWhiteSpace(newConfig.SnapshotsFolder))
        return Results.BadRequest(new { Error = "Choose a snapshots folder." });

    if (newConfig.Disks is null || newConfig.Disks.Any(disk =>
        string.IsNullOrWhiteSpace(disk.Id) ||
        string.IsNullOrWhiteSpace(disk.HardwareName) ||
        disk.Volumes is null ||
        disk.Volumes.Count == 0 ||
        disk.Volumes.Any(volume => string.IsNullOrWhiteSpace(volume.Id) ||
            string.IsNullOrWhiteSpace(volume.MountPoint) || volume.Capacity < 0 ||
            volume.Alias is null || volume.Alias.Length > 64 ||
            volume.RootFolderRelativePath is null ||
            Path.IsPathRooted(volume.RootFolderRelativePath) ||
            volume.RootFolderRelativePath.Split('\\', '/').Any(part => part == ".."))))
    {
        return Results.BadRequest(new { Error = "Each disk needs a unique ID, a name, and at least one valid volume." });
    }

    if (newConfig.FilePlacementRules is null || newConfig.FilePlacementRules.Any(rule =>
        string.IsNullOrWhiteSpace(rule.Id) ||
        rule.FullRelativePath is null ||
        rule.StartingDepth < 1 ||
        rule.AllowedVolumeIds is null ||
        rule.AllowedVolumeIds.Count == 0 ||
        rule.FullRelativePath.Replace('/', '\\').StartsWith('\\') ||
        rule.FullRelativePath.Split('\\', '/').Any(part => part == "..")))
    {
        return Results.BadRequest(new { Error = "Each placement rule needs an ID, a relative path, a starting depth of at least 1, and one or more allowed volumes." });
    }

    if (newConfig.ExcludedPathPatterns is null || newConfig.ExcludedPathPatterns.Count > 100 ||
        newConfig.ExcludedPathPatterns.Any(pattern => string.IsNullOrWhiteSpace(pattern) || pattern.Length > 512))
    {
        return Results.BadRequest(new { Error = "Never-move patterns must be non-empty and no longer than 512 characters; at most 100 are allowed." });
    }

    try
    {
        foreach (var pattern in newConfig.ExcludedPathPatterns)
            _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { Error = $"Invalid never-move pattern: {exception.Message}" });
    }

    var diskIds = newConfig.Disks.Select(disk => disk.Id).ToArray();
    var volumeIds = newConfig.Disks.SelectMany(disk => disk.Volumes).Select(volume => volume.Id).ToArray();
    var volumeAliases = newConfig.Disks
        .SelectMany((disk, diskIndex) => disk.Volumes.Select((volume, volumeIndex) =>
            string.IsNullOrWhiteSpace(volume.Alias) ? $"D{diskIndex + 1}-V{volumeIndex + 1}" : volume.Alias.Trim()))
        .ToArray();
    var ruleIds = newConfig.FilePlacementRules.Select(rule => rule.Id);
    var configuredVolumeIds = new HashSet<string>(volumeIds, StringComparer.OrdinalIgnoreCase);
    if (diskIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != newConfig.Disks.Count ||
        volumeIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != volumeIds.Length ||
        volumeAliases.Distinct(StringComparer.OrdinalIgnoreCase).Count() != volumeAliases.Length ||
        ruleIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != newConfig.FilePlacementRules.Count ||
        newConfig.FilePlacementRules.Any(rule => rule.AllowedVolumeIds.Any(id => !configuredVolumeIds.Contains(id))))
    {
        return Results.BadRequest(new { Error = "Disk, volume, and rule IDs must be unique, and every allowed volume must be configured." });
    }

    configManager.Save(newConfig);
    return Results.Ok();
});

// POST: /api/scan -> Starts a multithreaded scan; progress is available from /api/scan/status
app.MapPost("/api/scan", (ScanStartRequest? request, ConfigManager configManager, StateScanner scanner, IWebHostEnvironment environment) =>
{
    var config = configManager.Load();

    if (string.IsNullOrWhiteSpace(config.SnapshotsFolder))
        return Results.BadRequest(new { Error = "Snapshots folder is not configured. Set it on the Configuration page before starting a scan." });

    if (config.Disks.Count == 0)
        return Results.BadRequest(new { Error = "Add at least one physical disk before scanning." });

    var snapshotName = string.IsNullOrWhiteSpace(request?.SnapshotName)
        ? DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")
        : request.SnapshotName.Trim();
    if (snapshotName.Length > 120 || snapshotName is "." or ".." || snapshotName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        return Results.BadRequest(new { Error = "Use a snapshot name of 120 characters or fewer without filename-invalid characters." });

    string snapshotPath;
    try
    {
        var configuredFolder = config.SnapshotsFolder.Trim();
        var snapshotFolder = Path.IsPathRooted(configuredFolder)
            ? configuredFolder
            : Path.Combine(environment.ContentRootPath, configuredFolder);
        snapshotPath = Path.Combine(Path.GetFullPath(snapshotFolder), $"{snapshotName}.json");
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
    {
        return Results.BadRequest(new { Error = "The configured snapshots folder is not a valid path." });
    }

    if (File.Exists(snapshotPath))
        return Results.Conflict(new { Error = $"A snapshot named '{snapshotName}' already exists." });

    if (!scanner.TryStartScan(config, snapshotName, snapshotPath))
        return Results.Conflict(new { Error = "A scan is already running." });

    return Results.Accepted("/api/scan/status", scanner.GetStatus());
});

app.MapGet("/api/scan/status", (StateScanner scanner) => scanner.GetStatus());

app.MapPost("/api/scan/cancel", (StateScanner scanner) =>
{
    if (!scanner.TryCancelScan())
        return Results.Conflict(new { Error = "There is no active scan to cancel." });

    return Results.Accepted("/api/scan/status", scanner.GetStatus());
});

app.MapGet("/api/snapshots", (ConfigManager configManager, IWebHostEnvironment environment) =>
{
    var config = configManager.Load();
    if (string.IsNullOrWhiteSpace(config.SnapshotsFolder))
        return Results.Ok(Array.Empty<SnapshotListEntry>());

    string snapshotsFolder;
    try
    {
        snapshotsFolder = ResolveSnapshotsFolder(config, environment);
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
    {
        return Results.BadRequest(new { Error = "The configured snapshots folder is not a valid path." });
    }

    if (!Directory.Exists(snapshotsFolder))
        return Results.Ok(Array.Empty<SnapshotListEntry>());

    var snapshots = Directory.EnumerateFiles(snapshotsFolder, "*.json", SearchOption.TopDirectoryOnly)
        .Select(path => new SnapshotListEntry(
            Path.GetFileNameWithoutExtension(path),
            File.GetLastWriteTimeUtc(path),
            new FileInfo(path).Length))
        .OrderByDescending(snapshot => snapshot.LastModifiedUtc)
        .ToArray();
    return Results.Ok(snapshots);
});

app.MapPost("/api/plan", (PlanRequest? request, ConfigManager configManager, JsonStateRepository repository, PlacementPlanner planner, IWebHostEnvironment environment) =>
{
    if (string.IsNullOrWhiteSpace(request?.SnapshotName))
        return Results.BadRequest(new { Error = "Choose a snapshot." });

    var snapshotName = request.SnapshotName.Trim();
    if (snapshotName.Length > 120 ||
        snapshotName is "." or ".." ||
        snapshotName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
        snapshotName.Contains('/') ||
        snapshotName.Contains('\\') ||
        !string.Equals(Path.GetFileName(snapshotName), snapshotName, StringComparison.Ordinal))
    {
        return Results.BadRequest(new { Error = "Choose a valid snapshot name." });
    }

    var config = configManager.Load();
    if (string.IsNullOrWhiteSpace(config.SnapshotsFolder))
        return Results.BadRequest(new { Error = "Configure a snapshots folder before generating a plan." });

    try
    {
        var snapshotPath = Path.Combine(ResolveSnapshotsFolder(config, environment), $"{snapshotName}.json");
        var snapshot = repository.LoadSnapshot(snapshotPath);
        if (snapshot is null)
            return Results.NotFound(new { Error = "The selected snapshot was not found." });

        return Results.Ok(planner.CreatePlan(snapshot, config.FilePlacementRules, config.ExcludedPathPatterns));
    }
    catch (InvalidDataException exception)
    {
        return Results.BadRequest(new { Error = exception.Message });
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { Error = "The selected file is not a valid snapshot." });
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException)
    {
        return Results.BadRequest(new { Error = $"Could not read the selected snapshot: {exception.Message}" });
    }
});

static string ResolveSnapshotsFolder(AppConfig config, IWebHostEnvironment environment)
{
    var configuredFolder = config.SnapshotsFolder!.Trim();
    var snapshotsFolder = Path.IsPathRooted(configuredFolder)
        ? configuredFolder
        : Path.Combine(environment.ContentRootPath, configuredFolder);
    return Path.GetFullPath(snapshotsFolder);
}

// Default to port 5000 while allowing standard ASP.NET Core URL overrides.
app.Run(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:5000");

public record ScanStartRequest(string? SnapshotName);
public record PlanRequest(string? SnapshotName);
public record SnapshotListEntry(string Name, DateTime LastModifiedUtc, long Length);