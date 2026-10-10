using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using StorageBalancer.App.Subsystems.Storage;
using StorageBalancer.App.Configuration;
using StorageBalancer.App.Subsystems.Scanner;
using StorageBalancer.App.Subsystems.Planning;
using StorageBalancer.App.Subsystems.Execution;
using StorageBalancer.App.Subsystems.Logging;
using StorageBalancer.App.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Collections.Immutable;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IAppEventLogger, AppEventLogger>();
builder.Services.AddSingleton<ConfigManager>();
builder.Services.AddSingleton<JsonStateRepository>();
builder.Services.AddSingleton<StateScanner>();
builder.Services.AddSingleton<PlacementPlanner>();
builder.Services.AddSingleton<PlanExecutor>();
builder.Services.AddSingleton<ProcessCoordinator>();

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
        (!rule.DeferToFiller && (rule.AllowedVolumeAliases is null || rule.AllowedVolumeAliases.Count == 0)) ||
        rule.FullRelativePath.Replace('/', '\\').StartsWith('\\') ||
        rule.FullRelativePath.Split('\\', '/').Any(part => part == "..")))
    {
        return Results.BadRequest(new { Error = "Each placement rule needs a relative path, a starting depth of at least 1, and one or more allowed volume aliases (unless Defer to filler)." });
    }

    var aliases = newConfig.Volumes.Select(v => v.Alias.Trim()).ToArray();
    var configuredAliases = new HashSet<string>(aliases, StringComparer.OrdinalIgnoreCase);

    if (aliases.Distinct(StringComparer.OrdinalIgnoreCase).Count() != aliases.Length ||
        newConfig.FilePlacementRules.Any(rule => rule.AllowedVolumeAliases.Any(a => a != "*" && !configuredAliases.Contains(a))))
    {
        return Results.BadRequest(new { Error = "Volume Aliases must be unique, and every allowed volume must be configured." });
    }

    if (newConfig.Duplicates?.AllowedVolumeAliases != null)
        newConfig.Duplicates.AllowedVolumeAliases.RemoveAll(a => a != "*" && !configuredAliases.Contains(a));
    if (newConfig.Filler?.AllowedVolumeAliases != null)
        newConfig.Filler.AllowedVolumeAliases.RemoveAll(a => a != "*" && !configuredAliases.Contains(a));
    if (newConfig.Unmatched?.AllowedVolumeAliases != null)
        newConfig.Unmatched.AllowedVolumeAliases.RemoveAll(a => a != "*" && !configuredAliases.Contains(a));

    configManager.Save(newConfig);
    return Results.Ok();
});

