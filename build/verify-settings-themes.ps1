param(
    [string]$Executable = "..\artifacts\publish\win-x64\CrabDesk.WinUI.exe",
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$exe = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Executable))
if (-not (Test-Path -LiteralPath $exe)) {
    throw "CrabDesk executable not found: $exe"
}
if (@(Get-Process CrabDesk.WinUI -ErrorAction SilentlyContinue).Count -gt 0) {
    throw "Close the running CrabDesk instance before capturing settings themes."
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("CrabDesk.WinUIThemeTest." + [Guid]::NewGuid().ToString("N"))
$dataDirectory = Join-Path $testRoot "data"
$captureDirectory = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $testRoot "captures"
}
elseif ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $OutputDirectory))
}
[System.IO.Directory]::CreateDirectory($dataDirectory) | Out-Null
[System.IO.Directory]::CreateDirectory($captureDirectory) | Out-Null

$config = @{
    SchemaVersion = 16
    Settings = @{
        TakeOverDesktop = $false
        ShowSystemItems = $false
        ConfirmDeleteBox = $true
        ThemeMode = 0
        DesktopBehavior = @{
            LaunchToTray = $false
            RefreshAfterRename = $true
            ShowDesktopContextMenu = $false
            ToggleIconsOnDesktopDoubleClick = $false
        }
        Backup = @{ DailyBackup = $false; RetentionDays = 7; BackupDirectory = "" }
        Updates = @{ CheckOnStartup = $false; Channel = 0 }
    }
    Boxes = @(@{
        Id = [Guid]::NewGuid()
        Title = "Desktop"
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
$config | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $dataDirectory "config.json") -Encoding UTF8

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CrabDeskWinUICapture {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT rect, int size);
    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
'@

function Save-WindowCapture([IntPtr]$Handle, [string]$Path) {
    $bounds = New-Object CrabDeskWinUICapture+RECT
    [void][CrabDeskWinUICapture]::DwmGetWindowAttribute(
        $Handle,
        9,
        [ref]$bounds,
        [Runtime.InteropServices.Marshal]::SizeOf($bounds))
    $width = $bounds.Right - $bounds.Left
    $height = $bounds.Bottom - $bounds.Top
    if ($width -lt 760 -or $height -lt 520) {
        throw "WinUI capture is below the minimum viewport: ${width}x${height}"
    }
    $bitmap = [System.Drawing.Bitmap]::new($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try {
        if (-not [CrabDeskWinUICapture]::PrintWindow($Handle, $hdc, 2)) {
            throw "PrintWindow failed for $Path"
        }
    }
    finally {
        $graphics.ReleaseHdc($hdc)
        $graphics.Dispose()
    }
    try {
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
        $colors = [System.Collections.Generic.HashSet[int]]::new()
        for ($x = 0; $x -lt $bitmap.Width; $x += 32) {
            for ($y = 0; $y -lt $bitmap.Height; $y += 32) {
                $colors.Add($bitmap.GetPixel($x, $y).ToArgb()) | Out-Null
            }
        }
        if ($colors.Count -lt 8) {
            throw "WinUI capture appears blank: $Path"
        }
    }
    finally {
        $bitmap.Dispose()
    }
}

$previousDataDirectory = $env:CRABDESK_DATA_DIR
$env:CRABDESK_DATA_DIR = $dataDirectory
$pages = @("general", "hotkeys", "backup", "organization", "appearance", "boxes", "about")
$themes = @("System", "Light", "Dark")
$manifest = @()
try {
    foreach ($theme in $themes) {
        foreach ($page in $pages) {
            $process = Start-Process -FilePath $exe -ArgumentList @(
                "--show-settings",
                "--validation-page", $page,
                "--validation-theme", $theme,
                "--validation-width", "1040",
                "--validation-height", "720",
                "--validation-scale", "1") -PassThru
            try {
                $deadline = [DateTime]::UtcNow.AddSeconds(20)
                do {
                    Start-Sleep -Milliseconds 100
                    $process.Refresh()
                } while (($process.MainWindowHandle -eq 0 -or -not $process.Responding) -and
                         -not $process.HasExited -and [DateTime]::UtcNow -lt $deadline)
                if ($process.HasExited -or $process.MainWindowHandle -eq 0 -or -not $process.Responding) {
                    throw "WinUI page '$page' did not become responsive for theme '$theme'."
                }
                Start-Sleep -Milliseconds 700
                $path = Join-Path $captureDirectory "$theme-$page.png"
                Save-WindowCapture $process.MainWindowHandle $path
                $manifest += [pscustomobject]@{ Theme = $theme; Page = $page; Path = $path }
            }
            finally {
                if (-not $process.HasExited) {
                    $exit = Start-Process -FilePath $exe -ArgumentList "--exit-existing" -PassThru
                    [void]$exit.WaitForExit(5000)
                    [void]$process.WaitForExit(5000)
                }
                if (-not $process.HasExited) {
                    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                }
                Get-Process CrabDesk.IconGuard -ErrorAction SilentlyContinue |
                    Stop-Process -Force -ErrorAction SilentlyContinue
                Start-Sleep -Milliseconds 300
            }
        }
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $captureDirectory "manifest.json") -Encoding UTF8
    Write-Host "Captured and validated $($manifest.Count) WinUI settings screenshots in $captureDirectory"
}
finally {
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

Write-Host "WinUI settings theme verification passed."
