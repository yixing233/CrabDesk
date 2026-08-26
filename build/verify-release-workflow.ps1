param()

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$policy = Join-Path $PSScriptRoot "assert-release-configuration.ps1"
$releaseValidator = Join-Path $PSScriptRoot "verify-github-release.ps1"
$workflowPath = Join-Path $root ".github\workflows\release.yml"
$bootstrapperPublisherPath = Join-Path $PSScriptRoot "publish-bootstrapper.ps1"
$dependencyManifestPath = Join-Path $root "installer\setup-dependencies.json"
$dependencyResolverPath = Join-Path $PSScriptRoot "resolve-setup-dependencies.ps1"
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
    "Sign embedded payload",
    "Sign online setup",
    "Verify stable release signatures",
    "TimeStamperCertificate",
    "1.3.6.1.5.5.7.3.3",
    "SignerCertificate.Thumbprint",
    "CrabDesk-Setup-x64.exe",
    "CrabDesk-Payload-x64.exe",
    "publish-bootstrapper.ps1",
    "SHA256SUMS.txt",
    "--verify-tag",
    "docs\releases\v1.0.0.md"
)
foreach ($fragment in $requiredFragments) {
    if ($workflow.IndexOf($fragment, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Release workflow is missing required policy fragment: $fragment"
    }
}
$signPayloadIndex = $workflow.IndexOf("- name: Sign embedded payload", [System.StringComparison]::Ordinal)
$publishSetupIndex = $workflow.IndexOf("- name: Publish online setup", [System.StringComparison]::Ordinal)
$signSetupIndex = $workflow.IndexOf("- name: Sign online setup", [System.StringComparison]::Ordinal)
if ($signPayloadIndex -lt 0 -or
    $publishSetupIndex -le $signPayloadIndex -or
    $signSetupIndex -le $publishSetupIndex) {
    throw "Release workflow must sign the payload before embedding it, then sign the final online setup."
}

$bootstrapperPublisher = Get-Content -LiteralPath $bootstrapperPublisherPath -Raw -Encoding UTF8
if ($bootstrapperPublisher.IndexOf("resolve-setup-dependencies.ps1", [System.StringComparison]::Ordinal) -lt 0) {
    throw "Bootstrapper publish script does not validate the pinned dependency manifest."
}
if (-not (Test-Path -LiteralPath $dependencyManifestPath)) {
    throw "Pinned setup dependency manifest was not found: $dependencyManifestPath"
}
[xml]$winUiProject = Get-Content -LiteralPath (Join-Path $root "CrabDesk.WinUI\CrabDesk.WinUI.csproj") -Raw -Encoding UTF8
$runtimeReference = @($winUiProject.Project.ItemGroup.PackageReference |
    Where-Object { $_.Include -eq "Microsoft.WindowsAppSDK.Runtime" })[0]
& $dependencyResolverPath -WindowsAppRuntimeVersion ([string]$runtimeReference.Version) | Out-Null

if ($workflow.IndexOf("CrabDesk-Setup-Web-x64.exe", [System.StringComparison]::Ordinal) -ge 0) {
    throw "Release workflow still references the retired Web setup asset."
}
$createReleaseIndex = $workflow.IndexOf("- name: Create GitHub Release", [System.StringComparison]::Ordinal)
if ($createReleaseIndex -lt 0) {
    throw "Release workflow does not contain the GitHub Release step."
}
$createReleaseBlock = $workflow.Substring($createReleaseIndex)
if ($createReleaseBlock.IndexOf("CrabDesk-Payload-x64.exe", [System.StringComparison]::Ordinal) -ge 0) {
    throw "The embedded payload must not be uploaded as a separate GitHub Release asset."
}

Write-Host "Stable signing gate, single embedded-payload setup, dependency resolver and release-validator policy passed."
