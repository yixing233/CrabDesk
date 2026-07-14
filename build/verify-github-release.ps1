param(
    [Parameter(Mandatory = $true)]
    [string]$Owner,
    [Parameter(Mandatory = $true)]
    [string]$Repository,
    [string]$Tag = "v1.0.0",
    [string]$ExpectedPublisherSubject = "",
    [string]$GitHubToken = $env:GH_TOKEN,
    [string]$OutputDirectory = "..\artifacts\external-validation\github-release"
)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifacts = [System.IO.Path]::GetFullPath((Join-Path $root "artifacts"))
$output = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDirectory))
}
if (-not $output.StartsWith($artifacts, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "GitHub Release evidence must stay inside the repository artifacts directory."
}
if ($Owner -notmatch '^[A-Za-z0-9_.-]+$' -or $Repository -notmatch '^[A-Za-z0-9_.-]+$') {
    throw "GitHub owner or repository contains unsupported characters."
}
if ($Tag -notmatch '^v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw "Release tag is invalid: $Tag"
}
if (@(Get-Process CrabDesk.App,CrabDesk.IconGuard -ErrorAction SilentlyContinue).Count -gt 0) {
    throw "Close CrabDesk before validating the downloaded release installer."
}

$headers = @{
    Accept = "application/vnd.github+json"
    "User-Agent" = "CrabDesk-Release-Validator"
    "X-GitHub-Api-Version" = "2022-11-28"
}
if (-not [string]::IsNullOrWhiteSpace($GitHubToken)) {
    $headers.Authorization = "Bearer $GitHubToken"
}
$requiredAssets = @(
    "CrabDesk-Setup-x64.exe",
    "CrabDesk-portable-win-x64.zip",
    "SHA256SUMS.txt"
)
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("CrabDesk.ReleaseValidation." + [Guid]::NewGuid().ToString("N"))
$downloadRoot = Join-Path $tempRoot "downloads"
$portableRoot = Join-Path $tempRoot "portable"
[System.IO.Directory]::CreateDirectory($downloadRoot) | Out-Null
[System.IO.Directory]::CreateDirectory($portableRoot) | Out-Null

function Normalize-ReleaseNotes([string]$Value) {
    return (($Value -replace "`r`n", "`n").Trim())
}

function Get-VerifiedSignature([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne "Valid" -or $null -eq $signature.SignerCertificate) {
        throw "Authenticode signature is not trusted for ${Path}: $($signature.StatusMessage)"
    }
    if ($null -eq $signature.TimeStamperCertificate) {
        throw "Authenticode signature does not contain a trusted timestamp: $Path"
    }
    $ekuOids = @($signature.SignerCertificate.EnhancedKeyUsageList | ForEach-Object { $_.ObjectId.Value })
    if ($ekuOids -notcontains "1.3.6.1.5.5.7.3.3") {
        throw "Signer certificate is not valid for code signing: $Path"
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedPublisherSubject) -and
        -not [string]::Equals(
            $signature.SignerCertificate.Subject,
            $ExpectedPublisherSubject,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected publisher for ${Path}: $($signature.SignerCertificate.Subject)"
    }
    return [pscustomobject]@{
        File = [System.IO.Path]::GetFileName($Path)
        Status = [string]$signature.Status
        Subject = $signature.SignerCertificate.Subject
        Thumbprint = $signature.SignerCertificate.Thumbprint
        TimestampSubject = $signature.TimeStamperCertificate.Subject
        TimestampNotAfter = $signature.TimeStamperCertificate.NotAfter
    }
}

try {
    $releaseUrl = "https://api.github.com/repos/$Owner/$Repository/releases/tags/$Tag"
    $release = Invoke-RestMethod -Uri $releaseUrl -Headers $headers -Method Get
    if ($release.draft -or $release.prerelease) {
        throw "The formal $Tag release must not be a draft or prerelease."
    }
    if (-not [string]::Equals([string]$release.tag_name, $Tag, [System.StringComparison]::Ordinal)) {
        throw "GitHub returned an unexpected release tag: $($release.tag_name)"
    }
    $assetsByName = @{}
    foreach ($asset in @($release.assets)) {
        $assetsByName[[string]$asset.name] = $asset
    }
    foreach ($assetName in $requiredAssets) {
        if (-not $assetsByName.ContainsKey($assetName)) {
            throw "GitHub Release is missing required asset: $assetName"
        }
    }

    if ($Tag -eq "v1.0.0") {
        $notesPath = Join-Path $root "docs\releases\v1.0.0.md"
        $expectedNotes = Get-Content -LiteralPath $notesPath -Raw -Encoding UTF8
        if ((Normalize-ReleaseNotes ([string]$release.body)) -ne (Normalize-ReleaseNotes $expectedNotes)) {
            throw "GitHub Release notes do not match docs/releases/v1.0.0.md."
        }
    }

    $downloaded = @{}
    foreach ($assetName in $requiredAssets) {
        $destination = Join-Path $downloadRoot $assetName
        Invoke-WebRequest -Uri ([string]$assetsByName[$assetName].browser_download_url) `
            -Headers $headers -OutFile $destination -UseBasicParsing
        if (-not (Test-Path -LiteralPath $destination) -or (Get-Item -LiteralPath $destination).Length -le 0) {
            throw "Downloaded release asset is empty: $assetName"
        }
        $downloaded[$assetName] = $destination
    }

    $checksums = @{}
    foreach ($line in Get-Content -LiteralPath $downloaded["SHA256SUMS.txt"] -Encoding ASCII) {
        if ($line -match '^([0-9A-Fa-f]{64})\s+\*?(.+)$') {
            $checksums[$Matches[2].Trim()] = $Matches[1].ToLowerInvariant()
        }
    }
    foreach ($assetName in $requiredAssets | Where-Object { $_ -ne "SHA256SUMS.txt" }) {
        if (-not $checksums.ContainsKey($assetName)) {
            throw "SHA256SUMS.txt does not contain $assetName."
        }
        $actualHash = (Get-FileHash -LiteralPath $downloaded[$assetName] -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -ne $checksums[$assetName]) {
            throw "SHA-256 mismatch for $assetName."
        }
    }

    Expand-Archive -LiteralPath $downloaded["CrabDesk-portable-win-x64.zip"] -DestinationPath $portableRoot -Force
    $signatureTargets = @(
        $downloaded["CrabDesk-Setup-x64.exe"],
        (Join-Path $portableRoot "CrabDesk.App.exe"),
        (Join-Path $portableRoot "CrabDesk.IconGuard.exe")
    )
    foreach ($target in $signatureTargets) {
        if (-not (Test-Path -LiteralPath $target)) {
            throw "Signed release target is missing: $target"
        }
    }
    $signatures = @($signatureTargets | ForEach-Object { Get-VerifiedSignature $_ })
    if (@($signatures.Thumbprint | Sort-Object -Unique).Count -ne 1) {
        throw "Downloaded release artifacts were not signed by the same publisher certificate."
    }

    & (Join-Path $PSScriptRoot "verify-installer.ps1") -SetupExecutable $downloaded["CrabDesk-Setup-x64.exe"]

    [System.IO.Directory]::CreateDirectory($output) | Out-Null
    $evidence = [ordered]@{
        Passed = $true
        ValidatedAt = [DateTimeOffset]::Now
        Owner = $Owner
        Repository = $Repository
        Tag = $Tag
        ReleaseId = [long]$release.id
        ReleaseUrl = [string]$release.html_url
        PublishedAt = [DateTimeOffset]$release.published_at
        NotesMatched = $true
        InstallerLifecyclePassed = $true
        Assets = @($requiredAssets | ForEach-Object {
            [pscustomobject]@{
                Name = $_
                Size = [long]$assetsByName[$_].size
                Sha256 = if ($checksums.ContainsKey($_)) { $checksums[$_] } else { "" }
            }
        })
        Signatures = $signatures
    }
    $jsonPath = Join-Path $output "session.json"
    $temporaryJson = $jsonPath + ".tmp"
    $evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporaryJson -Encoding UTF8
    Move-Item -LiteralPath $temporaryJson -Destination $jsonPath -Force
    $markdown = @(
        "# CrabDesk GitHub Release Validation",
        "",
        "- Result: PASSED",
        "- Release: [$Owner/$Repository $Tag]($($release.html_url))",
        "- Published: $($release.published_at)",
        "- Publisher: $($signatures[0].Subject)",
        "- Certificate: $($signatures[0].Thumbprint)",
        "- Release notes matched: yes",
        "- Installer lifecycle passed: yes",
        "",
        "| Asset | Bytes | SHA-256 |",
        "| --- | ---: | --- |"
    )
    foreach ($asset in $evidence.Assets) {
        $markdown += "| $($asset.Name) | $($asset.Size) | $($asset.Sha256) |"
    }
    $markdown | Set-Content -LiteralPath (Join-Path $output "latest.md") -Encoding UTF8
    Write-Host "GitHub Release assets, notes, checksums, trusted signatures and installer lifecycle passed: $jsonPath"
}
finally {
    $resolvedTemp = [System.IO.Path]::GetFullPath($tempRoot)
    $systemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedTemp.StartsWith($systemTemp, [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
