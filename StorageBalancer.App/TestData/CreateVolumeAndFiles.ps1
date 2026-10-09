<#
.SYNOPSIS
    Creates fake storage pool volumes, files, and empty directories for testing plan execution.

.DESCRIPTION
    Sets up 4 fake volumes (M01, M02, M03, SSD) each with a 10 MB capacity limit.
    Simulates DrivePool with PoolPart.<guid> folders, placing files and empty directories
    designed to test:
      - File transfers across volumes (with checksumming and atomic commit)
      - Vacated folder cleanup (deleting source folders once all files are moved)
      - Pre-existing empty folder cleanup
      - Intact files that remain in place
#>

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$scriptDir = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($scriptDir)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}

$volumesDir = Join-Path $scriptDir "Volumes"
$snapshotsDir = Join-Path $scriptDir "Snapshots"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " StoragePoolBalancer - Test Environment Generator" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "Base Directory: $scriptDir"
Write-Host "Volumes Directory: $volumesDir"
Write-Host ""

# 1. Reset Volumes Directory
if (Test-Path $volumesDir) {
    Write-Host "Cleaning existing Volumes directory..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force $volumesDir
}
New-Item -ItemType Directory -Path $volumesDir -Force | Out-Null

if (-not (Test-Path $snapshotsDir)) {
    New-Item -ItemType Directory -Path $snapshotsDir -Force | Out-Null
}

# 2. Define Fake Volumes with PoolPart GUIDs
$volumes = @(
    @{ Alias = "M01"; Disk = "Disk 1"; PoolPart = "PoolPart.49c6937e-61e4-44df-9cf8-61f237bf3611" }
    @{ Alias = "M02"; Disk = "Disk 2"; PoolPart = "PoolPart.b87a9561-26c7-43a9-a764-802c636f0602" }
    @{ Alias = "M03"; Disk = "Disk 3"; PoolPart = "PoolPart.2c8928de-150e-4731-893d-d41980d28703" }
    @{ Alias = "SSD"; Disk = "Disk 4"; PoolPart = "PoolPart.ee0f16f1-a1eb-475b-9d41-320e8b2c4504" }
)

# Helper function to create binary dummy files with repeatable data pattern
function New-TestDummyFile {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][long]$SizeBytes
    )

    $parentDir = [System.IO.Path]::GetDirectoryName($Path)
    if (-not (Test-Path $parentDir)) {
        [System.IO.Directory]::CreateDirectory($parentDir) | Out-Null
    }

    $buffer = New-Object byte[] 65536
    # Fill buffer with a non-zero pattern to simulate real file content
    for ($i = 0; $i -lt $buffer.Length; $i++) {
        $buffer[$i] = [byte]($i % 251)
    }

    $fs = [System.IO.File]::Create($Path)
    try {
        $remaining = $SizeBytes
        while ($remaining -gt 0) {
            $toWrite = [Math]::Min($remaining, $buffer.Length)
            $fs.Write($buffer, 0, $toWrite)
            $remaining -= $toWrite
        }
    }
    finally {
        $fs.Dispose()
    }
}

# 3. Create Volume Folders & Files
foreach ($v in $volumes) {
    $volRoot = Join-Path $volumesDir $v.Alias
    $poolPartRoot = Join-Path $volRoot $v.PoolPart
    New-Item -ItemType Directory -Path $poolPartRoot -Force | Out-Null
    Write-Host "Created Volume [$($v.Alias)] at: $poolPartRoot" -ForegroundColor Green
}

Write-Host "`nPopulating test files and empty folders..." -ForegroundColor Cyan

# -------------------------------------------------------------
# Volume M01
# -------------------------------------------------------------
$m01Part = Join-Path (Join-Path $volumesDir "M01") "PoolPart.49c6937e-61e4-44df-9cf8-61f237bf3611"

# Rule Movies\Action target M01: Terminator stays on M01
New-TestDummyFile -Path (Join-Path $m01Part "Movies\Action\Terminator.mkv") -SizeBytes (2 * 1024 * 1024)

# Rule Torrents target SSD: linux_distro.iso will MOVE to SSD (M01\Torrents will be deleted)
New-TestDummyFile -Path (Join-Path $m01Part "Torrents\linux_distro.iso") -SizeBytes (1 * 1024 * 1024)

# Rule Utilities target M01: diagnostics.exe stays intact
New-TestDummyFile -Path (Join-Path $m01Part "Utilities\diagnostics.exe") -SizeBytes (512 * 1024)

# Pre-existing empty folder: should be cleaned up
New-Item -ItemType Directory -Path (Join-Path $m01Part "Documents\OldArchive") -Force | Out-Null

# -------------------------------------------------------------
# Volume M02
# -------------------------------------------------------------
$m02Part = Join-Path (Join-Path $volumesDir "M02") "PoolPart.b87a9561-26c7-43a9-a764-802c636f0602"

# Rule Movies\Action target M01: DieHard, Speed, and Bonus\making_of.mp4 will MOVE to M01
# After move: M02\Movies\Action\Bonus and M02\Movies\Action will become EMPTY and be deleted!
New-TestDummyFile -Path (Join-Path $m02Part "Movies\Action\DieHard.mkv") -SizeBytes (2 * 1024 * 1024)
New-TestDummyFile -Path (Join-Path $m02Part "Movies\Action\Speed.mkv") -SizeBytes ([long](1.5 * 1024 * 1024))
New-TestDummyFile -Path (Join-Path $m02Part "Movies\Action\Bonus\making_of.mp4") -SizeBytes (512 * 1024)

