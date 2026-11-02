#Requires -Version 5.1
<#
.SYNOPSIS
  Mas3D Unity entry point: drives apps/unity through the Unity CLI.

.DESCRIPTION
  The CLI lives at %LOCALAPPDATA%\Unity\bin\unity.exe (override with UNITY_CLI).
  It resolves the editor from ProjectVersion.txt and owns logging, timeouts and
  exit codes. Windows PowerShell 5.1 needs this file ASCII-only or BOM-prefixed;
  keep it ASCII so edits from any tool stay parseable.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/unity.ps1 status
  powershell -ExecutionPolicy Bypass -File tools/unity.ps1 selfcheck
  powershell -ExecutionPolicy Bypass -File tools/unity.ps1 scene
  powershell -ExecutionPolicy Bypass -File tools/unity.ps1 anchors
  powershell -ExecutionPolicy Bypass -File tools/unity.ps1 player
  powershell -ExecutionPolicy Bypass -File tools/unity.ps1 capture
  powershell -ExecutionPolicy Bypass -File tools/unity.ps1 test -Mode PlayMode
  powershell -ExecutionPolicy Bypass -File tools/unity.ps1 build -Out build/Mas3D.exe
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('status', 'editors', 'license', 'open', 'selfcheck', 'urp', 'scene', 'anchors', 'player', 'capture', 'test', 'smoke', 'build', 'run')]
    [string]$Action = 'status',

    [ValidateSet('EditMode', 'PlayMode')]
    [string]$Mode = 'EditMode',

    [string]$Target = 'StandaloneWindows64',
    [string]$Out = 'build/Mas3D.exe',
    [int]$Timeout = 0,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Rest = @()
)

# Native stderr (Unity log noise) must not become a terminating error under PS 5.1.
$ErrorActionPreference = 'Continue'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'apps/unity'

$cli = if ($env:UNITY_CLI) { $env:UNITY_CLI } else { Join-Path $env:LOCALAPPDATA 'Unity/bin/unity.exe' }
if (-not (Test-Path $cli)) {
    throw "Unity CLI not found: $cli (set UNITY_CLI to override)"
}

function Invoke-Unity {
    param([string[]] $CliArgs, [int[]] $AllowExit = @(0), [string] $ConsoleLog)
    Write-Verbose ("unity " + ($CliArgs -join ' '))
    if ($ConsoleLog) {
        & $cli --no-banner --non-interactive @CliArgs *> $ConsoleLog
    }
    else {
        & $cli --no-banner --non-interactive @CliArgs
    }
    if ($AllowExit -notcontains $LASTEXITCODE) {
        throw ("unity CLI exited with code {0}: unity {1}" -f $LASTEXITCODE, ($CliArgs -join ' '))
    }
}

# Batch step: run the project with -executeMethod; the CLI finds the editor and
# tails its own log, so we redirect Unity's log to a file and print only the tail.
function Invoke-Batch {
    param([string] $Method, [string[]] $Extra = @(), [switch] $Gpu)
    $logDir = Join-Path $project 'Logs'
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $logName = ($Method -split '\.')[-1]
    $logPath = Join-Path $logDir ("cli-{0}.log" -f $logName)
    $cliArgs = @('run', $project, '--no-tail')
    if ($Timeout -gt 0) { $cliArgs += @('--timeout', "$Timeout") }
    # unity run already adds -batchmode/-quit; only editor-side flags go after --.
    # Keep -executeMethod first: the CLI drops it when -nographics leads the tail.
    $cliArgs += '--'
    $cliArgs += @('-executeMethod', $Method)
    # screenshots need the GPU; every other batch step runs headless
    if (-not $Gpu) { $cliArgs += '-nographics' }
    $cliArgs += @('-logFile', $logPath)
    $cliArgs += $Extra
    $consoleLog = Join-Path $logDir ("cli-{0}.stdout.txt" -f $logName)
    Invoke-Unity -CliArgs $cliArgs -ConsoleLog $consoleLog
    if (Test-Path $logPath) {
        Write-Host ("log: {0}" -f $logPath)
        $marks = Get-Content $logPath | Select-String -Pattern '\[cli\]|error CS|Exception' | ForEach-Object { $_.Line }
        if ($marks) { $marks | ForEach-Object { Write-Host "  | $_" } }
        else { Get-Content $logPath -Tail 15 | ForEach-Object { Write-Host "  | $_" } }
    }
}

