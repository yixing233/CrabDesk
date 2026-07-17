param(
    [string]$Executable = "..\artifacts\publish\win-x64\CrabDesk.WinUI.exe",
    [string]$Repository = "dotnet/runtime",
    [int]$VisualDelaySeconds = 0,
    [switch]$UseBuildMetadata
)

$ErrorActionPreference = "Stop"
$exe = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Executable))
if (-not (Test-Path -LiteralPath $exe)) {
    throw "CrabDesk executable not found: $exe"
}
if (Get-Process CrabDesk.WinUI -ErrorAction SilentlyContinue) {
    throw "Close the running CrabDesk instance before the GitHub update verifier."
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("CrabDesk.UpdateTest." + [Guid]::NewGuid().ToString("N"))
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
$boxId = [Guid]::NewGuid()
$config = @{
    SchemaVersion = 15
    Settings = @{
        TakeOverDesktop = $false
        ShowSystemItems = $false
        ThemeMode = 0
        DesktopBehavior = @{ LaunchToTray = $true; RefreshAfterRename = $true }
        Updates = @{
            CheckOnStartup = $true
            Channel = 0
            RepositoryOwner = ""
            RepositoryName = ""
        }
    }
    Boxes = @(@{
        Id = $boxId
        Title = "Update test"
        MonitorId = "primary"
        Bounds = @{ X = 40; Y = 50; Width = 320; Height = 220 }
    })
    Assignments = @{}
}
$configPath = Join-Path $testRoot "config.json"
$config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $configPath -Encoding UTF8

$previousDataDirectory = $env:CRABDESK_DATA_DIR
$previousRepository = $env:CRABDESK_GITHUB_REPOSITORY
$env:CRABDESK_DATA_DIR = $testRoot
if (-not $UseBuildMetadata) {
    $env:CRABDESK_GITHUB_REPOSITORY = $Repository
}
else {
    Remove-Item Env:\CRABDESK_GITHUB_REPOSITORY -ErrorAction SilentlyContinue
}
$process = $null
try {
    $process = Start-Process -FilePath $exe -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $saved = $null
    do {
        Start-Sleep -Milliseconds 500
        if ($process.HasExited) { throw "CrabDesk exited during the GitHub update check." }
        try {
            $saved = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
        }
        catch {
            $saved = $null
        }
    } while (($null -eq $saved -or
              ([int]$saved.Settings.Updates.LastStatus -eq 0 -and
               [string]::IsNullOrWhiteSpace($saved.Settings.Updates.LatestKnownVersion))) -and
             [DateTime]::UtcNow -lt $deadline)

    if ($null -eq $saved -or [string]::IsNullOrWhiteSpace($saved.Settings.Updates.LatestKnownVersion)) {
        if ($null -eq $saved -or $saved.Settings.Updates.LastStatus -notin @(4, 5)) {
            if ($null -ne $saved) {
                Write-Host ($saved.Settings.Updates | ConvertTo-Json -Depth 5 -Compress)
            }
            throw "GitHub Releases did not produce a result or supported offline state."
        }
    }
    if ($saved.Settings.Updates.RepositoryOwner + "/" + $saved.Settings.Updates.RepositoryName -ne $Repository) {
        throw "The configured GitHub repository was not persisted correctly."
    }
    if ($null -eq $saved.Settings.Updates.LastCheckedAt) {
        throw "The last update check time was not persisted."
    }
    if (-not [string]::IsNullOrWhiteSpace($saved.Settings.Updates.LatestKnownVersion) -and
        [string]::IsNullOrWhiteSpace($saved.Settings.Updates.CachedETag)) {
        throw "The successful GitHub update check did not persist an ETag."
    }
    if ([string]::IsNullOrWhiteSpace($saved.Settings.Updates.LatestKnownVersion)) {
        Write-Host "GitHub Releases startup check completed with external state: $($saved.Settings.Updates.LastMessage)"
    }
    else {
        Write-Host "GitHub Releases startup check cached version $($saved.Settings.Updates.LatestKnownVersion) with ETag."
    }
    if ($VisualDelaySeconds -gt 0) {
        Start-Process -FilePath $exe -Wait
        Write-Host "Settings window is available for visual inspection."
        Start-Sleep -Seconds $VisualDelaySeconds
    }

    $exitCommand = Start-Process -FilePath $exe -ArgumentList "--exit-existing" -Wait -PassThru
    if ($exitCommand.ExitCode -ne 0) {
        throw "The GitHub-update exit command returned code $($exitCommand.ExitCode)."
    }
    if (-not $process.WaitForExit(10000)) {
        throw "CrabDesk did not exit cleanly after the GitHub update verifier."
    }
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    if ($null -eq $previousDataDirectory) {
        Remove-Item Env:\CRABDESK_DATA_DIR -ErrorAction SilentlyContinue
    }
    else {
        $env:CRABDESK_DATA_DIR = $previousDataDirectory
    }
    if ($null -eq $previousRepository) {
        Remove-Item Env:\CRABDESK_GITHUB_REPOSITORY -ErrorAction SilentlyContinue
    }
    else {
        $env:CRABDESK_GITHUB_REPOSITORY = $previousRepository
    }
    $resolvedRoot = [System.IO.Path]::GetFullPath($testRoot)
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedRoot.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
