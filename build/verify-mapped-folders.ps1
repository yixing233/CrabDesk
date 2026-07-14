param(
    [string]$Executable = "..\artifacts\publish\win-x64\CrabDesk.App.exe",
    [int]$VisualDelaySeconds = 0
)

$ErrorActionPreference = "Stop"
$exe = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Executable))
if (-not (Test-Path -LiteralPath $exe)) {
    throw "CrabDesk executable not found: $exe"
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("CrabDesk.MappedFolderTest." + [Guid]::NewGuid().ToString("N"))
$mappedDirectory = Join-Path $testRoot "mapped"
[System.IO.Directory]::CreateDirectory($mappedDirectory) | Out-Null
[System.IO.File]::WriteAllText((Join-Path $mappedDirectory "startup.txt"), "startup")

$previousDataDirectory = $env:CRABDESK_DATA_DIR
$env:CRABDESK_DATA_DIR = $testRoot
$regularBoxId = [Guid]::NewGuid()
$mappedBoxId = [Guid]::NewGuid()
$config = @{
    SchemaVersion = 6
    Settings = @{
        TakeOverDesktop = $false
        ShowSystemItems = $false
        ConfirmDeleteBox = $true
        ThemeMode = 0
        DesktopBehavior = @{
            LaunchToTray = $true
            RefreshAfterRename = $true
        }
    }
    Boxes = @(
        @{
            Id = $regularBoxId
            Title = "常用"
            MonitorId = "primary"
            Bounds = @{ X = 40; Y = 50; Width = 420; Height = 300 }
        },
        @{
            Id = $mappedBoxId
            Title = "项目目录"
            MonitorId = "primary"
            Bounds = @{ X = 500; Y = 50; Width = 360; Height = 280 }
            MappedFolder = @{
                Path = $mappedDirectory
                IsReadOnly = $false
            }
        }
    )
    Assignments = @{}
}
$config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $testRoot "config.json") -Encoding UTF8

$process = $null
try {
    $process = Start-Process -FilePath $exe -PassThru
    Start-Sleep -Seconds 5
    $process.Refresh()
    if ($process.HasExited) {
        throw "CrabDesk exited while loading a mapped folder box."
    }

    Start-Process -FilePath $exe -Wait
    $windowDeadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
    } while ($process.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $windowDeadline)
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw "The mapped folder settings page could not be opened."
    }
    if ($VisualDelaySeconds -gt 0) {
        Write-Host "Settings window is available for visual inspection."
        Start-Sleep -Seconds $VisualDelaySeconds
    }

    [System.IO.File]::WriteAllText((Join-Path $mappedDirectory "realtime.txt"), "realtime")
    Start-Sleep -Seconds 2
    Remove-Item -LiteralPath $mappedDirectory -Recurse -Force
    Start-Sleep -Seconds 3
    [System.IO.Directory]::CreateDirectory($mappedDirectory) | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $mappedDirectory "reconnected.txt"), "reconnected")
    Start-Sleep -Seconds 4
    $process.Refresh()
    if ($process.HasExited) {
        throw "CrabDesk exited while the mapped folder went offline or reconnected."
    }

    $exitCommand = Start-Process -FilePath $exe -ArgumentList "--exit-existing" -Wait -PassThru
    if ($exitCommand.ExitCode -ne 0) {
        throw "The mapped-folder exit command returned code $($exitCommand.ExitCode)."
    }
    if (-not $process.WaitForExit(10000)) {
        throw "CrabDesk did not exit cleanly after the mapped folder test."
    }

    $saved = Get-Content -LiteralPath (Join-Path $testRoot "config.json") -Raw -Encoding UTF8 | ConvertFrom-Json
    $mapped = @($saved.Boxes | Where-Object { $_.Id -eq $mappedBoxId })
    if ($saved.SchemaVersion -ne 13 -or $mapped.Count -ne 1) {
        throw "Mapped folder configuration was not persisted after schema migration."
    }
    if ($mapped[0].MappedFolder.Path -ne $mappedDirectory -or $mapped[0].MappedFolder.IsReadOnly) {
        throw "Mapped folder path or access mode changed unexpectedly."
    }
    Write-Host "Mapped folder startup, change monitoring, offline state and reconnect smoke test passed."
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
