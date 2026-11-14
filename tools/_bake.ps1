param([string] $Method = 'Mas3D.Editor.CliHouse.Bake', [string] $Log = 'bake')
$ErrorActionPreference = 'Continue'
& 'D:\Mas3D\tools\unity.ps1' run -- -executeMethod $Method -logFile ('D:\Mas3D\apps\unity\Logs\' + $Log + '.log')
exit $LASTEXITCODE
