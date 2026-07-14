param(
    [Parameter(Mandatory)]
    [ValidateSet("Baseline", "CrossScreen", "TopologyChanged", "Restored", "Finalize")]
    [string]$Stage,
    [string]$DataDirectory = "$env:LOCALAPPDATA\CrabDesk",
    [string]$OutputDirectory = "..\artifacts\external-validation\multi-monitor",
    [int]$WaitSeconds = 30,
    [switch]$RequireMixedDpi,
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
    $lines = @(
        "# CrabDesk Multi-monitor Validation",
        "",
        "- Machine: $($baseline.Machine)",
        "- OS: $($baseline.OS)",
        "- Baseline monitors: $(@($baseline.Monitors).Count)",
        "- Baseline DPI: $((@($baseline.Monitors | ForEach-Object DpiX | Sort-Object -Unique)) -join ', ')",
        "- Boxes: $(@($baseline.Boxes).Count)",
        "- Mixed DPI required: $($Session.RequireMixedDpi)",
        "- Explorer icon state expected after exit: $($baseline.ExpectedRestoredHideIcons)",
        "- Result: $(if ($null -ne $Session.Finalize -and $Session.Finalize.Passed) { 'PASSED' } else { 'IN PROGRESS' })",
        "",
        "| Checkpoint | Time | Monitors | Surfaces | Status |",
        "| --- | --- | ---: | ---: | --- |",
        "| Baseline | $($baseline.CapturedAt) | $(@($baseline.Monitors).Count) | $(@($baseline.Surfaces).Count) | Passed |"
    )
    foreach ($entry in @(
        @{ Name = "CrossScreen"; Value = $Session.CrossScreen },
        @{ Name = "TopologyChanged"; Value = $Session.TopologyChanged },
        @{ Name = "Restored"; Value = $Session.Restored }
    )) {
        if ($null -ne $entry.Value) {
            $lines += "| $($entry.Name) | $($entry.Value.CapturedAt) | $(@($entry.Value.Monitors).Count) | $(@($entry.Value.Surfaces).Count) | Passed |"
        }
    }
    if ($null -ne $Session.Finalize) {
        $lines += "| Finalize | $($Session.Finalize.CapturedAt) | 0 | 0 | Passed |"
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

function Assert-BoxesPreserved($Baseline, $Current) {
    $currentIds = @($Current.Boxes | ForEach-Object Id)
    $missing = @($Baseline.Boxes | Where-Object { $currentIds -notcontains $_.Id })
    if ($missing.Count -gt 0) {
        throw "Boxes were lost: $($missing.Title -join ', ')"
    }
}

function Capture-VerifiedSnapshot($Session) {
    $snapshot = Get-CrabDeskHardwareSnapshot -DataDirectory $data -WaitSeconds $WaitSeconds
    Assert-CrabDeskHardwareSnapshot -Snapshot $snapshot
    $startTimeDelta = [Math]::Abs((
        ([DateTimeOffset]$snapshot.ProcessStartTime) - ([DateTimeOffset]$Session.Baseline.ProcessStartTime)
    ).TotalSeconds)
    if ($snapshot.ProcessId -ne $Session.Baseline.ProcessId -or $startTimeDelta -gt 2) {
        throw "CrabDesk was restarted during the multi-monitor scenario."
    }
    if ($snapshot.HideIcons -ne $Session.Baseline.HideIcons) {
        throw "Explorer HideIcons changed during the multi-monitor scenario."
    }
    Assert-BoxesPreserved $Session.Baseline $snapshot
    Test-CrabDeskWinD -ProcessId $snapshot.ProcessId
    $snapshot = Get-CrabDeskHardwareSnapshot -DataDirectory $data -WaitSeconds $WaitSeconds
    Assert-CrabDeskHardwareSnapshot -Snapshot $snapshot
    Assert-BoxesPreserved $Session.Baseline $snapshot
    return $snapshot
}

switch ($Stage) {
    "Baseline" {
        if ((Test-Path -LiteralPath $sessionPath) -and -not $Force) {
            throw "A multi-monitor session already exists. Use -Force to replace it: $sessionPath"
        }
        $snapshot = Get-CrabDeskHardwareSnapshot -DataDirectory $data -WaitSeconds $WaitSeconds
        Assert-CrabDeskHardwareSnapshot -Snapshot $snapshot
        if (@($snapshot.Monitors).Count -lt 2) {
            throw "At least two active monitors are required for the multi-monitor baseline."
        }
        if (@($snapshot.Boxes).Count -lt 1) {
            throw "Create at least one box before the multi-monitor baseline."
        }
        $dpiValues = @($snapshot.Monitors | ForEach-Object DpiX | Sort-Object -Unique)
        if ($RequireMixedDpi -and $dpiValues.Count -lt 2) {
            throw "Mixed-DPI validation requires at least two distinct effective DPI values."
        }
        Test-CrabDeskWinD -ProcessId $snapshot.ProcessId
        $snapshot = Get-CrabDeskHardwareSnapshot -DataDirectory $data -WaitSeconds $WaitSeconds
        Assert-CrabDeskHardwareSnapshot -Snapshot $snapshot
        $session = [ordered]@{
            Scenario = "MultiMonitor"
            DataDirectory = $data
            RequireMixedDpi = [bool]$RequireMixedDpi
            BaselineTopology = Get-CrabDeskTopologySignature $snapshot
            Baseline = $snapshot
            CrossScreen = $null
            TopologyChanged = $null
            Restored = $null
            Finalize = $null
        }
        Save-Session $session
        Write-Host "Baseline captured. Move at least one existing box to another monitor, then run:"
        Write-Host ".\build\verify-multi-monitor.ps1 -Stage CrossScreen"
    }
    "CrossScreen" {
        if (-not (Test-Path -LiteralPath $sessionPath)) { throw "Capture Baseline first." }
        $session = Get-Content -LiteralPath $sessionPath -Raw -Encoding UTF8 | ConvertFrom-Json
        Use-SessionDataDirectory $session
        $snapshot = Capture-VerifiedSnapshot $session
        if ((Get-CrabDeskTopologySignature $snapshot) -ne $session.BaselineTopology) {
            throw "Keep the monitor topology unchanged until CrossScreen is captured."
        }
        $baselineById = @{}
        foreach ($box in $session.Baseline.Boxes) { $baselineById[$box.Id] = $box }
        $moved = @($snapshot.Boxes | Where-Object {
            $baselineById.ContainsKey($_.Id) -and $baselineById[$_.Id].MonitorId -ne $_.MonitorId
        })
        if ($moved.Count -eq 0) {
            throw "No box changed monitors after Baseline. Move a box across screens and retry."
        }
        $session.CrossScreen = $snapshot
        Save-Session $session
        Write-Host "Cross-screen box movement passed. Disconnect a monitor or change resolution/scaling, wait 10 seconds, then run:"
        Write-Host ".\build\verify-multi-monitor.ps1 -Stage TopologyChanged"
    }
    "TopologyChanged" {
        if (-not (Test-Path -LiteralPath $sessionPath)) { throw "Capture Baseline first." }
        $session = Get-Content -LiteralPath $sessionPath -Raw -Encoding UTF8 | ConvertFrom-Json
        Use-SessionDataDirectory $session
        if ($null -eq $session.CrossScreen) { throw "Capture CrossScreen before changing topology." }
        $snapshot = Capture-VerifiedSnapshot $session
        if ((Get-CrabDeskTopologySignature $snapshot) -eq $session.BaselineTopology) {
            throw "The active topology is unchanged. Disconnect a monitor or change resolution/scaling and retry."
        }
        $session.TopologyChanged = $snapshot
        Save-Session $session
        Write-Host "Topology-change migration passed. Restore the original monitor topology, wait 10 seconds, then run:"
        Write-Host ".\build\verify-multi-monitor.ps1 -Stage Restored"
    }
    "Restored" {
        if (-not (Test-Path -LiteralPath $sessionPath)) { throw "Capture Baseline first." }
        $session = Get-Content -LiteralPath $sessionPath -Raw -Encoding UTF8 | ConvertFrom-Json
        Use-SessionDataDirectory $session
        if ($null -eq $session.TopologyChanged) { throw "Capture TopologyChanged before Restored." }
        $snapshot = Capture-VerifiedSnapshot $session
        if ((Get-CrabDeskTopologySignature $snapshot) -ne $session.BaselineTopology) {
            throw "The original monitor topology has not been restored exactly."
        }
        if ($session.RequireMixedDpi) {
            $dpiValues = @($snapshot.Monitors | ForEach-Object DpiX | Sort-Object -Unique)
            if ($dpiValues.Count -lt 2) {
                throw "The restored topology no longer has mixed DPI."
            }
        }
        $session.Restored = $snapshot
        Save-Session $session
        Write-Host "Monitor reconnection, surface recreation and box preservation passed. Complete normal-exit recovery:"
        Write-Host ".\build\verify-multi-monitor.ps1 -Stage Finalize"
    }
    "Finalize" {
        if (-not (Test-Path -LiteralPath $sessionPath)) { throw "No multi-monitor session exists." }
        $session = Get-Content -LiteralPath $sessionPath -Raw -Encoding UTF8 | ConvertFrom-Json
        Use-SessionDataDirectory $session
        if ($null -eq $session.Restored) { throw "Run Restored before Finalize." }
        $executable = [string]$session.Baseline.Executable
        if (-not (Test-Path -LiteralPath $executable)) { throw "Validated executable not found: $executable" }
        $exit = Start-Process -FilePath $executable -ArgumentList "--exit-existing" -Wait -PassThru
        if ($exit.ExitCode -ne 0) { throw "The exit signal returned code $($exit.ExitCode)." }
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            $remaining = @(Get-Process CrabDesk.App,CrabDesk.IconGuard -ErrorAction SilentlyContinue)
            if ($remaining.Count -eq 0) { break }
            Start-Sleep -Milliseconds 500
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($remaining.Count -ne 0) { throw "CrabDesk processes remained after normal exit." }
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
        Write-Host "Multi-monitor, mixed-DPI/topology change, Win+D and final icon restoration passed."
        Write-Host "Report: $reportPath"
    }
}