switch ($Action) {
    # exit 6 means "no editor is connected" - not a failure
    'status' {
        Invoke-Unity -CliArgs @('status') -AllowExit @(0, 6)
        Write-Host '(exit 6 = no running Editor instance; start one with open or run)'
    }
    'editors'   { Invoke-Unity @('editors', 'list') }
    'license'   { Invoke-Unity @('license', 'list') }
    'open'      { Invoke-Unity @('open', $project) }
    'selfcheck' { Invoke-Batch 'Mas3D.Editor.Cli.SelfCheck' }
    'urp'       { Invoke-Batch 'Mas3D.Editor.CliSteps.MakeUrp' }
    'scene'     { Invoke-Batch 'Mas3D.Editor.CliSteps.CreateMainScene' }
    # room anchors solved from measured geometry (see spec 5.17)
    'anchors'   { Invoke-Batch 'Mas3D.Editor.CliAnchors.Solve' }
    # playable scene: dressed room + CharacterController player + Cinemachine camera
    'player'    { Invoke-Batch 'Mas3D.Editor.CliPlayer.BuildScene' }
    # Four third-person stills of the playable scene, then one PlayMode still that includes the
    # dialogue panel (GPU required, no -nographics). The batch path renders with camera.Render(),
    # which cannot see UI Toolkit overlays, so the panel shot runs through the test runner.
    'capture'   {
        Invoke-Batch 'Mas3D.Editor.CliPlayer.Capture' -Gpu
        $results = Join-Path $project 'TestResults/UiScene.xml'
        $marker = Join-Path $project 'Logs/smoke-marker.txt'
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $results) | Out-Null
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $marker) | Out-Null
        if (Test-Path $marker) { Remove-Item -LiteralPath $marker -Force }
        $args = @('test', $project, '--mode', 'PlayMode', '--output', $results, '--report-format', 'nunit', '--filter', 'Mas3D.Tests.PlayMode.BridgeSmokeTests.UiSceneShot')
        if ($Timeout -gt 0) { $args += @('--timeout', "$Timeout") }
        $bad = $false
        try { Invoke-Unity $args } catch { $bad = $true; Write-Host $_.Exception.Message }
        if (Test-Path $marker) {
            Get-Content -LiteralPath $marker -Encoding UTF8 | Where-Object { $_ -match '\[shot\]' } | ForEach-Object { Write-Host $_ }
        }
        else {
            Write-Host '[shot] marker missing: ui-scene.png not written'
            $bad = $true
        }
        Write-Host "results: $results"
        if ($bad) { exit 1 }
    }
    'test'      {
        $results = Join-Path $project "TestResults/$Mode.xml"
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $results) | Out-Null
        $args = @('test', $project, '--mode', $Mode, '--output', $results, '--report-format', 'nunit')
        if ($Timeout -gt 0) { $args += @('--timeout', "$Timeout") }
        Invoke-Unity $args
        Write-Host "results: $results"
    }
    # One end-to-end PlayMode case against the real mock bridge. The case writes its marker to
    # Logs/smoke-marker.txt: Debug.Log lands in the editor log, not the NUnit XML, so the file is
    # the channel that survives the CLI. A green CLI is not trusted alone - the XML decides.
    'smoke'     {
        $results = Join-Path $project 'TestResults/Smoke.xml'
        $marker = Join-Path $project 'Logs/smoke-marker.txt'
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $results) | Out-Null
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $marker) | Out-Null
        if (Test-Path $marker) { Remove-Item -LiteralPath $marker -Force }
        $args = @('test', $project, '--mode', 'PlayMode', '--output', $results, '--report-format', 'nunit', '--filter', 'Mas3D.Tests.PlayMode.BridgeSmokeTests.OneTurnAgainstTheRealMockBridge')
        if ($Timeout -gt 0) { $args += @('--timeout', "$Timeout") }
        $bad = $false
        try { Invoke-Unity $args } catch { $bad = $true; Write-Host $_.Exception.Message }
        if (-not (Test-Path $results)) { $bad = $true }
        elseif (Select-String -LiteralPath $results -Pattern 'result="Failed"|failed="[1-9]' -Quiet) { $bad = $true }
        if (Test-Path $marker) {
            Get-Content -LiteralPath $marker -Encoding UTF8 | Where-Object { $_ -match '\[(smoke|ui)\]' } | ForEach-Object { Write-Host $_ }
        }
        else {
            Write-Host '[smoke] marker missing: the case did not reach its last line'
            $bad = $true
        }
        Write-Host "results: $results"
        if ($bad) { exit 1 }
    }
    'build'     {
        $outPath = if ([System.IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $repoRoot $Out }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outPath) | Out-Null
        $args = @('build', $project, '--target', $Target, '-o', $outPath, '--no-tail')
        if ($Timeout -gt 0) { $args += @('--timeout', "$Timeout") }
        Invoke-Unity $args
    }
    'run'       {
        $args = @('run', $project)
        if ($Timeout -gt 0) { $args += @('--timeout', "$Timeout") }
        $args += '--'
        $args += $Rest
        Invoke-Unity $args
    }
}