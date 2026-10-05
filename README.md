# StoragePoolBalancer

Storage Pool Balancer scans configured physical disks and reports live inventory progress. It does not move or modify files.

## Run locally

Install the .NET 8 SDK, then run from the repository root:

```powershell
dotnet run --project .\StorageBalancer.App\StorageBalancer.App.csproj
```

Open [http://localhost:5000](http://localhost:5000) for the pool summary. Use Configuration to edit physical disks, volumes, DrivePool mode, and the snapshots folder. Volume capacity is entered in GiB. Enable DrivePool mode to scan the single `PoolPart.*` directory found at the top level of each configured volume; volumes with no match or multiple matches are reported instead of scanning an ambiguous root.

Start Scan opens a separate progress page. Enter a snapshot name or leave it blank to use the current date and time. Set a snapshots folder on the Configuration page before scanning; scans return a clear error if it is missing. Completed scans are saved as JSON files there. Use Cancel scan to stop an active scan. `ASPNETCORE_URLS` can override the default listening address.

The application writes `config.json` in its working directory by default. Set `CONFIG_FOLDER` to store it in another directory; relative paths resolve from the process working directory. The VS Code launch profile sets it to `${workspaceFolder}/../StoragePoolBalancer.TestEnv/config`, keeps the workspace root as its working directory, and sets the app project as its content root so `wwwroot` is served. Keep machine-specific disk paths out of source control. GitHub Actions builds the solution in Release mode on pushes and pull requests.
