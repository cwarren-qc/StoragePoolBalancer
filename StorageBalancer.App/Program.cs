using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using StorageBalancer.App.Subsystems.Storage;
using StorageBalancer.App.Configuration;
using StorageBalancer.App.Subsystems.Scanner;
using StorageBalancer.App.Subsystems.Planning;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ConfigManager>();
builder.Services.AddSingleton<JsonStateRepository>();
builder.Services.AddSingleton<StateScanner>();
builder.Services.AddSingleton<PlacementPlanner>();

builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/config", (ConfigManager configManager) =>
{
    return configManager.Load();
});

app.MapPost("/api/config", (AppConfig newConfig, ConfigManager configManager) =>
{
    if (string.IsNullOrWhiteSpace(newConfig.SnapshotsFolder))
        return Results.BadRequest(new { Error = "Choose a snapshots folder." });

    if (newConfig.Volumes is null || newConfig.Volumes.Count == 0 || newConfig.Volumes.Any(volume =>
        string.IsNullOrWhiteSpace(volume.Alias) ||
        string.IsNullOrWhiteSpace(volume.MountPoint) || volume.Capacity < 0 ||
        volume.Alias.Length > 64 ||
        volume.RootFolderRelativePath is null ||
        Path.IsPathRooted(volume.RootFolderRelativePath) ||
        volume.RootFolderRelativePath.Split('\\', '/').Any(part => part == "..")))
    {
        return Results.BadRequest(new { Error = "Each volume needs a unique Alias, a Mount Point, and a valid capacity." });
    }

    if (newConfig.FilePlacementRules is null || newConfig.FilePlacementRules.Any(rule =>
        rule.FullRelativePath is null ||
        rule.StartingDepth < 1 ||
        (!rule.DeferPlacement && (rule.AllowedVolumeAliases is null || rule.AllowedVolumeAliases.Count == 0)) ||
        rule.FullRelativePath.Replace('/', '\\').StartsWith('\\') ||
        rule.FullRelativePath.Split('\\', '/').Any(part => part == "..")))
    {
        return Results.BadRequest(new { Error = "Each placement rule needs a relative path, a starting depth of at least 1, and one or more allowed volume aliases (unless Deferred)." });
    }

    var aliases = newConfig.Volumes.Select(v => v.Alias.Trim()).ToArray();
    var configuredAliases = new HashSet<string>(aliases, StringComparer.OrdinalIgnoreCase);

    if (aliases.Distinct(StringComparer.OrdinalIgnoreCase).Count() != aliases.Length ||
        newConfig.FilePlacementRules.Any(rule => rule.AllowedVolumeAliases.Any(a => !configuredAliases.Contains(a))))
    {
        return Results.BadRequest(new { Error = "Volume Aliases must be unique, and every allowed volume must be configured." });
    }

    configManager.Save(newConfig);
    return Results.Ok();
});

app.MapPost("/api/scan", (ScanStartRequest? request, ConfigManager configManager, StateScanner scanner, IWebHostEnvironment environment) =>
{
    var config = configManager.Load();

    if (string.IsNullOrWhiteSpace(config.SnapshotsFolder))
        return Results.BadRequest(new { Error = "Snapshots folder is not configured." });

    if (config.Volumes.Count == 0)
        return Results.BadRequest(new { Error = "Add at least one volume before scanning." });

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

        return Results.Ok(planner.CreatePlan(snapshot, config.FilePlacementRules, request?.IncludeFiles ?? false));
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

app.Run(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:5000");

public record ScanStartRequest(string? SnapshotName);
public record PlanRequest(string? SnapshotName, bool IncludeFiles = false);
public record SnapshotListEntry(string Name, DateTime LastModifiedUtc, long Length);