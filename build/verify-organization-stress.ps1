param(
    [string]$Executable = "..\artifacts\publish\win-x64\CrabDesk.App.exe",
    [int]$FileCount = 100
)

$ErrorActionPreference = "Stop"
$exe = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Executable))
if (-not (Test-Path -LiteralPath $exe)) {
    throw "CrabDesk executable not found: $exe"
}
if (Get-Process CrabDesk.App -ErrorAction SilentlyContinue) {
    throw "Close the running CrabDesk instance before the organization stress verifier."
}

$testId = [Guid]::NewGuid().ToString("N")
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("CrabDesk.RuleStress." + $testId)
$desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
$createdPaths = New-Object System.Collections.Generic.List[string]
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null

$previousDataDirectory = $env:CRABDESK_DATA_DIR
$env:CRABDESK_DATA_DIR = $testRoot
$sourceBoxId = [Guid]::NewGuid()
$targetBoxId = [Guid]::NewGuid()
$ruleId = [Guid]::NewGuid()
$config = @{
    SchemaVersion = 8
    Settings = @{
        TakeOverDesktop = $false
        ShowSystemItems = $false
        ConfirmDeleteBox = $true
        ThemeMode = 0
        DesktopBehavior = @{ LaunchToTray = $true; RefreshAfterRename = $true }
    }
    Organization = @{
        Enabled = $true
        RunOnStartup = $false
        RunOnDesktopChanges = $true
        ReassignExistingItems = $false
    }
    OrganizationRules = @(@{
        Id = $ruleId
        Title = "Stress rule"
        Enabled = $true
        Priority = 10
        ItemKinds = @(0)
        NamePattern = "CrabDeskStress-$testId-*"
        Extensions = @("crabstress")
        Action = 0
        TargetBoxId = $targetBoxId
    })
    Boxes = @(
        @{
            Id = $sourceBoxId
            Title = "Source"
            MonitorId = "primary"
            Bounds = @{ X = 40; Y = 50; Width = 420; Height = 300 }
        },
        @{
            Id = $targetBoxId
            Title = "Stress target"
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
    Start-Sleep -Seconds 5
    if ($process.HasExited) {
        throw "CrabDesk exited before the stress test started."
    }

    for ($index = 0; $index -lt $FileCount; $index++) {
        $path = Join-Path $desktop ("CrabDeskStress-$testId-$index.crabstress")
        [System.IO.File]::WriteAllText($path, "stress-$index")
        $createdPaths.Add($path)
    }

    $configPath = Join-Path $testRoot "config.json"
    $deadline = [DateTime]::UtcNow.AddSeconds(35)
    $assignmentCount = 0
    do {
        Start-Sleep -Milliseconds 500
        try {
            $saved = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $assignments = @($saved.Assignments.PSObject.Properties)
            $assignmentCount = @($assignments | Where-Object { [Guid]$_.Value -eq $targetBoxId }).Count
        }
        catch {
            $assignmentCount = 0
        }
    } while ($assignmentCount -lt $FileCount -and [DateTime]::UtcNow -lt $deadline)

    if ($assignmentCount -ne $FileCount) {
        throw "Realtime organization assigned $assignmentCount of $FileCount rapid file events."
    }
    Write-Host "Realtime organization handled all $FileCount rapid desktop file events."

    Start-Process -FilePath $exe -ArgumentList "--undo-organization" -Wait
    $undoDeadline = [DateTime]::UtcNow.AddSeconds(12)
    do {
        Start-Sleep -Milliseconds 350
        try {
            $saved = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $assignmentCount = @($saved.Assignments.PSObject.Properties).Count
        }
        catch {
            $assignmentCount = $FileCount
        }
    } while ($assignmentCount -ne 0 -and [DateTime]::UtcNow -lt $undoDeadline)
    if ($assignmentCount -ne 0) {
        throw "Process-level undo left $assignmentCount assignments in the layout."
    }
    Write-Host "Process-level organization undo restored the previous assignment set."

    $exitCommand = Start-Process -FilePath $exe -ArgumentList "--exit-existing" -Wait -PassThru
    if ($exitCommand.ExitCode -ne 0) {
        throw "The organization-stress exit command returned code $($exitCommand.ExitCode)."
    }
    if (-not $process.WaitForExit(10000)) {
        throw "CrabDesk did not exit cleanly after the organization stress verifier."
    }
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    foreach ($path in $createdPaths) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
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
