#Requires -Version 5.1
# Resume-capable ModelScope fetch of the Llama-3-8B-Instruct weights (the LLM2Vec text encoder's base).
$ErrorActionPreference = 'Continue'
$env:HTTP_PROXY=''; $env:HTTPS_PROXY=''
$dir = 'D:/Mas3D/.superpowers/llama3'
$base = 'https://modelscope.cn/api/v1/models/LLM-Research/Meta-Llama-3-8B-Instruct/repo?Revision=master&FilePath='
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$files = @('model-00001-of-00004.safetensors','model-00002-of-00004.safetensors','model-00003-of-00004.safetensors','model-00004-of-00004.safetensors')
$jobs = @()
foreach ($f in $files) {
    $dest = Join-Path $dir $f
    $jobs += Start-Process -FilePath curl.exe -ArgumentList @('-sL','-C','-','--retry','20','--retry-delay','5','--retry-all-errors','-o',$dest,($base + $f)) -PassThru -NoNewWindow
}
$jobs | Wait-Process
foreach ($f in $files) {
    $p = Join-Path $dir $f
    if (Test-Path $p) { "$f $([math]::Round((Get-Item $p).Length/1MB,0))MB" }
}
'WEIGHTS DONE'
