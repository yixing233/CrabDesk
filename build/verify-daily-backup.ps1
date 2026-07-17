param(
    [string]$Executable = "..\artifacts\publish\win-x64\CrabDesk.WinUI.exe"
)

$ErrorActionPreference = "Stop"
$exe = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Executable))
if (-not (Test-Path -LiteralPath $exe)) {
    throw "CrabDesk executable not found: $exe"
}
if (@(Get-Process CrabDesk.WinUI,CrabDesk.IconGuard -ErrorAction SilentlyContinue).Count -gt 0) {
    throw "Close the running CrabDesk instance before verifying daily backups."
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("CrabDesk.BackupProcessTest." + [Guid]::NewGuid().ToString("N"))
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
$previousDataDirectory = $env:CRABDESK_DATA_DIR
$env:CRABDESK_DATA_DIR = $testRoot
$boxId = [Guid]::NewGuid()
$config = @{
    SchemaVersion = 5
    Settings = @{
        TakeOverDesktop = $true
        ShowSystemItems = $false
        Backup = @{
            DailyBackup = $true
            RetentionDays = 7
            BackupDirectory = ""
        }
    }
    Boxes = @(@{
        Id = $boxId
        Title = "每日备份测试"
        MonitorId = "primary"
        Bounds = @{ X = 40; Y = 50; Width = 420; Height = 300 }
    })
    Assignments = @{}
}
$config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $testRoot "config.json") -Encoding UTF8

$process = $null
try {
    $process = Start-Process -FilePath $exe -PassThru
    Start-Sleep -Seconds 7
    if ($process.HasExited) {
        throw "CrabDesk exited before creating the daily backup."
    }
    $exitCommand = Start-Process -FilePath $exe -ArgumentList "--exit-existing" -Wait -PassThru
    if ($exitCommand.ExitCode -ne 0) {
        throw "The daily-backup exit command returned code $($exitCommand.ExitCode)."
    }
    if (-not $process.WaitForExit(10000)) {
        throw "CrabDesk did not exit after the backup test."
    }

    $backups = @(Get-ChildItem -LiteralPath (Join-Path $testRoot "Backups") -Filter "*.crabdesk.json")
    if ($backups.Count -ne 1) {
        throw "Expected one daily backup, found $($backups.Count)."
    }
    $saved = Get-Content -LiteralPath (Join-Path $testRoot "config.json") -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $saved.Settings.Backup.LastBackupAt) {
        throw "Daily backup timestamp was not persisted."
    }
    Write-Host "Daily backup was created and its timestamp was persisted."
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

Write-Host "Daily backup integration test passed."
