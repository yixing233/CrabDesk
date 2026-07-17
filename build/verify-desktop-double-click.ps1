param(
    [string]$Executable = "..\artifacts\publish\win-x64\CrabDesk.WinUI.exe"
)

$ErrorActionPreference = "Stop"
$exe = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Executable))
if (-not (Test-Path -LiteralPath $exe)) {
    throw "CrabDesk executable not found: $exe"
}
if (Get-Process CrabDesk.WinUI -ErrorAction SilentlyContinue) {
    throw "Close the running CrabDesk instance before the desktop double-click verifier."
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName PresentationFramework
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class CrabDeskDoubleClickVerifier {
    public delegate bool EnumWindowsProc(IntPtr window, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct HitTest {
        public Point Point; public uint Flags; public int Item; public int SubItem; public int Group;
    }
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr data);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern int GetWindowRgnBox(IntPtr window, out Rect rect);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string windowName);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] public static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr window, StringBuilder className, int capacity);
    [DllImport("user32.dll", EntryPoint="SendMessageW")] public static extern IntPtr SendMessageRemote(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll")] public static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint allocationType, uint protection);
    [DllImport("kernel32.dll")] public static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint freeType);
    [DllImport("kernel32.dll")] public static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out UIntPtr written);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);

    public static IntPtr FindDesktopListView() {
        IntPtr result = IntPtr.Zero;
        EnumWindows((top, data) => {
            var view = FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view == IntPtr.Zero) return true;
            result = FindWindowEx(view, IntPtr.Zero, "SysListView32", "FolderView");
            return result == IntPtr.Zero;
        }, IntPtr.Zero);
        return result;
    }

    public static Point FindEmptyDesktopPoint(IntPtr listView) {
        Rect bounds;
        if (!GetClientRect(listView, out bounds)) throw new InvalidOperationException("Desktop bounds unavailable.");
        for (int y = bounds.Bottom - 40; y >= 40; y -= 40) {
            for (int x = bounds.Right - 40; x >= 40; x -= 40) {
                var hit = new HitTest { Point = new Point { X = x, Y = y } };
                if (HitTestItem(listView, hit) >= 0) continue;
                var screen = hit.Point;
                if (!ClientToScreen(listView, ref screen)) continue;
                var visible = WindowFromPoint(screen);
                if (visible == listView || IsChild(listView, visible) || IsChild(visible, listView)) return screen;
                var root = GetAncestor(visible, 2);
                var rootName = new StringBuilder(64);
                GetClassName(root, rootName, rootName.Capacity);
                if (rootName.ToString() == "WorkerW" || rootName.ToString() == "Progman") return screen;
            }
        }
        throw new InvalidOperationException("No visible empty desktop point was found.");
    }

    private static int HitTestItem(IntPtr listView, HitTest hit) {
        uint processId;
        GetWindowThreadProcessId(listView, out processId);
        var process = OpenProcess(0x0008 | 0x0010 | 0x0020 | 0x0400, false, processId);
        if (process == IntPtr.Zero) return 0;
        int size = Marshal.SizeOf(typeof(HitTest));
        var remote = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)size, 0x1000 | 0x2000, 0x04);
        if (remote == IntPtr.Zero) { CloseHandle(process); return 0; }
        var local = Marshal.AllocHGlobal(size);
        try {
            Marshal.StructureToPtr(hit, local, false);
            var buffer = new byte[size];
            Marshal.Copy(local, buffer, 0, size);
            UIntPtr written;
            if (!WriteProcessMemory(process, remote, buffer, size, out written)) return 0;
            return SendMessageRemote(listView, 0x1012, IntPtr.Zero, remote).ToInt32();
        }
        finally {
            Marshal.FreeHGlobal(local);
            VirtualFreeEx(process, remote, UIntPtr.Zero, 0x8000);
            CloseHandle(process);
        }
    }

    public static void ToggleDesktop() {
        keybd_event(0x5B, 0, 0, UIntPtr.Zero);
        keybd_event(0x44, 0, 0, UIntPtr.Zero);
        keybd_event(0x44, 0, 2, UIntPtr.Zero);
        keybd_event(0x5B, 0, 2, UIntPtr.Zero);
    }

    public static void Click(Point point) {
        SetCursorPos(point.X, point.Y);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }

}
'@

