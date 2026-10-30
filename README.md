# Mas3D

太空教室里的 Monika：浏览器 3D 场景 + 连续文字对话 + 语音与口型，对话走本机 AstrBot 插件，与 QQ 上的 Monika 共用同一套记忆与人设。

## 目录

| 路径 | 作用 |
| --- | --- |
| `apps/web` | Vite + React + three 前端：教室场景、Monika 运行时、对话面板、动作仲裁、语音播放 |
| `apps/bridge` | AstrBot 插件（Python）：自定义平台事件、QQ 会话复用、本机 HTTP/WS 端点、HAST-SEC 与 GPT-SoVITS 适配 |
| `packages/protocol` | 前后端帧与动作计划类型 + 运行时校验 |
| `packages/motion` | 与 DOM 无关的表现层：cue 推导、手势曲线、动作仲裁 |
| `tools` | 素材管线、VRM 面部位图提取、AstrBot 插件热链接脚本 |
| `openspec/changes/add-monika-space-classroom` | 本次变更的需求、设计决策、任务与验收记录 |

## 快速开始

```powershell
pnpm install
pnpm assets              # 从 素材/ 派生可发布的贴图与模型到 apps/web/public/assets
pnpm dev                 # 前端 http://localhost:5173
pnpm mock:bridge --port 8787 --scenario happy   # 没有 AstrBot 时的剧本化桥接
```

前端默认连真桥接 `ws://127.0.0.1:8790/mas3d/ws`；要连 mock 时用 `VITE_MAS3D_WS=ws://localhost:8787/mas3d/ws` 启动，或在地址后加 `?token=`。

### 依赖的外部服务

1. **AstrBot 桌面版**（`D:Astrbot`）：插件从 `~/.astrbot/data/plugins/astrbot_plugin_mas3d_game` 加载，用 `tools/link-plugin.ps1` 建目录联接指向本仓库 `apps/bridge`；启动后插件自起 `127.0.0.1:8790`。
2. **GPT-SoVITS**（`D:GPT-SoVITS-v2pro-20250604`）：`.\runtime\python.exe api_v2.py -a 127.0.0.1 -p 9880 -c GPT_SoVITS/configs/tts_infer.yaml`。
3. **HAST-SEC**（可选，`D:HAST-SEC-v0.1.1HAST-SEC-v0.1.1`）：`node service/start.mjs`，端口 8769；不起也不影响游戏，动作退回程序化手势。

细节见 `apps/bridge/README.md`（桥接、语音、HAST-SEC 三段）与 `apps/bridge/TECH.md`（依赖的 AstrBot 内部符号与升级风险）。

## 素材与许可

`素材/` 与派生出的 `apps/web/public/assets/` 都不入库（前者有数 GB 未整理资产，后者可由脚本重建）。唯一入库的大文件是 `apps/web/public/assets/monika/Monika.vrm`（22.01 MB），因为它是产品的核心资产且许可允许再分发。

```powershell
pnpm assets              # 按 tools/assets.manifest.json 派生
pnpm assets:check        # 只校验字节数与 sha256，缺项时打印 missing: <target> 并以 1 退出
```

- **Monika 模型**：作者 `tyler1234506`（hub.vroid.com，VRM `licenseName: Other`，`personal_commercial_use: disallow`，`modification: allow`，`redistribution: allow`）。界面页脚固定显示 `Monika model by tyler1234506 · hub.vroid.com · 非商用`。
- **Poly Haven**（Clay002 / WoodFloor070 / OfficeCeiling001 / NightSkyHDRI008）：CC0，派生出的贴图随仓库发布，不保留原始 EXR。
- **Styloo Classroom Asset Pack**：教室道具（`chairtable` / `blackboardbig` / `desk` / `lamp`）只在本地派生为 GLB 使用；许可待核，因此派生产物不入库、不随发布分发。
- **HAST-SEC**：`hast-sec-noncommercial-0.1`，只做意图级消费（拿 `/v13-api/plan` 的 JSON），不引入其 1.1 GB 运行时与权重。
- **GPT-SoVITS 音色权重**：本机自训练，不随仓库分发。

**整体按非商用处理**：以上任一条款与商用发行冲突时以禁止商用为准。
