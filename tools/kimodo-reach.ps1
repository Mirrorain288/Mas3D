#Requires -Version 5.1
# Generate several Kimodo clips for "sit and reach a hand up" and convert them to Unity motion JSON.
param(
    [int[]]$Seeds = @(7,21,42),
    [string]$Prompt = 'A woman sits on a bed with her back upright, head lifted, looking up at a person standing in front of her, she raises her right hand forward at chest height with an open palm to hold hands.',
    [double]$Duration = 4.0,
    [int]$Steps = 60
)
$ErrorActionPreference = 'Continue'
$root = 'D:/Mas3D'
$venv = Join-Path $root '.superpowers/kimodo-venv/Scripts'
$py = Join-Path $venv 'python.exe'
$work = Join-Path $root '.superpowers/kimodo-out'
$motion = Join-Path $root 'apps/unity/Assets/_Local/motion'
New-Item -ItemType Directory -Force -Path $work | Out-Null

$env:HTTP_PROXY = 'http://127.0.0.1:7897'
$env:HTTPS_PROXY = 'http://127.0.0.1:7897'
$env:HF_ENDPOINT = 'https://hf-mirror.com'
$env:HF_HUB_DISABLE_XET = '1'
$env:TEXT_ENCODERS_DIR = (Join-Path $root '.superpowers/text-encoders')
$env:TEXT_ENCODER_MODE = 'local'
$env:TEXT_ENCODER_DEVICE = 'cpu'
$env:LOCAL_CACHE = 'true'

Push-Location $work
foreach ($s in $Seeds) {
    $name = "reach$s"
    if (Test-Path (Join-Path $work "$name.npz")) { "skip gen $name"; continue }
    "=== gen $name seed=$s"
    & (Join-Path $venv 'kimodo_gen.exe') $Prompt --duration $Duration --diffusion_steps $Steps --seed $s --output $name --bvh --no-postprocess *>&1 |
        Tee-Object -FilePath (Join-Path $work "$name.gen.log") | Select-Object -Last 6
}
Pop-Location

foreach ($s in $Seeds) {
    $name = "reach$s"
    $npz = Get-ChildItem (Join-Path $work "$name*.npz") -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $npz) { "NO NPZ $name"; continue }
    "npz $($npz.Name) $([math]::Round($npz.Length/1KB,0)) KB"
    & $py (Join-Path $root 'tools/kimodo-to-unity-motion.py') $npz.FullName (Join-Path $motion "$name.json")
    "convert exit=$LASTEXITCODE"
}
Get-ChildItem (Join-Path $motion '*.json') | Select-Object Name,Length | Format-Table -AutoSize
