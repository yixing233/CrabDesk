param(
    [string]$Executable = "..\artifacts\publish\win-x64\CrabDesk.App.exe"
)

$ErrorActionPreference = "Stop"
$exe = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Executable))
if (-not (Test-Path -LiteralPath $exe)) {
    throw "CrabDesk executable not found: $exe"
}
if (@(Get-Process CrabDesk.App,CrabDesk.IconGuard -ErrorAction SilentlyContinue).Count -gt 0) {
    throw "Close the running CrabDesk instance before verifying dynamic wallpaper compatibility."
}
$wallpaperProcesses = @(Get-Process wallpaper32,wallpaper64 -ErrorAction SilentlyContinue)
if ($wallpaperProcesses.Count -eq 0) {
    throw "Wallpaper Engine must already be running for this compatibility test."
}

$source = @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class DynamicWallpaperVerifier {
    public delegate bool EnumProc(IntPtr hwnd, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);
}
'@
Add-Type -TypeDefinition $source -ErrorAction SilentlyContinue

function Get-WindowClass([IntPtr]$Handle) {
    $text = [System.Text.StringBuilder]::new(256)
    [void][DynamicWallpaperVerifier]::GetClassName($Handle, $text, 256)
    return $text.ToString()
}

function Find-WallpaperWindow([int[]]$ProcessIds) {
    $script:wallpaperWindow = $null
    [DynamicWallpaperVerifier]::EnumWindows({
        param($top, $data)
        [DynamicWallpaperVerifier]::EnumChildWindows($top, {
            param($child, $childData)
            [uint32]$windowProcessId = 0
            [void][DynamicWallpaperVerifier]::GetWindowThreadProcessId($child, [ref]$windowProcessId)
            if ($ProcessIds -contains $windowProcessId -and [DynamicWallpaperVerifier]::IsWindowVisible($child)) {
                $className = Get-WindowClass $child
                $parent = [DynamicWallpaperVerifier]::GetParent($child)
                $parentClass = Get-WindowClass $parent
                $rect = [DynamicWallpaperVerifier+Rect]::new()
                [void][DynamicWallpaperVerifier]::GetWindowRect($child, [ref]$rect)
                if ($className -like "WPE*" -and $parentClass -eq "WorkerW" -and
                    $rect.Right -gt $rect.Left -and $rect.Bottom -gt $rect.Top) {
                    $script:wallpaperWindow = [pscustomobject]@{
                        Handle = $child
                        ProcessId = [int]$windowProcessId
                        Class = $className
                        Parent = $parent
                        ParentClass = $parentClass
                        Width = $rect.Right - $rect.Left
                        Height = $rect.Bottom - $rect.Top
                    }
                    return $false
                }
            }
            return $true
        }, [IntPtr]::Zero) | Out-Null
        return $null -eq $script:wallpaperWindow
    }, [IntPtr]::Zero) | Out-Null
    return $script:wallpaperWindow
}

function Find-CrabDeskSurface([int]$ProcessId) {
    $script:crabSurface = $null
    [DynamicWallpaperVerifier]::EnumWindows({
        param($top, $data)
        [DynamicWallpaperVerifier]::EnumChildWindows($top, {
            param($child, $childData)
            [uint32]$windowProcessId = 0
            [void][DynamicWallpaperVerifier]::GetWindowThreadProcessId($child, [ref]$windowProcessId)
            if ($windowProcessId -eq $ProcessId -and [DynamicWallpaperVerifier]::IsWindowVisible($child)) {
                $parent = [DynamicWallpaperVerifier]::GetParent($child)
                $parentClass = Get-WindowClass $parent
                if ($parentClass -eq "SHELLDLL_DefView") {
                    $script:crabSurface = [pscustomobject]@{
                        Handle = $child
                        Class = Get-WindowClass $child
                        Parent = $parent
                        ParentClass = $parentClass
                    }
                    return $false
                }
            }
            return $true
        }, [IntPtr]::Zero) | Out-Null
        return $null -eq $script:crabSurface
    }, [IntPtr]::Zero) | Out-Null
    return $script:crabSurface
}

