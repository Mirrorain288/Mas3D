param([string] $Variant = 'a', [string] $Log = 'lamp')
$ErrorActionPreference = 'Continue'
& 'D:\Mas3D\tools\unity.ps1' run -- -executeMethod Mas3D.Editor.CliLamp.Sweep -variants $Variant -logFile ('D:\Mas3D\apps\unity\Logs\' + $Log + '.log')
exit $LASTEXITCODE