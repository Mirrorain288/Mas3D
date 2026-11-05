#Requires -Version 5.1
<#
.SYNOPSIS
  Mas3D full regression in one command: web, bridge, Unity EditMode, Unity PlayMode, smoke.

.DESCRIPTION
  Five gates, run in order. Every gate is judged by the child process exit code alone; the
  log text is written for humans to read afterwards, never grepped to decide a verdict. So a
  stale or cleverly worded log line cannot turn a red run green.

    web         pnpm.cmd -r test
    bridge      <python> -m pytest apps/bridge/tests -p no:cacheprovider --tb=no -q
    unity-edit  powershell -File tools/unity.ps1 test -Mode EditMode
    unity-play  powershell -File tools/unity.ps1 test -Mode PlayMode
    smoke       powershell -File tools/unity.ps1 smoke

  Output: one line per step, "<step> <PASS|FAIL|SKIP> exit=<n> <seconds>s", then one
  "summary: <passed>/<ran> ok" line. Exit code 0 when nothing failed, 1 when any step failed.
  SKIP is not a pass and is not counted in the summary.

  Unity runs one instance at a time. Do not start this while an Editor has the project open:
  the CLI then fails on the project lock, the Unity steps go FAIL, and no stale TestResults
  XML is read (the verdict never comes from that XML anyway).

.PARAMETER SkipWeb
  Skip the web (vitest) step.

.PARAMETER SkipBridge
  Skip the bridge (pytest) step.

.PARAMETER SkipUnity
  Skip the two Unity test steps. The smoke step has its own switch.

.PARAMETER SkipSmoke
  Skip the smoke step.

.PARAMETER Python
  Python interpreter with pytest installed. Lookup order: -Python, then $env:MAS3D_PYTHON,
  then python on PATH. A candidate must actually run; when none does, the bridge step SKIPs
  with "bridge SKIP (no python; set MAS3D_PYTHON)" and does not fail the run.

.PARAMETER Timeout
  Seconds handed to tools/unity.ps1 -Timeout for each Unity batch. 0 leaves it to the CLI.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/check.ps1

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/check.ps1 -SkipWeb -SkipBridge

.EXAMPLE
  $env:MAS3D_PYTHON = 'C:\Python312\python.exe'
  powershell -ExecutionPolicy Bypass -File tools/check.ps1 -SkipSmoke
#>
[CmdletBinding()]
param(
    [switch]$SkipWeb,
    [switch]$SkipBridge,
    [switch]$SkipUnity,
    [switch]$SkipSmoke,
    [string]$Python = '',
    [int]$Timeout = 0
)

# Native stderr (Unity CLI progress lines) must not become a terminating error under PS 5.1.
$ErrorActionPreference = 'Continue'

$repoRoot = Split-Path -Parent $PSScriptRoot
$logDir = Join-Path $repoRoot 'apps/unity/Logs'
$unity = Join-Path $PSScriptRoot 'unity.ps1'
$shell = Join-Path $PSHOME 'powershell.exe'
$comspec = if ($env:ComSpec) { $env:ComSpec } else { 'cmd.exe' }

New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$script:passed = 0
$script:ran = 0
$script:skips = @()
$script:fails = @()

function Quote {
    # Start-Process joins -ArgumentList with spaces and does not quote for us.
    param([string] $Text)
    if ($Text -match '\s') { return '"' + $Text + '"' }
    return $Text
}

function Invoke-Child {
    # Run one child process to completion and return its exit code. That is the whole verdict.
    param(
        [string] $File,
        [string[]] $ArgumentList = @(),
        [string] $Log = ''
    )
    $sp = @{
        FilePath         = $File
        WorkingDirectory = $repoRoot
        Wait             = $true
        PassThru         = $true
        NoNewWindow      = $true
    }
    if ($ArgumentList.Count -gt 0) { $sp['ArgumentList'] = $ArgumentList }
    if ($Log) {
        $sp['RedirectStandardOutput'] = $Log
        $sp['RedirectStandardError'] = [System.IO.Path]::ChangeExtension($Log, '.err.log')
    }
    try {
        $child = Start-Process @sp -ErrorAction Stop
    }
    catch {
        $message = 'launch failed: ' + $_.Exception.Message
        Write-Host ('  ' + $message)
        if ($Log) { try { Add-Content -LiteralPath $Log -Value $message -ErrorAction Stop } catch { } }
        return 1
    }
    if ($null -eq $child) { return 1 }
    # [int] $null is 0 in PowerShell, so a missing exit code would read as PASS. Never let it.
    if ($null -eq $child.ExitCode) { return 1 }
    return [int] $child.ExitCode
}

function Show-Step {
    param([string] $Name, [string] $State, [string] $Code, [double] $Seconds, [string[]] $Notes = @())
    Write-Host ('{0} {1} exit={2} {3:F1}s' -f $Name, $State, $Code, $Seconds)
    foreach ($note in $Notes) { if ($note) { Write-Host ('  ' + $note) } }
}

