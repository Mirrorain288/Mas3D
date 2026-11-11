#Requires -Version 5.1
# One-shot spike pipeline: Kimodo text -> NPZ -> Unity readable JSON -> rendered frames.
param(
    [string]$Prompt = 'A young woman sits on the edge of a bed, holding someone hands in front of her, gently swaying their joined hands, relaxed shoulders.',
    [double]$Duration = 4.0,
    [int]$Steps = 60,
    [int]$Seed = 7,
    [string]$Out = 'hands',
    [string]$Times = '0.4,1.2,2.0,2.8,3.6',
    [string]$Shot = 'kin',
    [switch]$SkipGen
)
$ErrorActionPreference = 'Continue'
$root = 'D:/Mas3D'
$venv = Join-Path $root '.superpowers/kimodo-venv/Scripts'
$py = Join-Path $venv 'python.exe'
$work = Join-Path $root '.superpowers/kimodo-out'
New-Item -ItemType Directory -Force -Path $work | Out-Null

$env:HTTP_PROXY = 'http://127.0.0.1:7897'
$env:HTTPS_PROXY = 'http://127.0.0.1:7897'
$env:HF_ENDPOINT = 'https://hf-mirror.com'
$env:HF_HUB_DISABLE_XET = '1'
$env:TEXT_ENCODERS_DIR = (Join-Path $root '.superpowers/text-encoders')
$env:TEXT_ENCODER_MODE = 'local'
$env:TEXT_ENCODER_DEVICE = 'cpu'
$env:LOCAL_CACHE = 'true'

if (-not $SkipGen) {
    Push-Location $work
    & (Join-Path $venv 'kimodo_gen.exe') $Prompt --duration $Duration --diffusion_steps $Steps --seed $Seed --output $Out --bvh *>&1 | Tee-Object -FilePath (Join-Path $work "$Out.gen.log")
    Pop-Location
}

$npz = Get-ChildItem (Join-Path $work "$Out*.npz") -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $npz) { 'NO NPZ'; exit 1 }
"npz: $($npz.FullName) $([math]::Round($npz.Length/1KB,0)) KB"

& $py (Join-Path $root 'tools/kimodo-to-unity-motion.py') $npz.FullName (Join-Path $root 'apps/unity/Assets/_Local/motion/hands.json')
if ($LASTEXITCODE -ne 0) { 'CONVERT FAILED'; exit 1 }

Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
& (Join-Path $root 'tools/unity.ps1') run -- -executeMethod Mas3D.Editor.CliMotion.Capture -times $Times -shot $Shot -hold 1 -pov 1 -fov 42 -logFile (Join-Path $root 'apps/unity/Logs/cli-kin.log')
Get-ChildItem (Join-Path $root ("apps/unity/Shots/" + $Shot + "-*.png")) -ErrorAction SilentlyContinue | Select-Object Name,Length | Format-Table -AutoSize
