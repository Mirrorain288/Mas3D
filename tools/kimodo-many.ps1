#Requires -Version 5.1
# One model load, N samples of "sit and reach a hand up", each converted to a Unity motion JSON.
param(
    [int]$Samples = 6,
    [int]$Seed = 7,
    [string]$Prompt = 'A woman sits upright on the edge of a bed, she extends her right arm forward toward a standing person, palm up, offering her hand, chin lifted, looking at the face of the person in front of her.',
    [double]$Duration = 4.0,
    [int]$Steps = 60,
    [string]$Out = 'many'
)
$ErrorActionPreference = 'Continue'
$root = 'D:/Mas3D'
$venv = Join-Path $root '.superpowers/kimodo-venv/Scripts'
$py = Join-Path $venv 'python.exe'
$work = Join-Path $root '.superpowers/kimodo-out'
$motion = Join-Path $root 'apps/unity/Assets/_Local/motion'

$env:HTTP_PROXY = 'http://127.0.0.1:7897'
$env:HTTPS_PROXY = 'http://127.0.0.1:7897'
$env:HF_ENDPOINT = 'https://hf-mirror.com'
$env:HF_HUB_DISABLE_XET = '1'
$env:TEXT_ENCODERS_DIR = (Join-Path $root '.superpowers/text-encoders')
$env:TEXT_ENCODER_MODE = 'local'
$env:TEXT_ENCODER_DEVICE = 'cpu'
$env:LOCAL_CACHE = 'true'

Push-Location $work
& (Join-Path $venv 'kimodo_gen.exe') $Prompt --duration $Duration --diffusion_steps $Steps --seed $Seed --num_samples $Samples --output $Out --bvh --no-postprocess *>&1 |
    Tee-Object -FilePath (Join-Path $work "$Out.gen.log") | Select-Object -Last 4
Pop-Location

Get-ChildItem (Join-Path $work "$Out*") -Recurse -Filter *.npz | ForEach-Object {
    $stem = $_.BaseName
    & $py (Join-Path $root 'tools/kimodo-to-unity-motion.py') $_.FullName (Join-Path $motion "$stem.json")
    "convert $stem exit=$LASTEXITCODE"
}
Get-ChildItem (Join-Path $motion "$Out*.json") | Select-Object Name,Length | Format-Table -AutoSize
