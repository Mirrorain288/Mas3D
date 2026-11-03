#requires -Version 5.1
<#
.SYNOPSIS
    Pushes to GitHub even when the github.com edge this network resolves to is
    being reset.

.DESCRIPTION
    DNS here can return a github.com edge IP that the network drops, while the
    other GitHub edges stay reachable. This script probes the direct path first;
    when that fails it starts a tiny local CONNECT tunnel pinned to an edge that
    passed a real TLS + SNI check, and runs git through that proxy. The tunnel is
    stopped and the temp script removed on the way out, and nothing is written to
    the global git config.

    Only git's smart HTTP transport is routed; keep LFS and release downloads on
    the direct path.

.PARAMETER Remote
    Remote name or URL. Defaults to origin.

.PARAMETER Branch
    Branch to push. Defaults to main.

.PARAMETER Port
    Local port for the tunnel. Defaults to 8899.

.PARAMETER GitArgs
    Extra arguments appended to the git push.

.PARAMETER Probe
    Only report which endpoints answer; push nothing.

.PARAMETER Direct
    Skip the tunnel entirely and push straight.

.PARAMETER ForceTunnel
    Skip the direct attempt and always use the tunnel.

.EXAMPLE
    tools\gh-push.ps1

.EXAMPLE
    tools\gh-push.ps1 -Branch dev -GitArgs --tags

.EXAMPLE
    tools\gh-push.ps1 -Probe
#>
[CmdletBinding()]
param(
    [string]$Remote = 'origin',
    [string]$Branch = 'main',
    [int]$Port = 8899,
    [string[]]$GitArgs = @(),
    [switch]$Probe,
    [switch]$Direct,
    [switch]$ForceTunnel
)

$ErrorActionPreference = 'Continue'
Set-StrictMode -Off

# Candidate GitHub edges. Only the one this network resolves to has been seen
# dropping traffic; the rest are the usual anycast set and are checked for real
# before use.
$Candidates = @(
    '140.82.112.3',
    '140.82.113.3',
    '140.82.114.3',
    '140.82.116.3',
    '20.201.28.151',
    '20.27.177.113',
    '4.237.22.34'
)

function Write-Log([string]$Text) { Write-Host "[gh-push] $Text" }

function Show-Sync {
    $local = (& git rev-parse HEAD) -join ''
    Write-Log ("local  HEAD = " + $local)
    if ($Remote -match '://') { return }
    & git rev-parse --verify --quiet ($Remote + '/' + $Branch) *> $null
    if ($LASTEXITCODE -ne 0) { return }
    $tracked = (& git rev-parse ($Remote + '/' + $Branch)) -join ''
    Write-Log ("remote ref   = " + $tracked)
    if ($local -and $tracked -and ($local -eq $tracked)) { Write-Log 'in sync' }
}

function Test-Tcp([string]$TargetHost, [int]$TargetPort, [int]$TimeoutMs = 3000) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        return $client.ConnectAsync($TargetHost, $TargetPort).Wait($TimeoutMs)
    } catch {
        return $false
    } finally {
        $client.Close()
    }
}