function Set-StepOutcome {
    param([string] $Name, [int] $Code, [double] $Seconds, [string] $Log = '', [string[]] $Extra = @())
    if ($Code -eq 0) {
        $script:passed++
        $script:ran++
        Show-Step -Name $Name -State 'PASS' -Code "$Code" -Seconds $Seconds
        return
    }
    $script:ran++
    $script:fails += $Name
    $notes = @($Extra)
    if ($Log) { $notes += ('log: ' + $Log) }
    $errLog = [System.IO.Path]::ChangeExtension($Log, '.err.log')
    if ($Log -and (Test-Path $errLog) -and (Get-Item $errLog).Length -gt 0) { $notes += ('err: ' + $errLog) }
    Show-Step -Name $Name -State 'FAIL' -Code "$Code" -Seconds $Seconds -Notes $notes
}

function Set-StepSkip {
    param([string] $Name, [string] $Note = '')
    $script:skips += $Name
    Show-Step -Name $Name -State 'SKIP' -Code '-' -Seconds 0 -Notes @($Note)
}

function Resolve-Python {
    # A name that resolves is not a usable interpreter: the Windows Store alias answers 9009
    # and prints nothing, so each candidate is asked to run before it is trusted.
    $candidates = @()
    if ($Python) { $candidates += $Python }
    if ($env:MAS3D_PYTHON) { $candidates += $env:MAS3D_PYTHON }
    $candidates += 'python'

    foreach ($name in $candidates) {
        $cmd = Get-Command $name -ErrorAction SilentlyContinue
        if (-not $cmd -or -not $cmd.Source) { continue }
        $code = 1
        try {
            & $cmd.Source -c 'import sys' *> $null
            if ($null -ne $LASTEXITCODE) { $code = $LASTEXITCODE }
        }
        catch {
            $code = 1
        }
        if ($code -eq 0) { return $cmd.Source }
    }
    return $null
}

function Invoke-UnityStep {
    param([string] $Name, [string] $LogName, [string[]] $Arguments)
    $log = Join-Path $logDir $LogName
    $cliArgs = @('-ExecutionPolicy', 'Bypass', '-File', (Quote $unity)) + $Arguments
    if ($Timeout -gt 0) { $cliArgs += @('-Timeout', [string] $Timeout) }
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $code = Invoke-Child -File $shell -ArgumentList $cliArgs -Log $log
    $watch.Stop()
    Set-StepOutcome -Name $Name -Code $code -Seconds $watch.Elapsed.TotalSeconds -Log $log
}

# --- 1. web -----------------------------------------------------------------
if ($SkipWeb) {
    Set-StepSkip 'web'
}
else {
    $pnpm = Get-Command pnpm.cmd -ErrorAction SilentlyContinue
    if (-not $pnpm) {
        # web is a mandatory gate: a missing pnpm.cmd must not turn into a silent pass.
        Set-StepOutcome -Name 'web' -Code 1 -Seconds 0 -Extra @('web FAIL (pnpm.cmd not on PATH)')
    }
    else {
        $log = Join-Path $logDir 'check-web.log'
        # cmd /c: Start-Process cannot redirect the streams of a .cmd file by itself, and
        # pnpm.ps1 is blocked by the execution policy on this machine.
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $code = Invoke-Child -File $comspec -ArgumentList @('/c', 'pnpm.cmd -r test') -Log $log
        $watch.Stop()
        Set-StepOutcome -Name 'web' -Code $code -Seconds $watch.Elapsed.TotalSeconds -Log $log
    }
}

# --- 2. bridge --------------------------------------------------------------
if ($SkipBridge) {
    Set-StepSkip 'bridge'
}
else {
    $py = Resolve-Python
    if (-not $py) {
        Set-StepSkip 'bridge' 'bridge SKIP (no python; set MAS3D_PYTHON)'
    }
    else {
        $log = Join-Path $logDir 'check-bridge.log'
        $env:PYTHONPATH = Join-Path $env:TEMP 'mas3d_pytest_lib'
        $env:PYTHONIOENCODING = 'utf-8'
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $code = Invoke-Child -File $py -ArgumentList @(
            '-m', 'pytest', 'apps/bridge/tests', '-p', 'no:cacheprovider', '--tb=no', '-q'
        ) -Log $log
        $watch.Stop()
        Set-StepOutcome -Name 'bridge' -Code $code -Seconds $watch.Elapsed.TotalSeconds -Log $log
    }
}

# --- 3. unity-edit / 4. unity-play -----------------------------------------
if ($SkipUnity) {
    Set-StepSkip 'unity-edit'
    Set-StepSkip 'unity-play'
}
else {
    Invoke-UnityStep -Name 'unity-edit' -LogName 'check-edit.log' -Arguments @('test', '-Mode', 'EditMode')
    Invoke-UnityStep -Name 'unity-play' -LogName 'check-play.log' -Arguments @('test', '-Mode', 'PlayMode')
}

# --- 5. smoke ---------------------------------------------------------------
if ($SkipSmoke) {
    Set-StepSkip 'smoke'
}
else {
    Invoke-UnityStep -Name 'smoke' -LogName 'check-smoke.log' -Arguments @('smoke')
}

if ($script:fails.Count -gt 0) {
    Write-Host ('failed: ' + ($script:fails -join ', '))
}
if ($script:skips.Count -gt 0) {
    Write-Host ('note: skipped ' + ($script:skips -join ', ') + ' (not counted as passes)')
}
if ($script:skips.Count -gt 0) {
    Write-Host ('summary: {0}/{1} ok ({2} skipped)' -f $passed, $ran, $script:skips.Count)
}
else {
    Write-Host ('summary: {0}/{1} ok' -f $passed, $ran)
}
if ($script:fails.Count -gt 0) { exit 1 }
exit 0
