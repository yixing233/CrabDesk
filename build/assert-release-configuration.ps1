param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$CertificateBase64 = $env:SIGNING_CERTIFICATE_BASE64,
    [string]$CertificatePassword = $env:SIGNING_CERTIFICATE_PASSWORD
)

$ErrorActionPreference = "Stop"
if ($Version -notmatch '^(?:\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?|\d{8}\.\d{2})$') {
    throw "Invalid release version: $Version"
}

# Signing is optional: releases are distributed unsigned and the in-app update
# chain relies on HTTPS plus the SHA-256SUMS digest. When certificate secrets
# are configured the workflow additionally signs and verifies every artifact.
$isPrerelease = $Version.Contains('-') -or $Version -match '^\d{8}\.\d{2}$'

Write-Host "Release configuration is valid for $(if ($isPrerelease) { 'prerelease' } else { 'stable' }) version $Version."
