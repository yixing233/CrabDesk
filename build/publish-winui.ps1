param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root "artifacts\publish\winui-$Runtime"
$guardOutput = Join-Path $root "artifacts\publish\icon-guard-$Runtime-temp"

function Remove-PublishDirectory([string]$path) {
    $workspace = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
    $resolved = [IO.Path]::GetFullPath($path)
    if (-not $resolved.StartsWith($workspace, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Publish cleanup escaped the workspace: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

Remove-PublishDirectory $output
Remove-PublishDirectory $guardOutput

dotnet publish (Join-Path $root "CrabDesk.WinUI\CrabDesk.WinUI.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o $output

dotnet publish (Join-Path $root "CrabDesk.IconGuard\CrabDesk.IconGuard.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishTrimmed=false `
    -o $guardOutput

Get-ChildItem -LiteralPath $guardOutput -Filter "CrabDesk.IconGuard.*" | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $output -Force
}

Remove-PublishDirectory $guardOutput

Write-Host "CrabDesk WinUI published to $output"
