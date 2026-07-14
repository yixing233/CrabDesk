param(
    [string]$SetupExecutable = "..\artifacts\installer\CrabDesk-Setup-x64.exe"
)

$ErrorActionPreference = "Stop"
$setup = if ([System.IO.Path]::IsPathRooted($SetupExecutable)) {
    [System.IO.Path]::GetFullPath($SetupExecutable)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $SetupExecutable))
}
if (-not (Test-Path -LiteralPath $setup)) {
    throw "CrabDesk setup executable not found: $setup"
}
if (@(Get-Process CrabDesk.App,CrabDesk.IconGuard -ErrorAction SilentlyContinue).Count -gt 0) {
    throw "Close the running CrabDesk instance before verifying the installer."
}

$uninstallKeys = @(
    "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{8AF9FCA9-D889-4ED7-B5A2-AC052B94016D}_is1",
    "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{8AF9FCA9-D889-4ED7-B5A2-AC052B94016D}_is1"
)
if ($uninstallKeys | Where-Object { Test-Path $_ }) {
    throw "An installed CrabDesk registration already exists; the isolated installer test will not replace it."
}

function Get-ExplorerHideIcons {
    try {
        return [int](Get-ItemPropertyValue "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" HideIcons)
    }
    catch {
        return 0
    }
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("CrabDesk.InstallerTest." + [Guid]::NewGuid().ToString("N"))
$installDirectory = Join-Path $testRoot "app"
$dataDirectory = Join-Path $testRoot "data"
$setupLog = Join-Path $testRoot "setup.log"
$uninstallLog = Join-Path $testRoot "uninstall.log"
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
[System.IO.Directory]::CreateDirectory($dataDirectory) | Out-Null
$previousDataDirectory = $env:CRABDESK_DATA_DIR
$previousHideIcons = Get-ExplorerHideIcons
$env:CRABDESK_DATA_DIR = $dataDirectory

$config = @{
    SchemaVersion = 13
    Settings = @{
        TakeOverDesktop = $true
        ShowSystemItems = $false
        ConfirmDeleteBox = $true
        ThemeMode = 0
        DesktopBehavior = @{
            LaunchToTray = $true
            RefreshAfterRename = $true
            ShowDesktopContextMenu = $false
            ToggleIconsOnDesktopDoubleClick = $false
        }
        Backup = @{
            DailyBackup = $false
            RetentionDays = 7
            BackupDirectory = ""
        }
        Updates = @{
            CheckOnStartup = $false
            Channel = 0
        }
    }
    Boxes = @(@{
        Id = [Guid]::NewGuid()
        Title = "Installer verification"
        MonitorId = "primary"
        Bounds = @{ X = 40; Y = 50; Width = 420; Height = 300 }
    })
    Assignments = @{}
    Organization = @{
        Enabled = $false
        RunOnStartup = $false
        RunOnDesktopChanges = $false
        ReassignExistingItems = $false
    }
    OrganizationRules = @()
}
$config | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $dataDirectory "config.json") -Encoding UTF8

$appProcess = $null
$uninstaller = Join-Path $installDirectory "unins000.exe"
try {
    $installArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=`"$installDirectory`" /LOG=`"$setupLog`""
    $installer = Start-Process -FilePath $setup -ArgumentList $installArguments -Wait -PassThru
    if ($installer.ExitCode -ne 0) {
        throw "CrabDesk setup exited with code $($installer.ExitCode). See $setupLog"
    }

    $installedApp = Join-Path $installDirectory "CrabDesk.App.exe"
    $installedGuard = Join-Path $installDirectory "CrabDesk.IconGuard.exe"
    foreach ($required in $installedApp, $installedGuard, (Join-Path $installDirectory "LICENSE"), (Join-Path $installDirectory "PRIVACY.md"), $uninstaller) {
        if (-not (Test-Path -LiteralPath $required)) {
            throw "Installed file is missing: $required"
        }
    }
    $productVersion = (Get-Item -LiteralPath $installedApp).VersionInfo.ProductVersion
    if (-not $productVersion.StartsWith("0.6.0", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Installed application version is unexpected: $productVersion"
    }

    $appProcess = Start-Process -FilePath $installedApp -PassThru
    Start-Sleep -Seconds 8
    $appProcess.Refresh()
    if ($appProcess.HasExited) {
        throw "The installed CrabDesk executable did not remain running."
    }

    $exitCommand = Start-Process -FilePath $installedApp -ArgumentList "--exit-existing" -Wait -PassThru
    if ($exitCommand.ExitCode -ne 0) {
        throw "The installed app exit command returned code $($exitCommand.ExitCode)."
    }
    if (-not $appProcess.WaitForExit(15000)) {
        throw "The installed CrabDesk process did not exit cleanly."
    }
    if ((Get-ExplorerHideIcons) -ne $previousHideIcons) {
        throw "Explorer desktop icon visibility was not restored after exiting the installed app."
    }

    $appProcess = Start-Process -FilePath $installedApp -PassThru
    Start-Sleep -Seconds 8
    if ($appProcess.HasExited) {
        throw "The installed CrabDesk process exited before the uninstall test."
    }

    $uninstallArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"$uninstallLog`""
    $uninstallProcess = Start-Process -FilePath $uninstaller -ArgumentList $uninstallArguments -Wait -PassThru
    if ($uninstallProcess.ExitCode -ne 0) {
        throw "CrabDesk uninstaller exited with code $($uninstallProcess.ExitCode). See $uninstallLog"
    }
    if (-not $appProcess.WaitForExit(15000)) {
        throw "Uninstall did not stop the running CrabDesk process."
    }
    if (Test-Path -LiteralPath $installedApp) {
        throw "CrabDesk application files remained after uninstall."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $dataDirectory "config.json"))) {
        throw "Uninstall unexpectedly removed the user's CrabDesk configuration."
    }
    if ((Get-ExplorerHideIcons) -ne $previousHideIcons) {
        throw "Explorer desktop icon visibility was not restored by uninstall."
    }
    if ($uninstallKeys | Where-Object { Test-Path $_ }) {
        throw "The CrabDesk uninstall registration remained after uninstall."
    }

    $hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    $signed = (Get-AuthenticodeSignature -LiteralPath $setup).Status -eq "Valid"
    Write-Host "InstallerVersion=$productVersion; SHA256=$hash; Signed=$signed"
}
finally {
    if ($appProcess -and -not $appProcess.HasExited) {
        Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $uninstaller) {
        $resolvedInstall = [System.IO.Path]::GetFullPath($installDirectory)
        $resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
        if ($resolvedInstall.StartsWith($resolvedTestRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            Start-Process -FilePath $uninstaller -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART" -Wait -ErrorAction SilentlyContinue
        }
    }
    if ($null -eq $previousDataDirectory) {
        Remove-Item Env:\CRABDESK_DATA_DIR -ErrorAction SilentlyContinue
    }
    else {
        $env:CRABDESK_DATA_DIR = $previousDataDirectory
    }
    $resolvedRoot = [System.IO.Path]::GetFullPath($testRoot)
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedRoot.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Installer install, launch, exit, uninstall and recovery verification passed."
