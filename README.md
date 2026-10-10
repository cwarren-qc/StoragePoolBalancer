# StoragePoolBalancer

Storage Pool Balancer scans configured physical disks and reports live inventory progress. It does not move or modify files.

## Run locally

Install the .NET 8 SDK, then run from the repository root:

```powershell
dotnet run --project .\StorageBalancer.App\StorageBalancer.App.csproj
```

Open [http://localhost:5000](http://localhost:5000) for the pool summary. Use Configuration to edit physical disks, volumes, DrivePool mode, and the snapshots folder. Volume capacity is entered in GiB. Enable DrivePool mode to scan the single `PoolPart.*` directory found at the top level of each configured volume; volumes with no match or multiple matches are reported instead of scanning an ambiguous root.

Start Scan opens a separate progress page. Enter a snapshot name or leave it blank to use the current date and time. Set a snapshots folder on the Configuration page before scanning; scans return a clear error if it is missing. Completed scans are saved as JSON files there. Use Cancel scan to stop an active scan. `ASPNETCORE_URLS` can override the default listening address.

The application writes `config.json` in the `config/` subfolder (`config/config.json`). Keep machine-specific disk paths out of source control. GitHub Actions builds the solution in Release mode on pushes and pull requests.

The Configuration page accepts one case-insensitive .NET regular expression per line under Never-move patterns. Patterns match pool-relative paths; a matching folder and its descendants are excluded from placement, and the planner splits ancestor folders as needed to keep excluded content in place. For example, `\$RECYCLE.BIN\\.*` matches files and folders inside `$RECYCLE.BIN`.

## Snapshot format

Each snapshot records its schema version, scan time, DrivePool mode, allocation-unit estimate, and the disk and volume configuration used for that scan. Every volume includes its alias, configured root, resolved `RootFolderPath`, and `OtherItemsSizeOnDisk`, the estimated allocation used by files and folder blocks outside that root. Blank aliases use the configured-order `D#-V#` label. `Folders` maps root-relative folder paths to sorted file entries (`Name`, `Size`, and estimated `SizeOnDisk`); `.` denotes the root itself, and an empty array represents an empty folder. Folder overhead inside the selected root can be estimated from the folder count and `AllocationUnitSize`. Volumes that could not be fully scanned are retained with `IsComplete: false` and an `Issues` list, so a partial snapshot is not mistaken for a complete inventory.

## Placement planning

Placement rules are saved in `FilePlacementRules` and evaluated from top to bottom. `FullRelativePath` is a literal, case-insensitive path prefix, `StartingDepth` selects the folder depth at which placement begins (the pool root is depth 1), and `AllowedVolumeIds` limits destinations. For example, `TV Series` with depth 2 starts at each series folder beneath it. Put specific rules before broad fallback rules. The planner prefers an allowed volume already containing the folder, checks estimated remaining capacity, and splits an oversized folder into its children when needed. Older rules containing `AllowedDiskIds` are migrated on load to include every configured volume on those disks.

The overview page loads a selected snapshot and shows a preview only. Its volume grid shows final logical size and a provenance bar, then a selected volume's grouped incoming and outgoing moves with folder/file counts. Transfer sizes are logical bytes. It does not execute moves. When a file has multiple existing copies and the destination lacks one, the preview proposes moving one source copy; additional copies are left untouched.
