<#
.SYNOPSIS
Builds the current source into a fresh test build directory and (re)starts it.

.DESCRIPTION
Verification builds used to pile up as one-off artifacts/<name>-build folders.
This keeps one predictable location for manual testing, stops any CrabDesk
instance already running from that folder, publishes the current source, and
starts the result.

The publish target is removed first so a stale file from an earlier build can
never be served: a leftover assembly is exactly how a fix that was present in
source looked absent at runtime.

.PARAMETER Name
Label for the build folder, so consecutive tests stay distinguishable.

.PARAMETER Arguments
Extra command-line arguments for CrabDesk, e.g. --show-widgets.

.PARAMETER NoLaunch
Publish only; do not start the app.
#>
param(
    [string]$Name = "test",
    [string[]]$Arguments = @(),
    [switch]$NoLaunch,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$output = Join-Path $root "artifacts\$Name-build"
$workspace = $root.TrimEnd('\') + '\'
$resolvedOutput = [System.IO.Path]::GetFullPath($output)
if (-not $resolvedOutput.StartsWith($workspace, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Build cleanup escaped the workspace: $resolvedOutput"
}

$exe = Join-Path $resolvedOutput "CrabDesk.WinUI.exe"

# CrabDesk holds a machine-local single-instance mutex, so a second copy only
# forwards its command line to the first and exits. Any instance still running
# therefore owns the desktop, keeps serving its own binaries and would swallow
# this launch, so every instance has to be stopped first.
Get-Process CrabDesk.WinUI -ErrorAction SilentlyContinue | ForEach-Object {
    $running = $null
    try { $running = $_.Path } catch { }
    Write-Host "Stopping CrabDesk PID $($_.Id) ($running)"
    $_.CloseMainWindow() | Out-Null
    if (-not $_.WaitForExit(5000)) { $_.Kill(); $_.WaitForExit(5000) | Out-Null }
}
# Give the desktop takeover a moment to release the Explorer hand-off.
Start-Sleep -Milliseconds 500

if (Test-Path -LiteralPath $resolvedOutput) {
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}

Write-Host "Publishing $Configuration/$Runtime to $output"
dotnet publish (Join-Path $root "CrabDesk.WinUI\CrabDesk.WinUI.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -p:WindowsAppSDKSelfContained=false `
    -o $resolvedOutput
if ($LASTEXITCODE -ne 0) {
    throw "Publish failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $exe)) {
    throw "Publish did not produce $exe"
}

$stamp = (Get-Item -LiteralPath $exe).LastWriteTime
Write-Host "Built $exe ($stamp)"

if ($NoLaunch) { return }

Start-Process -FilePath $exe -ArgumentList $Arguments
Start-Sleep -Seconds 3
$started = Get-Process CrabDesk.WinUI -ErrorAction SilentlyContinue |
    Where-Object { try { $_.Path -ieq $exe } catch { $false } }
if (-not $started) {
    throw "CrabDesk did not stay running after launch."
}
$started | ForEach-Object { Write-Host "Running PID $($_.Id)" }
