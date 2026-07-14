param(
    [string]$Executable = "..\artifacts\publish\win-x64\CrabDesk.App.exe"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "hardware-validation-common.ps1")

$exe = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Executable))
if (-not (Test-Path -LiteralPath $exe)) {
    throw "CrabDesk executable not found: $exe"
}
if (@(Get-Process CrabDesk.App,CrabDesk.IconGuard -ErrorAction SilentlyContinue).Count -gt 0) {
    throw "Close the running CrabDesk instance before validating hardware checkpoints."
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("CrabDesk.HardwareCheckpointTest." + [Guid]::NewGuid().ToString("N"))
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
$previousDataDirectory = $env:CRABDESK_DATA_DIR
$previousHideIcons = Get-CrabDeskExplorerHideIcons
$env:CRABDESK_DATA_DIR = $testRoot
$process = $null

try {
    $process = Start-Process -FilePath $exe -PassThru
    $snapshot = Get-CrabDeskHardwareSnapshot -DataDirectory $testRoot -WaitSeconds 30
    Assert-CrabDeskHardwareSnapshot -Snapshot $snapshot
    if ($snapshot.ExpectedRestoredHideIcons -ne $previousHideIcons) {
        throw "Recovery marker did not preserve the pre-takeover Explorer icon state. Expected=$previousHideIcons, Marker=$($snapshot.ExpectedRestoredHideIcons)."
    }

    $exit = Start-Process -FilePath $exe -ArgumentList "--exit-existing" -Wait -PassThru
    if ($exit.ExitCode -ne 0 -or -not $process.WaitForExit(15000)) {
        throw "CrabDesk did not exit cleanly after the hardware-checkpoint smoke test."
    }
    Start-Sleep -Seconds 2
    if ((Get-CrabDeskExplorerHideIcons) -ne $previousHideIcons) {
        throw "Hardware-checkpoint smoke test did not restore Explorer icon visibility."
    }
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

Write-Host "Hardware checkpoint snapshot, active takeover icon state, recovery marker and clean-exit restoration passed."
