# Mas3D Unity 桌面版 · 设计文档

- 日期：2026-11-01
- 状态：待 YG 批准（未批准前不写产品代码）
- 决策来源：m07196（想整体重构为 Unity）、m07202（要画面/性能、键鼠操作角色、双击就能跑）、m07206（操作的是「我自己」）、m07209（四件动作）、m07212（玩家模型 Me.vrm）、m07219（保留打字面板 + 加语音识别）、m07261（按住键开麦）、m07264（选方案 A）

## 1. 要解决什么

现在的成品是一个浏览器里的 3D 房间：React + three.js + @pixiv/three-vrm，她从 AstrBot 拿台词、用 GPT-SoVITS 说话（前端配中文字幕）、按 HAST-SEC 的 plan 摆手势。画面已经让 YG 满意，但三件事做不到：拿不到原生渲染的性能与画质上限、玩家没有身体只能打字、发布形态是网页而不是一个能双击运行的程序。

所以这次要换的是**客户端引擎与交互形态**，不是内容：把一个「隔着屏幕看她」的页面，变成「我走进这个房间、有身体、能碰她」的桌面游戏。

玩家要的四件事，已确认只有这四件，不扩：**双手牵手、摸头（我摸她 / 她摸我）、并肩坐在床上、亲吻。**

## 2. 目标 / 非目标 / 反目标

**目标**

1. Windows 桌面独立包，双击 exe 启动，不经过浏览器。
2. 原生渲染：URP，1080p 下稳定 60 fps（本机 RTX 4060 Laptop，8 GB 显存）。
3. 玩家有可见身体（Me.vrm），第三人称，WASD + 鼠标移动，能走到她面前。
4. 四件动作都能真的做出来，且接触不穿模。
5. 对话：保留打字面板；新增按住键说话，识别结果**填进输入框**由玩家决定发不发。
6. 她的语音、表情、口型、手势维持现有质感（HAST-SEC 计划 + GPT-SoVITS 音频 + viseme）。

**非目标**

- 不做 Unity WebGL、不做移动端、不做多人联机。
- 不改 AstrBot 人格、不改 bridge 协议语义（只新增 ASR 端点）。
- 不重做房间美术：沿用导入的 MiSide 卧室网格，本地自用、不分发。
- 不做房间编辑 / 换房 / 换装系统（以后再说）。

**反目标（即使技术上跑通也算失败）**

- 变成「Unity 壳 + 内嵌浏览器」，本质还是网页。
- 她变成站桩木偶：只会播放预制动画，和 HAST-SEC 的实时计划脱钩。
- 动作穿模靠镜头切走掩盖。
- 双人动作要玩家精确站位到厘米才能触发（体验上等于做不出来）。

## 3. 已经实测的事实（不用再争）

**服务与端口**

| 服务 | 端口 | 说明 |
|---|---|---|
| bridge（本插件 uvicorn） | 8790 | Bearer 令牌，令牌文件 apps/bridge/data/mas3d/token（64 hex） |
| AstrBot 面板 | 6185 | 只做管理，不参与游戏主通道 |
| GPT-SoVITS api_v2 | 9880 | text_lang=en，中文先翻译再合成，逐句时间线 |
| HAST-SEC | 8769 | /v13-api/plan，懒加载 CUDA，首次请求要 2 s 起步 |
| Snowluma QQ | 3000/3001 | 与本次无关，不能动 |

**bridge 对外接口（现状，Unity 直接复用）**

- GET /mas3d/api/health、GET|POST /mas3d/api/session
- POST /mas3d/api/motion/plan → {ok, plan:{base, operation, timing, intents[], reply}}
- POST /mas3d/api/voice/synth → {id, audioUrl, durationMs, sentenceStartsMs, englishText}；音频走 GET /mas3d/api/voice/<id>.wav
- WS /mas3d/ws：客户端发 {type:"say", text}（≤300 字）→ 服务端 ack{turnId} → thinking{turnId} → reply{turnId,text} → intent{turnId,plan}；错误 {type:"error",code,message}，连接不断。

**模型事实（已解析）**

