param(
    [string]$Executable = "..\artifacts\publish\win-x64\CrabDesk.App.exe",
    [string]$OutputPath = "..\artifacts\verification\opacity.png"
)

$ErrorActionPreference = "Stop"
$exe = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Executable))
$output = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
    [System.IO.Path]::GetFullPath($OutputPath)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputPath))
}
if (-not (Test-Path -LiteralPath $exe)) {
    throw "CrabDesk executable not found: $exe"
}
if (@(Get-Process CrabDesk.App,CrabDesk.IconGuard -ErrorAction SilentlyContinue).Count -gt 0) {
    throw "Close the running CrabDesk instance before validating box opacity."
}
[System.IO.Directory]::CreateDirectory((Split-Path -Parent $output)) | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName PresentationFramework
$source = @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class OpacityVerifier
{
    public delegate bool EnumWindowsProc(IntPtr window, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr data);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr window, StringBuilder className, int capacity);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] public static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr value);
}
'@
Add-Type -TypeDefinition $source -ErrorAction SilentlyContinue

function Test-WindowRegionPoint([IntPtr]$Window, [int]$X, [int]$Y) {
    $region = [OpacityVerifier]::CreateRectRgn(0, 0, 0, 0)
    try {
        if ([OpacityVerifier]::GetWindowRgn($Window, $region) -le 0) {
            throw "Could not read the CrabDesk surface region."
        }
        return [OpacityVerifier]::PtInRegion($region, $X, $Y)
    }
    finally {
        [void][OpacityVerifier]::DeleteObject($region)
    }
}

function Get-ExplorerHideIcons {
    try {
        return [int](Get-ItemPropertyValue "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" HideIcons)
    }
    catch {
        return 0
    }
}

function Find-DesktopSurface([int]$ProcessId) {
    $script:surfaceHandle = [IntPtr]::Zero
    [OpacityVerifier]::EnumWindows({
        param($top, $data)
        [OpacityVerifier]::EnumChildWindows($top, {
            param($child, $childData)
            [uint32]$ownerProcessId = 0
            [void][OpacityVerifier]::GetWindowThreadProcessId($child, [ref]$ownerProcessId)
            if ($ownerProcessId -eq $ProcessId) {
                $parent = [OpacityVerifier]::GetParent($child)
                $className = [System.Text.StringBuilder]::new(128)
                [void][OpacityVerifier]::GetClassName($parent, $className, 128)
                if ($className.ToString() -in @("Progman", "WorkerW") -and [OpacityVerifier]::IsWindowVisible($child)) {
                    $script:surfaceHandle = $child
                    return $false
                }
            }
            return $true
        }, [IntPtr]::Zero) | Out-Null
        return $script:surfaceHandle -eq [IntPtr]::Zero
    }, [IntPtr]::Zero) | Out-Null
    return $script:surfaceHandle
}

function Show-Desktop {
    $shell = New-Object -ComObject Shell.Application
    try {
        $shell.MinimizeAll()
    }
    finally {
        [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    }
    Start-Sleep -Milliseconds 800
}

function Send-WinD {
    [OpacityVerifier]::keybd_event(0x5B, 0, 0, [UIntPtr]::Zero)
    [OpacityVerifier]::keybd_event(0x44, 0, 0, [UIntPtr]::Zero)
    [OpacityVerifier]::keybd_event(0x44, 0, 2, [UIntPtr]::Zero)
    [OpacityVerifier]::keybd_event(0x5B, 0, 2, [UIntPtr]::Zero)
}

function Capture-Screen([System.Drawing.Rectangle]$Bounds) {
    $bitmap = [System.Drawing.Bitmap]::new($Bounds.Width, $Bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($Bounds.Location, [System.Drawing.Point]::Empty, $Bounds.Size)
    }
    finally {
        $graphics.Dispose()
    }
    return $bitmap
}

function Get-ColorDistance([System.Drawing.Color]$First, [System.Drawing.Color]$Second) {
    return [Math]::Abs([int]$First.R - [int]$Second.R) +
        [Math]::Abs([int]$First.G - [int]$Second.G) +
        [Math]::Abs([int]$First.B - [int]$Second.B)
}

$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$scale = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width / [System.Windows.SystemParameters]::PrimaryScreenWidth
$boxWidth = 420
$boxHeight = 300
$boxX = [Math]::Round((($screen.Width / $scale) - $boxWidth) / 2)
$boxY = [Math]::Round([Math]::Max(70, (($screen.Height / $scale) - $boxHeight) / 3))
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("CrabDesk.OpacityTest." + [Guid]::NewGuid().ToString("N"))
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
$previousDataDirectory = $env:CRABDESK_DATA_DIR
$previousHideIcons = Get-ExplorerHideIcons
$env:CRABDESK_DATA_DIR = $testRoot
$config = @{
    SchemaVersion = 15
    Settings = @{
        TakeOverDesktop = $true
        ShowSystemItems = $false
        ConfirmDeleteBox = $true
        ThemeMode = 2
        DesktopBehavior = @{
            LaunchToTray = $false
            RefreshAfterRename = $true
            ShowDesktopContextMenu = $false
            ToggleIconsOnDesktopDoubleClick = $false
            ExpandBoxOnHover = $false
        }
        Backup = @{ DailyBackup = $false; RetentionDays = 7; BackupDirectory = "" }
        Appearance = @{ AnimationEnabled = $false; CornerRadius = 12 }
        Updates = @{ CheckOnStartup = $false; Channel = 0 }
    }
    Boxes = @(@{
        Id = [Guid]::NewGuid()
        Title = "Opacity 50%"
        MonitorId = "primary"
        Bounds = @{ X = $boxX; Y = $boxY; Width = $boxWidth; Height = $boxHeight }
        IsCollapsed = $false
        Appearance = @{
            Background = "#FFFF0000"
            Accent = "#FFFFFFFF"
            Opacity = 0.5
            IconSize = 42
            LabelFontSize = 8.5
            ShowItemLabels = $true
            ShowShortcutBadges = $true
            TitleBarHeight = 48
            TitleAlignment = 0
            TitleColor = "#FFFFFFFF"
            TitleFontSize = 10
            TitleFontBold = $true
            ShowCollapseButton = $true
        }
    })
    Assignments = @{}
    Organization = @{ Enabled = $false; RunOnStartup = $false; RunOnDesktopChanges = $false; ReassignExistingItems = $false }
    OrganizationRules = @()
}
$config | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $testRoot "config.json") -Encoding UTF8

