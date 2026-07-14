param(
    [string]$OutputDirectory = "..\artifacts\external-validation\readiness",
    [switch]$RequireReady
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "hardware-validation-common.ps1")

$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifacts = [System.IO.Path]::GetFullPath((Join-Path $root "artifacts"))
$external = Join-Path $artifacts "external-validation"
$output = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDirectory))
}
if (-not $output.StartsWith($artifacts, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Readiness evidence must stay inside the repository artifacts directory."
}

function Read-Sessions([string]$PathFragment) {
    if (-not (Test-Path -LiteralPath $external)) {
        return @()
    }
    return @(Get-ChildItem -LiteralPath $external -Recurse -Filter session.json -File |
        Where-Object { $_.FullName.IndexOf($PathFragment, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 } |
        ForEach-Object {
            try {
                [pscustomobject]@{ Path = $_.FullName; Data = (Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json) }
            }
            catch {
                [pscustomobject]@{ Path = $_.FullName; Data = $null; Error = $_.Exception.Message }
            }
        })
}

function Find-CompletedMultiMonitorSession([object[]]$Sessions, [string]$WindowsName) {
    return @($Sessions | Where-Object {
        $session = $_.Data
        $null -ne $session -and
        [string]$session.Baseline.OS -match $WindowsName -and
        @($session.Baseline.Monitors).Count -ge 2 -and
        [bool]$session.RequireMixedDpi -and
        @($session.Baseline.Monitors | ForEach-Object DpiX | Sort-Object -Unique).Count -ge 2 -and
        $null -ne $session.CrossScreen -and
        $null -ne $session.TopologyChanged -and
        $null -ne $session.Restored -and
        [bool]$session.Finalize.Passed
    } | Select-Object -First 1)
}

$latestVerificationPath = Join-Path $artifacts "verification\latest.json"
$latestVerification = if (Test-Path -LiteralPath $latestVerificationPath) {
    Get-Content -LiteralPath $latestVerificationPath -Raw -Encoding UTF8 | ConvertFrom-Json
}
else {
    $null
}
$verificationResults = if ($null -ne $latestVerification) { @($latestVerification.Results) } else { @() }
$runtimeResult = @($verificationResults | Where-Object Name -eq "Runtime stability" | Select-Object -First 1)
$localReleaseGateReady = $verificationResults.Count -gt 0 -and
    @($verificationResults | Where-Object Status -ne "Passed").Count -eq 0 -and
    $runtimeResult.Count -eq 1 -and
    [double]$runtimeResult[0].DurationSeconds -ge 900

$sleepSessions = Read-Sessions "\sleep-resume\"
$completedSleep = @($sleepSessions | Where-Object {
    $null -ne $_.Data -and $null -ne $_.Data.AfterResume -and [bool]$_.Data.Finalize.Passed
} | Select-Object -First 1)
$multiSessions = Read-Sessions "\multi-monitor\"
$windows10Multi = Find-CompletedMultiMonitorSession $multiSessions "Windows 10"
$windows11Multi = Find-CompletedMultiMonitorSession $multiSessions "Windows 11"
$releaseSessions = Read-Sessions "\github-release\"
$formalRelease = @($releaseSessions | Where-Object {
    $null -ne $_.Data -and
    [bool]$_.Data.Passed -and
    [string]$_.Data.Tag -eq "v1.0.0" -and
    [bool]$_.Data.NotesMatched -and
    [bool]$_.Data.InstallerLifecyclePassed -and
    @($_.Data.Signatures).Count -eq 3
} | Select-Object -First 1)

$monitors = @([CrabDeskHardwareProbe]::GetMonitors())
$codeSigningCertificates = @(Get-ChildItem Cert:\CurrentUser\My | Where-Object {
    $_.HasPrivateKey -and
    $_.NotAfter -gt (Get-Date) -and
    @($_.EnhancedKeyUsageList | ForEach-Object { $_.ObjectId.Value }) -contains "1.3.6.1.5.5.7.3.3"
})
$gitWorkspace = Test-Path -LiteralPath (Join-Path $root ".git")
$gitRemote = ""
if ($gitWorkspace) {
    $gitRemote = (& git -C $root remote get-url origin 2>$null | Select-Object -First 1)
}

$requirements = @(
    [pscustomobject]@{
        Name = "Release verification gate"
        Ready = $localReleaseGateReady
        Evidence = if ($localReleaseGateReady) { $latestVerificationPath } else { "Run verify-all.ps1 -IncludeDesktop -IncludeExplorerRestart -StabilitySeconds 900" }
    },
    [pscustomobject]@{
        Name = "Real sleep/resume"
        Ready = $completedSleep.Count -gt 0
        Evidence = if ($completedSleep.Count -gt 0) { $completedSleep[0].Path } else { "No completed sleep-resume session" }
    },
    [pscustomobject]@{
        Name = "Windows 10 mixed-DPI multi-monitor"
        Ready = $windows10Multi.Count -gt 0
        Evidence = if ($windows10Multi.Count -gt 0) { $windows10Multi[0].Path } else { "No completed Windows 10 mixed-DPI session" }
    },
    [pscustomobject]@{
        Name = "Windows 11 mixed-DPI multi-monitor"
        Ready = $windows11Multi.Count -gt 0
        Evidence = if ($windows11Multi.Count -gt 0) { $windows11Multi[0].Path } else { "No completed Windows 11 mixed-DPI session" }
    },
    [pscustomobject]@{
        Name = "Trusted signed GitHub v1.0.0 release"
        Ready = $formalRelease.Count -gt 0
        Evidence = if ($formalRelease.Count -gt 0) { $formalRelease[0].Path } else { "No completed GitHub Release validation session" }
    }
)
$ready = @($requirements | Where-Object { -not $_.Ready }).Count -eq 0
$report = [ordered]@{
    Ready = $ready
    AuditedAt = [DateTimeOffset]::Now
    Machine = [Environment]::MachineName
    OS = Get-CrabDeskOsDescription
    CurrentEnvironment = [ordered]@{
        GitWorkspace = $gitWorkspace
        GitRemote = [string]$gitRemote
        MonitorCount = $monitors.Count
        DpiValues = @($monitors.DpiX | Sort-Object -Unique)
        LocalCodeSigningCertificateCount = $codeSigningCertificates.Count
    }
    Requirements = $requirements
}

[System.IO.Directory]::CreateDirectory($output) | Out-Null
$jsonPath = Join-Path $output "latest.json"
$temporaryJson = $jsonPath + ".tmp"
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporaryJson -Encoding UTF8
Move-Item -LiteralPath $temporaryJson -Destination $jsonPath -Force
$markdown = @(
    "# CrabDesk External Readiness Audit",
    "",
    "- Audited: $($report.AuditedAt)",
    "- Machine: $($report.Machine)",
    "- OS: $($report.OS)",
    "- Result: $(if ($ready) { 'READY' } else { 'NOT READY' })",
    "- Current monitors: $($monitors.Count) ($((@($monitors.DpiX | Sort-Object -Unique)) -join ', ') DPI)",
    "- Git workspace: $gitWorkspace",
    "- Local code-signing certificates: $($codeSigningCertificates.Count)",
    "",
    "| Requirement | Ready | Evidence |",
    "| --- | --- | --- |"
)
foreach ($requirement in $requirements) {
    $markdown += "| $($requirement.Name) | $(if ($requirement.Ready) { 'Yes' } else { 'No' }) | $($requirement.Evidence -replace '\|', '\|') |"
}
$markdown | Set-Content -LiteralPath (Join-Path $output "latest.md") -Encoding UTF8
Write-Host "External readiness: $(if ($ready) { 'READY' } else { 'NOT READY' }). Report: $jsonPath"

if ($RequireReady -and -not $ready) {
    throw "CrabDesk external release requirements are not complete."
}