- Monkia.vrm（她）与 D:\Mas3D\素材\Me.vrm（玩家）都是 **VRM 0.0** → Unity 只装 UniVRM 0.x 一套。
- Me.vrm：16,641,704 B，UniGLTF-2.64.1，标题 Boy TBB，作者 @SherryKatzu & @Rarestgrass；54 骨骼、30 根手指骨、14 表情（含 a/i/u/e/o/blink/blink_l/blink_r/joy/angry/sorrow/fun）；**缺 jaw**；3 mesh / 3 skin / 16 材质 / 26 贴图；KHR_materials_unlit（MToon）。许可：商用、改造、再分发、性表现均允许，署名非必需。
- 两具模型都没有 jaw → 口型只能走 a/i/u/e/o viseme，两边共用一套驱动代码。

**ASR 实测（本机，2026-11-01）**

- 权重：C:\Users\Rain zhou\.cache\whisper\tiny.pt（75,572,083 B）、small.pt（483,617,219 B）。
- 运行环境已就绪：D:\Astrbot\backend\python（3.12.12）自带 torch 2.11.0+cu128、openai-whisper 20250625、fastapi、uvicorn、soundfile。**零安装**即可推理。
- 实测（3 句约 5 s 中文，16 kHz 单声道，Huihui TTS 合成音）：tiny CER 22.9%（默认输出**繁体**）→ 加 initial_prompt="以下是一段普通话简体中文字幕。" 后 0 / 0 / 6.25%；small CER 6.5% → 约 2%。唯一残留错误是「他/她」同音混淆。
- 耗时：tiny 0.34–1.16 s（实时比 0.13），small 0.57–0.80 s（0.15），tiny 纯 CPU 1.02–1.25 s（0.23）。加载 tiny 0.59 s、small 2.3 s。
- 显存：总量 8,188 MiB，当前已用 2,893 MiB，**余 7,099 MiB**。small 常驻约 0.5 GB，有余量。
- 合成音是无噪样本，真实麦克风会明显变差 → 默认 small，GPU 紧张时降 tiny。

**房间资产**

- apps/web/public/assets/room/mita-room.glb（gitignore）：33 网格 / 27,233 三角面 / 31 材质 / 21 贴图；地板 Mesh_0140out、墙 Mesh_0115out、斜屋顶 Mesh_0116out、门框 Mesh_0009out（无门扇）、窗 Mesh_0106out。
- web 侧已打的补丁都在 apps/web/src/scene/roomDressing.ts：白模材质替换（paintedWhite / paintedTrim）、墙纸保底、镜子参数、平顶（EAVE_HEIGHT = 3.0、CEILING_TILE_M = 0.75）、家具复制（书桌组 Mesh_0146out+Mesh_0025out、抽屉柜 Mesh_0138out）。
- 授权：从商业游戏 NinjaRipper 抓的，**只本地自用、不进公开仓库**。

**本机环境**

- **没有装 Unity**（全盘无 Unity.exe、无 Hub）。C: 剩 33.9 GB，D: 剩 96.8 GB → 装在 D:。
- 代理 127.0.0.1:7897（只有 push 和下载大包时才需要）。

## 4. 方案比较（已由 YG 选定 A）

**A. 同仓 apps/unity/，web 版保留可跑**（选定）
bridge / protocol / openspec 全部不动，Unity 作为新客户端接 8790。可随时回退；现有 Playwright 探针继续当协议回归基线。代价：两套客户端并存，packages/protocol 的 zod schema 要在 C# 手写镜像。

**B. Unity 独占，web 版打 tag 归档** — 干净，但在 Unity 侧 smoke 建起来之前完全没有自动化验证。

**C. Unity + 内嵌 WebView 保 React 聊天面板** — 省 UI 重写，但包里带浏览器内核，WebView 与渲染层的字幕/口型同步更难对齐。

YG 的原话（m07264）：「做A，等 Unity 侧有了自己的 smoke 探针（这条我会在第一阶段就建），再把 web 版降级成归档」。

## 5. 已定的实现决定（我的判断，可推翻）

