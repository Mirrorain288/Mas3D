# Mas3D 桥接插件（apps/bridge）

AstrBot 插件 `astrbot_plugin_mas3d_game`：把浏览器里的 3D 教室接到 AstrBot，让游戏里的对话和 QQ 上的 Monika 共用同一个会话（同一个 `unified_msg_origin`）。

## 安装（开发期热链接）

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\link-plugin.ps1
```

（本机没有 PowerShell 7 的 `pwsh`，用系统自带的 Windows PowerShell 即可。）

脚本在 `%USERPROFILE%\.astrbot\data\plugins\astrbot_plugin_mas3d_game` 建一个指向 `<repo>\apps\bridge` 的目录联接（Junction），之后改代码不需要再复制。重装/换目录时先 `-Force`：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\link-plugin.ps1 -Force
```

然后在 AstrBot 面板里重载插件，日志应出现 `mas3d_game 0.1.0 loaded`。

## 端点（随任务逐步落地）

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/mas3d/api/health` | 插件版本、当前 umo、是否降级 |
| WS | `/mas3d/ws?token=…` | 回合对话：`ack → thinking →（intent）→ reply` |
| POST | `/mas3d/api/motion/plan` | HAST-SEC 动作意图代理 |
| POST | `/mas3d/api/voice/synth` | 本地 GPT-SoVITS 合成 + 逐句时间线 |

全部只绑 `127.0.0.1`，令牌存放在 `plugin_data/mas3d/token`（首次启动生成），前端用 `Authorization: Bearer` 或 WS query 传入。

## 测试

```powershell
py -m venv .venv; .\.venv\Scripts\python.exe -m pip install pytest pytest-asyncio
.\.venv\Scripts\python.exe -m pytest apps/bridge/tests -q
```

测试用 stub 的 `astrbot` 模块导入插件（真实的 AstrBot 只在运行时加载），因此不需要把 AstrBot 的 Python 环境弄脏。

## 许可与隐私

- 插件本身随仓库分发；运行时依赖 AstrBot 与本地 GPT-SoVITS，均不在本仓库内。
- 游戏回合会带着 `（3D教室）` 前缀写进真实 QQ 会话历史，这是明确的产品选择，可在设置里把 umo 换成独立会话。
