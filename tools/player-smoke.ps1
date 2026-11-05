#Requires -Version 5.1
<#
.SYNOPSIS
  Player self-test gate: launch the built Windows player with -selftest and judge the marker it writes.

.DESCRIPTION
  Two modes.

    online (default)  The script picks a free port from 8791 upward, starts its own
                      tools/mock-bridge.mjs on it, runs the player against that port and
                      expects exit code 0 plus five marker lines: token src, bridge ok,
                      reply ok, action hands held, done.

    -Offline          No bridge at all. The player must wait 45 s, write
                      "[selftest] bridge timeout" and exit 3. This proves the failure
                      path really runs, not just the happy one.

  Port 8790 is never touched: on this machine it belongs to the real AstrBot. The mock
  the script starts is the only process it ever kills, and it kills it by PID.

  Verdicts: 0 PASS, 1 FAIL, 2 SKIP (no player exe, or no node in online mode).

.PARAMETER Exe
  Player executable. Default: build/Mas3D.exe under the repo root.

.PARAMETER Timeout
  Seconds to wait for the player to exit before it is killed. Default 150: the offline
  path alone spends 45 s waiting for a bridge that never answers.

.PARAMETER Offline
  Do not start a mock bridge; expect exit code 3 and "[selftest] bridge timeout".

.PARAMETER Reply
  Text the mock bridge answers with (passed to tools/mock-bridge.mjs --reply). Empty keeps
  the scenario's own copy. Used to prove a short reply (two CJK chars) still counts as answered.

.PARAMETER WithToken
  Pass -token to the player instead of letting it find the token on disk. Only for
  checking that the -token override still wins; the default path is the one that has to
  work for a double-click, so it is the one the gate exercises.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/player-smoke.ps1

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/player-smoke.ps1 -Offline -Timeout 90
#>
[CmdletBinding()]
param(
    [string]$Exe = '',
    [int]$Timeout = 150,
    [switch]$Offline,
    [switch]$WithToken,
    [string]$Reply = ''
)

# Native stderr must not become a terminating error under PS 5.1.
$ErrorActionPreference = 'Continue'

$repoRoot = Split-Path -Parent $PSScriptRoot
$logDir = Join-Path $repoRoot 'apps/unity/Logs'
if (-not $Exe) { $Exe = Join-Path $repoRoot 'build/Mas3D.exe' }

# Self-test artifacts. Kept under Logs because the report has to quote them.
$marker = Join-Path $logDir 'player-marker.log'
$playerLog = Join-Path $logDir 'player-run.log'
$mockLog = Join-Path $logDir 'player-mock.log'
$mockErr = Join-Path $logDir 'player-mock.err.log'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

function Quote {
    # Start-Process joins -ArgumentList with spaces and does not quote for us.
    param([string] $Text)
    if ($Text -match '\s') { return '"' + $Text + '"' }
    return $Text
}

function Find-FreePort {
    # Bind and release, so a port held by anything else (8790 included) is never chosen.
    param([int] $Start, [int] $Tries = 40)
    for ($port = $Start; $port -lt ($Start + $Tries); $port++) {
        $listener = $null
        try {
            $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $port)
            $listener.Start()
            $listener.Stop()
            return $port
        }
        catch {
            if ($null -ne $listener) { try { $listener.Stop() } catch { } }
        }
    }
    return 0
}

function Test-PortOpen {
    param([int] $Port, [int] $Milliseconds = 200)
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $task = $client.ConnectAsync([System.Net.IPAddress]::Loopback, $Port)
        if (-not $task.Wait($Milliseconds)) { return $false }
        return $client.Connected
    }
    catch { return $false }
    finally { $client.Dispose() }
}

function Test-MarkerLine {
    param([string[]] $Lines, [string] $Pattern)
    foreach ($line in $Lines) { if ($line -match $Pattern) { return $true } }
    return $false
}

if (-not (Test-Path -LiteralPath $Exe)) {
    Write-Host 'player SKIP (no build/Mas3D.exe)'
    exit 2
}

# A leftover marker from an earlier run would be read as this run's evidence.
if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker -Force -ErrorAction SilentlyContinue }
if (Test-Path -LiteralPath $mockLog) { Remove-Item -LiteralPath $mockLog -Force -ErrorAction SilentlyContinue }
if (Test-Path -LiteralPath $mockErr) { Remove-Item -LiteralPath $mockErr -Force -ErrorAction SilentlyContinue }

$mode = if ($Offline) { 'offline' } else { 'online' }
$node = $null
$mock = $null
$player = $null
$port = 0
$killed = $false
$code = -1
$watch = [System.Diagnostics.Stopwatch]::StartNew()

