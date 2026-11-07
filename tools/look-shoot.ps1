# One-command visual acceptance run.
#
#   powershell -ExecutionPolicy Bypass -File tools\look-shoot.ps1
#   powershell -ExecutionPolicy Bypass -File tools\look-shoot.ps1 -Date 2026-11-06 -SkipBuild
#
# 1. rebuilds the playable scene (writes the material/light numbers into Main.unity)
# 2. renders the five delivery shots plus the shadow-off references, including a room-only
#    pair with the player body hidden (-hideplayer 1) (GPU required)
# 3. copies everything into .superpowers/look/<date>/ and runs both checks there
#
# Exit code is the number of failed checks (0 means the look is acceptable).
# Only ASCII in this file: Windows PowerShell 5.1 mis-parses UTF-8 without a BOM.

param(
    [string] $Date = (Get-Date -Format 'yyyy-MM-dd'),
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Continue'
$repo = Split-Path -Parent $PSScriptRoot
$out = Join-Path $repo ".superpowers/look/$Date"
$shots = Join-Path $repo 'apps/unity/Shots'
$ref = Join-Path $repo 'docs/superpowers/specs/assets/2026-11-06-reference.png'

New-Item -ItemType Directory -Force -Path $out | Out-Null

if (-not $SkipBuild) {
    Write-Host '[look] rebuilding the playable scene'
    & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'unity.ps1') player | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host "[look] player exited $LASTEXITCODE"; exit 2 }

    Write-Host '[look] rendering shots (shadow pair and room-only pair included)'
    & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'unity.ps1') capture -noShadows 1 -hideplayer 1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host "[look] capture exited $LASTEXITCODE"; exit 2 }
}

$wanted = @('play-01.png', 'play-02.png', 'play-03.png', 'play-04.png',
            'no-shadow-01.png', 'no-shadow-03.png',
            'room-shadow-01.png', 'room-noshadow-01.png',
            'avatar-seated.png', 'face.png', 'eyes.png', 'ui-scene.png',
            'foot-01.png', 'foot-02.png', 'foot-03.png', 'foot-04.png',
            'foot-05.png', 'foot-06.png', 'foot-07.png', 'foot-08.png')
foreach ($name in $wanted) {
    $source = Join-Path $shots $name
    if (Test-Path $source) { Copy-Item $source (Join-Path $out $name) -Force }
    else { Write-Host "[look] missing $name" }
}

if (-not (Test-Path $ref)) { Write-Host "[look] reference portrait missing: $ref"; exit 2 }

node (Join-Path $PSScriptRoot 'look-compare.mjs') --mine $out --ref $ref --report (Join-Path $out 'metrics.json')
$failed = 0
if ($LASTEXITCODE -ne 0) { $failed++ }

# Pair 1: play-01, player body and furniture together, the original 3% rule from R17.
node (Join-Path $PSScriptRoot 'shadow-diff.mjs') (Join-Path $out 'play-01.png') (Join-Path $out 'no-shadow-01.png')
if ($LASTEXITCODE -ne 0) { $failed++ }

# Pair 2: player body hidden, room only. This is the "furniture leaves its own shadow" rule:
# 2% of the lower half may be enough, but one patch must darken by 25% or more, otherwise the
# floor is just uniformly dimmed.
node (Join-Path $PSScriptRoot 'shadow-diff.mjs') (Join-Path $out 'room-shadow-01.png') (Join-Path $out 'room-noshadow-01.png') --min-lower 0.02 --min-peak 0.25
if ($LASTEXITCODE -ne 0) { $failed++ }

Write-Host ("[look] results in {0} (failed checks: {1})" -f $out, $failed)
exit $failed
