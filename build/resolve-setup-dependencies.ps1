param(
    [Parameter(Mandatory = $true)]
    [string]$WindowsAppRuntimeVersion,
    [string]$ManifestPath = "..\installer\setup-dependencies.json"
)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$manifest = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $ManifestPath))
$installerDirectory = [System.IO.Path]::GetFullPath((Join-Path $root "installer")).TrimEnd('\') + '\'
if (-not $manifest.StartsWith($installerDirectory, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Setup dependency manifest must stay inside the installer directory."
}
if (-not (Test-Path -LiteralPath $manifest)) {
    throw "Setup dependency manifest was not found: $manifest"
}

$dependencies = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($name in @("dotNetDesktopRuntime", "windowsAppRuntime", "visualCppRuntime")) {
    $dependency = $dependencies.$name
    if ($null -eq $dependency) {
        throw "Setup dependency manifest is missing $name."
    }
    $uri = $null
    if (-not [Uri]::TryCreate([string]$dependency.url, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne "https" -or
        $uri.Host -notin @("download.microsoft.com", "download.visualstudio.microsoft.com")) {
        throw "$name must use a fixed HTTPS URL on an approved Microsoft download host."
    }
    if ([string]$dependency.sha256 -notmatch '^[0-9A-Fa-f]{64}$') {
        throw "$name does not contain a valid SHA-256 value."
    }
}

if ([string]$dependencies.windowsAppRuntime.version -ne $WindowsAppRuntimeVersion) {
    throw "Windows App Runtime manifest version $($dependencies.windowsAppRuntime.version) does not match project package version $WindowsAppRuntimeVersion."
}
if ([string]$dependencies.windowsAppRuntime.packageName -ne "Microsoft.WindowsAppRuntime.1.8") {
    throw "Windows App Runtime package detection name is invalid."
}
if ([string]$dependencies.dotNetDesktopRuntime.version -notmatch '^8\.0\.\d+$') {
    throw "The setup must use a stable .NET 8 Desktop Runtime patch."
}
$visualCppVersion = $null
if (-not [Version]::TryParse([string]$dependencies.visualCppRuntime.version, [ref]$visualCppVersion)) {
    throw "Visual C++ runtime version is invalid."
}

return [pscustomobject]@{
    DotNetVersion = [string]$dependencies.dotNetDesktopRuntime.version
    DotNetUrl = [string]$dependencies.dotNetDesktopRuntime.url
    DotNetSha256 = ([string]$dependencies.dotNetDesktopRuntime.sha256).ToLowerInvariant()
    WindowsAppRuntimeVersion = [string]$dependencies.windowsAppRuntime.version
    WindowsAppRuntimePackageName = [string]$dependencies.windowsAppRuntime.packageName
    WindowsAppRuntimeUrl = [string]$dependencies.windowsAppRuntime.url
    WindowsAppRuntimeSha256 = ([string]$dependencies.windowsAppRuntime.sha256).ToLowerInvariant()
    VisualCppVersion = [string]$dependencies.visualCppRuntime.version
    VisualCppUrl = [string]$dependencies.visualCppRuntime.url
    VisualCppSha256 = ([string]$dependencies.visualCppRuntime.sha256).ToLowerInvariant()
}
