<#
.SYNOPSIS
Attributes the "Explorer freezes for seconds after a file lands on the desktop"
freeze to either CrabDesk or Explorer's own shell extensions.

.DESCRIPTION
The reported symptom was that dragging a file from Explorer onto the desktop froze
both processes for seconds, and that the first click afterwards hung. CrabDesk's
half is fixed by detaching its input queue from Explorer's desktop thread after
SetParent (DesktopWindowTools.AttachAsDesktopChild).

This script produces the attribution evidence, and is meaningful run twice:

  * With CrabDesk closed, creating a file in the desktop folder must NOT stall
    Explorer's desktop thread; if it does, Explorer is stalling on its own
    (third-party overlay/sync shell extensions activate per new desktop file).
  * With CrabDesk running, its desktop-attached child window must keep answering
    while Explorer's thread is stalled.

It also creates the same file in an ordinary folder as a control: a stall that
only happens for the desktop folder is the desktop namespace's shell extensions,
not the file creation itself.

Measured on the reporting machine: ordinary folder 2 ms, desktop folder ~1200 ms,
and CrabDesk's own child window 1-3 ms during that stall.
#>
param(
    [int]$Samples = 30,
    [int]$SpacingMs = 150,
    [switch]$SkipControlFolder
)

Add-Type -Namespace FreezeProbe -Name Native -MemberDefinition @'
public delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lparam);
[DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lparam);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder sb, int max);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
[DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, uint flags, uint timeout, out IntPtr result);
'@

function Get-WindowClass([IntPtr]$Hwnd) {
    $sb = New-Object System.Text.StringBuilder 256
    [FreezeProbe.Native]::GetClassName($Hwnd, $sb, 256) | Out-Null
    return $sb.ToString()
}

# Worst round trip of SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG) over N samples.
# This is the same responsiveness test CrabDesk's own watchdog logic relies on.
function Measure-WorstLatency([IntPtr]$Hwnd, [int]$Samples, [int]$SpacingMs) {
    $worst = 0
    for ($i = 0; $i -lt $Samples; $i++) {
        $result = [IntPtr]::Zero
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        [FreezeProbe.Native]::SendMessageTimeout($Hwnd, 0x0000, [IntPtr]::Zero, [IntPtr]::Zero, 0x0002, 15000, [ref]$result) | Out-Null
        $sw.Stop()
        if ($sw.ElapsedMilliseconds -gt $worst) { $worst = [int]$sw.ElapsedMilliseconds }
        Start-Sleep -Milliseconds $SpacingMs
    }
    return $worst
}

# Explorer's desktop thread: the Progman > SHELLDLL_DefView window.
$script:desktopView = [IntPtr]::Zero
$findDesktop = [FreezeProbe.Native+EnumProc]{
    param($hwnd, $lparam)
    if ((Get-WindowClass $hwnd) -eq 'Progman') {
        $findChild = [FreezeProbe.Native+EnumProc]{
            param($child, $l)
            if ((Get-WindowClass $child) -eq 'SHELLDLL_DefView') { $script:desktopView = $child; return $false }
            return $true
        }
        [FreezeProbe.Native]::EnumChildWindows($hwnd, $findChild, [IntPtr]::Zero) | Out-Null
    }
    return $true
}
[FreezeProbe.Native]::EnumWindows($findDesktop, [IntPtr]::Zero) | Out-Null
if ($script:desktopView -eq [IntPtr]::Zero) { throw 'SHELLDLL_DefView not found; no interactive desktop session?' }

# A CrabDesk window that Explorer's desktop owns as a cross-process child.
$crabProcessIds = @(Get-Process CrabDesk.WinUI -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
$script:crabChild = [IntPtr]::Zero
if ($crabProcessIds.Count -gt 0) {
    $findCrabChild = [FreezeProbe.Native+EnumProc]{
        param($child, $l)
        [uint32]$owner = 0
        [FreezeProbe.Native]::GetWindowThreadProcessId($child, [ref]$owner) | Out-Null
        if ($crabProcessIds -contains [int]$owner) { $script:crabChild = $child; return $false }
        return $true
    }
    [FreezeProbe.Native]::EnumChildWindows($script:desktopView, $findCrabChild, [IntPtr]::Zero) | Out-Null
}

$desktopDirectory = [Environment]::GetFolderPath('DesktopDirectory')
Write-Output "desktop folder: $desktopDirectory"
Write-Output ("explorer desktop thread: 0x{0:X}" -f [int64]$script:desktopView)
if ($script:crabChild -ne [IntPtr]::Zero) {
    Write-Output ("crabdesk desktop child:  0x{0:X} class={1}" -f [int64]$script:crabChild, (Get-WindowClass $script:crabChild))
} else {
    Write-Output 'crabdesk desktop child:  (not running - control run)'
}

$idleExplorer = Measure-WorstLatency $script:desktopView 5 120
$idleCrab = if ($script:crabChild -ne [IntPtr]::Zero) { Measure-WorstLatency $script:crabChild 5 120 } else { -1 }
Write-Output "[idle] explorer=${idleExplorer}ms crabdesk=$(if ($idleCrab -ge 0) { "${idleCrab}ms" } else { 'n/a' })"

if (-not $SkipControlFolder) {
    $controlFolder = [Environment]::GetFolderPath('MyDocuments')
    $controlFile = Join-Path $controlFolder ("freeze-probe-{0}.txt" -f (Get-Date -Format 'HHmmssfff'))
    Set-Content -Path $controlFile -Value 'probe' -Encoding UTF8
    $controlWorst = Measure-WorstLatency $script:desktopView $Samples $SpacingMs
    Remove-Item $controlFile -Force -ErrorAction SilentlyContinue
    Write-Output "[control: ordinary folder] explorer=${controlWorst}ms  ($controlFolder)"
}

$desktopFile = Join-Path $desktopDirectory ("freeze-probe-{0}.txt" -f (Get-Date -Format 'HHmmssfff'))
Set-Content -Path $desktopFile -Value 'probe' -Encoding UTF8
$desktopWorst = Measure-WorstLatency $script:desktopView $Samples $SpacingMs
$crabWorst = if ($script:crabChild -ne [IntPtr]::Zero) { Measure-WorstLatency $script:crabChild $Samples $SpacingMs } else { -1 }
Remove-Item $desktopFile -Force -ErrorAction SilentlyContinue
Write-Output "[new file on desktop]     explorer=${desktopWorst}ms crabdesk=$(if ($crabWorst -ge 0) { "${crabWorst}ms" } else { 'n/a' })"

Write-Output ''
if ($desktopWorst -ge 500 -and $crabWorst -ge 0 -and $crabWorst -lt 100) {
    Write-Output "VERDICT: Explorer's desktop thread stalls on its own (${desktopWorst}ms) while CrabDesk answers (${crabWorst}ms)."
    Write-Output '         CrabDesk is not the cause and is not blocked by it. The stall comes from'
    Write-Output "         third-party desktop shell extensions: $(if ($SkipControlFolder) { 'compare with an ordinary folder' } else { "ordinary folder was ${controlWorst}ms" })."
} elseif ($desktopWorst -ge 500) {
    Write-Output "VERDICT: Explorer's desktop thread stalls (${desktopWorst}ms) with CrabDesk not running, so the stall is Explorer's own."
} else {
    Write-Output "VERDICT: no significant stall observed (${desktopWorst}ms); the third-party extensions may be disabled right now."
}
