param(
    [string]$SetupExecutable = "..\artifacts\release\CrabDesk-Setup-x64.exe"
)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$setup = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $SetupExecutable))
if (-not (Test-Path -LiteralPath $setup) -or (Get-Item -LiteralPath $setup).Length -le 0) {
    throw "Online setup was not found: $setup"
}
if ([System.IO.Path]::GetFileName($setup) -ne "CrabDesk-Setup-x64.exe") {
    throw "The public online setup must use the fixed CrabDesk-Setup-x64.exe name."
}

$releaseDirectory = Split-Path -Parent $setup
foreach ($runtimeFile in @("coreclr.dll", "hostfxr.dll", "hostpolicy.dll")) {
    if (Test-Path -LiteralPath (Join-Path $releaseDirectory $runtimeFile)) {
        throw "Native AOT setup unexpectedly requires an adjacent $runtimeFile."
    }
}

$diagnosticRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("CrabDesk.SetupDiagnostic." + [Guid]::NewGuid().ToString("N"))
$diagnosticPath = Join-Path $diagnosticRoot "diagnostic.txt"
[System.IO.Directory]::CreateDirectory($diagnosticRoot) | Out-Null
try {
    $process = Start-Process -FilePath $setup -ArgumentList @("--diagnose", $diagnosticPath) -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Setup diagnostic mode exited with code $($process.ExitCode)."
    }
    if (-not (Test-Path -LiteralPath $diagnosticPath)) {
        throw "Setup diagnostic mode did not produce its report."
    }
    $diagnostic = Get-Content -LiteralPath $diagnosticPath -Raw -Encoding UTF8
    foreach ($fragment in @(
        "Platform=Windows-x64",
        "PayloadAsset=CrabDesk-Payload-x64.exe",
        "PayloadEmbedded=True",
        "Dependency.VisualCppRuntime=",
        "Dependency.DotNetDesktopRuntime=",
        "Dependency.WindowsAppRuntime="
    )) {
        if ($diagnostic.IndexOf($fragment, [System.StringComparison]::Ordinal) -lt 0) {
            throw "Setup diagnostic report is missing: $fragment"
        }
    }
    $hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "NativeAotSetup=$setup; SHA256=$hash"
    Write-Host $diagnostic.Trim()
}
finally {
    $resolvedDiagnosticRoot = [System.IO.Path]::GetFullPath($diagnosticRoot)
    $systemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedDiagnosticRoot.StartsWith($systemTemp, [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedDiagnosticRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Native AOT online setup and dependency diagnostic verification passed."
