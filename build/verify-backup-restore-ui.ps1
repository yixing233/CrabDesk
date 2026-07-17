param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

dotnet test (Join-Path $root "CrabDesk.Tests\CrabDesk.Tests.csproj") `
    -c $Configuration `
    --filter "FullyQualifiedName~BackupServiceTests"
if ($LASTEXITCODE -ne 0) {
    throw "Backup service verification failed with exit code $LASTEXITCODE."
}

dotnet test (Join-Path $root "CrabDesk.WinUI.Tests\CrabDesk.WinUI.Tests.csproj") `
    -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "WinUI backup presentation verification failed with exit code $LASTEXITCODE."
}

Write-Host "Backup restore and WinUI presentation verification passed."
