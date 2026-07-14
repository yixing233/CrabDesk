param(
    [string]$Executable = "..\artifacts\publish\win-x64\CrabDesk.App.exe"
)

$ErrorActionPreference = "Stop"
$exe = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Executable))
if (-not (Test-Path -LiteralPath $exe)) {
    throw "CrabDesk executable not found: $exe"
}

$running = @(Get-Process CrabDesk.App -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    throw "Close the running CrabDesk instance before verifying backup restore UI."
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("CrabDesk.BackupUiTest." + [Guid]::NewGuid().ToString("N"))
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
$resultPath = Join-Path $testRoot "result.json"
$configPath = Join-Path $testRoot "config.json"
$previousDataDirectory = $env:CRABDESK_DATA_DIR
$env:CRABDESK_DATA_DIR = $testRoot
$config = @{
    SchemaVersion = 14
    Settings = @{
        TakeOverDesktop = $false
        ShowSystemItems = $false
        ConfirmDeleteBox = $false
        ThemeMode = 1
        DesktopBehavior = @{
            LaunchToTray = $false
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
    Boxes = @(@{
        Id = [Guid]::NewGuid()
        Title = "Original Layout"
        MonitorId = "primary"
        Bounds = @{ X = 40; Y = 50; Width = 420; Height = 300 }
        IsCollapsed = $false
        IsSystemBox = $false
    })
    Assignments = @{}
    Organization = @{
        Enabled = $false
        RunOnStartup = $false
        RunOnDesktopChanges = $false
        ReassignExistingItems = $false
    }
    OrganizationRules = @()
}
$config | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $configPath -Encoding UTF8

$process = $null
try {
    $arguments = "--verify-backup-ui `"$resultPath`""
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru
    if (-not $process.WaitForExit(30000)) {
        throw "CrabDesk backup UI verification did not finish within 30 seconds."
    }
    if ($process.ExitCode -ne 0) {
        $detail = if (Test-Path -LiteralPath $resultPath) {
            (Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8)
        }
        else {
            "result file was not created"
        }
        throw "CrabDesk backup UI verification exited with code $($process.ExitCode): $detail"
    }
    if (-not (Test-Path -LiteralPath $resultPath)) {
        throw "CrabDesk did not write the backup UI verification result."
    }

    $result = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $checks = @(
        "BackupCreated",
        "FailureWasReported",
        "FailedRestorePreservedCurrentState",
        "SuccessfulRestoreRecoveredBackup",
        "ResetCreatedBackup",
        "ResetBackupRestoredLayout"
    )
    foreach ($check in $checks) {
        if ($result.$check -ne $true) {
            throw "Backup UI check '$check' failed: $($result.Message)"
        }
    }

    $saved = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($saved.Boxes.Count -lt 1 -or $saved.Boxes[0].Title -ne "Original Layout") {
        throw "The restored config did not preserve the original layout."
    }
    Write-Host $result.Message
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

Write-Host "Backup restore UI integration test passed."
