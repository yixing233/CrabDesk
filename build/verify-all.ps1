param(
    [switch]$IncludeDesktop,
    [switch]$IncludeExplorerRestart,
    [switch]$SkipNetwork,
    [switch]$SkipInstaller,
    [switch]$ContinueOnFailure,
    [int]$StabilitySeconds = 0,
    [string]$OutputDirectory = "..\artifacts\verification"
)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$output = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDirectory))
$artifacts = [System.IO.Path]::GetFullPath((Join-Path $root "artifacts"))
if (-not $output.StartsWith($artifacts, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Verification output must stay inside the repository artifacts directory."
}
if ($StabilitySeconds -ne 0 -and $StabilitySeconds -lt 30) {
    throw "StabilitySeconds must be zero or at least 30."
}
if (@(Get-Process CrabDesk.App,CrabDesk.IconGuard -ErrorAction SilentlyContinue).Count -gt 0) {
    throw "Close the running CrabDesk instance before starting the full verification suite."
}
[System.IO.Directory]::CreateDirectory($output) | Out-Null

function Get-ExplorerHideIcons {
    try {
        return [int](Get-ItemPropertyValue "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" HideIcons)
    }
    catch {
        return 0
    }
}

function Assert-EnvironmentRestored([int]$ExpectedHideIcons) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $processes = @(Get-Process CrabDesk.App,CrabDesk.IconGuard -ErrorAction SilentlyContinue)
        $hideIcons = Get-ExplorerHideIcons
        if ($processes.Count -eq 0 -and $hideIcons -eq $ExpectedHideIcons) {
            return
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Verification step did not restore the process and Explorer icon baseline. Processes=$($processes.Count), HideIcons=$hideIcons, Expected=$ExpectedHideIcons"
}

$baselineHideIcons = Get-ExplorerHideIcons
$results = [System.Collections.Generic.List[object]]::new()
$suiteFailed = $false

function Add-SkippedStep([string]$Name, [string]$Reason) {
    $results.Add([pscustomobject]@{
        Name = $Name
        Status = "Skipped"
        DurationSeconds = 0
        Message = $Reason
    })
    Write-Host "[SKIP] $Name - $Reason" -ForegroundColor Yellow
}

function Invoke-VerificationStep([string]$Name, [scriptblock]$Action) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Host "[RUN ] $Name" -ForegroundColor Cyan
    try {
        & $Action
        Assert-EnvironmentRestored $baselineHideIcons
        $watch.Stop()
        $results.Add([pscustomobject]@{
            Name = $Name
            Status = "Passed"
            DurationSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 2)
            Message = ""
        })
        Write-Host "[PASS] $Name ($([Math]::Round($watch.Elapsed.TotalSeconds, 1))s)" -ForegroundColor Green
    }
    catch {
        $watch.Stop()
        $script:suiteFailed = $true
        $results.Add([pscustomobject]@{
            Name = $Name
            Status = "Failed"
            DurationSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 2)
            Message = $_.Exception.Message
        })
        Write-Host "[FAIL] $Name - $($_.Exception.Message)" -ForegroundColor Red
        if (-not $ContinueOnFailure) {
            throw
        }
    }
}

