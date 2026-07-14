param(
    [string]$Executable = "..\artifacts\publish\win-x64\CrabDesk.App.exe",
    [switch]$ConfirmExplorerRestart
)

$ErrorActionPreference = "Stop"
$exe = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Executable))
if (-not (Test-Path -LiteralPath $exe)) {
    throw "CrabDesk executable not found: $exe"
}
if (-not $ConfirmExplorerRestart) {
    throw "Explorer restart is disruptive. Re-run with -ConfirmExplorerRestart to execute this test."
}
if (@(Get-Process CrabDesk.App,CrabDesk.IconGuard -ErrorAction SilentlyContinue).Count -gt 0) {
    throw "Close the running CrabDesk instance before verifying Explorer restart recovery."
}

$source = @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class ExplorerRestartVerifier {
    public delegate bool EnumProc(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder value, int count);
}
'@
Add-Type -TypeDefinition $source -ErrorAction SilentlyContinue

function Test-CrabDeskSurface {
    param([int]$ProcessId)

    $script:surfaceFound = $false
    [ExplorerRestartVerifier]::EnumWindows({
        param($top, $data)
        [ExplorerRestartVerifier]::EnumChildWindows($top, {
            param($child, $childData)
            [uint32]$childProcess = 0
            [void][ExplorerRestartVerifier]::GetWindowThreadProcessId($child, [ref]$childProcess)
            if ($childProcess -eq $ProcessId) {
                $parent = [ExplorerRestartVerifier]::GetParent($child)
                $className = New-Object Text.StringBuilder 128
                [void][ExplorerRestartVerifier]::GetClassName($parent, $className, 128)
                if ($className.ToString() -in @("Progman", "WorkerW") -and [ExplorerRestartVerifier]::IsWindowVisible($child)) {
                    $script:surfaceFound = $true
                }
            }
            return $true
        }, [IntPtr]::Zero) | Out-Null
        return $true
    }, [IntPtr]::Zero) | Out-Null
    return $script:surfaceFound
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("CrabDesk.ExplorerRestartTest." + [Guid]::NewGuid().ToString("N"))
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
$previousDataDirectory = $env:CRABDESK_DATA_DIR
try {
    $previousHideIcons = [int](Get-ItemPropertyValue "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" HideIcons -ErrorAction Stop)
}
catch {
    $previousHideIcons = 0
}
$env:CRABDESK_DATA_DIR = $testRoot
$config = @{
    SchemaVersion = 15
    Settings = @{
        TakeOverDesktop = $true
        ShowSystemItems = $false
        DesktopBehavior = @{ LaunchToTray = $true; RefreshAfterRename = $true }
        Backup = @{ DailyBackup = $false; RetentionDays = 7; BackupDirectory = "" }
        Updates = @{ CheckOnStartup = $false; Channel = 0 }
    }
    Boxes = @(@{
        Id = [Guid]::NewGuid()
        Title = "Explorer restart verification"
        MonitorId = "primary"
        Bounds = @{ X = 40; Y = 50; Width = 420; Height = 300 }
    })
    Assignments = @{}
    Organization = @{ Enabled = $false; RunOnStartup = $false; RunOnDesktopChanges = $false; ReassignExistingItems = $false }
    OrganizationRules = @()
}
$config | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $testRoot "config.json") -Encoding UTF8

$process = Start-Process -FilePath $exe -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (-not (Test-CrabDeskSurface $process.Id) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
    if (-not (Test-CrabDeskSurface $process.Id)) {
        throw "Initial CrabDesk desktop surface was not available."
    }

    Get-Process explorer -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3
    if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) {
        Start-Process explorer.exe
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Test-CrabDeskSurface $process.Id) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 750
    }
    if (-not (Test-CrabDeskSurface $process.Id)) {
        throw "CrabDesk did not reconnect its desktop surface after Explorer restarted."
    }
    Write-Host "CrabDesk reconnected its visible desktop surface after Explorer restarted."

    $exitCommand = Start-Process -FilePath $exe -ArgumentList "--exit-existing" -Wait -PassThru
    if ($exitCommand.ExitCode -ne 0) {
        throw "The Explorer-restart exit command returned code $($exitCommand.ExitCode)."
    }
    if (-not $process.WaitForExit(10000)) {
        throw "CrabDesk did not exit cleanly after the Explorer restart test."
    }
}
finally {
    if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) {
        Start-Process explorer.exe
    }
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 4
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

$hidden = Get-ItemPropertyValue "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" HideIcons -ErrorAction SilentlyContinue
$hiddenValue = if ($null -eq $hidden) { 0 } else { [int]$hidden }
if ($hiddenValue -ne $previousHideIcons) {
    throw "Explorer desktop icon visibility was not restored after the restart test."
}
Write-Host "Explorer icon visibility remained restored after reconnect and clean exit."