# Pre-existing empty folder: should be cleaned up
New-Item -ItemType Directory -Path (Join-Path $m02Part "Temp\Unused") -Force | Out-Null

# -------------------------------------------------------------
# Volume M03
# -------------------------------------------------------------
$m03Part = Join-Path (Join-Path $volumesDir "M03") "PoolPart.2c8928de-150e-4731-893d-d41980d28703"

# Rule Photos\2024 target M02: beach.jpg and family.jpg will MOVE to M02
# After move: M03\Photos\2024\Vacation and M03\Photos\2024 will become EMPTY and be deleted!
New-TestDummyFile -Path (Join-Path $m03Part "Photos\2024\Vacation\beach.jpg") -SizeBytes (800 * 1024)
New-TestDummyFile -Path (Join-Path $m03Part "Photos\2024\family.jpg") -SizeBytes (600 * 1024)

# Rule Backups target M03: system_state.json stays intact
New-TestDummyFile -Path (Join-Path $m03Part "Backups\system_state.json") -SizeBytes (512 * 1024)

# Pre-existing empty folder: should be cleaned up
New-Item -ItemType Directory -Path (Join-Path $m03Part "Music\Classical\Beethoven") -Force | Out-Null

# -------------------------------------------------------------
# Volume SSD
# -------------------------------------------------------------
$ssdPart = Join-Path (Join-Path $volumesDir "SSD") "PoolPart.ee0f16f1-a1eb-475b-9d41-320e8b2c4504"
# SSD starts empty; receives linux_distro.iso from M01

# 4. Generate matching config.json
$configPath = Join-Path $scriptDir "config.json"
$configObject = [ordered]@{
    DrivePoolMode = $true
    SnapshotsFolder = (Join-Path $scriptDir "Snapshots")
    Volumes = @(
        @{
            Alias = "M01"
            MountPoint = (Join-Path $volumesDir "M01")
            RootFolderRelativePath = ""
            Capacity = 10485760 # 10 MB
            Disk = "Disk 1"
        },
        @{
            Alias = "M02"
            MountPoint = (Join-Path $volumesDir "M02")
            RootFolderRelativePath = ""
            Capacity = 10485760 # 10 MB
            Disk = "Disk 2"
        },
        @{
            Alias = "M03"
            MountPoint = (Join-Path $volumesDir "M03")
            RootFolderRelativePath = ""
            Capacity = 10485760 # 10 MB
            Disk = "Disk 3"
        },
        @{
            Alias = "SSD"
            MountPoint = (Join-Path $volumesDir "SSD")
            RootFolderRelativePath = ""
            Capacity = 10485760 # 10 MB
            Disk = "Disk 4"
        }
    )
    FilePlacementRules = @(
        @{
            FullRelativePath = "Movies\Action"
            StartingDepth = 1
            DeferToFiller = $false
            AllowedVolumeAliases = @("M01")
        },
        @{
            FullRelativePath = "Photos\2024"
            StartingDepth = 1
            DeferToFiller = $false
            AllowedVolumeAliases = @("M02")
        },
        @{
            FullRelativePath = "Torrents"
            StartingDepth = 1
            DeferToFiller = $false
            AllowedVolumeAliases = @("SSD")
        },
        @{
            FullRelativePath = "Utilities"
            StartingDepth = 1
            DeferToFiller = $false
            AllowedVolumeAliases = @("M01")
        },
        @{
            FullRelativePath = "Backups"
            StartingDepth = 1
            DeferToFiller = $false
            AllowedVolumeAliases = @("M03")
        }
    )
    Duplicates = @{
        Consolidate = $true
        AllowedVolumeAliases = @("M01", "M02", "M03", "SSD")
    }
    Filler = @{
        Consolidate = $false
        AllowedVolumeAliases = @("M01", "M02", "M03", "SSD")
    }
    Unmatched = @{
        Consolidate = $false
        AllowedVolumeAliases = @("M01", "M02", "M03", "SSD")
    }
}

$jsonText = $configObject | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText($configPath, $jsonText, [System.Text.Encoding]::UTF8)

Write-Host "Updated configuration file at: $configPath" -ForegroundColor Green

Write-Host "`n==========================================================" -ForegroundColor Cyan
Write-Host " Test Environment Ready" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "Volumes: M01, M02, M03, SSD (each 10.0 MB capacity)"
Write-Host "Planned Moves:"
Write-Host "  * M02 -> M01: DieHard.mkv (2.0 MB), Speed.mkv (1.5 MB), making_of.mp4 (500 KB)"
Write-Host "  * M03 -> M02: beach.jpg (800 KB), family.jpg (600 KB)"
Write-Host "  * M01 -> SSD: linux_distro.iso (1.0 MB)"
Write-Host "Planned Folder Deletions:"
Write-Host "  * M02: Movies\Action\Bonus and Movies\Action (vacated after file moves)"
Write-Host "  * M03: Photos\2024\Vacation and Photos\2024 (vacated after file moves)"
Write-Host "  * M01: Torrents (vacated after file move)"
Write-Host "  * Pre-existing empty folders: M01\Documents\OldArchive, M02\Temp\Unused, M03\Music\Classical\Beethoven"
Write-Host "==========================================================`n" -ForegroundColor Cyan
