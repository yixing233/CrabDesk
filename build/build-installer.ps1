param(
    [string]$Version = "",
    [string]$IsccPath = ""
)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$publishDirectory = Join-Path $root "artifacts\publish\win-x64"
$scriptPath = Join-Path $root "installer\CrabDesk.iss"
if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory "CrabDesk.WinUI.exe"))) {
    throw "Published CrabDesk files were not found. Run .\build\publish.ps1 first."
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $root "CrabDesk.WinUI\CrabDesk.WinUI.csproj") -Raw -Encoding UTF8
    $Version = @($project.Project.PropertyGroup.Version | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })[0]
}
if ($Version -notmatch '^(?:\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?|\d{8}\.\d{2})$') {
    throw "Invalid installer version: $Version"
}

$command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
$candidates = @(
    $IsccPath,
    $command.Source,
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
$iscc = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 ISCC.exe was not found."
}

$installerDirectory = Join-Path $root "artifacts\installer"
[System.IO.Directory]::CreateDirectory($installerDirectory) | Out-Null

& $iscc "/DMyAppVersion=$Version" "/DOutputDir=$installerDirectory" "/DOutputBaseFilename=CrabDesk-Payload-x64" $scriptPath
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

$payload = Join-Path $installerDirectory "CrabDesk-Payload-x64.exe"
if (-not (Test-Path -LiteralPath $payload)) {
    throw "Inno Setup did not produce the expected payload: $payload"
}
$size = (Get-Item -LiteralPath $payload).Length / 1MB
Write-Host ("CrabDesk {0} framework-dependent payload built at {1} ({2:N2} MB)" -f $Version, $payload, $size)