$startedAt = [DateTimeOffset]::Now
try {
    Invoke-VerificationStep "Release build" {
        dotnet build (Join-Path $root "CrabDesk.sln") -c Release
        if ($LASTEXITCODE -ne 0) { throw "dotnet build exited with code $LASTEXITCODE." }
    }
    Invoke-VerificationStep "Automated tests" {
        dotnet test (Join-Path $root "CrabDesk.sln") -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw "dotnet test exited with code $LASTEXITCODE." }
    }
    Invoke-VerificationStep "Self-contained publish" {
        & (Join-Path $PSScriptRoot "publish.ps1")
    }
    Invoke-VerificationStep "Backup restore UI" {
        & (Join-Path $PSScriptRoot "verify-backup-restore-ui.ps1")
    }
    Invoke-VerificationStep "Daily backup" {
        & (Join-Path $PSScriptRoot "verify-daily-backup.ps1")
    }
    Invoke-VerificationStep "Launch to tray and single instance" {
        & (Join-Path $PSScriptRoot "verify-launch-to-tray.ps1")
    }
    Invoke-VerificationStep "Mapped folders" {
        & (Join-Path $PSScriptRoot "verify-mapped-folders.ps1")
    }
    Invoke-VerificationStep "Organization rules" {
        & (Join-Path $PSScriptRoot "verify-organization-rules.ps1")
    }
    Invoke-VerificationStep "Organization stress and undo" {
        & (Join-Path $PSScriptRoot "verify-organization-stress.ps1")
    }
    Invoke-VerificationStep "Desktop double-click" {
        & (Join-Path $PSScriptRoot "verify-desktop-double-click.ps1")
    }
    Invoke-VerificationStep "Settings themes" {
        & (Join-Path $PSScriptRoot "verify-settings-themes.ps1") -OutputDirectory (Join-Path $output "themes")
    }
    Invoke-VerificationStep "Release workflow policy" {
        & (Join-Path $PSScriptRoot "verify-release-workflow.ps1")
    }
    if ($SkipNetwork) {
        Add-SkippedStep "GitHub update service" "Skipped by -SkipNetwork"
    }
    else {
        Invoke-VerificationStep "GitHub update service" {
            & (Join-Path $PSScriptRoot "verify-github-updates.ps1")
        }
    }
    if ($SkipInstaller) {
        Add-SkippedStep "Installer build and lifecycle" "Skipped by -SkipInstaller"
        Add-SkippedStep "Authenticode signing pipeline" "Skipped by -SkipInstaller"
    }
    else {
        Invoke-VerificationStep "Installer build" {
            & (Join-Path $PSScriptRoot "build-installer.ps1")
        }
        Invoke-VerificationStep "Installer lifecycle" {
            & (Join-Path $PSScriptRoot "verify-installer.ps1")
        }
        Invoke-VerificationStep "Authenticode signing pipeline" {
            & (Join-Path $PSScriptRoot "verify-signing.ps1")
        }
    }
    if ($IncludeDesktop) {
        Invoke-VerificationStep "Live desktop host" {
            & (Join-Path $PSScriptRoot "verify-desktop.ps1")
        }
        Invoke-VerificationStep "Hardware checkpoint contract" {
            & (Join-Path $PSScriptRoot "verify-hardware-validation-common.ps1")
        }
        Invoke-VerificationStep "Composited box opacity" {
            & (Join-Path $PSScriptRoot "verify-opacity.ps1") -OutputPath (Join-Path $output "opacity.png")
        }
        if (@(Get-Process wallpaper32,wallpaper64 -ErrorAction SilentlyContinue).Count -gt 0) {
            Invoke-VerificationStep "Wallpaper Engine compatibility" {
                & (Join-Path $PSScriptRoot "verify-dynamic-wallpaper.ps1")
            }
        }
        else {
            Add-SkippedStep "Wallpaper Engine compatibility" "Wallpaper Engine is not running"
        }
    }
    else {
        Add-SkippedStep "Live desktop host" "Requires -IncludeDesktop"
        Add-SkippedStep "Composited box opacity" "Requires -IncludeDesktop"
        Add-SkippedStep "Wallpaper Engine compatibility" "Requires -IncludeDesktop"
    }
    if ($IncludeExplorerRestart) {
        Invoke-VerificationStep "Explorer restart recovery" {
            & (Join-Path $PSScriptRoot "verify-explorer-restart.ps1") -ConfirmExplorerRestart
        }
    }
    else {
        Add-SkippedStep "Explorer restart recovery" "Disruptive; requires -IncludeExplorerRestart"
    }
    if ($StabilitySeconds -gt 0) {
        Invoke-VerificationStep "Runtime stability" {
            & (Join-Path $PSScriptRoot "verify-runtime-stability.ps1") -DurationSeconds $StabilitySeconds
        }
    }
    else {
        Add-SkippedStep "Runtime stability" "Use -StabilitySeconds 900 for the release gate"
    }
}
finally {
    $finishedAt = [DateTimeOffset]::Now
    $report = [ordered]@{
        StartedAt = $startedAt
        FinishedAt = $finishedAt
        DurationSeconds = [Math]::Round(($finishedAt - $startedAt).TotalSeconds, 2)
        Machine = [Environment]::MachineName
        OS = [Environment]::OSVersion.VersionString
        BaselineHideIcons = $baselineHideIcons
        Results = $results
        ExternalRequirements = @(
            "Real sleep and resume",
            "Windows 10/11 multi-monitor mixed-DPI hardware",
            "Trusted Authenticode certificate",
            "Official GitHub repository and v1.0.0 release"
        )
    }
    $jsonPath = Join-Path $output "latest.json"
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
    $markdown = @(
        "# CrabDesk Verification Report",
        "",
        "- Started: $startedAt",
        "- Finished: $finishedAt",
        "- Duration: $($report.DurationSeconds) seconds",
        "- Result: $(if ($suiteFailed) { 'FAILED' } else { 'PASSED' })",
        "",
        "| Step | Status | Seconds | Message |",
        "| --- | --- | ---: | --- |"
    )
    foreach ($result in $results) {
        $message = ($result.Message -replace '\|', '\|') -replace "`r?`n", " "
        $markdown += "| $($result.Name) | $($result.Status) | $($result.DurationSeconds) | $message |"
    }
    $markdown | Set-Content -LiteralPath (Join-Path $output "latest.md") -Encoding UTF8
    Write-Host "Verification report: $jsonPath"
}

if ($suiteFailed) {
    throw "One or more CrabDesk verification steps failed."
}
Write-Host "CrabDesk verification suite passed."
