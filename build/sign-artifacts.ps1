param(
    [Parameter(Mandatory = $true)]
    [string]$CertificatePath,
    [Parameter(Mandatory = $true)]
    [string]$CertificatePassword,
    [Parameter(Mandatory = $true)]
    [string[]]$Files,
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [switch]$SkipTrustValidation
)

$ErrorActionPreference = "Stop"
$certificate = [System.IO.Path]::GetFullPath($CertificatePath)
if (-not (Test-Path -LiteralPath $certificate)) {
    throw "Signing certificate was not found: $certificate"
}
$signTool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } |
    Sort-Object { $_.VersionInfo.FileVersionRaw } -Descending |
    Select-Object -First 1
if (-not $signTool) {
    throw "Windows SDK x64 signtool.exe was not found."
}

$resolvedFiles = @($Files | ForEach-Object { [System.IO.Path]::GetFullPath($_) })
if ($resolvedFiles.Count -eq 0) {
    throw "No files were provided for signing."
}
foreach ($file in $resolvedFiles) {
    if (-not (Test-Path -LiteralPath $file)) {
        throw "Signing target was not found: $file"
    }
    $arguments = @("sign", "/fd", "SHA256", "/f", $certificate, "/p", $CertificatePassword)
    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
        $arguments += @("/td", "SHA256", "/tr", $TimestampUrl)
    }
    $arguments += $file
    & $signTool.FullName @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Authenticode signing failed for $file with exit code $LASTEXITCODE."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $file
    if ($null -eq $signature.SignerCertificate -or $signature.Status -in @("NotSigned", "HashMismatch")) {
        throw "No intact Authenticode signature was found after signing $file."
    }
    if (-not $SkipTrustValidation -and $signature.Status -ne "Valid") {
        throw "Authenticode trust validation failed for ${file}: $($signature.StatusMessage)"
    }
    Write-Host "Signed $file ($($signature.SignerCertificate.Subject))"
}
