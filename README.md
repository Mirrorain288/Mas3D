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

`素材/` 与派生出的 `apps/web/public/assets/` 都不入库（前者有数 GB 未整理资产，后者可由脚本重建）。唯一入库的大文件是两副 VRM：`monika/Monkia.vrm`（16.35 MB，默认）与 `monika/Monika.vrm`（22.01 MB，回退），它们是产品的核心资产且许可允许再分发。

```powershell
pnpm assets              # 按 tools/assets.manifest.json 派生
pnpm assets:check        # 只校验字节数与 sha256，缺项时打印 missing: <target> 并以 1 退出
```

- **默认模型 `Monkia.vrm`**：作者 `LilowLeeLani`（hub.vroid.com，VRM `licenseName: Other`，`credit: necessary`，`modification: allow`，`redistribution: allow`，企业/个人商用 disallow，性表现 disallow）。
- **回退模型 `Monika.vrm`**：作者 `tyler1234506`（hub.vroid.com，同一档条款，reference `DDLC blazer`）。
- 页脚署名不是写死的：它由当前加载模型的 VRM meta（`title`/`author`）派生（`apps/web/src/avatar/modelCredit.ts`），换模型后自动显示对应作者。
- **Poly Haven**（Clay002 / WoodFloor070 / OfficeCeiling001 / NightSkyHDRI008）：CC0，派生出的贴图随仓库发布，不保留原始 EXR。
- **Styloo Classroom Asset Pack**：教室道具（`chairtable` / `blackboardbig` / `desk` / `lamp`）只在本地派生为 GLB 使用；许可待核，因此派生产物不入库、不随发布分发。
- **HAST-SEC**：`hast-sec-noncommercial-0.1`，只做意图级消费（拿 `/v13-api/plan` 的 JSON），不引入其 1.1 GB 运行时与权重。
- **GPT-SoVITS 音色权重**：本机自训练，不随仓库分发。

**整体按非商用处理**：以上任一条款与商用发行冲突时以禁止商用为准。

## 换 VRM 模型

不用改代码：模型 URL 由环境变量 `VITE_VRM_URL` 覆盖（`apps/web/src/avatar/MonikaAvatar.tsx` 的 `VRM_URL`），未设置时用默认的 `Monkia.vrm`。

```powershell
node tools/inspect-vrm.mjs "素材/新模型.vrm"    # 体检：ready / needs-alias-map / needs-port
node e2e/vrm-probe.mjs --vrm /assets/monika/X.vrm  # 实测表情与口型是否真的写进形变（apps/web 下跑）
node e2e/smoke.mjs                                # 完整冒烟（8 项）
```

1. 体检 `ready` 后把模型放进 `apps/web/public/assets/monika/`（该目录除入库的两副 VRM 外都被 `.gitignore` 排除，不会误入库）。
2. 只在本机切换时用环境变量，不要改代码（也不用写 env 文件）：

```powershell
$env:VITE_VRM_URL = "/assets/monika/Monika.vrm"   # 回退到旧模型（默认已是 Monkia.vrm）
pnpm --filter @mas3d/web dev                     # 或 pnpm --filter @mas3d/web build
Remove-Item Env:\VITE_VRM_URL                    # 清掉就回到默认模型
```

   想让新模型成为仓库默认：改 `MonikaAvatar.tsx` 的 `VRM_URL` 兜底值，并把文件名加进 `.gitignore` 的 `monika/` 白名单（现在放行 `Monkia.vrm` 与 `Monika.vrm` 两副）。这会连许可一起变，入库前必须确认新模型的再分发条款与署名要求。
3. 表情名变了就重生成映射：`node tools/extract-face-map.mjs <模型> > src/avatar/face.map.json`（脚本会校验 57 个 target 与几个关键下标，名字表不同会直接退出 1）。

**已知坑：带真实 binds 的 VRM 会把表情清零**。有的模型在 `VRM.blendShapeGroups` 里带 bind（`{index, mesh, weight}`），three-vrm 的表情管理器每帧 `update()` 时把自己的权重（默认 0）写回同一批 `Fcl_*` 形变，把 `FaceDriver` 的直写覆盖掉——症状是表情/口型「先亮一下，约 1 秒后归零」。加载时用 `detachExpressions()` 注销管理器里的表达式，让 `FaceDriver` 成为唯一写入者（`apps/web/src/avatar/vrmExpressions.ts`）。入库的 `Monika.vrm` 恰好 binds 全空，所以这个坑只有换模型才会遇到。

坐姿是「hips 贴座面 + 膝盖按脚掌厚度补偿」：`applySitPose` / `placeSeated` 先把 hips 对齐由场景量出的座面高度，随后用模型自己量出的脚掌厚度（站立脚踝高度 − 站立包围盒最低点）求解小腿要多弯多少，把脚放回地面（`apps/web/src/avatar/seatFit.ts`，收敛到 2 mm 内）。标定姿态原本就是埋着的——两副模型分别埋 6.1 cm 与 10.0 cm，只是课桌挡住看不见——所以目标高度不能写死，必须按模型量；换模型后 `stats().seatFit` 会给出实测的 `clearanceM` 与 `kneeDelta`。整体升降会让角色浮在座面上、按身高缩放会动到手位置与相机，都不采用。命中盒与相机仍按标定体型定。