1. **引擎与管线**：Unity 6 LTS + URP + Windows IL2CPP 桌面包。理由：房间 3 万面、两三盏灯，HDRP 的收益抵不过开销。
2. **工程位置**：apps/unity/（同仓）。Assets/_Local/ 放不进公开仓库的资产（房间 glb、导出的 FBX、录音样本），在 .gitignore 里挡掉。
3. **网络层**：ClientWebSocket 连 ws://127.0.0.1:8790/mas3d/ws，Bearer 从令牌文件读；帧类型用 C# 手写镜像，并写**契约测试**对照 packages/protocol/src/frames.ts 的字段（防漂移）。
4. **玩家**：CharacterController + Cinemachine 第三人称相机；房间静态网格生成 MeshCollider。
5. **UI**：UI Toolkit 做打字面板 / 设置 / 日志。中文输入法兼容要在 P1 就验，不行退 uGUI。
6. **ASR**：端点 POST /mas3d/api/asr（wav bytes，16 kHz 单声道，≤30 s），落在 bridge 进程内复用现成 Python，不新起服务；模型常驻；默认 small、降级 tiny；固定 initial_prompt 强制简体；ASR 与 TTS 共用一把串行锁，避免抢 CUDA。
7. **语音交互**：按住 V 开麦 → 松开提交 → 识别文本填进输入框（**不自动发送**）；没有耳机时也不会把她的话录回来（按住才采）。
8. **她的行为**：HAST-SEC plan 仍是主动作来源（Tier2），本地 Tier0 待机 + Tier1 预制手势兜底（沿用现有降级契约）。
9. **动作仲裁**：同一个状态机里，接触类动作（玩家主动）优先级高于说话类动作（被动），避免「她在挥手时手被拉走」。
10. **房间导入**：先试 glTF 运行时导入（glTFast）+ C# 复刻 roomDressing 的材质补丁与平顶；若材质/性能不达标，回 Blender 烘 FBX + 贴图。
11. **行为层权威**（评审后定）：不搞「C# 全量复刻 TS 语义」。bridge 的 plan 允许**纯增量可选字段**（如 expression 名、手势档位提示），web 端忽略未知字段，C# 端按需消费；C# 只移植 Tier0 待机与 Tier1 预制手势的映射。理由：语义以 bridge 为唯一出处，两套客户端才不会各说各话。
12. **ASR 进程隔离**（评审后定）：**不进 AstrBot 插件进程**。bridge 是 AstrBot 的 Star 插件，把 torch + whisper 常驻进去等于让 QQ 长连接背负一个 0.5 GB 的 CUDA 租户。改为独立 sidecar（复用 AstrBot 自带的 python 解释器，端口 9871，POST /asr），bridge 只做代理端点 /mas3d/api/asr。代价是 P6 多一个启动项，收益是 ASR 崩了不影响 QQ。
13. **演出态与操控权**（评审后定）：接触类动作进入「演出态」——锁玩家移动、切专用 Cinemachine rig，Esc 随时中断；演出态里相机自动取能看见接触点的机位（否则 ≥3 s 接触的判据没法看）。
14. **锚点必须过「无穿模」关**（YG 追加约束）：锚点不能靠肉眼挑，一律从实测几何反推，见下条。
15. **锚点选取与防穿模**（YG 追加约束的落地）：
    - **来源**：锚点由房间导入后的**世界包围盒**算出来，不是手填坐标。落座槽位 = 座面网格顶面 y + 1 cm，xz 取座面矩形中心；站立锚点取地块中心，且离所有静态碰撞体 ≥ 0.45 m（玩家胶囊半径 0.3 + 余量）。
    - **禁止区**（实测已知）：墙体壳 `Mesh_0115out` 内表面 0.35 m 以内；门框 `Mesh_0009out` 的门洞扫掠区与门扇开启扇形；书桌组（`Mesh_0146out` 桌体 + `Mesh_0025out` 显示器/主机/支架，是世界里复制出来的那一套）的桌下空间；抽屉柜 `Mesh_0138out` 正面 0.5 m；镜子 `Mesh_0014out` 正面 0.4 m；床体 `Mesh_0121out` 内部（不许站在床垫里，坐只允许床沿与长凳 `Mesh_0001out`）；窗 `Mesh_0106out` 与窗帘的下垂范围。
    - **验证（P2 就要挂上）**：①`Physics.OverlapCapsule` 对全部家具碰撞体必须返回空；②入座/起身的过渡动画逐帧用 `Physics.ComputePenetration` 断言穿透深度为 0；③每个锚点出一张从玩家视点拍的图进 smoke，肉眼复核。任一不过就换锚点，不调容差。：Unity smoke 探针由我建，且**提前到 P1**（不是 P5）——先有「启动→连 bridge→走一轮→截图」的最小闭环，后面每个阶段都挂上去；PlayMode 测试批处理负责运行时世界坐标采集，IL2CPP 包只做冒烟与人工验收。

## 6. 四件动作的验收判据（可测，不靠肉眼）

