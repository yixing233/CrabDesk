param(
    [string]$Executable = "..\artifacts\publish\win-x64\CrabDesk.App.exe",
    [int]$DurationSeconds = 900,
    [int]$SampleIntervalSeconds = 5,
    [int]$FilesPerFolder = 120
)

$ErrorActionPreference = "Stop"
if ($DurationSeconds -lt 30) {
    throw "DurationSeconds must be at least 30."
}
if ($SampleIntervalSeconds -lt 1) {
    throw "SampleIntervalSeconds must be positive."
}
if ($FilesPerFolder -lt 20) {
    throw "FilesPerFolder must be at least 20."
}

$exe = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Executable))
if (-not (Test-Path -LiteralPath $exe)) {
    throw "CrabDesk executable not found: $exe"
}
if (@(Get-Process CrabDesk.App -ErrorAction SilentlyContinue).Count -gt 0) {
    throw "Close the running CrabDesk instance before running the stability test."
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("CrabDesk.StabilityTest." + [Guid]::NewGuid().ToString("N"))
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
$previousDataDirectory = $env:CRABDESK_DATA_DIR
$env:CRABDESK_DATA_DIR = $testRoot
$mappedFolders = @()
$boxes = @()
for ($folderIndex = 0; $folderIndex -lt 4; $folderIndex++) {
    $folder = Join-Path $testRoot ("mapped-" + $folderIndex)
    [System.IO.Directory]::CreateDirectory($folder) | Out-Null
    $mappedFolders += $folder
    for ($fileIndex = 0; $fileIndex -lt $FilesPerFolder; $fileIndex++) {
        $extension = @(".txt", ".log", ".md", ".json")[$fileIndex % 4]
        [System.IO.File]::WriteAllText(
            (Join-Path $folder ("item-{0:D3}{1}" -f $fileIndex, $extension)),
            "CrabDesk stability fixture $folderIndex/$fileIndex")
    }
    $boxes += @{
        Id = [Guid]::NewGuid()
        Title = "缓存压力 $($folderIndex + 1)"
        MonitorId = "primary"
        Bounds = @{
            X = 32 + ($folderIndex % 2) * 450
            Y = 42 + [Math]::Floor($folderIndex / 2) * 330
            Width = 420
            Height = 300
        }
        Appearance = @{
            Background = "#FF2A2D32"
            Accent = "#FF4EA1D3"
            Opacity = 1
            IconSize = 32 + $folderIndex * 8
        }
        MappedFolder = @{
            Path = $folder
            IsReadOnly = $true
        }
    }
}

$config = @{
    SchemaVersion = 15
    Settings = @{
        TakeOverDesktop = $true
        ShowSystemItems = $false
        ConfirmDeleteBox = $false
        ThemeMode = 1
        DesktopBehavior = @{
            LaunchToTray = $true
            RefreshAfterRename = $true
            ShowDesktopContextMenu = $false
            ToggleIconsOnDesktopDoubleClick = $false
        }
        Backup = @{
            DailyBackup = $false
            RetentionDays = 7
            BackupDirectory = ""
        }
        Updates = @{
            CheckOnStartup = $false
            Channel = 0
        }
    }
    Boxes = $boxes
    Assignments = @{}
    Organization = @{
        Enabled = $false
        RunOnStartup = $false
        RunOnDesktopChanges = $false
        ReassignExistingItems = $false
    }
    OrganizationRules = @()
}
$config | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $testRoot "config.json") -Encoding UTF8

$process = $null
try {
    $process = Start-Process -FilePath $exe -PassThru
    Start-Sleep -Seconds 10
    $process.Refresh()
    if ($process.HasExited) {
        throw "CrabDesk exited during stability-test warmup."
    }

    $baselinePrivate = $process.PrivateMemorySize64
    $baselineHandles = $process.HandleCount
    $peakPrivate = $baselinePrivate
    $peakWorking = $process.WorkingSet64
    $peakHandles = $baselineHandles
    $sampleCount = 0
    $deadline = [DateTime]::UtcNow.AddSeconds($DurationSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $folderIndex = $sampleCount % $mappedFolders.Count
        $fileIndex = $sampleCount % $FilesPerFolder
        $path = Join-Path $mappedFolders[$folderIndex] ("item-{0:D3}.txt" -f ($fileIndex - ($fileIndex % 4)))
        [System.IO.File]::AppendAllText($path, "`nrefresh $sampleCount")
        $ephemeral = Join-Path $mappedFolders[$folderIndex] ("ephemeral-$sampleCount.tmp")
        [System.IO.File]::WriteAllText($ephemeral, "temporary")
        Start-Sleep -Milliseconds 350
        [System.IO.File]::Delete($ephemeral)

        Start-Sleep -Seconds $SampleIntervalSeconds
        $process.Refresh()
        if ($process.HasExited) {
            throw "CrabDesk exited during the stability test."
        }
        $peakPrivate = [Math]::Max($peakPrivate, $process.PrivateMemorySize64)
        $peakWorking = [Math]::Max($peakWorking, $process.WorkingSet64)
        $peakHandles = [Math]::Max($peakHandles, $process.HandleCount)
        $sampleCount++
    }

    $privateGrowth = $peakPrivate - $baselinePrivate
    $handleGrowth = $peakHandles - $baselineHandles
    if ($peakPrivate -gt 600MB) {
        throw "Private memory exceeded 600 MB (peak $([Math]::Round($peakPrivate / 1MB, 1)) MB)."
    }
    if ($privateGrowth -gt 128MB) {
        throw "Private memory grew by more than 128 MB (growth $([Math]::Round($privateGrowth / 1MB, 1)) MB)."
    }
    if ($handleGrowth -gt 1000) {
        throw "Process handles grew by more than 1000 (growth $handleGrowth)."
    }

    $exitCommand = Start-Process -FilePath $exe -ArgumentList "--exit-existing" -Wait -PassThru
    if ($exitCommand.ExitCode -ne 0) {
        throw "The stability-test exit command returned code $($exitCommand.ExitCode)."
    }
    if (-not $process.WaitForExit(15000)) {
        throw "CrabDesk did not exit cleanly after the stability test."
    }
    Write-Host ("Samples={0}; PeakPrivateMB={1}; PeakWorkingSetMB={2}; HandleGrowth={3}" -f `
        $sampleCount,
        [Math]::Round($peakPrivate / 1MB, 1),
        [Math]::Round($peakWorking / 1MB, 1),
        $handleGrowth)
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    if ($null -eq $previousDataDirectory) {
        Remove-Item Env:\CRABDESK_DATA_DIR -ErrorAction SilentlyContinue
    }
    else {
        $env:CRABDESK_DATA_DIR = $previousDataDirectory
    }
    $resolvedRoot = [System.IO.Path]::GetFullPath($testRoot)
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedRoot.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Runtime stability test passed."
