param(
    [string]$Executable = "..\artifacts\publish\win-x64\CrabDesk.App.exe",
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$exe = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Executable))
if (-not (Test-Path -LiteralPath $exe)) {
    throw "CrabDesk executable not found: $exe"
}
if (@(Get-Process CrabDesk.App -ErrorAction SilentlyContinue).Count -gt 0) {
    throw "Close the running CrabDesk instance before capturing settings themes."
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("CrabDesk.ThemeTest." + [Guid]::NewGuid().ToString("N"))
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
$previousDataDirectory = $env:CRABDESK_DATA_DIR
$env:CRABDESK_DATA_DIR = $dataDirectory
$boxId = [Guid]::NewGuid()

$config = @{
    SchemaVersion = 14
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
        Backup = @{
            DailyBackup = $false
            RetentionDays = 7
            BackupDirectory = ""
        }
        Updates = @{
            CheckOnStartup = $false
            Channel = 0
        }
    }
    Boxes = @(@{
        Id = $boxId
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
    OrganizationRules = @(
        @{
            Id = [Guid]::NewGuid(); Title = "Shortcuts"; Enabled = $true; Priority = 0
            ItemKinds = @(2); NamePattern = '*'; Extensions = @('.lnk', '.url'); Action = 0; TargetBoxId = $boxId
        },
        @{
            Id = [Guid]::NewGuid(); Title = "Folders"; Enabled = $true; Priority = 1
            ItemKinds = @(1); NamePattern = '*'; Extensions = @(); Action = 0; TargetBoxId = $boxId
        },
        @{
            Id = [Guid]::NewGuid(); Title = "Documents"; Enabled = $true; Priority = 2
            ItemKinds = @(0); NamePattern = '*'; Extensions = @('.doc', '.docx', '.pdf', '.xlsx', '.pptx', '.txt'); Action = 0; TargetBoxId = $boxId
        },
        @{
            Id = [Guid]::NewGuid(); Title = "Images"; Enabled = $true; Priority = 3
            ItemKinds = @(0); NamePattern = '*'; Extensions = @('.bmp', '.jpg', '.jpeg', '.png', '.gif', '.tiff'); Action = 0; TargetBoxId = $boxId
        },
        @{
            Id = [Guid]::NewGuid(); Title = "Archives"; Enabled = $true; Priority = 4
            ItemKinds = @(0); NamePattern = '*'; Extensions = @('.7z', '.bz2', '.gz', '.rar', '.tar', '.zip'); Action = 0; TargetBoxId = $boxId
        }
    )
}
$config | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $dataDirectory "config.json") -Encoding UTF8

$process = $null
try {
    $arguments = "--capture-settings-themes `"$captureDirectory`""
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru
    if (-not $process.WaitForExit(60000)) {
        throw "CrabDesk did not finish capturing settings themes within 60 seconds."
    }
    if ($process.ExitCode -ne 0) {
        $errorPath = Join-Path $captureDirectory "error.txt"
        $detail = if (Test-Path -LiteralPath $errorPath) {
            Get-Content -LiteralPath $errorPath -Raw -Encoding UTF8
        }
        else {
            "no error detail was written"
        }
        throw "Theme capture exited with code $($process.ExitCode): $detail"
    }

    $manifestPath = Join-Path $captureDirectory "manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "Theme capture manifest was not created."
    }
    $report = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $manifest = @()
    foreach ($entry in $report.Captures) {
        $manifest += $entry
    }
    if ($manifest.Count -lt 21) {
        throw "Expected at least 21 theme screenshots, found $($manifest.Count)."
    }
    foreach ($theme in 0, 1, 2) {
        if (@($manifest | Where-Object { $_.Theme -eq $theme }).Count -lt 7) {
            throw "Theme $theme does not contain all seven settings pages."
        }
    }
    $themeStates = @()
    foreach ($state in $report.States) {
        $themeStates += $state
    }
    if ($themeStates.Count -ne 3) {
        throw "Expected three resolved theme states, found $($themeStates.Count)."
    }
    foreach ($state in $themeStates) {
        if ($state.WindowChromeMatches -ne $true) {
            throw "Window chrome did not match theme $($state.Theme)."
        }
        if ($state.TrayThemeMatches -ne $true) {
            throw "Tray menu colors or renderer did not match theme $($state.Theme)."
        }
    }
    $sliderStates = @($report.SliderStates)
    if ($sliderStates.Count -ne 27) {
        throw "Expected 27 slider visual states across three themes, found $($sliderStates.Count)."
    }
    foreach ($state in $sliderStates) {
        if ($state.IsFullyVisible -ne $true -or $state.SliderHeight -lt 28 -or
            $state.TrackHeight -lt 16 -or $state.ThumbHeight -lt 15) {
            throw "Slider '$($state.Name)' is clipped in theme $($state.Theme). Slider=$($state.SliderHeight), Track=$($state.TrackHeight), Thumb=$($state.ThumbHeight), Top=$($state.ThumbTop)"
        }
    }
    $ruleTableStates = @($report.RuleTableStates)
    if ($ruleTableStates.Count -ne 3) {
        throw "Expected one organization-rule table state per theme, found $($ruleTableStates.Count)."
    }
    foreach ($state in $ruleTableStates) {
        if ($state.ItemCount -ne 5 -or $state.Width -lt 500 -or $state.Height -lt 180) {
            throw "Organization-rule table is incomplete in theme $($state.Theme). Items=$($state.ItemCount), Size=$($state.Width)x$($state.Height)"
        }
    }

    Add-Type -AssemblyName System.Drawing
    foreach ($entry in $manifest) {
        if (-not (Test-Path -LiteralPath $entry.Path)) {
            throw "Theme screenshot is missing: $($entry.Path)"
        }
        if ((Get-Item -LiteralPath $entry.Path).Length -lt 5000) {
            throw "Theme screenshot appears blank or incomplete: $($entry.Path)"
        }
        $bitmap = [System.Drawing.Bitmap]::new($entry.Path)
        try {
            if ($bitmap.Width -lt 820 -or $bitmap.Height -lt 560) {
                throw "Theme screenshot is smaller than the minimum settings window: $($entry.Path)"
            }
            $colors = [System.Collections.Generic.HashSet[int]]::new()
            for ($x = 0; $x -lt $bitmap.Width; $x += 32) {
                for ($y = 0; $y -lt $bitmap.Height; $y += 32) {
                    $colors.Add($bitmap.GetPixel($x, $y).ToArgb()) | Out-Null
                }
            }
            if ($colors.Count -lt 8) {
                throw "Theme screenshot does not contain enough rendered UI colors: $($entry.Path)"
            }
        }
        finally {
            $bitmap.Dispose()
        }
    }

    $light = [System.Drawing.Bitmap]::new((Join-Path $captureDirectory "Light-01.png"))
    $dark = [System.Drawing.Bitmap]::new((Join-Path $captureDirectory "Dark-01.png"))
    try {
        $lightPixel = $light.GetPixel(260, 30)
        $darkPixel = $dark.GetPixel(260, 30)
        $lightLuminance = ($lightPixel.R + $lightPixel.G + $lightPixel.B) / 3
        $darkLuminance = ($darkPixel.R + $darkPixel.G + $darkPixel.B) / 3
        if ($lightLuminance -lt 180 -or $darkLuminance -gt 100 -or ($lightLuminance - $darkLuminance) -lt 100) {
            throw "Light and dark theme backgrounds were not rendered distinctly."
        }
    }
    finally {
        $light.Dispose()
        $dark.Dispose()
    }

    Write-Host "Captured and validated $($manifest.Count) settings screenshots in $captureDirectory"
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

Write-Host "Settings theme verification passed."
