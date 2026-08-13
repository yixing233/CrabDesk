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

$isPrerelease = $Version.Contains('-') -or $Version -match '^\d{8}\.\d{2}$'
if (-not $isPrerelease -and
    ([string]::IsNullOrWhiteSpace($CertificateBase64) -or
     [string]::IsNullOrWhiteSpace($CertificatePassword))) {
    throw "Stable releases require SIGNING_CERTIFICATE_BASE64 and SIGNING_CERTIFICATE_PASSWORD."
}

Write-Host "Release configuration is valid for $(if ($isPrerelease) { 'prerelease' } else { 'stable' }) version $Version."
