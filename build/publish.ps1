param(
    [string]$Configuration = "Release",
    [string]$Version = "",
    [string]$GitHubOwner = $env:CRABDESK_GITHUB_OWNER,
    [string]$GitHubRepository = $env:CRABDESK_GITHUB_REPOSITORY_NAME,
    [ValidateSet("Full", "Web")]
    [string]$PackageKind = "Full"
)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifacts = [System.IO.Path]::GetFullPath((Join-Path $root "artifacts"))
$publishName = if ($PackageKind -eq "Web") { "win-x64-web" } else { "win-x64" }
$output = [System.IO.Path]::GetFullPath((Join-Path $artifacts "publish\$publishName"))
$guardOutput = [System.IO.Path]::GetFullPath((Join-Path $artifacts "publish\guard"))

if (-not $output.StartsWith($artifacts, [System.StringComparison]::OrdinalIgnoreCase) -or
    -not $guardOutput.StartsWith($artifacts, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Publish paths must stay inside the repository artifacts directory."
}

Remove-Item -LiteralPath $output -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $guardOutput -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $output | Out-Null
New-Item -ItemType Directory -Path $guardOutput | Out-Null

$buildProperties = @()
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $buildProperties += "-p:Version=$Version"
}
if (-not [string]::IsNullOrWhiteSpace($GitHubOwner)) {
    $buildProperties += "-p:CrabDeskGitHubOwner=$GitHubOwner"
}
if (-not [string]::IsNullOrWhiteSpace($GitHubRepository)) {
    $buildProperties += "-p:CrabDeskGitHubRepository=$GitHubRepository"
}
$buildProperties += "-p:CrabDeskPackageKind=$PackageKind"

if ($PackageKind -eq "Web") {
    dotnet publish (Join-Path $root "CrabDesk.WinUI\CrabDesk.WinUI.csproj") `
        -c $Configuration -r win-x64 --self-contained false `
        -p:SelfContained=false -p:WindowsAppSDKSelfContained=false -p:PublishTrimmed=false `
        -p:DebugType=None -p:DebugSymbols=false `
        -o $output @buildProperties
}
else {
    dotnet publish (Join-Path $root "CrabDesk.WinUI\CrabDesk.WinUI.csproj") `
        -c $Configuration -r win-x64 --self-contained true `
        -p:DebugType=None -p:DebugSymbols=false `
        -o $output @buildProperties
}
if ($LASTEXITCODE -ne 0) {
    throw "CrabDesk.WinUI publish failed with exit code $LASTEXITCODE."
}

Get-ChildItem -LiteralPath $output -Filter "*.pdb" | Remove-Item -Force

if ($PackageKind -eq "Web") {
    dotnet publish (Join-Path $root "CrabDesk.IconGuard\CrabDesk.IconGuard.csproj") `
        -c $Configuration -r win-x64 --self-contained false `
        -p:SelfContained=false -p:PublishTrimmed=false `
        -p:DebugType=None -p:DebugSymbols=false `
        -o $guardOutput
}
else {
    dotnet publish (Join-Path $root "CrabDesk.IconGuard\CrabDesk.IconGuard.csproj") `
        -c $Configuration -r win-x64 --self-contained true `
        -p:PublishTrimmed=false `
        -p:DebugType=None -p:DebugSymbols=false `
        -o $guardOutput
}
if ($LASTEXITCODE -ne 0) {
    throw "CrabDesk.IconGuard publish failed with exit code $LASTEXITCODE."
}

Get-ChildItem -LiteralPath $guardOutput -Filter "CrabDesk.IconGuard.*" | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $output -Force
}
Remove-Item -LiteralPath $guardOutput -Recurse -Force

Write-Host "CrabDesk published to $output"
