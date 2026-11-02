# Renders a player's-eye shot from every anchor and seat slot in Anchors/anchors.json.
param(
    [string]$Project = 'D:\Mas3D\apps\unity',
    [string]$OutDir = 'D:\Mas3D\apps\unity\Shots'
)
$ErrorActionPreference = 'Continue'
$data = Get-Content -Raw (Join-Path $Project 'Anchors\anchors.json') | ConvertFrom-Json
$centreX = ($data.interior.x0 + $data.interior.x1) / 2
$centreZ = ($data.interior.z0 + $data.interior.z1) / 2

function Invoke-Anchors([string]$camX, [string]$camY, [string]$camZ, [string]$lookY, [string]$shot) {
    $cam = "$camX,$camY,$camZ"
    $look = "$centreX,$lookY,$centreZ"
    Write-Host "[shot] $shot cam=$cam look=$look"
    & D:\Mas3D\tools\unity.ps1 run -- -executeMethod Mas3D.Editor.CliCapture.Capture -rebuild 1 -dress 1 -shot $shot -cam $cam -look $look -fov 62 | Out-Null
}

foreach ($a in $data.anchors) {
    $x = [string]::Format('{0:0.00}', $a.x)
    $z = [string]::Format('{0:0.00}', $a.z)
    Invoke-Anchors $x '1.60' $z '1.05' ("anchor-" + $a.id.ToLower())
}
foreach ($s in $data.seats) {
    $x = [string]::Format('{0:0.00}', $s.x)
    $z = [string]::Format('{0:0.00}', $s.z)
    $eye = [string]::Format('{0:0.00}', ($s.y + 0.72))
    $look = [string]::Format('{0:0.00}', ($s.y + 0.35))
    Invoke-Anchors $x $eye $z $look ("seat-" + $s.id.ToLower())
}
Write-Host '[shot] done'
