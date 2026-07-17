param()

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$sourceFiles = @(
    (Join-Path $root "artifacts\publish\win-x64\CrabDesk.WinUI.exe"),
    (Join-Path $root "artifacts\publish\win-x64\CrabDesk.IconGuard.exe"),
    (Join-Path $root "artifacts\installer\CrabDesk-Setup-x64.exe")
)
foreach ($file in $sourceFiles) {
    if (-not (Test-Path -LiteralPath $file)) {
        throw "Signing verification source was not found: $file"
    }
}

$originalStatuses = @{}
foreach ($file in $sourceFiles) {
    $originalStatuses[$file] = (Get-AuthenticodeSignature -LiteralPath $file).Status
}
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("CrabDesk.SigningTest." + [Guid]::NewGuid().ToString("N"))
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
$passwordText = [Guid]::NewGuid().ToString("N")
$securePassword = ConvertTo-SecureString $passwordText -AsPlainText -Force
$pfxPath = Join-Path $testRoot "development-signing.pfx"
$certificate = $null
try {
    $certificate = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject "CN=CrabDesk Development Signing Test" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddDays(2)
    Export-PfxCertificate -Cert $certificate -FilePath $pfxPath -Password $securePassword | Out-Null

    $testFiles = @()
    foreach ($source in $sourceFiles) {
        $destination = Join-Path $testRoot ([System.IO.Path]::GetFileName($source))
        Copy-Item -LiteralPath $source -Destination $destination
        $testFiles += $destination
    }
    & (Join-Path $PSScriptRoot "sign-artifacts.ps1") `
        -CertificatePath $pfxPath `
        -CertificatePassword $passwordText `
        -Files $testFiles `
        -TimestampUrl "" `
        -SkipTrustValidation

    foreach ($file in $testFiles) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file
        if ($null -eq $signature.SignerCertificate -or
            $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint -or
            $signature.SignerCertificate.SignatureAlgorithm.FriendlyName -notmatch "sha256") {
            throw "Development Authenticode signature verification failed for $file."
        }
    }
}
finally {
    if ($certificate) {
        Remove-Item -LiteralPath ("Cert:\CurrentUser\My\" + $certificate.Thumbprint) -Force -ErrorAction SilentlyContinue
    }
    foreach ($file in $sourceFiles) {
        $currentStatus = (Get-AuthenticodeSignature -LiteralPath $file).Status
        if ($currentStatus -ne $originalStatuses[$file]) {
            throw "Signing verification modified the original artifact: $file"
        }
    }
    $resolvedRoot = [System.IO.Path]::GetFullPath($testRoot)
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedRoot.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Application and installer Authenticode signing pipeline verification passed with an isolated development certificate."