function Find-DesktopSurface([int]$ProcessId) {
    $script:surfaceHandle = [IntPtr]::Zero
    [CrabDeskDoubleClickVerifier]::EnumWindows({
        param($top, $data)
        [CrabDeskDoubleClickVerifier]::EnumChildWindows($top, {
            param($child, $childData)
            [uint32]$ownerProcessId = 0
            [void][CrabDeskDoubleClickVerifier]::GetWindowThreadProcessId($child, [ref]$ownerProcessId)
            if ($ownerProcessId -eq $ProcessId) {
                $parent = [CrabDeskDoubleClickVerifier]::GetParent($child)
                $className = [System.Text.StringBuilder]::new(128)
                [void][CrabDeskDoubleClickVerifier]::GetClassName($parent, $className, 128)
                if ($className.ToString() -in @("Progman", "WorkerW", "SHELLDLL_DefView") -and [CrabDeskDoubleClickVerifier]::IsWindowVisible($child)) {
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

function Get-HideIcons {
    $value = Get-ItemPropertyValue "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" HideIcons -ErrorAction SilentlyContinue
    if ($null -eq $value) { return 0 }
    return [int]$value
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("CrabDesk.DoubleClickTest." + [Guid]::NewGuid().ToString("N"))
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
$boxId = [Guid]::NewGuid()
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$scale = $screen.Width / [System.Windows.SystemParameters]::PrimaryScreenWidth
$boxX = [Math]::Max(40, [Math]::Round(($screen.Width / $scale) - 380))
$boxY = [Math]::Max(50, [Math]::Round(($screen.Height / $scale) - 300))
$config = @{
    SchemaVersion = 10
    Settings = @{
        TakeOverDesktop = $true
        ShowSystemItems = $false
        ConfirmDeleteBox = $true
        ThemeMode = 0
        DesktopBehavior = @{
            LaunchToTray = $true
            RefreshAfterRename = $true
            ToggleIconsOnDesktopDoubleClick = $true
        }
    }
    Boxes = @(@{
        Id = $boxId
        Title = "Double-click test"
        MonitorId = "primary"
        Bounds = @{ X = $boxX; Y = $boxY; Width = 320; Height = 220 }
    })
    Assignments = @{}
}
$config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $testRoot "config.json") -Encoding UTF8

$originalHidden = Get-HideIcons
$previousDataDirectory = $env:CRABDESK_DATA_DIR
$env:CRABDESK_DATA_DIR = $testRoot
$process = $null
$desktopShown = $false
$shell = $null
try {
    $process = Start-Process -FilePath $exe -PassThru
    Start-Sleep -Seconds 6
    if ($process.HasExited) { throw "CrabDesk exited before the double-click test." }

    $shell = New-Object -ComObject Shell.Application
    $shell.MinimizeAll()
    $desktopShown = $true
    Start-Sleep -Seconds 1
    $listView = [CrabDeskDoubleClickVerifier]::FindDesktopListView()
    if ($listView -eq [IntPtr]::Zero) { throw "Explorer desktop list view was not found." }
    $surface = Find-DesktopSurface $process.Id
    if ($surface -eq [IntPtr]::Zero) { throw "CrabDesk desktop surface was not found." }
    if ((Get-HideIcons) -ne $originalHidden) { throw "Desktop hosting changed Explorer's native icon visibility before the gesture." }
    $visibleRegion = [CrabDeskDoubleClickVerifier+Rect]::new()
    if ([CrabDeskDoubleClickVerifier]::GetWindowRgnBox($surface, [ref]$visibleRegion) -le 1) {
        throw "CrabDesk did not render the desktop box surface."
    }
    $point = [CrabDeskDoubleClickVerifier]::FindEmptyDesktopPoint($listView)

    [CrabDeskDoubleClickVerifier]::Click($point)
    Start-Sleep -Milliseconds 80
    [CrabDeskDoubleClickVerifier]::Click($point)
    Start-Sleep -Milliseconds 900
    if ((Get-HideIcons) -ne 1) { throw "Double-click did not hide Explorer's native desktop icons." }
    $hiddenRegion = [CrabDeskDoubleClickVerifier+Rect]::new()
    if ([CrabDeskDoubleClickVerifier]::GetWindowRgnBox($surface, [ref]$hiddenRegion) -le 1) {
        throw "Double-click unexpectedly removed the desktop box surface."
    }

    Start-Sleep -Milliseconds 650
    [CrabDeskDoubleClickVerifier]::Click($point)
    Start-Sleep -Milliseconds 80
    [CrabDeskDoubleClickVerifier]::Click($point)
    Start-Sleep -Milliseconds 900
    if ((Get-HideIcons) -ne $originalHidden) { throw "Double-click did not restore Explorer's native desktop icons." }
    $restoredRegion = [CrabDeskDoubleClickVerifier+Rect]::new()
    if ([CrabDeskDoubleClickVerifier]::GetWindowRgnBox($surface, [ref]$restoredRegion) -le 1 -or
        [Math]::Abs($restoredRegion.Left - $visibleRegion.Left) -gt 2 -or
        [Math]::Abs($restoredRegion.Top - $visibleRegion.Top) -gt 2) {
        throw "Double-click changed the desktop box surface geometry."
    }
    Write-Host "Desktop empty-area double-click hid and restored Explorer's native icons while preserving the CrabDesk box surface."

    $exitCommand = Start-Process -FilePath $exe -ArgumentList "--exit-existing" -Wait -PassThru
    if ($exitCommand.ExitCode -ne 0) {
        throw "The desktop-double-click exit command returned code $($exitCommand.ExitCode)."
    }
    if (-not $process.WaitForExit(10000)) {
        throw "CrabDesk did not exit cleanly after the double-click verifier."
    }
    Start-Sleep -Seconds 2
    if ((Get-HideIcons) -ne $originalHidden) {
        throw "Desktop-double-click validation did not restore the original Explorer icon state."
    }
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    if ($desktopShown) {
        $shell.UndoMinimizeAll()
    }
    if ($null -ne $shell) {
        [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
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
