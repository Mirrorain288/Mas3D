param([string] $Method = 'Mas3D.Editor.CliHouse.Bake', [string] $Log = 'bake', [string] $Extra = '')
$ErrorActionPreference = 'Continue'
$list = @('run', '--', '-executeMethod', $Method, '-logFile', ('D:\Mas3D\apps\unity\Logs\' + $Log + '.log'))
if ($Extra -ne '') { $list += $Extra.Split(' ') }
& 'D:\Mas3D\tools\unity.ps1' @list
exit $LASTEXITCODE
