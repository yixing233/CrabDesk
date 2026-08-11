param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root "artifacts\publish\winui-$Runtime"

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

dotnet publish (Join-Path $root "CrabDesk.WinUI\CrabDesk.WinUI.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o $output

Write-Host "CrabDesk WinUI published to $output"
