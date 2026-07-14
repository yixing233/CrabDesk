param(
    [Parameter(Mandatory)]
    [ValidateSet("Baseline", "AfterResume", "Finalize")]
    [string]$Stage,
    [string]$DataDirectory = "$env:LOCALAPPDATA\CrabDesk",
    [string]$OutputDirectory = "..\artifacts\external-validation\sleep-resume",
    [int]$WaitSeconds = 30,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "hardware-validation-common.ps1")

$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifacts = [System.IO.Path]::GetFullPath((Join-Path $root "artifacts"))
$output = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDirectory))
if (-not $output.StartsWith($artifacts, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Validation output must stay inside the repository artifacts directory."
}
$data = [System.IO.Path]::GetFullPath($DataDirectory)
$dataDirectoryWasProvided = $PSBoundParameters.ContainsKey("DataDirectory")
$sessionPath = Join-Path $output "session.json"
$reportPath = Join-Path $output "latest.md"
[System.IO.Directory]::CreateDirectory($output) | Out-Null

function Save-Session($Session) {
    Write-CrabDeskHardwareReport -Path $sessionPath -Report $Session
    $baseline = $Session.Baseline
    $after = $Session.AfterResume
    $final = $Session.Finalize
    $lines = @(
        "# CrabDesk Sleep/Resume Validation",
        "",
        "- Machine: $($baseline.Machine)",
        "- OS: $($baseline.OS)",
        "- Baseline: $($baseline.CapturedAt)",
        "- Monitors: $(@($baseline.Monitors).Count)",
        "- Boxes: $(@($baseline.Boxes).Count)",
        "- Mapped folders: $(@($baseline.Boxes | Where-Object { -not [string]::IsNullOrWhiteSpace($_.MappedPath) }).Count)",
        "- Explorer icon state expected after exit: $($baseline.ExpectedRestoredHideIcons)",
        "- After resume: $(if ($null -eq $after) { 'Pending' } else { $after.CapturedAt })",
        "- Final cleanup: $(if ($null -eq $final) { 'Pending' } else { $final.CapturedAt })",
        "- Result: $(if ($null -ne $final -and $final.Passed) { 'PASSED' } elseif ($null -ne $after) { 'RESUME PASSED; FINALIZE PENDING' } else { 'BASELINE CAPTURED' })",
        "",
        "| Checkpoint | Process | Surfaces | HideIcons | Status |",
        "| --- | ---: | ---: | ---: | --- |",
        "| Baseline | $($baseline.ProcessId) | $(@($baseline.Surfaces).Count) | $($baseline.HideIcons) | Passed |"
    )
    if ($null -ne $after) {
        $lines += "| AfterResume | $($after.ProcessId) | $(@($after.Surfaces).Count) | $($after.HideIcons) | Passed |"
    }
    if ($null -ne $final) {
        $lines += "| Finalize | 0 | 0 | $($final.HideIcons) | $(if ($final.Passed) { 'Passed' } else { 'Failed' }) |"
    }
    $lines | Set-Content -LiteralPath $reportPath -Encoding UTF8
}

function Use-SessionDataDirectory($Session) {
    $sessionData = [System.IO.Path]::GetFullPath([string]$Session.DataDirectory)
    if ($script:dataDirectoryWasProvided -and
        -not $script:data.Equals($sessionData, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "DataDirectory does not match the captured session: $sessionData"
    }
    $script:data = $sessionData
}

switch ($Stage) {
    "Baseline" {
        if ((Test-Path -LiteralPath $sessionPath) -and -not $Force) {
            throw "A sleep/resume session already exists. Use -Force to replace it: $sessionPath"
        }
        $snapshot = Get-CrabDeskHardwareSnapshot -DataDirectory $data -WaitSeconds $WaitSeconds
        Assert-CrabDeskHardwareSnapshot -Snapshot $snapshot -RequireMappedFolder
        Test-CrabDeskWinD -ProcessId $snapshot.ProcessId
        $snapshot = Get-CrabDeskHardwareSnapshot -DataDirectory $data -WaitSeconds $WaitSeconds
        Assert-CrabDeskHardwareSnapshot -Snapshot $snapshot -RequireMappedFolder
        $session = [ordered]@{
            Scenario = "SleepResume"
            DataDirectory = $data
            Baseline = $snapshot
            AfterResume = $null
            Finalize = $null
        }
        Save-Session $session
        Write-Host "Baseline captured. Put Windows to sleep from the power menu, resume, wait 20 seconds, then run:"
        Write-Host ".\build\verify-sleep-resume.ps1 -Stage AfterResume"
    }
    "AfterResume" {
        if (-not (Test-Path -LiteralPath $sessionPath)) {
            throw "Capture Baseline before verifying resume: $sessionPath"
        }
        $session = Get-Content -LiteralPath $sessionPath -Raw -Encoding UTF8 | ConvertFrom-Json
        Use-SessionDataDirectory $session
        $baseline = $session.Baseline
        if ($baseline.Machine -ne [Environment]::MachineName) {
            throw "The baseline belongs to machine '$($baseline.Machine)'."
        }
        $snapshot = Get-CrabDeskHardwareSnapshot -DataDirectory $data -WaitSeconds $WaitSeconds
        Assert-CrabDeskHardwareSnapshot -Snapshot $snapshot -RequireMappedFolder
        $startTimeDelta = [Math]::Abs((
            ([DateTimeOffset]$snapshot.ProcessStartTime) - ([DateTimeOffset]$baseline.ProcessStartTime)
        ).TotalSeconds)
        if ($snapshot.ProcessId -ne $baseline.ProcessId -or $startTimeDelta -gt 2) {
            throw "CrabDesk did not preserve the same process across sleep/resume."
        }
        if ($snapshot.HideIcons -ne $baseline.HideIcons) {
            throw "Explorer HideIcons changed across sleep/resume. Baseline=$($baseline.HideIcons), Current=$($snapshot.HideIcons)."
        }
        $currentBoxIds = @($snapshot.Boxes | ForEach-Object Id)
        $missingBoxes = @($baseline.Boxes | Where-Object { $currentBoxIds -notcontains $_.Id })
        if ($missingBoxes.Count -gt 0) {
            throw "Boxes were lost across sleep/resume: $($missingBoxes.Title -join ', ')"
        }
        $baselineMapped = @($baseline.Boxes | Where-Object { -not [string]::IsNullOrWhiteSpace($_.MappedPath) })
        $currentMappedPaths = @($snapshot.Boxes | ForEach-Object MappedPath)
        $missingMapped = @($baselineMapped | Where-Object { $currentMappedPaths -notcontains $_.MappedPath })
        if ($missingMapped.Count -gt 0) {
            throw "Mapped-folder boxes were lost across sleep/resume: $($missingMapped.MappedPath -join ', ')"
        }
        Test-CrabDeskWinD -ProcessId $snapshot.ProcessId
        $snapshot = Get-CrabDeskHardwareSnapshot -DataDirectory $data -WaitSeconds $WaitSeconds
        Assert-CrabDeskHardwareSnapshot -Snapshot $snapshot -RequireMappedFolder
        $session.AfterResume = $snapshot
        Save-Session $session
        Write-Host "Sleep/resume recovery passed. Exit and Explorer icon restoration remain to be checked:"
        Write-Host ".\build\verify-sleep-resume.ps1 -Stage Finalize"
    }
    "Finalize" {
        if (-not (Test-Path -LiteralPath $sessionPath)) {
            throw "No sleep/resume session exists: $sessionPath"
        }
        $session = Get-Content -LiteralPath $sessionPath -Raw -Encoding UTF8 | ConvertFrom-Json
        Use-SessionDataDirectory $session
        if ($null -eq $session.AfterResume) {
            throw "Run the AfterResume checkpoint before Finalize."
        }
        $executable = [string]$session.Baseline.Executable
        if (-not (Test-Path -LiteralPath $executable)) {
            throw "The validated executable no longer exists: $executable"
        }
        $exit = Start-Process -FilePath $executable -ArgumentList "--exit-existing" -Wait -PassThru
        if ($exit.ExitCode -ne 0) {
            throw "The exit signal returned code $($exit.ExitCode)."
        }
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            $remaining = @(Get-Process CrabDesk.App,CrabDesk.IconGuard -ErrorAction SilentlyContinue)
            if ($remaining.Count -eq 0) { break }
            Start-Sleep -Milliseconds 500
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($remaining.Count -ne 0) {
            throw "CrabDesk processes remained after normal exit."
        }
        $hideIcons = Get-CrabDeskExplorerHideIcons
        if ($hideIcons -ne $session.Baseline.ExpectedRestoredHideIcons) {
            throw "Normal exit did not restore Explorer HideIcons. Expected=$($session.Baseline.ExpectedRestoredHideIcons), Current=$hideIcons."
        }
        $session.Finalize = [pscustomobject]@{
            CapturedAt = [DateTimeOffset]::Now
            HideIcons = $hideIcons
            Passed = $true
        }
        Save-Session $session
        Write-Host "Sleep/resume, Win+D persistence, mapped-folder recovery and final icon restoration passed."
        Write-Host "Report: $reportPath"
    }
}
