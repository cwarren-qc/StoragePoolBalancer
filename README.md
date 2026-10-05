# StoragePoolBalancer

Storage Pool Balancer scans configured physical disks and reports live inventory progress. It does not move or modify files.

## Run locally

Install the .NET 8 SDK, then run from the repository root:

```powershell
dotnet run --project .\StorageBalancer.App\StorageBalancer.App.csproj
```

Open [http://localhost:5000](http://localhost:5000), add each physical disk and its mounted volumes, and save the configuration. Volume capacity is entered in GiB. Enable DrivePool mode to scan the single `PoolPart.*` directory found at the top level of each configured volume; volumes with no match or multiple matches are reported instead of scanning an ambiguous root. Start a scan to see the current path and file, folder, and byte counts for each disk. Use Cancel scan to stop the active scan.

The application writes `config.json` in its working directory. Keep machine-specific disk paths out of source control. GitHub Actions builds the solution in Release mode on pushes and pull requests.