try {
    if (-not $Offline) {
        $nodeCmd = Get-Command node -ErrorAction SilentlyContinue
        if (-not $nodeCmd -or -not $nodeCmd.Source) {
            Write-Host 'player SKIP (no node; online mode needs tools/mock-bridge.mjs)'
            exit 2
        }
        $node = $nodeCmd.Source

        $port = Find-FreePort -Start 8791
        if ($port -eq 0) {
            Write-Host 'player FAIL (no free port in 8791..8830)'
            exit 1
        }

        # Own mock only. No --token: the server then accepts the token the player sends.
        $mockArgs = @('tools/mock-bridge.mjs', '--port', [string] $port, '--scenario', 'happy')
        if ($Reply) { $mockArgs += @('--reply', $Reply) }
        $mock = Start-Process -FilePath $node -ArgumentList $mockArgs -WorkingDirectory $repoRoot -PassThru -NoNewWindow -RedirectStandardOutput $mockLog -RedirectStandardError $mockErr

        $ready = $false
        $waitMock = [System.Diagnostics.Stopwatch]::StartNew()
        while ($waitMock.Elapsed.TotalSeconds -lt 15) {
            if (Test-PortOpen -Port $port) { $ready = $true; break }
            if ($mock.HasExited) { break }
            Start-Sleep -Milliseconds 100
        }
        if (-not $ready) {
            Write-Host ('player FAIL (mock bridge did not listen on port {0})' -f $port)
            if (Test-Path -LiteralPath $mockErr) {
                Get-Content -LiteralPath $mockErr -Encoding UTF8 | ForEach-Object { Write-Host ('  ' + $_) }
            }
            exit 1
        }
    }
    else {
        # Offline still needs a port nothing listens on, so the player's retry loop really fails.
        $port = Find-FreePort -Start 8791
        if ($port -eq 0) {
            Write-Host 'player FAIL (no free port in 8791..8830)'
            exit 1
        }
    }

    # No -token by default: the packaged player has to find the token on disk by itself, which
    # is exactly what a double-click does. -WithToken keeps one run for the override path; the
    # placeholder there is fixed so no secret is copied into a log.
    $playerArgs = @(
        '-batchmode', '-nographics',
        '-logFile', (Quote $playerLog),
        '-bridge', ('127.0.0.1:' + $port),
        '-selftest', (Quote $marker)
    )
    if ($WithToken) { $playerArgs += @('-token', 'selftest-mock-token') }

    $watch.Restart()
    # Start-Process -PassThru hands back a Process whose ExitCode reads as $null in this shell
    # (control test: "cmd /c exit 7" also read back empty, and [int]$null is 0), which would
    # silently turn every real code into 0. Process.Start with UseShellExecute=$false keeps the
    # handle, so ExitCode is the code the player really exited with.
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $Exe
    $startInfo.Arguments = ($playerArgs -join ' ')
    $startInfo.WorkingDirectory = $repoRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    try {
        $player = [System.Diagnostics.Process]::Start($startInfo)
    }
    catch {
        # No conclusion line otherwise: the script would die mid-run and the reader sees nothing.
        Write-Host ('player FAIL (launch: ' + $_.Exception.Message + ')')
        exit 1
    }

    while (-not $player.HasExited -and $watch.Elapsed.TotalSeconds -lt $Timeout) {
        Start-Sleep -Milliseconds 100
        $player.Refresh()
    }
    if ($player.HasExited) {
        try { $code = [int] $player.ExitCode } catch { $code = -1 }
    }
    else {
        $killed = $true
        try { $player.Kill() } catch { }
        try { $null = $player.WaitForExit(5000) } catch { }
    }
}
finally {
    if ($null -ne $mock -and -not $mock.HasExited) { try { $mock.Kill() } catch { } }
    if ($null -ne $player -and -not $player.HasExited) { try { $player.Kill() } catch { } }
}

$watch.Stop()

$lines = @()
if (Test-Path -LiteralPath $marker) { $lines = @(Get-Content -LiteralPath $marker -Encoding UTF8) }

# What this mode promises. Patterns are regexes matched against whole marker lines.
if ($Offline) {
    $wantCode = 3
    $wants = @(
        @{ Label = 'bridge timeout'; Pattern = '\[selftest\] bridge timeout' }
    )
}
else {
    $wantCode = 0
    $tokenPattern = if ($WithToken) {
        '\[selftest\] token src=-token len=[0-9]+'
    }
    else {
        '\[selftest\] token src=(?!none\b)\S+ len=64'
    }
    $wants = @(
        @{ Label = 'token src='; Pattern = $tokenPattern },
        @{ Label = 'bridge ok'; Pattern = '\[selftest\] bridge ok' },
        @{ Label = 'reply ok len='; Pattern = '\[selftest\] reply ok len=[1-9]' },
        @{ Label = 'action hands held='; Pattern = '\[selftest\] action hands held=[0-9]+\.[0-9]+ ok' },
        @{ Label = 'done'; Pattern = '\[selftest\] done' }
    )
}

$missing = @()
foreach ($want in $wants) {
    if (-not (Test-MarkerLine -Lines $lines -Pattern $want.Pattern)) { $missing += $want.Label }
}

$got = if ($killed) { 'killed after ' + [string] $Timeout + 's' } else { [string] $code }
$ok = ($missing.Count -eq 0 -and -not $killed -and $code -eq $wantCode)

Write-Host ('player {0} ({1}) exit={2} {3:F1}s' -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $mode, $got, $watch.Elapsed.TotalSeconds)

if ($missing.Count -gt 0) {
    Write-Host ('  marker missing lines: ' + ($missing -join ', '))
}
if (-not $killed -and $code -ne $wantCode) {
    Write-Host ('  expected exit={0}, got {1}' -f $wantCode, $code)
}
Write-Host ('  marker: ' + $(if (Test-Path -LiteralPath $marker) { $marker } else { $marker + ' (absent)' }))
if (Test-Path -LiteralPath $playerLog) { Write-Host ('  player log: ' + $playerLog) }
if ($null -ne $mockLog -and (Test-Path -LiteralPath $mockLog)) { Write-Host ('  mock log: ' + $mockLog) }
foreach ($line in $lines) { Write-Host ('  ' + $line) }

if ($ok) { exit 0 }
exit 1
