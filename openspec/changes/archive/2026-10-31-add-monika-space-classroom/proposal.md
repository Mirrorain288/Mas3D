# Proposal

## Why

本机已经具备做这件事的全部原料，但没有任何东西把它们连起来：`素材/` 里有 Styloo 教室资产包（GLB+FBX 双份）、Poly Haven 的 Clay002 / WoodFloor070 / OfficeCeiling001 三套 PBR 材质、NightSkyHDRI008 8K 星空 HDRI，以及 `素材/莫妮卡.vrm`（22.01 MB，VRM **0.0**，562 节点、面部网格 57 个命名 morph、**0 个动画片段**、无 lookAt 装配、18 个表情组但 binds 全空）；`D:\Astrbot` 上持续运行着 AstrBot 4.28.2，实测数据目录是 `C:\Users\Rain zhou\.astrbot\data`，QQ 私聊会话 umo 为 `Monika:FriendMessage:2441388327`，绑定人设 `persona_id=Monika`，当天单个 conversation 的 content 已达 1.38 MB；`D:\HAST-SEC-v0.1.1` 已部署完毕（Node 服务、8769 端口、runtime 1.1 GB 与 models 24.3 MB 均已就位）；`D:\GPT-SoVITS-v2pro-20250604` 里有自训练的 Monika 音色权重并已在 9880 端口提供 `api_v2`，实测一句英文 4.40 秒音频合成耗时 2.37 秒（32 kHz 单声道），AstrBot 侧也配着同一套 `gsv_tts_selfhost` provider。

缺的是产品：`openspec/specs/` 与 `openspec/changes/archive/` 只有 `.gitkeep`，`README.md` 只有一行 `# Mas3D`，仓库里没有一行业务代码。

因此这次变更解决的问题是：**把已经活在 QQ 文本框里的 Monika 搬进一间 3D 太空教室，并让教室里的她和 QQ 上的她共用同一份记忆与上下文**，同时保留「AstrBot 或 HAST-SEC 不可用时游戏仍然能跑」的底线。

## What Changes

- **新增浏览器端 3D 游戏**（Vite + TypeScript + React + Three.js + 兼容 VRM 0.0 的角色运行库 + Zustand）：太空教室场景、Monika 固定在课桌前、程序化待机（呼吸/眨眼/微动）、视线跟随摄像机、对话界面、动作调度器、本地存档。
- **新增 AstrBot 插件 `astrbot_plugin_monika_3d`**：用 `register_platform_adapter` 注册自定义平台适配器，向游戏暴露 HTTP/WS 端点；构造事件后把 `unified_msg_origin` 指向既有 QQ 私聊会话，从而使游戏与 QQ **共用同一条 conversation、同一套人设、同一批记忆插件和同一组模型 provider**。
- **新增动作意图层**：把模型回复归一化为 `{reply, emotion, gaze, gesture, intensity}` 一类的有限表现指令，映射到 VRM 表情 preset 与预制动作；非法/超范围意图降级为纯表情。
- **新增 HAST-SEC 适配器**：可选动作后端，消费其 `/v13-api/*`、`/v15-api/*` 契约；不可用时整体回退到本地预制动画，游戏不受影响。
- **新增语音与口型**：桥接进程直连本机 GPT-SoVITS `api_v2` 合成语音（中文台词先转英文再合成，字幕保持中文），前端以音频包络驱动面部 `Fcl_MTH_*` 五元音形变做口型，语音默认开启、失败时安静退回纯字幕；语音只在浏览器播放，不进入 QQ。
- **素材入库策略**：大体积二进制（VRM、8K EXR、教室 GLB/FBX）不入 Git 历史，通过 `.gitignore` + 本地目录约定 + 构建期拷贝处理。
- **署名与许可**：产品界面内提供可见的作者署名（VRM `author: tyler1234506`，licenseName `Other`，personal_commercial_use=disallow），整体按非商用处理。
- 不实现（首版）：多人联机、VR、角色自由移动、任意中文指令生成任意骨骼动作、完整剧情系统、语音识别（ASR）与语音克隆训练、手机端适配、复杂物理接触。

## Capabilities

### New Capabilities

- `scene/space-classroom`: 太空教室场景的构成与运行约束——星空天空盒、教室几何与材质装配、坐姿机位、可交互范围、渲染性能与资源加载策略。
- `avatar/monika-runtime`: Monika 角色运行时——VRM 0.0 装载、骨骼与表情通道探测、程序化待机（呼吸/眨眼/微动）、视线跟随、表情与上肢动作播放、缺通道时的降级、许可署名义务。
- `ui/dialogue-console`: 文字对话界面与会话记录——输入框、消息流、等待与错误状态、历史回溯、本地持久化（会话记录与基础设置）、不可用时仍可阅读历史。
- `bridge/astrbot-session`: 与 AstrBot 的会话桥接——共享 QQ 私聊会话的会话身份、消息往返协议、来源标记、超时与失败降级、并发与速率保护、密钥不出后端。
- `motion/action-intent`: 动作意图的解析与校验——从回复文本/情绪标注得到受支持的有限表现集合，非法意图的收敛策略，以及"无动作后端"时的行为一致性。
- `motion/hast-sec-adapter`: HAST-SEC 动作后端的接入——可用性探测、同源校验的规避路径、会话编号规则、动作计划消费与回放、失败时的本地兜底。
- `voice/tts-lipsync`: 语音与口型的产生与同步——本地 GPT-SoVITS 合成的可复现性、中文台词英文化、音频包络与元音映射驱动的口型、语音与文字的并行、合成不可用时的安静降级、口型通道独占。

### Modified Capabilities

（无：本仓库此前没有任何已定稿能力。）

## Impact

- **新增代码**：前端应用（`apps/` 或根级 `web/`）、共享类型包、AstrBot 插件目录（独立于本仓库发布到 `~/.astrbot/data/plugins/`）。
- **对 AstrBot 的影响**：只新增一个插件与一个平台适配器实例，不改动既有 13 个已装插件；因为共用会话，游戏内发言会写入 `Monika:FriendMessage:2441388327` 的对话历史，QQ 侧会看到同一段记忆（这是本变更的既定目标，不是副作用）。
- **对 HAST-SEC 的影响**：只读消费其 HTTP 契约，不改源码；因其 `public_api.mjs` 要求 `Origin` 必须等于 `http://<Host>` 且 `content-type: application/json`，浏览器跨源直连会被拒，必须经同源代理转发。
- **端口与进程**：AstrBot 面板 6185（插件端点随其进程）、前端 dev 5173、HAST-SEC 8769（当前未启动，需要时手动 `npm start`）。
- **依赖**：前端 Three.js 系；HAST-SEC 侧已有 `three@0.180.0` 与 `@pixiv/three-vrm@3.5.5`，注意前端版本对齐以免出现两套 three 语义。
- **新增外部进程**：GPT-SoVITS `api_v2`（`D:\GPT-SoVITS-v2pro-20250604`，127.0.0.1:9880，需自行启动）；它与 QQ 共用同一块 GPU，因此游戏侧合成按单并发排队，并可在设置里一键关掉语音。
- **语音资源**：音色权重来自本机自训练，参考音频与翻译密钥只存在后端配置，不入库；仓库内不新增任何模型文件。
- **许可约束**：VRM 二次分发允许但必须署名、禁止商用与性化；HAST-SEC 为 `hast-sec-noncommercial-0.1`。产品不得商用发行。
