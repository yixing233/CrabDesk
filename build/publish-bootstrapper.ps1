param(
    [string]$Configuration = "Release",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$releaseDirectory = Join-Path $root "artifacts\release"
$publishDirectory = Join-Path $releaseDirectory "bootstrapper"
$output = Join-Path $releaseDirectory "CrabDesk-Setup-x64.exe"
$payload = Join-Path $root "artifacts\installer\CrabDesk-Payload-x64.exe"
$projectPath = Join-Path $root "CrabDesk.WinUI\CrabDesk.WinUI.csproj"
if (-not (Test-Path -LiteralPath $payload) -or (Get-Item -LiteralPath $payload).Length -le 0) {
    throw "Framework-dependent payload was not found. Run .\build\build-installer.ps1 first."
}
$payloadSha256 = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant()

[xml]$project = Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = @($project.Project.PropertyGroup.Version | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })[0]
}
$runtimeReference = @($project.Project.ItemGroup.PackageReference |
    Where-Object { $_.Include -eq "Microsoft.WindowsAppSDK.Runtime" })[0]
if ($null -eq $runtimeReference -or [string]::IsNullOrWhiteSpace([string]$runtimeReference.Version)) {
    throw "Microsoft.WindowsAppSDK.Runtime version was not found in CrabDesk.WinUI.csproj."
}
$windowsAppRuntimeVersion = [string]$runtimeReference.Version
$dependencies = & (Join-Path $PSScriptRoot "resolve-setup-dependencies.ps1") `
    -WindowsAppRuntimeVersion $windowsAppRuntimeVersion

$properties = @(
    "-p:DotNetDesktopInstallerUrl=$($dependencies.DotNetUrl)",
    "-p:DotNetDesktopInstallerSha256=$($dependencies.DotNetSha256)",
    "-p:DotNetDesktopMinimumVersion=$($dependencies.DotNetVersion)",
    "-p:WindowsAppRuntimeInstallerUrl=$($dependencies.WindowsAppRuntimeUrl)",
    "-p:WindowsAppRuntimeInstallerSha256=$($dependencies.WindowsAppRuntimeSha256)",
    "-p:VisualCppInstallerUrl=$($dependencies.VisualCppUrl)",
    "-p:VisualCppInstallerSha256=$($dependencies.VisualCppSha256)",
    "-p:VisualCppMinimumVersion=$($dependencies.VisualCppVersion)",
    "-p:CrabDeskPayloadPath=$payload",
    "-p:PayloadSha256=$payloadSha256"
)
if (-not [string]::IsNullOrWhiteSpace($Version)) { $properties += "-p:Version=$Version" }

[System.IO.Directory]::CreateDirectory($releaseDirectory) | Out-Null
if (Test-Path -LiteralPath $publishDirectory) {
    $resolvedPublish = [System.IO.Path]::GetFullPath($publishDirectory)
    $resolvedRelease = [System.IO.Path]::GetFullPath($releaseDirectory).TrimEnd('\') + '\'
    if (-not $resolvedPublish.StartsWith($resolvedRelease, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Bootstrapper cleanup escaped the release directory."
    }
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

dotnet publish (Join-Path $root "CrabDesk.Bootstrapper\CrabDesk.Bootstrapper.csproj") `
    -c $Configuration -r win-x64 --self-contained true `
    -p:PublishAot=true -p:StripSymbols=true `
    -p:DebugType=None -p:DebugSymbols=false `
    -o $publishDirectory @properties
if ($LASTEXITCODE -ne 0) {
    throw "Bootstrapper publish failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath (Join-Path $publishDirectory "CrabDesk.Bootstrapper.exe") -Destination $output -Force
Remove-Item -LiteralPath $publishDirectory -Recurse -Force
$size = (Get-Item -LiteralPath $output).Length / 1MB
Write-Host ("Online setup published to {0:N2} MB: {1}" -f $size, $output)
