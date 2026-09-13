param(
    [string]$Configuration = "Release",
    [string]$Version = "",
    [string]$GitHubOwner = $env:CRABDESK_GITHUB_OWNER,
    [string]$GitHubRepository = $env:CRABDESK_GITHUB_REPOSITORY_NAME
)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifacts = [System.IO.Path]::GetFullPath((Join-Path $root "artifacts"))
$output = [System.IO.Path]::GetFullPath((Join-Path $artifacts "publish\win-x64"))
if (-not $output.StartsWith($artifacts, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Publish paths must stay inside the repository artifacts directory."
}

# Publishing over a running instance leaves the output in a mixed state:
# locked exe/dll survive the cleanup and the .pri copy is skipped, which makes
# the next launch fail with "Cannot locate resource ms-appx:///MainWindow.xaml".
$runningInstances = Get-Process "CrabDesk.WinUI" -ErrorAction SilentlyContinue
if ($runningInstances) {
    Write-Host "Stopping running CrabDesk.WinUI instances before publishing."
    $runningInstances | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

Remove-Item -LiteralPath $output -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $output -Force | Out-Null

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

dotnet publish (Join-Path $root "CrabDesk.WinUI\CrabDesk.WinUI.csproj") `
    -c $Configuration -r win-x64 --self-contained false `
    -p:SelfContained=false -p:WindowsAppSDKSelfContained=false -p:PublishTrimmed=false `
    -p:DebugType=None -p:DebugSymbols=false `
    -o $output @buildProperties
if ($LASTEXITCODE -ne 0) {
    throw "CrabDesk.WinUI publish failed with exit code $LASTEXITCODE."
}

$pri = Join-Path $output "CrabDesk.WinUI.pri"
if (-not (Test-Path -LiteralPath $pri)) {
    throw "Published output is missing CrabDesk.WinUI.pri; the app would fail at startup with 'Cannot locate resource ms-appx:///MainWindow.xaml'."
}

Get-ChildItem -LiteralPath $output -Filter "*.pdb" | Remove-Item -Force
foreach ($runtimeFile in @("coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "Microsoft.WindowsAppRuntime.dll")) {
    if (Test-Path -LiteralPath (Join-Path $output $runtimeFile)) {
        throw "Framework-dependent publish unexpectedly contains app-local runtime file: $runtimeFile"
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $output "CrabDesk.WinUI.runtimeconfig.json"))) {
    throw "Framework-dependent publish did not produce CrabDesk.WinUI.runtimeconfig.json."
}

Write-Host "Framework-dependent CrabDesk published to $output"
