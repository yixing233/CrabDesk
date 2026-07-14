param(
    [string]$Executable = "..\artifacts\publish\win-x64\CrabDesk.App.exe"
)

$ErrorActionPreference = "Stop"
$exe = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Executable))
if (-not (Test-Path -LiteralPath $exe)) {
    throw "CrabDesk executable not found: $exe"
}
if (@(Get-Process CrabDesk.App,CrabDesk.IconGuard -ErrorAction SilentlyContinue).Count -gt 0) {
    throw "Close the running CrabDesk instance before verifying organization rules."
}

$testId = [Guid]::NewGuid().ToString("N")
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("CrabDesk.RuleTest." + $testId)
$desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
$desktopFile = Join-Path $desktop ("CrabDeskRuleTest-" + $testId + ".crabrule")
$desktopLiveFile = Join-Path $desktop ("CrabDeskRuleLiveTest-" + $testId + ".crablive")
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
[System.IO.File]::WriteAllText($desktopFile, "CrabDesk organization rule integration test")

$previousDataDirectory = $env:CRABDESK_DATA_DIR
$env:CRABDESK_DATA_DIR = $testRoot
$sourceBoxId = [Guid]::NewGuid()
$targetBoxId = [Guid]::NewGuid()
$ruleId = [Guid]::NewGuid()
$config = @{
    SchemaVersion = 4
    Settings = @{
        TakeOverDesktop = $true
        ShowSystemItems = $false
        ConfirmDeleteBox = $true
        ThemeMode = 0
        DesktopBehavior = @{
            RefreshAfterRename = $false
        }
    }
    Organization = @{
        Enabled = $true
        RunOnStartup = $true
        RunOnDesktopChanges = $true
        ReassignExistingItems = $false
    }
    OrganizationRules = @(@{
        Id = $ruleId
        Title = "CrabDesk 测试规则"
        Enabled = $true
        Priority = 10
        ItemKinds = @(0)
        NamePattern = "*"
        Extensions = @("crabrule", "crablive")
        Action = 0
        TargetBoxId = $targetBoxId
    })
    Boxes = @(
        @{
            Id = $sourceBoxId
            Title = "常用"
            MonitorId = "primary"
            Bounds = @{ X = 40; Y = 50; Width = 420; Height = 300 }
        },
        @{
            Id = $targetBoxId
            Title = "规则目标"
            MonitorId = "primary"
            Bounds = @{ X = 500; Y = 50; Width = 360; Height = 280 }
        }
    )
    Assignments = @{}
}
$config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $testRoot "config.json") -Encoding UTF8

$process = $null
try {
    $process = Start-Process -FilePath $exe -PassThru
    Start-Sleep -Seconds 8
    if ($process.HasExited) {
        throw "CrabDesk exited before applying startup organization rules."
    }
    [System.IO.File]::WriteAllText($desktopLiveFile, "CrabDesk realtime organization rule integration test")
    Start-Sleep -Seconds 4
    $exitCommand = Start-Process -FilePath $exe -ArgumentList "--exit-existing" -Wait -PassThru
    if ($exitCommand.ExitCode -ne 0) {
        throw "The organization-rules exit command returned code $($exitCommand.ExitCode)."
    }
    if (-not $process.WaitForExit(10000)) {
        throw "CrabDesk did not exit after the rule test."
    }

    $saved = Get-Content -LiteralPath (Join-Path $testRoot "config.json") -Raw -Encoding UTF8 | ConvertFrom-Json
    $assignedTargets = @($saved.Assignments.PSObject.Properties | ForEach-Object { [Guid]$_.Value })
    if ($assignedTargets.Count -ne 2 -or @($assignedTargets | Where-Object { $_ -ne $targetBoxId }).Count -ne 0) {
        $assignmentDump = ($saved.Assignments | ConvertTo-Json -Compress)
        throw "Startup and realtime rules did not assign both test desktop items to the target box. Count=$($assignedTargets.Count), Assignments=$assignmentDump"
    }
    Write-Host "Startup and realtime organization rules assigned both matching desktop items."
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $desktopFile -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $desktopLiveFile -Force -ErrorAction SilentlyContinue
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

Write-Host "Organization rule integration test passed."
