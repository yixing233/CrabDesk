$ErrorActionPreference = "Stop"

if (-not ("CrabDeskHardwareProbe" -as [type])) {
    $source = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public sealed class CrabDeskMonitorProbe
{
    public string Id { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int WorkLeft { get; set; }
    public int WorkTop { get; set; }
    public int WorkWidth { get; set; }
    public int WorkHeight { get; set; }
    public uint DpiX { get; set; }
    public uint DpiY { get; set; }
    public bool IsPrimary { get; set; }
}

public sealed class CrabDeskSurfaceProbe
{
    public long Handle { get; set; }
    public string ClassName { get; set; }
    public long ParentHandle { get; set; }
    public string ParentClass { get; set; }
    public bool Visible { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long Style { get; set; }
    public long ExtendedStyle { get; set; }
}

public static class CrabDeskHardwareProbe
{
    public delegate bool EnumWindowProc(IntPtr hwnd, IntPtr data);
    public delegate bool EnumMonitorProc(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, EnumMonitorProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);

    public static CrabDeskMonitorProbe[] GetMonitors()
    {
        var result = new List<CrabDeskMonitorProbe>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>(), DeviceName = "" };
            if (!GetMonitorInfo(monitor, ref info)) return true;
            uint dpiX = 96, dpiY = 96;
            try
            {
                uint readX;
                uint readY;
                if (GetDpiForMonitor(monitor, 0, out readX, out readY) == 0)
                {
                    dpiX = readX;
                    dpiY = readY;
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            result.Add(new CrabDeskMonitorProbe
            {
                Id = info.DeviceName,
                Left = info.Monitor.Left,
                Top = info.Monitor.Top,
                Width = info.Monitor.Right - info.Monitor.Left,
                Height = info.Monitor.Bottom - info.Monitor.Top,
                WorkLeft = info.WorkArea.Left,
                WorkTop = info.WorkArea.Top,
                WorkWidth = info.WorkArea.Right - info.WorkArea.Left,
                WorkHeight = info.WorkArea.Bottom - info.WorkArea.Top,
                DpiX = dpiX,
                DpiY = dpiY,
                IsPrimary = (info.Flags & 1) != 0
            });
            return true;
        }, IntPtr.Zero);
        return result.ToArray();
    }

    public static CrabDeskSurfaceProbe[] GetDesktopSurfaces(int targetProcessId)
    {
        var result = new List<CrabDeskSurfaceProbe>();
        var seen = new HashSet<long>();
        EnumWindows((top, data) =>
        {
            EnumChildWindows(top, (child, childData) =>
            {
                uint processId;
                GetWindowThreadProcessId(child, out processId);
                if (processId != targetProcessId) return true;
                var parent = GetParent(child);
                var parentClass = ReadClass(parent);
                if (!String.Equals(parentClass, "Progman", StringComparison.Ordinal) &&
                    !String.Equals(parentClass, "WorkerW", StringComparison.Ordinal)) return true;
                var numericHandle = child.ToInt64();
                if (!seen.Add(numericHandle)) return true;
                Rect rect;
                GetWindowRect(child, out rect);
                result.Add(new CrabDeskSurfaceProbe
                {
                    Handle = numericHandle,
                    ClassName = ReadClass(child),
                    ParentHandle = parent.ToInt64(),
                    ParentClass = ReadClass(parent),
                    Visible = IsWindowVisible(child),
                    Left = rect.Left,
                    Top = rect.Top,
                    Width = rect.Right - rect.Left,
                    Height = rect.Bottom - rect.Top,
                    Style = GetWindowLongPtr(child, -16).ToInt64(),
                    ExtendedStyle = GetWindowLongPtr(child, -20).ToInt64()
                });
                return true;
            }, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        return result.ToArray();
    }

    public static void SendWinD()
    {
        keybd_event(0x5B, 0, 0, UIntPtr.Zero);
        keybd_event(0x44, 0, 0, UIntPtr.Zero);
        keybd_event(0x44, 0, 2, UIntPtr.Zero);
        keybd_event(0x5B, 0, 2, UIntPtr.Zero);
    }

    private static string ReadClass(IntPtr handle)
    {
        var text = new StringBuilder(256);
        GetClassName(handle, text, text.Capacity);
        return text.ToString();
    }
}
'@
    Add-Type -TypeDefinition $source
}

function Get-CrabDeskExplorerHideIcons {
    try {
        return [int](Get-ItemPropertyValue "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" HideIcons)
    }
    catch {
        return 0
    }
}

function Get-CrabDeskExpectedRestoredHideIcons([string]$DataDirectory) {
    $path = Join-Path $DataDirectory "desktop-visibility.lock"
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        try {
            if (Test-Path -LiteralPath $path) {
                $recovery = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
                if ($null -ne $recovery.PreviousHidden) {
                    return [int][bool]$recovery.PreviousHidden
                }
            }
        }
        catch {
            if ([DateTime]::UtcNow -ge $deadline) { throw }
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "CrabDesk recovery marker was not available: $path"
}

function Get-CrabDeskOsDescription {
    $key = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion"
    $productName = [string]$key.ProductName
    if ([int]$key.CurrentBuildNumber -ge 22000 -and $productName.StartsWith("Windows 10")) {
        $productName = $productName.Replace("Windows 10", "Windows 11")
    }
    return "$productName $($key.DisplayVersion) (build $($key.CurrentBuildNumber).$($key.UBR))"
}

function Read-CrabDeskConfig([string]$DataDirectory) {
    $path = Join-Path $DataDirectory "config.json"
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        try {
            if (Test-Path -LiteralPath $path) {
                return Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
            }
        }
        catch {
            if ([DateTime]::UtcNow -ge $deadline) { throw }
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "CrabDesk config was not found: $path"
}

function Get-CrabDeskHardwareSnapshot {
    param(
        [Parameter(Mandatory)] [string]$DataDirectory,
        [int]$WaitSeconds = 30
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
    do {
        $processes = @(Get-Process CrabDesk.App -ErrorAction SilentlyContinue)
        if ($processes.Count -eq 1) {
            $process = $processes[0]
            $process.Refresh()
            $monitors = @([CrabDeskHardwareProbe]::GetMonitors())
            $surfaces = @([CrabDeskHardwareProbe]::GetDesktopSurfaces($process.Id))
            if ($monitors.Count -gt 0 -and $surfaces.Count -eq $monitors.Count) {
                break
            }
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)

    if ($processes.Count -ne 1) {
        throw "Exactly one CrabDesk.App process must be running; found $($processes.Count)."
    }
    if ($monitors.Count -eq 0) {
        throw "Windows did not report any active monitors."
    }
    if ($surfaces.Count -ne $monitors.Count) {
        throw "CrabDesk did not expose one desktop surface per monitor within $WaitSeconds seconds. Monitors=$($monitors.Count), Surfaces=$($surfaces.Count)."
    }

    $config = Read-CrabDeskConfig $DataDirectory
    $boxes = @($config.Boxes | ForEach-Object {
        $mappedPath = if ($null -ne $_.MappedFolder) { [string]$_.MappedFolder.Path } else { "" }
        [pscustomobject]@{
            Id = [string]$_.Id
            Title = [string]$_.Title
            MonitorId = [string]$_.MonitorId
            X = [double]$_.Bounds.X
            Y = [double]$_.Bounds.Y
            Width = [double]$_.Bounds.Width
            Height = [double]$_.Bounds.Height
            MappedPath = $mappedPath
            MappedAvailable = if ([string]::IsNullOrWhiteSpace($mappedPath)) { $null } else { Test-Path -LiteralPath $mappedPath -PathType Container }
        }
    })
    $process.Refresh()
    return [pscustomobject]@{
        CapturedAt = [DateTimeOffset]::Now
        Machine = [Environment]::MachineName
        OS = Get-CrabDeskOsDescription
        ProcessId = $process.Id
        ProcessStartTime = [DateTimeOffset]$process.StartTime
        Executable = $process.Path
        IconGuardProcessCount = @(Get-Process CrabDesk.IconGuard -ErrorAction SilentlyContinue).Count
        HideIcons = Get-CrabDeskExplorerHideIcons
        ExpectedRestoredHideIcons = Get-CrabDeskExpectedRestoredHideIcons $DataDirectory
        Monitors = $monitors
        Surfaces = $surfaces
        ConfigPath = Join-Path $DataDirectory "config.json"
        SchemaVersion = [int]$config.SchemaVersion
        Boxes = $boxes
        AssignmentCount = @($config.Assignments.psobject.Properties).Count
    }
}

function Get-CrabDeskTopologySignature($Snapshot) {
    return (@($Snapshot.Monitors | Sort-Object Id | ForEach-Object {
        "$($_.Id):$($_.Left),$($_.Top),$($_.Width)x$($_.Height)@$($_.DpiX)x$($_.DpiY):$($_.IsPrimary)"
    }) -join "|")
}

function Assert-CrabDeskHardwareSnapshot {
    param(
        [Parameter(Mandatory)] $Snapshot,
        [switch]$RequireMappedFolder
    )

    if ($Snapshot.IconGuardProcessCount -lt 1) {
        throw "IconGuard is not running."
    }
    if ($Snapshot.HideIcons -ne 1) {
        throw "Explorer native icons must remain hidden while CrabDesk owns the desktop surface."
    }
    if (@($Snapshot.Monitors | Where-Object IsPrimary).Count -ne 1) {
        throw "The active topology must contain exactly one primary monitor."
    }
    if (@($Snapshot.Boxes | Group-Object Id | Where-Object Count -gt 1).Count -gt 0) {
        throw "Duplicate box identifiers were found in config.json."
    }
    $mappedBoxes = @($Snapshot.Boxes | Where-Object { -not [string]::IsNullOrWhiteSpace($_.MappedPath) })
    if ($RequireMappedFolder -and $mappedBoxes.Count -eq 0) {
        throw "Create at least one mapped-folder box before this checkpoint."
    }
    $unavailableMapped = @($mappedBoxes | Where-Object { $_.MappedAvailable -ne $true })
    if ($unavailableMapped.Count -gt 0) {
        throw "Mapped folders are unavailable: $($unavailableMapped.MappedPath -join ', ')"
    }

    foreach ($surface in $Snapshot.Surfaces) {
        if (-not $surface.Visible -or $surface.ParentClass -notin @("Progman", "WorkerW")) {
            throw "Desktop surface $($surface.Handle) is not a visible Progman/WorkerW child."
        }
        if (($surface.Style -band 0x40000000) -eq 0) {
            throw "Desktop surface $($surface.Handle) does not have WS_CHILD."
        }
        if (($surface.ExtendedStyle -band 0x00040000) -ne 0) {
            throw "Desktop surface $($surface.Handle) unexpectedly has WS_EX_APPWINDOW."
        }
        $monitor = @($Snapshot.Monitors | Where-Object {
            [Math]::Abs($_.Left - $surface.Left) -le 2 -and
            [Math]::Abs($_.Top - $surface.Top) -le 2 -and
            [Math]::Abs($_.Width - $surface.Width) -le 2 -and
            [Math]::Abs($_.Height - $surface.Height) -le 2
        })
        if ($monitor.Count -ne 1) {
            throw "Desktop surface $($surface.Handle) does not match one active monitor rectangle."
        }
    }

    foreach ($box in $Snapshot.Boxes) {
        $monitor = @($Snapshot.Monitors | Where-Object Id -eq $box.MonitorId)
        if ($monitor.Count -ne 1) {
            throw "Box '$($box.Title)' references inactive monitor '$($box.MonitorId)'."
        }
        $dipWidth = $monitor[0].WorkWidth / ($monitor[0].DpiX / 96.0)
        $dipHeight = $monitor[0].WorkHeight / ($monitor[0].DpiY / 96.0)
        if ($box.X -lt -0.5 -or $box.Y -lt -0.5 -or
            $box.X + $box.Width -gt $dipWidth + 0.5 -or
            $box.Y + $box.Height -gt $dipHeight + 0.5) {
            throw "Box '$($box.Title)' is outside the visible DIP work area of '$($box.MonitorId)'."
        }
    }
}

function Test-CrabDeskWinD([int]$ProcessId, [int]$Attempts = 4) {
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        [CrabDeskHardwareProbe]::SendWinD()
        Start-Sleep -Milliseconds 900
        $surfaces = @([CrabDeskHardwareProbe]::GetDesktopSurfaces($ProcessId))
        if ($surfaces.Count -eq 0 -or @($surfaces | Where-Object { -not $_.Visible }).Count -gt 0) {
            throw "A CrabDesk desktop surface disappeared after Win+D attempt $attempt."
        }
    }
}

function Write-CrabDeskHardwareReport {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] $Report
    )
    $directory = Split-Path -Parent $Path
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    $Report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding UTF8
}
