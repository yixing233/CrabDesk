param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$output = Join-Path $root "artifacts\publish\winui-$Runtime"
$workspace = $root.TrimEnd('\') + '\'
$resolvedOutput = [System.IO.Path]::GetFullPath($output)
if (-not $resolvedOutput.StartsWith($workspace, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Publish cleanup escaped the workspace: $resolvedOutput"
}
if (Test-Path -LiteralPath $resolvedOutput) {
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}

dotnet publish (Join-Path $root "CrabDesk.WinUI\CrabDesk.WinUI.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -p:WindowsAppSDKSelfContained=false `
    -o $resolvedOutput
if ($LASTEXITCODE -ne 0) {
    throw "CrabDesk WinUI publish failed with exit code $LASTEXITCODE."
}

Write-Host "Framework-dependent CrabDesk WinUI published to $resolvedOutput"