| 动作 | 判据 |
|---|---|
| 双手牵手 | 两掌心接触点世界坐标误差 ≤ 2 cm，持续 ≥ 3 s，位置抖动方差低于阈值 |
| 摸头（我摸她） | 玩家指尖到她头顶表面距离 ≤ 3 cm，她的头部做受力反馈 |
| 摸头（她摸我） | 玩家必须处于蹲/坐状态（她坐床沿、手臂长度不够站立的 1.7 m）；指尖到玩家头顶 ≤ 3 cm |
| 并肩坐床 | 床沿两个座位槽；入座后髋部吸附误差 ≤ 1 cm；双腿与床体无穿插 |
| 亲吻 | 头-头距离 ≤ 6 cm、持续 ≥ 2 s、面部朝向对齐偏差 ≤ 15°、她的表情切到闭眼 |

## 7. 阶段与依赖（排依赖，不排期）

- **P0 引擎就位**：装 Unity Hub + Unity 6 LTS + URP + Windows IL2CPP 模块；空场景 60 fps 基线。（需要 YG 登录 Unity 账号激活许可证）
- **P1 通道打通 + smoke**：Unity 连 bridge，能发 say、收 reply/intent；打字面板可输入中文；Monkia.vrm 与 Me.vrm 加载出来；**同阶段建好 Unity 侧 smoke**（启动→连接→一轮对话→截图），此后各阶段都挂在这条链上。
- **P2 房间与移动**：房间导入、材质补丁、平顶、家具；玩家 CharacterController 走动；相机；**锚点图 + 防穿模三项验证（§5.15）**；家具位置在此阶段定稿。
- **P3 语音**：/mas3d/api/asr + 按住说话 + 文本回填。
- **P4 四件动作**：按第 6 节逐件做到判据。
- **P5 回归与归档**：smoke 覆盖到四件动作（含第 6 节判据）；全绿后把 **web 版降级归档**（单开一步，不混在功能提交里）。
- **P6 打包**：IL2CPP 出包，双击可跑；桥接与 AstrBot 的启动顺序、失败提示。

## 8. 已由 YG 拍板的两件事（2026-11-01）

1. **手怎么到位、靠什么判定「碰到」→ 瞄准确认 + 演出态**。玩家走近后，准星对准她的手掌或头顶（或床沿这类交互体），出现提示，按 E 进入演出态：锁玩家移动、切专用机位、接触由运行时 IK 求解并保持，Esc 随时中断。**不做**实时驱动手臂 IK——判据要采世界坐标并保持 3 s 低抖动，演出入口是纯函数 `TryEnterAction(id)`，PlayMode 测试可直接调用；实时驱动会把误差绑到玩家鼠标轨迹上，同一条测试换个帧率就飘，还得另造输入机器人。
2. **她自己会不会走动 → 锚点 + 锚点间过渡，不做 NavMesh**。4–6 个锚点（床沿双座、床尾、书桌椅、窗边、门内），图连通、带朝向，移动时播放行走/坐下动画；说话中不位移，位移中只做原地 idle。**协议一个字不改**：她的位姿是 Unity 本地状态，将来要下行只走 §5.11 的纯增量可选字段。
3. **YG 追加的硬约束（m07323）**：锚点不许选容易穿模的位置。明细见 §5.15。

## 9. 留给实现者

- Unity 具体版本号与 URP 版本、渲染管线设置细节。
- glTF 运行时导入 vs Blender 转 FBX 的最终取舍。
- UI Toolkit vs uGUI 的最终取舍（打字面板输入法）。
- 玩家角色动画资产来源（Mixamo / 自建 / VRoid 兼容）、第三人称相机与晕动处理。
- 双人动作对齐的具体 IK 方案（Animation Rigging vs 自写 TwoBoneIK + 接触点求解）。
- 房间碰撞体生成策略（每网格 MeshCollider vs 合并、玩家胶囊半径）。

## 10. 验收与回退

- **验收**：P5 的 smoke 全绿 + 第 6 节判据全部通过 + 双击出包可跑一轮完整对话与至少一件动作。
- **回退**：apps/unity/ 是新增目录，任何时候删掉不影响 web 版；bridge 只加不改（/mas3d/api/asr 是纯增量端点），协议语义不变。
- **安全边界**：房间与语音样本属本地自用资产，不进公开仓库；公开仓库只提交代码、配置与文档。