function Send-WinD {
    [DynamicWallpaperVerifier]::keybd_event(0x5B, 0, 0, [UIntPtr]::Zero)
    [DynamicWallpaperVerifier]::keybd_event(0x44, 0, 0, [UIntPtr]::Zero)
    [DynamicWallpaperVerifier]::keybd_event(0x44, 0, 2, [UIntPtr]::Zero)
    [DynamicWallpaperVerifier]::keybd_event(0x5B, 0, 2, [UIntPtr]::Zero)
}

function Get-ExplorerHideIcons {
    try {
        return [int](Get-ItemPropertyValue "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" HideIcons)
    }
    catch {
        return 0
    }
}

$wallpaperIds = @($wallpaperProcesses | Select-Object -ExpandProperty Id)
$wallpaper = Find-WallpaperWindow $wallpaperIds
if ($null -eq $wallpaper) {
    throw "Wallpaper Engine is running but no visible WPE wallpaper window hosted by WorkerW was found."
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("CrabDesk.WallpaperTest." + [Guid]::NewGuid().ToString("N"))
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
$previousDataDirectory = $env:CRABDESK_DATA_DIR
$previousHideIcons = Get-ExplorerHideIcons
$env:CRABDESK_DATA_DIR = $testRoot
$config = @{
    SchemaVersion = 14
    Settings = @{
        TakeOverDesktop = $true
        ShowSystemItems = $false
        ConfirmDeleteBox = $true
        ThemeMode = 0
        DesktopBehavior = @{
            LaunchToTray = $true
            RefreshAfterRename = $true
            ShowDesktopContextMenu = $false
            ToggleIconsOnDesktopDoubleClick = $false
        }
        Backup = @{ DailyBackup = $false; RetentionDays = 7; BackupDirectory = "" }
        Updates = @{ CheckOnStartup = $false; Channel = 0 }
    }
    Boxes = @(@{
        Id = [Guid]::NewGuid()
        Title = "Wallpaper Engine compatibility"
        MonitorId = "primary"
        Bounds = @{ X = 40; Y = 50; Width = 420; Height = 300 }
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
$config | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $testRoot "config.json") -Encoding UTF8

$process = $null
try {
    $process = Start-Process -FilePath $exe -PassThru
    Start-Sleep -Seconds 8
    $process.Refresh()
    if ($process.HasExited) {
        throw "CrabDesk exited while Wallpaper Engine was active."
    }
    $surface = Find-CrabDeskSurface $process.Id
    if ($null -eq $surface) {
        throw "CrabDesk did not attach a visible surface below SHELLDLL_DefView while Wallpaper Engine was active."
    }
    if ($surface.Parent -eq $wallpaper.Parent -or $surface.ParentClass -eq $wallpaper.ParentClass) {
        throw "CrabDesk and Wallpaper Engine were attached to the same desktop layer."
    }

    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        Send-WinD
        Start-Sleep -Milliseconds 900
        if (-not [DynamicWallpaperVerifier]::IsWindowVisible($surface.Handle)) {
            throw "CrabDesk surface became hidden after Win+D attempt $($attempt + 1)."
        }
        if (-not [DynamicWallpaperVerifier]::IsWindowVisible($wallpaper.Handle)) {
            throw "Wallpaper Engine window became hidden after Win+D attempt $($attempt + 1)."
        }
    }

    $exitCommand = Start-Process -FilePath $exe -ArgumentList "--exit-existing" -Wait -PassThru
    if ($exitCommand.ExitCode -ne 0) {
        throw "CrabDesk exit command returned code $($exitCommand.ExitCode)."
    }
    if (-not $process.WaitForExit(15000)) {
        throw "CrabDesk did not exit cleanly after the dynamic wallpaper test."
    }
    if ((Get-ExplorerHideIcons) -ne $previousHideIcons) {
        throw "Explorer icon visibility was not restored after the dynamic wallpaper test."
    }
    $wallpaperAfter = Find-WallpaperWindow $wallpaperIds
    if ($null -eq $wallpaperAfter -or $wallpaperAfter.Handle -ne $wallpaper.Handle) {
        throw "Wallpaper Engine did not remain attached after CrabDesk exited."
    }
    Write-Host ("WallpaperClass={0}; WallpaperParent={1}; SurfaceClass={2}; SurfaceParent={3}" -f `
        $wallpaper.Class, $wallpaper.ParentClass, $surface.Class, $surface.ParentClass)
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

Write-Host "Wallpaper Engine compatibility verification passed without stopping the wallpaper process."
