<#
    Renders the room in Unity from the same four viewpoints the web build uses.
    Pure ASCII on purpose: Windows PowerShell 5.1 mis-parses UTF-8 scripts without a BOM.

    Usage:
      Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
      & D:\Mas3D\tools\room-shots.ps1            # all four
      & D:\Mas3D\tools\room-shots.ps1 -Only front
#>
param(
    [string]$Only = '',
    [string]$Suffix = ''
)

$ErrorActionPreference = 'Continue'
$unity = 'D:\Mas3D\tools\unity.ps1'
$shots = @(
    @{ name = 'front';   cam = '-2.6,1.5,0';    look = '1.5,1.1,0';    fov = 46 },
    @{ name = 'left45';  cam = '-0.6,1.5,-2.4'; look = '1.8,1.15,1.2';  fov = 50 },
    @{ name = 'right45'; cam = '0.6,1.5,2.4';   look = '1.6,1.15,-1.2'; fov = 50 },
    @{ name = 'wide';    cam = '-3.1,1.6,0.2';  look = '1.4,1.05,0.1';  fov = 62 }
)

foreach ($s in $shots) {
    if ($Only -ne '' -and $s.name -ne $Only) { continue }
    $shot = 'room-' + $s.name + $Suffix
    Write-Host ('[shots] ' + $shot)
    & $unity run -- -executeMethod Mas3D.Editor.CliCapture.Capture -rebuild 1 -dress 1 `
        -shot $shot -cam $s.cam -look $s.look -fov $s.fov
    if ($LASTEXITCODE -ne 0) { Write-Host ('[shots] ' + $shot + ' failed: ' + $LASTEXITCODE) }
}