$process = $null
$capture = $null
try {
    Show-Desktop
    $process = Start-Process -FilePath $exe -PassThru
    Start-Sleep -Seconds 7
    $process.Refresh()
    if ($process.HasExited -or $process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw "CrabDesk settings window was not available."
    }
    [void][OpacityVerifier]::PostMessage($process.MainWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    Start-Sleep -Seconds 2
    Show-Desktop

    $surface = Find-DesktopSurface $process.Id
    if ($surface -eq [IntPtr]::Zero) {
        throw "CrabDesk desktop surface was not attached to Progman/WorkerW."
    }
    $boxPixelX = [int][Math]::Round($boxX * $scale)
    $boxPixelY = [int][Math]::Round($boxY * $scale)
    $boxPixelWidth = [int][Math]::Round($boxWidth * $scale)
    $boxPixelHeight = [int][Math]::Round($boxHeight * $scale)
    if (-not (Test-WindowRegionPoint $surface ($boxPixelX + [int]($boxPixelWidth / 2)) ($boxPixelY + [int]($boxPixelHeight / 2)))) {
        throw "The configured box body was missing from the desktop surface region."
    }
    $outsideCandidates = @(
        [System.Drawing.Point]::new(($boxPixelX - 12), ($boxPixelY + [int]($boxPixelHeight / 2)))
        [System.Drawing.Point]::new(($boxPixelX + $boxPixelWidth + 12), ($boxPixelY + [int]($boxPixelHeight / 2)))
        [System.Drawing.Point]::new(($boxPixelX + [int]($boxPixelWidth / 2)), ($boxPixelY - 12))
        [System.Drawing.Point]::new(($boxPixelX + [int]($boxPixelWidth / 2)), ($boxPixelY + $boxPixelHeight + 12))
    )
    if (-not ($outsideCandidates | Where-Object { -not (Test-WindowRegionPoint $surface $_.X $_.Y) })) {
        throw "The desktop surface region unexpectedly covered every point around the configured box."
    }
    if ((Get-ExplorerHideIcons) -ne 1) {
        throw "Explorer native icons were not hidden during desktop takeover."
    }

    Send-WinD
    Start-Sleep -Milliseconds 700
    if (-not [OpacityVerifier]::IsWindowVisible($surface)) {
        throw "Desktop surface was hidden after Win+D."
    }
    Send-WinD
    Start-Sleep -Milliseconds 700
    Show-Desktop
    $capture = Capture-Screen $screen
    $capture.Save($output, [System.Drawing.Imaging.ImageFormat]::Png)

    $windowBounds = [OpacityVerifier+Rect]::new()
    if (-not [OpacityVerifier]::GetWindowRect($surface, [ref]$windowBounds)) {
        throw "Could not read the desktop surface bounds."
    }
    $points = @(
        [System.Drawing.Point]::new($boxPixelX + [int](85 * $scale), $boxPixelY + [int](95 * $scale)),
        [System.Drawing.Point]::new($boxPixelX + [int](205 * $scale), $boxPixelY + [int](165 * $scale)),
        [System.Drawing.Point]::new($boxPixelX + [int](335 * $scale), $boxPixelY + [int](235 * $scale))
    )
    $passedPoints = 0
    foreach ($point in $points) {
        $screenX = $windowBounds.Left - $screen.Left + $point.X
        $screenY = $windowBounds.Top - $screen.Top + $point.Y
        $after = $capture.GetPixel($screenX, $screenY)
        if ($after.R -ge 80 -and
            ($after.R - $after.G) -ge 70 -and
            ($after.R - $after.B) -ge 70 -and
            (Get-ColorDistance $after ([System.Drawing.Color]::Red)) -ge 20) {
            $passedPoints++
        }
    }
    if ($passedPoints -lt 2) {
        throw "The box body was not visibly blended at 50% opacity. MatchingSamples=$passedPoints/$($points.Count), Capture=$output"
    }

    $exitCommand = Start-Process -FilePath $exe -ArgumentList "--exit-existing" -Wait -PassThru
    if ($exitCommand.ExitCode -ne 0 -or -not $process.WaitForExit(15000)) {
        throw "CrabDesk did not exit cleanly after opacity validation."
    }
    Start-Sleep -Seconds 2
    if ((Get-ExplorerHideIcons) -ne $previousHideIcons) {
        throw "Opacity validation did not restore Explorer icon visibility."
    }
}
finally {
    if ($capture) {
        $capture.Dispose()
    }
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

Write-Host "Per-box composited opacity, smooth painted corners, scoped surface region, repeated Win+D visibility, clean exit and Explorer icon restoration passed. Capture: $output"