app.MapPost("/api/scan", (ScanStartRequest? request, ConfigManager configManager, StateScanner scanner, IWebHostEnvironment environment) =>
{
    var config = configManager.Load();

    if (config.Volumes.Count == 0)
        return Results.BadRequest(new { Error = "Add at least one volume before scanning." });

    bool saveToDisk = request?.SaveToDisk ?? (!string.IsNullOrWhiteSpace(request?.SnapshotName) && !string.Equals(request.SnapshotName, "in-memory", StringComparison.OrdinalIgnoreCase));

    string snapshotName = string.IsNullOrWhiteSpace(request?.SnapshotName) || string.Equals(request.SnapshotName, "in-memory", StringComparison.OrdinalIgnoreCase)
        ? "In-Memory Scan"
        : request.SnapshotName.Trim();

    string? snapshotPath = null;
    if (saveToDisk)
    {
        if (string.IsNullOrWhiteSpace(config.SnapshotsFolder))
            return Results.BadRequest(new { Error = "Snapshots folder is not configured." });

        if (snapshotName.Length > 120 || snapshotName is "." or ".." || snapshotName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Results.BadRequest(new { Error = "Use a snapshot name of 120 characters or fewer without filename-invalid characters." });

        try
        {
            var snapshotFolder = ResolveSnapshotsFolder(config, environment);
            var baseName = GetSnapshotBaseName(snapshotName);

            if (ResolveSnapshotFilePath(snapshotFolder, baseName) != null)
                return Results.Conflict(new { Error = $"A snapshot named '{snapshotName}' already exists." });

            snapshotPath = Path.Combine(snapshotFolder, $"{baseName}.snapshot");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Results.BadRequest(new { Error = "The configured snapshots folder is not a valid path." });
        }
    }

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

    var snapshots = Directory.EnumerateFiles(snapshotsFolder, "*", SearchOption.TopDirectoryOnly)
        .Where(path =>
            path.EndsWith(".snapshot.gz", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".snapshot", StringComparison.OrdinalIgnoreCase))
        .Select(path =>
        {
            var fileName = Path.GetFileName(path);
            var baseName = GetSnapshotBaseName(fileName);
            var fi = new FileInfo(path);
            return new
            {
                Path = path,
                BaseName = baseName,
                LastModifiedUtc = fi.LastWriteTimeUtc,
                Length = fi.Length
            };
        })
        .GroupBy(x => x.BaseName, StringComparer.OrdinalIgnoreCase)
        .Select(g =>
        {
            var preferred = g
                .OrderByDescending(x => x.Path.EndsWith(".snapshot", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => x.Path.EndsWith(".snapshot.gz", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => x.LastModifiedUtc)
                .First();
            return new SnapshotListEntry(preferred.BaseName, preferred.LastModifiedUtc, preferred.Length);
        })
        .OrderByDescending(snapshot => snapshot.LastModifiedUtc)
        .ToArray();

    return Results.Ok(snapshots);
});

app.MapPost("/api/plan", (PlanRequest? request, ConfigManager configManager, JsonStateRepository repository, PlacementPlanner planner, PlanExecutor executor, StateScanner scanner, ProcessCoordinator coordinator, IWebHostEnvironment environment, IAppEventLogger logger) =>
{
    var config = configManager.Load();

    bool isInMemory = string.IsNullOrWhiteSpace(request?.SnapshotName) ||
                      string.Equals(request.SnapshotName, "in-memory", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(request.SnapshotName, "In-Memory Scan", StringComparison.OrdinalIgnoreCase);

    PoolSnapshot? snapshot;
    string snapshotName;

    if (isInMemory)
    {
        snapshot = scanner.GetLastSnapshot();
        if (snapshot is null)
            return Results.BadRequest(new { Error = "No in-memory scan available. Run a state scan first." });

        snapshotName = "In-Memory Scan";

        var existingPlan = coordinator.GetCurrentPlan();
        if (existingPlan != null && existingPlan.SnapshotScannedAt == snapshot.ScannedAt)
        {
            if (request?.IncludeFiles == true)
            {
                return Results.Ok(existingPlan);
            }

            var stripped = existingPlan.Placements
                .Select(p => p with { Files = null })
                .ToImmutableArray();

            return Results.Ok(existingPlan with { Placements = stripped });
        }
    }
    else
    {
        snapshotName = request!.SnapshotName!.Trim();
        if (snapshotName.Length > 120 ||
            snapshotName is "." or ".." ||
            snapshotName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            snapshotName.Contains('/') ||
            snapshotName.Contains('\\') ||
            !string.Equals(Path.GetFileName(snapshotName), snapshotName, StringComparison.Ordinal))
        {
            return Results.BadRequest(new { Error = "Choose a valid snapshot name." });
        }

        if (string.IsNullOrWhiteSpace(config.SnapshotsFolder))
            return Results.BadRequest(new { Error = "Configure a snapshots folder before generating a plan." });

        try
        {
            var snapshotFolder = ResolveSnapshotsFolder(config, environment);
            var snapshotPath = ResolveSnapshotFilePath(snapshotFolder, snapshotName);
            if (snapshotPath is null)
                return Results.NotFound(new { Error = "The selected snapshot was not found." });

            snapshot = repository.LoadSnapshot(snapshotPath);
            if (snapshot is null)
                return Results.NotFound(new { Error = "The selected snapshot was not found." });
        }
        catch (InvalidDataException exception)
        {
            return Results.BadRequest(new { Error = $"Corrupted or invalid snapshot file: {exception.Message}" });
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { Error = "The selected file is not a valid snapshot." });
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return Results.BadRequest(new { Error = $"Could not read the selected snapshot: {exception.Message}" });
        }
    }

    try
    {
        logger.LogInfo("Balancing", $"Generating placement plan for snapshot '{snapshotName}'...");
        var fullPlan = planner.CreatePlan(
            snapshot,
            config.FilePlacementRules,
            config.Duplicates,
            config.Filler,
            config.Unmatched,
            includeFiles: true);

        executor.CachePlan(snapshotName, snapshot.ScannedAt, config, fullPlan);

        long totalMoveBytes = fullPlan.Placements.Sum(p => p.SizeMoved);
        long totalFilesMoved = fullPlan.Volumes.Sum(v => v.FilesMovedIn);
        logger.LogInfo("Balancing", $"Generated plan for snapshot '{snapshotName}'. Total placements: {fullPlan.Placements.Length}, files moving: {totalFilesMoved:N0}, data moving: {totalMoveBytes:N0} bytes.");

        if (request?.IncludeFiles == true)
        {
            return Results.Ok(fullPlan);
        }

        var strippedPlacements = fullPlan.Placements
            .Select(p => p with { Files = null })
            .ToImmutableArray();

        return Results.Ok(fullPlan with { Placements = strippedPlacements });
    }
    catch (Exception exception)
    {
        logger.LogError("Balancing", $"Failed to generate plan for snapshot '{snapshotName}': {exception.Message}", exception);
        return Results.BadRequest(new { Error = $"Failed to generate plan: {exception.Message}" });
    }
});

app.MapPost("/api/execution/start", (ExecutionStartRequest? request, ConfigManager configManager, JsonStateRepository repository, PlanExecutor executor, StateScanner scanner, IWebHostEnvironment environment) =>
{
    var config = configManager.Load();

    bool isInMemory = string.IsNullOrWhiteSpace(request?.SnapshotName) ||
                      string.Equals(request.SnapshotName, "in-memory", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(request.SnapshotName, "In-Memory Scan", StringComparison.OrdinalIgnoreCase);

    PoolSnapshot? snapshot;
    string snapshotName;

    if (isInMemory)
    {
        snapshot = scanner.GetLastSnapshot();
        if (snapshot is null)
            return Results.BadRequest(new { Error = "No in-memory scan available. Run a state scan first." });

        snapshotName = "In-Memory Scan";
    }
    else
    {
        snapshotName = request!.SnapshotName!.Trim();
        if (string.IsNullOrWhiteSpace(config.SnapshotsFolder))
            return Results.BadRequest(new { Error = "Configure a snapshots folder before executing." });

        string? snapshotPath;
        try
        {
            var snapshotFolder = ResolveSnapshotsFolder(config, environment);
            snapshotPath = ResolveSnapshotFilePath(snapshotFolder, snapshotName);
            if (snapshotPath is null)
                return Results.NotFound(new { Error = "The selected snapshot was not found." });
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { Error = $"Invalid snapshot path: {ex.Message}" });
        }

        try
        {
            snapshot = repository.LoadSnapshot(snapshotPath);
            if (snapshot is null)
                return Results.NotFound(new { Error = "The selected snapshot was not found." });
        }
        catch (InvalidDataException ex)
        {
            return Results.BadRequest(new { Error = $"Corrupted or invalid snapshot file: {ex.Message}" });
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { Error = "The selected file is not a valid snapshot." });
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { Error = $"Could not read the selected snapshot: {ex.Message}" });
        }
    }

    var effectiveRequest = (request is not null && isInMemory)
        ? request with { SnapshotName = snapshotName }
        : request ?? new ExecutionStartRequest(snapshotName);

    string? snapshotsFolder = null;
    if (!string.IsNullOrWhiteSpace(config.SnapshotsFolder))
    {
        try { snapshotsFolder = ResolveSnapshotsFolder(config, environment); } catch { }
    }

    if (!executor.TryStartExecution(config, snapshot, effectiveRequest, snapshotsFolder))
        return Results.Conflict(new { Error = "An execution or simulation is already running." });

    return Results.Accepted("/api/execution/status", executor.GetStatus());
});

app.MapGet("/api/execution/status", (PlanExecutor executor) => executor.GetStatus());

app.MapGet("/api/plan/folder-cleanup", (PlanExecutor executor, string? status, string? search, int? limit, int? offset) =>
{
    var (total, items) = executor.QueryFolderCleanup(status, search, limit ?? 500, offset ?? 0);
    return Results.Ok(new { Total = total, Items = items });
});

app.MapGet("/api/execution/folder-cleanup", (PlanExecutor executor, string? status, string? search, int? limit, int? offset) =>
{
    var (total, items) = executor.QueryFolderCleanup(status, search, limit ?? 500, offset ?? 0);
    return Results.Ok(new { Total = total, Items = items });
});

app.MapPost("/api/execution/cancel", (PlanExecutor executor) =>
{
    if (!executor.TryCancel())
        return Results.Conflict(new { Error = "There is no active execution to cancel." });

    return Results.Accepted("/api/execution/status", executor.GetStatus());
});

app.MapPost("/api/process/start", (ProcessStartRequest? request, ProcessCoordinator coordinator) =>
{
    if (!coordinator.TryStartProcess(request?.MaxFilesToCopy))
        return Results.Conflict(new { Error = "A process, scan, or execution is already running." });

    return Results.Accepted("/api/process/status", coordinator.GetStatus());
});

app.MapGet("/api/process/status", (ProcessCoordinator coordinator) => coordinator.GetStatus());

app.MapPost("/api/process/cancel", (ProcessCoordinator coordinator) =>
{
    if (!coordinator.TryCancelProcess())
        return Results.Conflict(new { Error = "There is no active process to cancel." });

    return Results.Accepted("/api/process/status", coordinator.GetStatus());
});

app.MapGet("/api/process/plan", (ProcessCoordinator coordinator, bool? includeFiles) =>
{
    var plan = coordinator.GetCurrentPlan();
    if (plan == null)
        return Results.NotFound(new { Error = "No plan has been generated yet." });

    if (includeFiles == true)
        return Results.Ok(plan);

    var strippedPlacements = plan.Placements
        .Select(p => p with { Files = null })
        .ToImmutableArray();

    return Results.Ok(plan with { Placements = strippedPlacements });
});

static string ResolveSnapshotsFolder(AppConfig config, IWebHostEnvironment environment)
{
    var configuredFolder = config.SnapshotsFolder!.Trim();
    var snapshotsFolder = Path.IsPathRooted(configuredFolder)
        ? configuredFolder
        : Path.Combine(environment.ContentRootPath, configuredFolder);
    return Path.GetFullPath(snapshotsFolder);
}

static string GetSnapshotBaseName(string fileName)
{
    var name = Path.GetFileName(fileName);
    if (name.EndsWith(".snapshot.gz", StringComparison.OrdinalIgnoreCase))
        return name[..^".snapshot.gz".Length];
    if (name.EndsWith(".snapshot", StringComparison.OrdinalIgnoreCase))
        return name[..^".snapshot".Length];
    return Path.GetFileNameWithoutExtension(name);
}

static string? ResolveSnapshotFilePath(string snapshotsFolder, string snapshotName)
{
    if (string.IsNullOrWhiteSpace(snapshotName) || !Directory.Exists(snapshotsFolder))
        return null;

    var trimmed = snapshotName.Trim();

    // 1. Direct file match if full filename was provided
    var directPath = Path.Combine(snapshotsFolder, trimmed);
    if (File.Exists(directPath))
        return directPath;

    // 2. Candidate extensions in priority order (.snapshot -> .snapshot.gz)
    string[] candidateExtensions = [".snapshot", ".snapshot.gz"];
    foreach (var ext in candidateExtensions)
    {
        var candidate = Path.Combine(snapshotsFolder, $"{trimmed}{ext}");
        if (File.Exists(candidate))
            return candidate;
    }

    // 3. Fallback: normalize base name if user provided an extension and try candidates
    var baseName = GetSnapshotBaseName(trimmed);
    foreach (var ext in candidateExtensions)
    {
        var candidate = Path.Combine(snapshotsFolder, $"{baseName}{ext}");
        if (File.Exists(candidate))
            return candidate;
    }

    return null;
}

app.Run(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:5000");

public record ScanStartRequest(string? SnapshotName = null, bool? SaveToDisk = null);
public record PlanRequest(string? SnapshotName = null, bool IncludeFiles = false);
public record SnapshotListEntry(string Name, DateTime LastModifiedUtc, long Length);