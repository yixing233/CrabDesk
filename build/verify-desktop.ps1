param(
    [string]$Executable = "..\artifacts\publish\win-x64\CrabDesk.App.exe"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$exe = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Executable))
if (-not (Test-Path -LiteralPath $exe)) {
    throw "CrabDesk executable not found: $exe"
}
if (@(Get-Process CrabDesk.App,CrabDesk.IconGuard -ErrorAction SilentlyContinue).Count -gt 0) {
    throw "Close the running CrabDesk instance before verifying the desktop host."
}

$source = @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class DesktopVerifier {
    public delegate bool EnumProc(IntPtr hwnd, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder value, int count);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetWindowRgnBox(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hwnd, ref Point point);
    [DllImport("user32.dll")] public static extern IntPtr ChildWindowFromPointEx(IntPtr parent, Point point, uint flags);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool UpdateWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] public static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr value);
}
'@
Add-Type -TypeDefinition $source -ErrorAction SilentlyContinue

function Find-DesktopSurface([int]$ProcessId) {
    $script:surfaceHandle = [IntPtr]::Zero
    [DesktopVerifier]::EnumWindows({
        param($top, $data)
        [DesktopVerifier]::EnumChildWindows($top, {
            param($child, $childData)
            [uint32]$windowProcessId = 0
            [void][DesktopVerifier]::GetWindowThreadProcessId($child, [ref]$windowProcessId)
            if ($windowProcessId -eq $ProcessId) {
                $parent = [DesktopVerifier]::GetParent($child)
                $className = [System.Text.StringBuilder]::new(128)
                [void][DesktopVerifier]::GetClassName($parent, $className, 128)
                if ($className.ToString() -eq "SHELLDLL_DefView" -and [DesktopVerifier]::IsWindowVisible($child)) {
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

function Test-IsTopLevelWindow([IntPtr]$Window) {
    $script:topLevelFound = $false
    [DesktopVerifier]::EnumWindows({
        param($candidate, $data)
        if ($candidate -eq $Window) {
            $script:topLevelFound = $true
            return $false
        }
        return $true
    }, [IntPtr]::Zero) | Out-Null
    return $script:topLevelFound
}

function Test-WindowRegionPoint([IntPtr]$Window, [int]$X, [int]$Y) {
    $region = [DesktopVerifier]::CreateRectRgn(0, 0, 0, 0)
    try {
        if ([DesktopVerifier]::GetWindowRgn($Window, $region) -le 0) {
            throw "Could not read the CrabDesk surface region."
        }
        return [DesktopVerifier]::PtInRegion($region, $X, $Y)
    }
    finally {
        [void][DesktopVerifier]::DeleteObject($region)
    }
}

function Get-BoxRegionHeight([IntPtr]$Window, [int]$X, [int]$Top, [int]$MaximumHeight) {
    $region = [DesktopVerifier]::CreateRectRgn(0, 0, 0, 0)
    try {
        if ([DesktopVerifier]::GetWindowRgn($Window, $region) -le 0) {
            throw "Could not read the CrabDesk surface region."
        }
        $last = $Top - 1
        for ($y = $Top; $y -lt $Top + $MaximumHeight; $y++) {
            if ([DesktopVerifier]::PtInRegion($region, $X, $y)) {
                $last = $y
            }
        }
        return $last - $Top + 1
    }
    finally {
        [void][DesktopVerifier]::DeleteObject($region)
    }
}

function Send-WinD {
    [DesktopVerifier]::keybd_event(0x5B, 0, 0, [UIntPtr]::Zero)
    [DesktopVerifier]::keybd_event(0x44, 0, 0, [UIntPtr]::Zero)
    [DesktopVerifier]::keybd_event(0x44, 0, 2, [UIntPtr]::Zero)
    [DesktopVerifier]::keybd_event(0x5B, 0, 2, [UIntPtr]::Zero)
}

function Send-WinM {
    [DesktopVerifier]::keybd_event(0x5B, 0, 0, [UIntPtr]::Zero)
    [DesktopVerifier]::keybd_event(0x4D, 0, 0, [UIntPtr]::Zero)
    [DesktopVerifier]::keybd_event(0x4D, 0, 2, [UIntPtr]::Zero)
    [DesktopVerifier]::keybd_event(0x5B, 0, 2, [UIntPtr]::Zero)
}

function Open-And-Close-TaskView {
    [DesktopVerifier]::keybd_event(0x5B, 0, 0, [UIntPtr]::Zero)
    [DesktopVerifier]::keybd_event(0x09, 0, 0, [UIntPtr]::Zero)
    [DesktopVerifier]::keybd_event(0x09, 0, 2, [UIntPtr]::Zero)
    [DesktopVerifier]::keybd_event(0x5B, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 1200
    [DesktopVerifier]::keybd_event(0x1B, 0, 0, [UIntPtr]::Zero)
    [DesktopVerifier]::keybd_event(0x1B, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 900
}

function Assert-SurfaceAtPoint([IntPtr]$Surface, [DesktopVerifier+Point]$Point, [string]$Context) {
    if (-not [DesktopVerifier]::IsWindowVisible($Surface)) {
        throw "Desktop surface was hidden $Context."
    }
    $bounds = [DesktopVerifier+Rect]::new()
    if (-not [DesktopVerifier]::GetWindowRect($Surface, [ref]$bounds) -or
        -not (Test-WindowRegionPoint $Surface ($Point.X - $bounds.Left) ($Point.Y - $bounds.Top))) {
        throw "Desktop surface did not retain the expected box region $Context."
    }
}

function New-CoverageWindow([uint32]$Style, [int]$X, [int]$Y, [int]$Width, [int]$Height, [string]$Title) {
    $window = [DesktopVerifier]::CreateWindowEx(
        0,
        "BUTTON",
        $Title,
        $Style,
        $X,
        $Y,
        $Width,
        $Height,
        [IntPtr]::Zero,
        [IntPtr]::Zero,
        [IntPtr]::Zero,
        [IntPtr]::Zero)
    if ($window -eq [IntPtr]::Zero) {
        throw "Could not create the normal-window coverage probe."
    }
    [void][DesktopVerifier]::SetWindowPos($window, [IntPtr]::Zero, $X, $Y, $Width, $Height, 0x0040)
    [void][DesktopVerifier]::SetWindowPos($window, [IntPtr](-1), $X, $Y, $Width, $Height, 0x0040)
    [void][DesktopVerifier]::SetWindowPos($window, [IntPtr](-2), $X, $Y, $Width, $Height, 0x0040)
    [void][DesktopVerifier]::BringWindowToTop($window)
    [void][DesktopVerifier]::SetForegroundWindow($window)
    [void][DesktopVerifier]::UpdateWindow($window)
    Start-Sleep -Milliseconds 500
    if (([DesktopVerifier]::GetWindowLongPtr($window, -20).ToInt64() -band 0x00000008L) -ne 0) {
        [void][DesktopVerifier]::DestroyWindow($window)
        throw "The coverage probe remained topmost and cannot validate normal window layering."
    }
    return $window
}

function Get-ExplorerHideIcons {
    try {
        return [int](Get-ItemPropertyValue "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" HideIcons)
    }
    catch {
        return 0
    }
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("CrabDesk.DesktopTest." + [Guid]::NewGuid().ToString("N"))
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
$previousDataDirectory = $env:CRABDESK_DATA_DIR
$previousHideIcons = Get-ExplorerHideIcons
$env:CRABDESK_DATA_DIR = $testRoot
$boxId = [Guid]::NewGuid()
$ruleId = [Guid]::NewGuid()
$desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
$testStem = "CrabDeskCollapseTest-" + [Guid]::NewGuid().ToString("N")
$desktopTestFile = Join-Path $desktop ($testStem + ".txt")
[System.IO.File]::WriteAllText($desktopTestFile, "CrabDesk collapsed-box icon visibility test")
$boxX = 650
$boxY = 100
$boxWidth = 420
$boxHeight = 300
$titleHeight = 52
$config = @{
    SchemaVersion = 13
    Settings = @{
        TakeOverDesktop = $true
        ShowSystemItems = $false
        ConfirmDeleteBox = $true
        ThemeMode = 0
        DesktopBehavior = @{
            LaunchToTray = $false
            RefreshAfterRename = $true
            ShowDesktopContextMenu = $false
            ToggleIconsOnDesktopDoubleClick = $false
            ExpandBoxOnHover = $true
        }
        Backup = @{ DailyBackup = $false; RetentionDays = 7; BackupDirectory = "" }
        Appearance = @{ AnimationEnabled = $true }
        Updates = @{ CheckOnStartup = $false; Channel = 0 }
    }
    Boxes = @(@{
        Id = $boxId
        Title = "Desktop host verification"
        MonitorId = "primary"
        Bounds = @{ X = $boxX; Y = $boxY; Width = $boxWidth; Height = $boxHeight }
        IsCollapsed = $true
        Appearance = @{
            Background = "#FF2A2D32"
            Accent = "#FF4EA1D3"
            Opacity = 1
            IconSize = 42
            LabelFontSize = 8.5
            ShowItemLabels = $true
            ShowShortcutBadges = $true
            TitleBarHeight = $titleHeight
            TitleAlignment = 0
            TitleColor = "Auto"
            TitleFontSize = 10
            TitleFontBold = $true
            ShowCollapseButton = $true
        }
    })
    Assignments = @{}
    Organization = @{
        Enabled = $true
        RunOnStartup = $true
        RunOnDesktopChanges = $false
        ReassignExistingItems = $false
    }
    OrganizationRules = @(@{
        Id = $ruleId
        Title = "Collapsed box verification"
        Enabled = $true
        Priority = 10
        ItemKinds = @(0)
        NamePattern = $testStem
        Extensions = @(".txt")
        Action = 0
        TargetBoxId = $boxId
    })
}
$config | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $testRoot "config.json") -Encoding UTF8

$process = $null
$forcedProcess = $null
$coverWindow = [IntPtr]::Zero
$fullscreenWindow = [IntPtr]::Zero
$inputForm = $null
$originalCursor = [DesktopVerifier+Point]::new()
[void][DesktopVerifier]::GetCursorPos([ref]$originalCursor)
try {
    $process = Start-Process -FilePath $exe -PassThru
    Start-Sleep -Seconds 8
    $process.Refresh()
    $settingsHandle = $process.MainWindowHandle
    if ($settingsHandle -eq [IntPtr]::Zero) {
        throw "CrabDesk settings window was not available."
    }
    [void][DesktopVerifier]::PostMessage($settingsHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    Start-Sleep -Seconds 2
    $process.Refresh()
    if ($process.HasExited -or $process.MainWindowHandle -ne [IntPtr]::Zero) {
        throw "Closing settings did not keep CrabDesk running only in the tray."
    }

    $surface = Find-DesktopSurface $process.Id
    if ($surface -eq [IntPtr]::Zero) {
        throw "CrabDesk desktop surface was not attached to SHELLDLL_DefView."
    }
    $surfaceStyle = [DesktopVerifier]::GetWindowLongPtr($surface, -16).ToInt64()
    $surfaceExtendedStyle = [DesktopVerifier]::GetWindowLongPtr($surface, -20).ToInt64()
    if (($surfaceStyle -band 0x40000000L) -eq 0 -or
        ($surfaceExtendedStyle -band 0x00000080L) -eq 0 -or
        ($surfaceExtendedStyle -band 0x08000000L) -eq 0 -or
        ($surfaceExtendedStyle -band 0x00000008L) -ne 0) {
        throw "Desktop surface styles do not guarantee direct non-activating child input without topmost. Style=$surfaceStyle, ExStyle=$surfaceExtendedStyle"
    }
    if (Test-IsTopLevelWindow $surface) {
        throw "Desktop surface was enumerated as a top-level window and could enter Alt+Tab or the taskbar."
    }
    $initialSurfaceBounds = [DesktopVerifier+Rect]::new()
    [void][DesktopVerifier]::GetWindowRect($surface, [ref]$initialSurfaceBounds)
    [void][DesktopVerifier]::SetCursorPos($initialSurfaceBounds.Right - 10, $initialSurfaceBounds.Bottom - 10)
    Start-Sleep -Milliseconds 350
    $emptyAreaExtendedStyle = [DesktopVerifier]::GetWindowLongPtr($surface, -20).ToInt64()
    if (($emptyAreaExtendedStyle -band 0x00000020L) -eq 0) {
        throw "Desktop surface did not become click-through over an empty desktop area. ExStyle=$emptyAreaExtendedStyle"
    }
    Send-WinD
    Start-Sleep -Milliseconds 900
    $collapsedHeight = Get-BoxRegionHeight $surface ($boxX + 200) $boxY $boxHeight
    if ($collapsedHeight -ne $titleHeight -or
        (Test-WindowRegionPoint $surface ($boxX + 200) ($boxY + $titleHeight + 12))) {
        throw "Collapsed box region did not use the configured 52px title bar."
    }
    if ((Get-ExplorerHideIcons) -ne 1) {
        throw "Explorer native icons were not hidden during desktop takeover."
    }
    $liveConfig = Get-Content -LiteralPath (Join-Path $testRoot "config.json") -Raw -Encoding UTF8 | ConvertFrom-Json
    if (@($liveConfig.Assignments.PSObject.Properties).Count -ne 1) {
        throw "The collapsed-box test item was not assigned to the verification box."
    }
    $surfaceBounds = [DesktopVerifier+Rect]::new()
    [void][DesktopVerifier]::GetWindowRect($surface, [ref]$surfaceBounds)
    [void][DesktopVerifier]::SetCursorPos($surfaceBounds.Right - 10, $surfaceBounds.Bottom - 10)
    Start-Sleep -Milliseconds 250
    [void][DesktopVerifier]::SetCursorPos(
        $surfaceBounds.Left + $boxX + 20,
        $surfaceBounds.Top + $boxY + 20)
    [DesktopVerifier]::mouse_event(0x0001, 10, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 900
    $contentExtendedStyle = [DesktopVerifier]::GetWindowLongPtr($surface, -20).ToInt64()
    if (($contentExtendedStyle -band 0x00000020L) -ne 0) {
        throw "Desktop surface remained click-through while the pointer was over box content. ExStyle=$contentExtendedStyle"
    }
    $hoverExpandedHeight = Get-BoxRegionHeight $surface ($boxX + 200) $boxY $boxHeight
    if ($hoverExpandedHeight -lt 295) {
        $screenProbe = [DesktopVerifier+Point]::new()
        $screenProbe.X = $surfaceBounds.Left + $boxX + 20
        $screenProbe.Y = $surfaceBounds.Top + $boxY + 20
        $hit = [DesktopVerifier]::WindowFromPoint($screenProbe)
        $parent = [DesktopVerifier]::GetParent($surface)
        $parentProbe = $screenProbe
        [void][DesktopVerifier]::ScreenToClient($parent, [ref]$parentProbe)
        $child = [DesktopVerifier]::ChildWindowFromPointEx($parent, $parentProbe, 0)
        $hitClass = [System.Text.StringBuilder]::new(128)
        $childClass = [System.Text.StringBuilder]::new(128)
        [void][DesktopVerifier]::GetClassName($hit, $hitClass, 128)
        [void][DesktopVerifier]::GetClassName($child, $childClass, 128)
        throw "A real mouse move over the collapsed title bar did not expand the box region. Height=$hoverExpandedHeight, WindowFromPoint=$hit/$($hitClass.ToString()), ChildFromParent=$child/$($childClass.ToString()), Surface=$surface"
    }
    [void][DesktopVerifier]::SetCursorPos($surfaceBounds.Right - 10, $surfaceBounds.Bottom - 10)
    Start-Sleep -Milliseconds 900
    $hoverRestoredHeight = Get-BoxRegionHeight $surface ($boxX + 200) $boxY $boxHeight
    if ($hoverRestoredHeight -ne $titleHeight) {
        throw "Leaving the hover-expanded box did not restore its collapsed region. Height=$hoverRestoredHeight"
    }
    $collapseX = $boxX + $boxWidth - 49
    $collapseY = $boxY + 26
    $collapsePoint = [IntPtr](($collapseY -shl 16) -bor ($collapseX -band 0xffff))
    # Use direct messages so hover expansion cannot make the box visually expanded before the animation starts.
    [void][DesktopVerifier]::SendMessage($surface, 0x0201, [IntPtr]1, $collapsePoint)
    [void][DesktopVerifier]::SendMessage($surface, 0x0202, [IntPtr]::Zero, $collapsePoint)
    Start-Sleep -Milliseconds 60
    $animatingHeight = Get-BoxRegionHeight $surface ($boxX + 200) $boxY $boxHeight
    if ($animatingHeight -le 52 -or $animatingHeight -ge 300) {
        throw "Expand animation did not expose an intermediate window-region height. Height=$animatingHeight"
    }
    Start-Sleep -Milliseconds 220
    $expandedAfterAnimation = Get-BoxRegionHeight $surface ($boxX + 200) $boxY $boxHeight
    if ($expandedAfterAnimation -lt 295) {
        throw "Expand animation did not reach the full box height."
    }
    [void][DesktopVerifier]::SendMessage($surface, 0x0201, [IntPtr]1, $collapsePoint)
    [void][DesktopVerifier]::SendMessage($surface, 0x0202, [IntPtr]::Zero, $collapsePoint)
    [void][DesktopVerifier]::SetCursorPos($surfaceBounds.Right - 10, $surfaceBounds.Bottom - 10)
    Start-Sleep -Milliseconds 260
    $collapsedAfterAnimation = Get-BoxRegionHeight $surface ($boxX + 200) $boxY $boxHeight
    if ($collapsedAfterAnimation -ne $titleHeight -or
        (Test-WindowRegionPoint $surface ($boxX + 200) ($boxY + $titleHeight + 12))) {
        throw "Collapse animation did not return to the configured title-bar height."
    }
    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        Send-WinD
        Start-Sleep -Milliseconds 900
        if (-not [DesktopVerifier]::IsWindowVisible($surface)) {
            throw "Desktop surface was hidden after Win+D attempt $($attempt + 1)."
        }
    }
    $probePoint = [DesktopVerifier+Point]::new()
    $probePoint.X = $surfaceBounds.Left + $boxX + 20
    $probePoint.Y = $surfaceBounds.Top + $boxY + 20
    Assert-SurfaceAtPoint $surface $probePoint "after repeated Win+D"

    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        Send-WinM
        Start-Sleep -Milliseconds 700
        Assert-SurfaceAtPoint $surface $probePoint "after Win+M attempt $($attempt + 1)"
    }

    Open-And-Close-TaskView
    Assert-SurfaceAtPoint $surface $probePoint "after opening and closing Task View"

    $coverWindow = New-CoverageWindow 0x10CF0000 ($probePoint.X - 120) ($probePoint.Y - 80) 360 240 "CrabDesk normal-window coverage"
    $coverHit = [DesktopVerifier]::WindowFromPoint($probePoint)
    if ($coverHit -ne $coverWindow) {
        $coverHitClass = [System.Text.StringBuilder]::new(128)
        [void][DesktopVerifier]::GetClassName($coverHit, $coverHitClass, 128)
        throw "A normal top-level window did not cover the desktop box. Window=$coverWindow, Hit=$coverHit, Class=$($coverHitClass.ToString())"
    }
    $script:coverageClickReceived = $false
    $inputForm = [System.Windows.Forms.Form]::new()
    $inputForm.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedToolWindow
    $inputForm.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
    $inputForm.Location = [System.Drawing.Point]::new($probePoint.X - 80, $probePoint.Y - 50)
    $inputForm.ClientSize = [System.Drawing.Size]::new(160, 100)
    $inputButton = [System.Windows.Forms.Button]::new()
    $inputButton.Dock = [System.Windows.Forms.DockStyle]::Fill
    $inputButton.Text = "Input coverage probe"
    $inputButton.Add_Click({ $script:coverageClickReceived = $true })
    $inputForm.Controls.Add($inputButton)
    $inputForm.Show()
    $inputForm.Activate()
    [void][DesktopVerifier]::SetCursorPos($probePoint.X, $probePoint.Y)
    [DesktopVerifier]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [DesktopVerifier]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    $inputDeadline = [DateTime]::UtcNow.AddSeconds(2)
    do {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 20
    } while (-not $script:coverageClickReceived -and [DateTime]::UtcNow -lt $inputDeadline)
    if (-not $script:coverageClickReceived) {
        throw "Desktop input routing swallowed a click intended for a normal window covering the box."
    }
    $inputForm.Close()
    $inputForm.Dispose()
    $inputForm = $null
    [void][DesktopVerifier]::DestroyWindow($coverWindow)
    $coverWindow = [IntPtr]::Zero
    Start-Sleep -Milliseconds 400
    Assert-SurfaceAtPoint $surface $probePoint "after closing the normal coverage window"

    $fullscreenWindow = New-CoverageWindow ([uint32]2415919104) $surfaceBounds.Left $surfaceBounds.Top ($surfaceBounds.Right - $surfaceBounds.Left) ($surfaceBounds.Bottom - $surfaceBounds.Top) "CrabDesk fullscreen coverage"
    $fullscreenHit = [DesktopVerifier]::WindowFromPoint($probePoint)
    if ($fullscreenHit -ne $fullscreenWindow) {
        $fullscreenHitClass = [System.Text.StringBuilder]::new(128)
        [void][DesktopVerifier]::GetClassName($fullscreenHit, $fullscreenHitClass, 128)
        throw "A non-topmost fullscreen window did not cover the desktop box. Window=$fullscreenWindow, Hit=$fullscreenHit, Class=$($fullscreenHitClass.ToString())"
    }
    [void][DesktopVerifier]::DestroyWindow($fullscreenWindow)
    $fullscreenWindow = [IntPtr]::Zero
    Start-Sleep -Milliseconds 400
    Assert-SurfaceAtPoint $surface $probePoint "after closing the fullscreen coverage window"

    $exitCommand = Start-Process -FilePath $exe -ArgumentList "--exit-existing" -Wait -PassThru
    if ($exitCommand.ExitCode -ne 0) {
        throw "Tray-equivalent exit command returned code $($exitCommand.ExitCode)."
    }
    if (-not $process.WaitForExit(15000)) {
        throw "CrabDesk did not exit through the tray-equivalent signal."
    }
    Start-Sleep -Seconds 3
    if ((Get-ExplorerHideIcons) -ne $previousHideIcons) {
        throw "Clean exit did not restore the original Explorer icon visibility."
    }

    $forcedProcess = Start-Process -FilePath $exe -PassThru
    Start-Sleep -Seconds 8
    if ($forcedProcess.HasExited) {
        throw "CrabDesk exited before the forced-exit recovery test."
    }
    Stop-Process -Id $forcedProcess.Id -Force -ErrorAction Stop
    $forcedProcess.WaitForExit()
    Start-Sleep -Seconds 5
    if ((Get-ExplorerHideIcons) -ne $previousHideIcons) {
        throw "IconGuard did not restore the original Explorer icon visibility after forced exit."
    }
}
finally {
    if ($coverWindow -ne [IntPtr]::Zero) {
        [void][DesktopVerifier]::DestroyWindow($coverWindow)
    }
    if ($fullscreenWindow -ne [IntPtr]::Zero) {
        [void][DesktopVerifier]::DestroyWindow($fullscreenWindow)
    }
    if ($null -ne $inputForm) {
        $inputForm.Close()
        $inputForm.Dispose()
    }
    [void][DesktopVerifier]::SetCursorPos($originalCursor.X, $originalCursor.Y)
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    if ($forcedProcess -and -not $forcedProcess.HasExited) {
        Stop-Process -Id $forcedProcess.Id -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $desktopTestFile -Force -ErrorAction SilentlyContinue
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

Write-Host "Desktop host, child/tool-window styles, assigned-icon hiding, real-mouse hover expansion, animated collapse, Win+D, Win+M, Task View, normal/fullscreen coverage, clean exit and IconGuard recovery passed."
