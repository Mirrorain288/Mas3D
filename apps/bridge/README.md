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

## HAST-SEC 动作后端（可选）

HAST-SEC 是本地动作意图模型：它在自己的浏览器页面里解码轨迹，服务端只输出高层意图 JSON。本项目**只做意图级消费**（拿 `base` + `intents` 自己驱动上臂/前臂骨骼），不引入它的 three 版本与 GB 级运行时。

启动（工作目录必须是 HAST-SEC 项目根）：

```powershell
cd D:\HAST-SEC-v0.1.1\HAST-SEC-v0.1.1
node service/start.mjs
# 日志出现 HAST-SEC v0.1: http://127.0.0.1:8769
```

确认：

```powershell
curl.exe http://127.0.0.1:8769/v13-api/status
# {"runtime":{...},"training":false}
```

桥接侧的约定：

- 代理端点 `POST /mas3d/api/motion/plan`，转发到 `http://127.0.0.1:8769/v13-api/plan`。
- 上游强校验 `Origin === "http://" + Host`，httpx/curl 会改写 `Host`，所以用 `http.client` 手写这两个头部（见 `apps/bridge/hastsec.py`）。
- 入参整形：`generation: "v15"`、`text` ≤ 300 字、`context` ≤ 4 条 / 总长 ≤ 1200 / 单条 ≤ 600，会话 GUID 持久化在 `plugin_data/mas3d/session.json`。
- 探活 1.5 秒、请求 6 秒（冷启动实测 2.62 s，热态 0.74–1.01 s）；失败冷却 60 秒，冷却期间前端按 `unsupported`/空计划处理，退回 Tier 1 程序化手势。
- 前端 `apps/web/src/avatar/hastMotion.ts` 把 `side/action/height` 映射成上臂/前臂旋转，遵守 `speed/strength/hold/delay` 与 `timing`，整段结束后回待机。

不启动 HAST-SEC 时游戏照常运行：动作后端返回 `bridge_down`，角色只用 Tier 0 待机与 Tier 1 预制手势。

## 测试

```powershell
py -m venv .venv; .\.venv\Scripts\python.exe -m pip install pytest pytest-asyncio
.\.venv\Scripts\python.exe -m pytest apps/bridge/tests -q
```

测试用 stub 的 `astrbot` 模块导入插件（真实的 AstrBot 只在运行时加载），因此不需要把 AstrBot 的 Python 环境弄脏。

## 许可与隐私

- 插件本身随仓库分发；运行时依赖 AstrBot 与本地 GPT-SoVITS，均不在本仓库内。
- 游戏回合会带着 `（3D教室）` 前缀写进真实 QQ 会话历史，这是明确的产品选择，可在设置里把 umo 换成独立会话。