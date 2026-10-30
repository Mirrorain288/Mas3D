<#
.SYNOPSIS
    把本仓库的 apps/bridge 热链接到 AstrBot 插件目录。

.DESCRIPTION
    在 %USERPROFILE%\.astrbot\data\plugins\astrbot_plugin_mas3d_game 建一个目录联接
    （Junction）指向 <repo>\apps\bridge。改代码后只需在 AstrBot 面板重载插件，
    不用复制文件。

    已存在的联接如果指向别处（例如另一个 worktree），会先删掉旧联接，但只在
    -Force 时才这么做；目标目录不是联接而是真实目录时也要求 -Force，绝不静默删数据。

.PARAMETER RepoRoot
    仓库根目录，默认是本脚本所在目录的上一层。

.PARAMETER PluginDir
    AstrBot 插件目录，默认 %USERPROFILE%\.astrbot\data\plugins。

.PARAMETER Force
    覆盖已存在的联接或同名真实目录。
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = '',
    [string]$PluginDir = '',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# PowerShell 5.1 在 param 默认值求值时 $PSScriptRoot 还是空的，所以放到脚本体里补
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
if (-not $PluginDir) { $PluginDir = Join-Path $env:USERPROFILE '.astrbot\data\plugins' }

$source = Join-Path $RepoRoot 'apps\bridge'
$target = Join-Path $PluginDir 'astrbot_plugin_mas3d_game'

if (-not (Test-Path (Join-Path $source 'main.py'))) {
    throw "找不到插件源码（缺 main.py）：$source"
}

if (-not (Test-Path $PluginDir)) {
    New-Item -ItemType Directory -Path $PluginDir -Force | Out-Null
}

if (Test-Path $target) {
    $item = Get-Item $target -Force
    $linkType = $item.LinkType
    $resolved = if ($item.Target) { [System.IO.Path]::GetFullPath($item.Target) } else { $null }
    $wanted = [System.IO.Path]::GetFullPath($source)

    if ($linkType -eq 'Junction' -or $linkType -eq 'SymbolicLink') {
        if ($resolved -eq $wanted) {
            Write-Host "已是最新联接：$target -> $resolved"
            return
        }
        if (-not $Force) {
            throw "已有联接指向 $resolved，不是当前仓库。确认后加 -Force 重指向 $wanted"
        }
        # 只删重解析点，不碰目标内容
        $item.Delete()
        Write-Host "已移除旧联接：$resolved"
    }
    else {
        if (-not $Force) {
            throw "$target 是真实目录（$linkType），不是联接。确认可以覆盖后加 -Force"
        }
        Remove-Item -LiteralPath $target -Recurse -Force
        Write-Host "已删除同名真实目录：$target"
    }
}

New-Item -ItemType Junction -Path $target -Target $source | Out-Null
$check = Get-Item $target -Force
Write-Host "已创建联接：$target -> $($check.Target)"
Write-Host "下一步：在 AstrBot 面板重载插件，日志应出现 mas3d_game 0.1.0 loaded"