# A TCP handshake is not enough: the TLS + SNI exchange has to succeed, so use
# curl when it is there and fall back to a bare connect otherwise.
function Test-GithubEdge([string]$Edge) {
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if (-not $curl) {
        return (Test-Tcp $Edge 443 4000)
    }
    $code = (& curl.exe -sS -m 12 -o NUL -w '%{http_code}' --resolve "github.com:443:$Edge" https://github.com/ 2>$null) -join ''
    return ($code.Trim() -eq '200')
}

function Quote-Arg([string]$Value) {
    if ($Value -match '[\s"]') { return '"' + ($Value -replace '"', '\"') + '"' }
    return $Value
}

# git reports progress on stderr, and PowerShell 5.1 turns those lines into
# NativeCommandError as soon as they pass through a redirect or a pipeline. So
# git runs detached with its streams redirected to files and the text is
# replayed as plain output afterwards.
$GitOutPath = Join-Path $env:TEMP 'mas3d-gh-push.out.log'
$GitErrPath = Join-Path $env:TEMP 'mas3d-gh-push.err.log'
function Invoke-Git([string[]]$GitArgList) {
    $argumentLine = ($GitArgList | ForEach-Object { Quote-Arg $_ }) -join ' '
    $proc = Start-Process -FilePath 'git.exe' -ArgumentList $argumentLine -NoNewWindow -Wait -PassThru -RedirectStandardOutput $GitOutPath -RedirectStandardError $GitErrPath
    foreach ($path in @($GitOutPath, $GitErrPath)) {
        if (Test-Path $path) { Get-Content -Path $path | ForEach-Object { Write-Host $_ } }
    }
    return $proc.ExitCode
}

$node = Get-Command node -ErrorAction SilentlyContinue
if (-not $node) { throw 'node.exe not found on PATH' }
$nodePath = $node.Source

$directOk = Test-Tcp 'github.com' 443 4000
Write-Log ("direct github.com:443 reachable=" + $directOk)

function Get-GoodEdges {
    $found = @()
    foreach ($edge in $Candidates) {
        $ok = Test-GithubEdge $edge
        Write-Log ("edge " + $edge + " ok=" + $ok)
        if ($ok) { $found += $edge }
    }
    return ,$found
}

if ($Probe) {
    $all = Get-GoodEdges
    Write-Log ("probe done: direct=" + $directOk + " usable edges=" + ($all -join ', '))
    exit 0
}

if ($Direct) {
    Write-Log 'pushing directly'
    $code = Invoke-Git (@('push', $Remote, $Branch) + $GitArgs)
    if ($code -eq 0) { Show-Sync }
    exit $code
}

if ($directOk -and -not $ForceTunnel) {
    Write-Log 'trying a direct push first'
    $code = Invoke-Git (@('push', $Remote, $Branch) + $GitArgs)
    if ($code -eq 0) {
        Write-Log 'direct push worked'
        Show-Sync
        exit 0
    }
    Write-Log ("direct push failed with exit " + $code + ", falling back to the tunnel")
}

# The edge scan costs a TLS round trip per candidate, so it only runs once the
# direct path has actually failed.
$good = Get-GoodEdges
if ($good.Count -eq 0) {
    Write-Log 'no reachable GitHub edge found'
    exit 2
}

$tunnelJs = @'
import http from 'node:http';
import net from 'node:net';

// Pin every CONNECT to a GitHub edge that answered a real TLS + SNI request,
// and let the client keep its own SNI and Host header.
const edge = process.argv[2];
const listenPort = Number(process.argv[3]);
const log = (m) => console.log('[tunnel] ' + m);

const server = http.createServer((req, res) => { res.writeHead(405); res.end(); });
server.on('clientError', (_e, socket) => { try { socket.destroy(); } catch (_) {} });

server.on('connect', (req, client, head) => {
  const target = req.url || 'github.com:443';
  log('connect ' + target);
  client.on('error', (e) => log('client error ' + e.code));
  const upstream = net.connect(443, edge, () => {
    if (client.destroyed) { upstream.destroy(); return; }
    client.write('HTTP/1.1 200 Connection Established\r\n\r\n');
    if (head && head.length) upstream.write(head);
    client.pipe(upstream);
    upstream.pipe(client);
  });
  upstream.on('error', (e) => { log('upstream error ' + e.code); client.destroy(); });
  client.on('close', () => upstream.destroy());
});

server.on('error', (e) => log('server error ' + e.message));
server.listen(listenPort, '127.0.0.1', () => log('listening ' + listenPort + ' -> ' + edge + ':443'));
'@

$tunnelPath = Join-Path $env:TEMP 'mas3d-gh-tunnel.mjs'
Set-Content -Path $tunnelPath -Value $tunnelJs -Encoding ASCII
$proxy = 'http://127.0.0.1:' + $Port
$gitWithProxy = @('-c', ('http.proxy=' + $proxy), '-c', 'http.lowSpeedLimit=1000', '-c', 'http.lowSpeedTime=30')

$exit = 3
$proc = $null
try {
    foreach ($edge in $good) {
        Write-Log ("starting tunnel on " + $Port + " via " + $edge)
        $proc = Start-Process -FilePath $nodePath -ArgumentList @($tunnelPath, $edge, "$Port") -PassThru -WindowStyle Hidden

        $ready = $false
        for ($i = 0; $i -lt 40; $i++) {
            if (Test-Tcp '127.0.0.1' $Port 300) { $ready = $true; break }
            Start-Sleep -Milliseconds 150
        }
        if (-not $ready) {
            Write-Log 'tunnel did not come up'
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            $proc = $null
            continue
        }

        Write-Log 'checking the remote through the tunnel'
        $lsExit = Invoke-Git ($gitWithProxy + @('ls-remote', '--heads', $Remote, $Branch))
        if ($lsExit -ne 0) {
            Write-Log ("remote check failed with exit " + $lsExit + ", trying the next edge")
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            $proc = $null
            continue
        }

        Write-Log 'pushing through the tunnel'
        $exit = Invoke-Git ($gitWithProxy + @('push', $Remote, $Branch) + $GitArgs)
        if ($exit -eq 0) {
            Write-Log 'push succeeded'
        } else {
            Write-Log ("push failed with exit " + $exit)
        }
        break
    }
} finally {
    if ($proc -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -Path $tunnelPath -Force -ErrorAction SilentlyContinue
}

if ($exit -eq 0) { Show-Sync }

exit $exit
