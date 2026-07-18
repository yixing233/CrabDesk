param(
    [string]$Configuration = "Release",
    [string]$Version = "",
    [string]$GitHubOwner = $env:CRABDESK_GITHUB_OWNER,
    [string]$GitHubRepository = $env:CRABDESK_GITHUB_REPOSITORY_NAME
)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$output = Join-Path $root "artifacts\release\CrabDesk-Setup-Web-x64.exe"
$properties = @()
if (-not [string]::IsNullOrWhiteSpace($Version)) { $properties += "-p:Version=$Version" }
if (-not [string]::IsNullOrWhiteSpace($GitHubOwner)) { $properties += "-p:CrabDeskGitHubOwner=$GitHubOwner" }
if (-not [string]::IsNullOrWhiteSpace($GitHubRepository)) { $properties += "-p:CrabDeskGitHubRepository=$GitHubRepository" }

New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
dotnet publish (Join-Path $root "CrabDesk.Bootstrapper\CrabDesk.Bootstrapper.csproj") `
    -c $Configuration -r win-x64 --self-contained true `
    -p:PublishAot=true -p:StripSymbols=true `
    -p:DebugType=None -p:DebugSymbols=false `
    -o (Join-Path $root "artifacts\release\bootstrapper") @properties
if ($LASTEXITCODE -ne 0) { throw "Bootstrapper publish failed with exit code $LASTEXITCODE." }

Copy-Item (Join-Path $root "artifacts\release\bootstrapper\CrabDesk.Bootstrapper.exe") $output -Force
Remove-Item (Join-Path $root "artifacts\release\bootstrapper") -Recurse -Force
$size = (Get-Item $output).Length / 1MB
Write-Host ("Web bootstrapper published to {0:N2} MB: {1}" -f $size, $output)
