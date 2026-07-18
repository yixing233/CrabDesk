param()

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$policy = Join-Path $PSScriptRoot "assert-release-configuration.ps1"
$releaseValidator = Join-Path $PSScriptRoot "verify-github-release.ps1"
$workflowPath = Join-Path $root ".github\workflows\release.yml"
if (-not (Test-Path -LiteralPath $workflowPath)) {
    throw "Release workflow was not found: $workflowPath"
}

function Assert-Fails([scriptblock]$Action, [string]$ExpectedMessage) {
    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") {
            throw
        }
        return
    }
    throw "Expected release policy failure containing: $ExpectedMessage"
}

& $policy -Version "1.0.0-beta.1" -CertificateBase64 "" -CertificatePassword ""
& $policy -Version "1.0.0" -CertificateBase64 "test-certificate" -CertificatePassword "test-password"
Assert-Fails { & $policy -Version "1.0.0" -CertificateBase64 "" -CertificatePassword "" } "Stable releases require"
Assert-Fails { & $policy -Version "release-one" -CertificateBase64 "test" -CertificatePassword "test" } "Invalid release version"
Assert-Fails { & $releaseValidator -Owner "invalid/owner" -Repository "repo" } "unsupported characters"
Assert-Fails { & $releaseValidator -Owner "owner" -Repository "repo" -Tag "invalid" } "Release tag is invalid"

$workflow = Get-Content -LiteralPath $workflowPath -Raw -Encoding UTF8
$requiredFragments = @(
    "assert-release-configuration.ps1",
    "Sign application binaries",
    "Sign installer",
    "Verify stable release signatures",
    "TimeStamperCertificate",
    "1.3.6.1.5.5.7.3.3",
    "SignerCertificate.Thumbprint",
    "CrabDesk-Setup-x64.exe",
    "CrabDesk-Setup-Web-x64.exe",
    "CrabDesk-portable-win-x64.zip",
    "CrabDesk-portable-web-win-x64.zip",
    "SHA256SUMS.txt",
    "--verify-tag",
    "docs\releases\v1.0.0.md"
)
foreach ($fragment in $requiredFragments) {
    if ($workflow.IndexOf($fragment, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Release workflow is missing required policy fragment: $fragment"
    }
}

Write-Host "Stable signing gate, signature verification, fixed release assets, v1.0.0 notes and release-validator input policy passed."
