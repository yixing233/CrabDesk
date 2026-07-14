param(
    [string]$Executable = "..\artifacts\publish\win-x64\CrabDesk.App.exe"
)

$ErrorActionPreference = "Stop"
$exe = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Executable))
if (-not (Test-Path -LiteralPath $exe)) {
    throw "CrabDesk executable not found: $exe"
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("CrabDesk.TrayTest." + [Guid]::NewGuid().ToString("N"))
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
$previousDataDirectory = $env:CRABDESK_DATA_DIR
$env:CRABDESK_DATA_DIR = $testRoot
$boxId = [Guid]::NewGuid()
$config = @{
    SchemaVersion = 3
    Settings = @{
        TakeOverDesktop = $true
        ShowSystemItems = $true
        ConfirmDeleteBox = $true
        ThemeMode = 0
        DesktopBehavior = @{
            LaunchToTray = $true
            RefreshAfterRename = $true
        }
    }
    Boxes = @(@{
        Id = $boxId
        Title = "常用"
        MonitorId = "primary"
        Bounds = @{ X = 40; Y = 50; Width = 420; Height = 300 }
    })
    Assignments = @{}
}
$config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $testRoot "config.json") -Encoding UTF8

$process = $null
try {
    $process = Start-Process -FilePath $exe -PassThru
    Start-Sleep -Seconds 7
    $process.Refresh()
    if ($process.HasExited) {
        throw "CrabDesk exited instead of starting in the tray."
    }
    if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
        throw "Launch-to-tray created a visible settings window."
    }
    Write-Host "Launch-to-tray started CrabDesk without a settings window."

    $activation = Start-Process -FilePath $exe -Wait -PassThru
    if ($activation.ExitCode -ne 0) {
        throw "The second CrabDesk launch exited with code $($activation.ExitCode)."
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
    } while ($process.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw "A second launch did not open settings from the tray instance."
    }
    Write-Host "A second launch opened settings from the tray instance."

    $exitCommand = Start-Process -FilePath $exe -ArgumentList "--exit-existing" -Wait -PassThru
    if ($exitCommand.ExitCode -ne 0) {
        throw "The exit command process exited with code $($exitCommand.ExitCode)."
    }
    if (-not $process.WaitForExit(10000)) {
        throw "The tray instance did not exit cleanly."
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

Write-Host "Launch-to-tray behavior passed."
