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

- Monkia.vrm（她）与 D:\Mas3D\素材\Me.vrm（玩家）**磁盘上是 VRM 0.0，导入后运行时是 VRM 1.0**（2026-11-03 两轮实测）。GLB 里 `extensionsUsed` 含 `VRM`、`specVersion 0.0`、`exporterVersion "VRoid Studio-1.21.0"`、骨骼 `hips` 一套共 54 根；导入器 `VrmScriptedImporter` 默认 `MigrateToVrm1 = true`（`Editor/ScriptedImporter/VrmScriptedImporter.cs:16`），所以场景里拿到的是 `Vrm10Instance` + `UniHumanoid.Humanoid` + `Animator` 各一个、骨骼 `J_Bip_*`。工具链只装 `com.vrmc.vrm` 一套（0.131.4，OpenUPM），asmdef 要同时引用 `VRM10` 与 `UniHumanoid`。
- Monkia：3 skinned mesh / 38,413 三角面 / 161 骨 / 57 blendShape / 17 材质（`VRM10/Universal Render Pipeline/MToon10`）；T-pose 包围盒 **1.37 × 1.61 × 0.42**（身高 1.61 m）。
- Me：GLB 体积 16,641,704 B，标题 Boy TBB，作者 @SherryKatzu & @Rarestgrass；实测 3 skinned mesh / 44,653 三角面 / 128 骨 / 57 blendShape / 16 材质；包围盒 **1.88 × 2.03 × 0.38**（含张开的 T-pose 手臂）。许可：商用、改造、再分发、性表现均允许，署名非必需。
- 两具都没有独立 jaw 骨 → 口型走 a/i/u/e/o 五个 viseme。可用表情键要**运行时读** `Vrm10Instance.Runtime.Expression.ExpressionKeys`，代码里不许假设某个预设一定存在（`SetWeight` 对不存在的键是静默忽略）。

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
    - **验证（P2 就要挂上）**：①`Physics.OverlapCapsule` 对全部家具碰撞体必须返回空；②入座/起身的过渡动画逐帧用 `Physics.ComputePenetration` 断言穿透深度为 0；③每个锚点出一张从玩家视点拍的图进 smoke，肉眼复核。任一不过就换锚点，不调容差。
16. **Unity smoke 提前到 P1**：探针由实现者自建，不等 P5——先有「启动 → 连 bridge → 走一轮 → 截图」的最小闭环，后面每个阶段都挂上去；PlayMode 测试批处理负责运行时世界坐标采集，IL2CPP 包只做冒烟与人工验收。
17. **锚点实测表**（2026-11-01 用 `apps/web/e2e/room-map.mjs` 量的当前场景，世界坐标；Unity 里几何同源，数值可直接复用）：
    - 站立锚点，判据为「离墙 ≥ 0.35 m 且离最近静态碰撞体 ≥ 0.45 m」，括号内为最近的碰撞体与实测净空：
      | 锚点 | x / z | 实测净空 |
      | --- | --- | --- |
      | A1 门前 | 2.40 / 2.60 | 1.00（`Mesh_0025out` 右墙书桌组） |
      | A2 床尾 | 0.10 / 1.60 | 0.96（`Mesh_0001out` 长凳） |
      | A3 床右侧 | 1.75 / −0.15 | 0.65（`Mesh_0121out` 床体） |
      | A4 房中偏左 | −1.60 / 1.20 | 1.28（`Mesh_0001out` 长凳） |
      | A5 远角 | 2.60 / 3.60 | 1.97（`Mesh_0025out` 右墙书桌组） |
      | A6 床尾偏右 | 1.35 / 1.05 | 0.74（`Mesh_0001out` 长凳） |
    - 落座槽位 S1（**web 侧旧值，仅供对照**）：长凳 `Mesh_0001out` 顶面 y = 0.600，取 y = 0.610，xz 取矩形中心 (0.140, 0.345)。**Unity 构建不用这组数**：见下方第 131 行起的 Unity 实测表，座面顶 0.604、`seat.y = 0.614`、xz (0.105, −0.024)，`apps/unity/Anchors/anchors.json` 与 `AvatarSeatTests.cs` 都以后者为准；床沿另有两个位置，但床体 `Mesh_0121out` 顶面 0.91 高于床沿坐姿的膝盖线，坐下时会压进被褥，P2 再定要不要保留。
    - 已否决的候选（都是「看着空、实测不行」）：床与左墙之间净空只有 **0.28 m**（`Mesh_0121out` max x 1.10 之外是 `Mesh_0146out` 左书桌的 −1.11），站不进去；(1.55, −1.60) 距床体 **0.446 m** 差 4 mm 被否决；(2.20, 0.20) 距右墙书桌 **0.20 m** 被否决。
    - 这张表是 P2 的输入，不是终稿：家具在 P2 定稿后要**重跑一次 `room-map.mjs` 重算**，不许沿用旧数。
    - **Unity 侧实测（2026-11-02，取代上面 web 表用于 Unity 构建）**：web 侧对房间做过 yaw −90° + 位移，坐标不能照抄，所以在 Unity 里用同一套判据重算了一遍。生成命令 `powershell -ExecutionPolicy Bypass -File tools/unity.ps1 anchors`（内部走 `Mas3D.Editor.CliAnchors.Solve`，读 `Assets/_Local/room/mita-room.glb` 的实测包围盒），产出 `apps/unity/Anchors/anchors.json`、俯视验证图 `plan.png`、玩家视点图 `anchor-views.png`（工具 `tools/anchor-plan.mjs`、`tools/anchor-shots.ps1`）。
      - 室内平面取墙裙 `Mesh_0116out` 包围盒：x −3.79..3.21、z −3.35..3.35（**7.00 × 6.70 m**）；判定用玩家胶囊 r = 0.30 / h = 1.70，离墙 ≥ 0.35、离家具 ≥ 0.45，栅格 0.05 m，共 9297 个合法点。
      | 锚点 | x / z | 朝向 | 离墙 | 最近碰撞体 |
      | --- | --- | --- | --- | --- |
      | A1 门内 | 1.33 / −1.93 | −40.0° | 1.42 | 0.60（门框 `Mesh_0010out.001`） |
      | A2 床尾 | −0.72 / −0.03 | 86.0° | 3.07 | 0.54（长凳 `Mesh_0001out`） |
      | A3 桌前 | 1.33 / 1.42 | −131.2° | 1.88 | 0.49（床体 `Mesh_0121out`） |
      | A4 窗边 | 1.93 / −1.88 | −49.7° | 1.28 | 0.60（镜子 `Mesh_0014out`） |
      | A5 远角 | −3.02 / 2.57 | 133.3° | 0.77 | 3.47（长凳 `Mesh_0001out`） |
      - 落座槽位：**S1 长凳**座面顶 0.60 → y = 0.61，xz (0.10, −0.02)，朝向 −90°（背向床、面向书桌）；**S2 床沿**包围盒顶 0.91 → y = 0.92，xz (1.74, −0.02)，与 web 表同样标记为「顶面 = 被面、高于坐姿膝盖线」，P2 定家具时再决定留不留。
      - 与 web 表的差异不是误差：web 的 A1「门前」在 Unity 里对应门洞另一侧，web 的 A6 床尾偏右与 A2 合并成一个（间距 < 0.6 m 被 MinAnchorSeparation 拦下），web 的 A4「房中偏左」在 Unity 里落成 A5 远角（吸附漂移 1.18 m 的房中点没有价值，已删）。
      - 5 个锚点离墙离家具全部过线（漂移 ≤ 0.18 m）。
      - **验证已落地（第 115 行 ①②③ 的第①条 + 落座）**：`Assets/Tests/PlayMode/RoomAnchorTests.cs`，跑法 `tools/unity.ps1 test -Mode PlayMode`。测试实例化房间、给每个 `MeshFilter` 挂非凸 `MeshCollider`（实测 **33 个**），再对每个锚点做 `Physics.OverlapCapsuleNonAlloc`（站姿 r = 0.30 / h = 1.70，从 y = 0.32 到 1.40），断言除地板外无命中；落座槽位按 r = 0.22、从座面 + 0.10 到 + 0.72 断言。当前 **2/2 通过**。
      - 测试逼出两条真实约束，写进判据：①**地面覆盖不算障碍**——地毯等高度 ≤ 0.07 m 的饰件允许被踩（求解器同规则），否则 A3 会误报命中 `Mesh_0126out`；②**坐姿胶囊半径只能是 0.22**——长凳座面中心 (0.10, −0.02) 离床体 `Mesh_0121out` 边界仅 0.25 m，用站姿 0.30 会顶进床沿，这是几何事实不是调容差。

18. **P2 可玩场景（2026-11-02 实测，已跑通）**：房间 + 玩家 + 第三人称相机进了 `Assets/Scenes/Main.unity`，一条命令重建、一条命令出图。
    - 生成：`tools/unity.ps1 player`（`Mas3D.Editor.CliPlayer.BuildScene`）—— 装房间、按包围盒归零（与 `CliAnchors` 同一套归零，否则锚点全偏）、换自有材质、平顶、布光、夜空卡片、玩家与相机，并把 `Anchors/anchors.json` 复制到 `Assets/StreamingAssets/mas3d/`。实测日志：`[rebuild] … renderers=33 unresolved=0`、`[dress] lights moon=1.2 lamp=1.2 glow=2 eaveY=3.00`、`[player] materials=33 eave=3.00`、`[player] scene written`。
    - 出图：`tools/unity.ps1 capture`（`Mas3D.Editor.CliPlayer.Capture`，**必须带 GPU，不加 -nographics**）—— 四个朝向 1280×720 存 `apps/unity/Shots/play-01..04.png`。非 play mode 下靠 `CinemachineBrain.ManualUpdate()`（Cinemachine 3.6+）推进再 `Camera.Render()`。
    - 玩家：胶囊 r = 0.30 / h = 1.70、眼高 1.62、走 2.0 m/s / 跑 3.4 m/s、加速度 18、重力 −20、转向 720°/s；出生 `SpawnLift = 0.10`（地板顶面 y ≈ 0.04，按 0.03 生会让胶囊陷进地板，**陷进去的 CharacterController 永远报 isGrounded = false**）。
    - 相机：`CinemachineThirdPersonFollow`，距离 2.8 m、肩偏 (0.28, 0, 0)、fov 55、near 0.05、避障半径 0.20（`IgnoreTag "Player"`）；默认俯角 14°，在 A2 出生点相机落在 y ≈ 2.57。
    - **外壳碰撞必须反向**：导入的外壳是朝外的闭合盒，非凸 `MeshCollider` 对 CharacterController 的 sweep 是单面的，直接挂会让玩家穿过地板和墙。`Assets/Scripts/RoomColliders.cs` 给墙/屋面/地板三个外壳网格生成反绕序（每个三角形交换 0/2 顶点）的 `inner-collider` 子物体，家具只留单面。实测「从 A2 向北撞墙」停在 z = 2.94，证明反向 collider 生效。
    - **着色器全局必须每进程重设**：房间着色器自己乘 `_RoomExposure` / `_RoomSaturation`（`RoomSurface.shader:103`、`RoomMirror.shader:77`），这两个是 `Shader.SetGlobalFloat` 全局，**不随场景保存**，默认 0 会把室内乘成纯黑，只剩 unlit 的夜空卡片可见。`Assets/Scripts/RoomLook.cs` 在 `RoomBootstrap.Boot()` 里先 `RoomLook.Apply()`（postExposure −1.2、saturation 60，与 `RoomDress.Tuning` 镜像）。
    - **编辑器工具里 Awake 不跑**：`PlayerMotor` 的控制器配置与 Head 创建放在幂等的 `EnsureReady()`，`Awake` 只转发；`RoomBootstrap.Boot()` 显式调用，否则无头截图时眼高取空、相机贴地。
    - 验收：`tools/unity.ps1 test -Mode PlayMode` **6/6 通过**（`PlayerWalkTests.cs`：A2→A3 两段步行无侵入、相机不出外壳不穿墙、每个锚点落在地面、撞墙不穿透）。
19. **P3 语音（2026-11-03 实测，已跑通）**：`/mas3d/api/asr` + 按住说话 + 识别文本回填进输入框（**不自动发送**）。
    - 服务端：`apps/bridge/asr.py` 用 **openai-whisper**，不是 faster-whisper —— 面板环境已经装着 openai-whisper + torch（cu128）、`small`/`tiny` 权重都在缓存里，换库要多带一份 `ctranslate2` 却没有可测收益；理由写在 `apps/bridge/TECH.md` §2.1。模型 `small`，加载失败退 `tiny`，两者都失败才 503；`language="zh"`、`initial_prompt` 固定一句普通话提示；`import whisper` 放在 `_load` 里延迟执行，面板启动阶段不碰 torch/CUDA。
    - 端点：`POST /mas3d/api/asr`（`apps/bridge/web_api.py:273`），body 就是**裸 WAV 字节**（不看 `Content-Type`；`multipart/form-data` 会被 400 拒掉，因为开头不是 `RIFF`），成功 200 → `{"ok": true, "asr": {"text", "language", "durationMs", "model"}}`。两个上限：**8 MiB**（`Content-Length` 与读完 body 各校验一次，超限 413）、**30 秒**（超长 400）。只收 RIFF/WAVE + PCM + 单声道 + 16-bit，声明采样率 8 000–192 000 Hz，推理前线性重采样到 16 kHz 后按新长度再查一次时长。鉴权与其它端点同一份 `Bearer` 令牌。
    - **一把锁**：ASR 与 TTS 共用 `_voice_lock`，**ASR 的锁**归 worker 任务所有（`asyncio.shield`），调用方被取消或断开不会提前放锁（代价：被取消的那段仍跑完一次推理、继续占着 GPU）；TTS 侧仍是 `async with`，取消即放锁，属已知的债。等锁预算 `LOCK_WAIT_S = 15 s`，排不上号 → 503 `语音识别忙，稍后再试`；整体预算 `ASR_TIMEOUT_S = 45 s` 用完 → 504 `语音识别超时`；模型不可用 → 503 `语音识别不可用`。面板只显示一句统一的 `语音识别没有响应，先打字吧。`，401/413/503/504 不区分，具体原因进 `Player.log`；客户端超时 55 s（比桥接的 45 s 长，好让 504 先回来）。
    - Unity 侧：`Assets/Scripts/Voice/VoiceInput.cs` 按住 `V` 开始录、松开提交，`MinSeconds = 0.3` 以下的误触直接丢弃；状态词 `正在听…（松开 V 提交）` / `已填入，按 Enter 发送` / `没听清，再说一次？` / `语音识别没有响应，先打字吧。` / `没有可用的麦克风`（设备缺失时直接用录音器的原因）。识别结果只写进 `DialoguePanel` 的输入框，**发送仍由玩家按 Enter**。`BridgeSettings.Url` 不动：ASR 端点由 ws 地址推导（同 host/port，路径换成 `/mas3d/api/asr`）。
    - 接线：`CliPlayer.BuildScene` 给 Session 物体挂 `VoiceInput`、写 `Panel`（`BuildDialogue()` 建的那块面板）与 `boot.Voice`；`RoomBootstrap.Boot()` 末尾 `Voice.Bind(panel, new BridgeSettings { Token = DialogueBridge.ReadTokenFromDisk() })`，面板优先取 `Dialogue.Panel`。令牌文件缺失只 `Debug.LogWarning` 一次照常起，按住 V 时的失败由 `VoiceInput` 自己落到面板上——不能让没配令牌的机器连房间都进不去。
    - 收口修复（2026-11-04，Lead 亲跑）：①**录音采样率按 `AudioClip.frequency` 报**（`VoiceCapture.ReadMono`）——申请 16 kHz 不等于拿到，驱动给 44.1/48 kHz 时写错头会让短片段变调、48 kHz 的 12 秒被算成 36 秒直接 400；②`AsrClient.Abort()` 真的掐连接（只 `StopCoroutine` 会让桥接 worker 继续推理占着锁，下一次采集等满 15 s 拿 503），且被中止的请求一个回调都不发；③客户端超时 55 s（见上一条）；④面板在按住期间被销毁时 `Update` 兜底 `AbandonTake()` 关麦，而不是录到 30 秒上限；⑤按住期间按过 Enter 这件事记在 `_submittedThisTake`，松手不再清零，迟到转写不会落进刚清空的框；⑥`VoiceSceneTests` 改为等 `Voice.Recorder` / `Voice.Client` 非空并断言 `MicrophoneRecorder`、`/mas3d/api/asr` 端点与令牌一致——这两个字段是普通 C# 实例、Unity 不序列化，非空只可能来自 `Bind`，所以删掉 `RoomBootstrap` 的 Bind 会红。
    - 验收（2026-11-04 终态，Lead 亲跑）：`tools/unity.ps1 player` 重建场景 exit 0，且 `[player] actor seated on S1: hips=0.754 seat.y=0.614 gap=0.140` 仍在；`test -Mode PlayMode` **120 total / 119 passed / 0 failed / 1 skipped**（跳过仍是 `[Explicit] UiSceneShot`；比 P4 终态的 59 多出的 61 条是 P3 自带的 `WavEncoderTests` 17 + `VoiceInputTests` 26 + `AsrClientTests` 14 + `MicrophoneRecorderTests` 3 + `VoiceSceneTests` 1）；`test -Mode EditMode` **44/44**；`smoke` 一回合 `[smoke] turn ok turnId=990d1bb1-e619-444a-a9df-9f74df3ade21`。
    - 判别力（别把"全绿"当证据）：删掉采样率回退 → `ReportsTheClipsRealRateNotTheRequestedOne` / `AveragesChannelsDownToMono` 红（`Expected: 44100 But was: 16000`）；删掉 `Update` 的 `AbandonTake` → `ADestroyedPanelEndsTheTakeInsteadOfRecordingForever` 红（`Expected: False But was: True`）；把 `_superseded = _submittedThisTake` 改回 `false` → `ASubmitMadeDuringTheHoldKeepsTheLateTranscriptOut` 红（`Expected: <string.Empty> But was: "晚到的转写"`）；删掉 `Transcribe` 里的 `ReferenceEquals(_active, request)` 守卫 → 两条 abort 用例红；把 `RoomBootstrap` 的 Bind 断掉 → `TheSceneBindsVoiceToTheDialoguePanel` 红。
    - **先红后绿**：`VoiceSceneTests.TheSceneBindsVoiceToTheDialoguePanel` 先写、先跑红——`error CS1061: 'RoomBootstrap' does not contain a definition for 'Voice'`；编译错不进 `TestResults/PlayMode.xml`（那次 `TestResults/PlayMode.xml` 仍是旧文件），红文本当时存在 `apps/unity/Logs/fix5-red-scene.log`——该目录被 `apps/unity/.gitignore` 忽略，只能在开发机上复核，提交里看不到；后续同类红证请在报告里贴断言原文。
20. **P4 前置：角色接入的五层与判据**（2026-11-03 实测，`tools/unity.ps1 player|capture|test` 全绿）：她现在是 `Main.unity` 里的常驻物件（`Actors` 空物体下的 `Monkia`），不再由 P4 临时生成。
    - **五层分工**（每层只管一件事，改一层不必碰其他层）：`AvatarRig` 只认 VRM —— 骨、表情权重、包围盒，查不到就返回 null；`AvatarSeat` 只认座面 —— `Sit(SeatEntry)` 摆位、`HipsGap` 抬根、`Seated` / `HipsWorld`；`AvatarIdle` 只认时间 —— 呼吸 3.0 s / 1.1°、摆动 7.2 s / 0.8°、眨眼 4.2 s / 0.12 s，`CaptureBaseline()` 重取静息；`AvatarMood` 只认 `TurnPhase` —— 只写 `Relaxed` / `Happy` / `Sad` 三键，每次写过 `HasExpression`；`AvatarActor` 只做「四个组件装在一个物体上 + 一根胶囊 + 两个绑定」，自己不摆姿势。
      - 胶囊是替身不是网格：`radius 0.20` / `height 1.05`（比玩家的 0.30 窄，否则 S1 会顶进床沿）、`center = 髋部 + 0.20 m`、非触发器。玩家控制器要扫的是「这里坐了个人」，而蒙皮网格碰撞体会跟着呼吸每帧重建。
    - **S1 落座实测**：座面顶 y = 0.604、`seat.y = 0.614`、`HipsGap = 0.14` → 髋关节钉在 **0.754**（`[player] actor seated on S1: hips=0.754 seat.y=0.614 gap=0.140`）；模型臀面在髋关节下 0.141 m，于是臀面落在 **0.613**，正好贴住 0.604 的座面（用 0.05 会让臀部陷进座面 8 cm）。**脚不落地**（Lead 裁决 2026-11-03）：髋 0.754 减小腿 0.407 得踝 **0.255**、趾根 0.196、鞋底离地 **0.15–0.19 m**；要让脚掌触地只能把臀部埋进座面 11 cm，所以判据是「踝部自然下垂 + 不穿地板」，不是「脚踩地」。**坐姿下根节点 y ≈ −0.11**（0.754 − 站立髋高 0.866）：任何需要她身高的代码（胶囊 center、出生点判定、日志）必须读 `Seat.HipsWorld`，读 `transform.position` 会拿到地板下的数。
    - **表情只写三键**：`Relaxed`（`Idle` / `Sending` 0.35、`Thinking` 0.20）、`Happy` 0.65 / 2.5 s（`Answered`）、`Sad` 0.50 / 3.0 s（`Failed`），到点回落 `Relaxed`。`Blink` 与 `Aa` 一律不碰：眨眼归 `AvatarIdle`，口型将来归音频。**不为表情改 `ConversationController`**：它没有 `Phase` 事件，`AvatarMood.Update()` 每帧轮询 `Controller.Phase` 就够（一次比较 + 分支），加事件要动桥接侧状态机、还要给 `DialogueBridge` 兜底，收益为零；`Controller == null` 是合法的「永远不变脸」，赋值 null 必须是无操作。
    - **场景存站姿，坐下发生在 `Start`**：`AvatarSeat.Seated` 是运行期字段（无 `[SerializeField]`），存档救不回来；而 `AvatarPose` 把「第一次见到的姿势」记成静息，把坐姿存进场景会在载入后再叠一次坐姿表、把腿折两遍。所以 `CliPlayer.BuildScene` 建完先把 `Stand()` 到 A5 站着（`[player] actor parked at rest on A5 (-3.02, 2.57), seats herself on S1`），`AvatarActor.Start()` 再 `SitAt(_seatId)`。**放在 `Start` 而不是 `Awake`**：`AvatarRig` 也在自己的 `Awake` 里绑定 `Vrm10Instance`，同一物体上组件的 Awake 顺序不保证，坐得早了 `Bone(Hips)` 会返回 null。`Capture` 是编辑模式、没有 Awake/Start，所以显式调 `SitAt`；编辑模式也没有 Awake 来绑 rig，`AvatarRig.EnsureBound()` 就是给这类调用方的（不然日志里是 `[seat] rig has no Hips bone`，PlayMode 还会把它当未处理错误判整个用例失败）。
    - **第五张图 `Shots/avatar-seated.png`**：`[play] wrote ... avatar-seated.png fov=38 eye=(-2.30, 1.96, 1.08) look=(0.11, 0.98, -0.02)`。**偏离计划 5.5 的公式并记录原因**：`S1 − forward * 2.2` 在 S1（`facingY = −90°`，forward = −X）等于把相机放到她背后，第一次拍出来只有后脑勺和头发、髋部还被下沿裁掉。改成前方 3/4：`eye = S1 + forward * 2.4 + right * 1.1 + up * 1.35`、`lookAt = S1 + up * 0.37`、fov 38。人眼看图：坐在长凳上、臀在座面、大腿向前并拢、小腿垂下、鞋底离地 0.15–0.19 m、手臂垂在身侧且不压坐垫、与床和长凳都不穿模。
    - 验收（2026-11-03 终态，Lead 亲跑）：`test -Mode PlayMode` **59 total / 58 passed / 0 failed / 1 skipped**（跳过的是 `[Explicit] UiSceneShot`；`AvatarSceneTests` 7/7：坐在 S1 且髋高与 `seat.y + HipsGap` 差 ≤ 0.04、A2 出生点不被 `Monkia` 占（先做「她的胶囊真的在 S1 上」正对照，否则扫空也绿）、S1 正前方 0.7 m 的过道体积未被她的胶囊占用（体积断言，不测可达性）、站起后胶囊被移除且重坐再长出来、`Spawn` 建出整只 actor（四层 + 胶囊尺寸/中心）、`FitBody` 两次只留一个胶囊、无 rig 的 actor 不抛也不长胶囊），`test -Mode EditMode` **44/44**，`smoke` 桥接一回合仍绿。
    - **场景测试必须自己收尾**：`AvatarSceneTests` 用 `LoadSceneAsync(..., LoadSceneMode.Additive)` 进 `Main`、`finally` 里卸载（`finally` 不能 yield，所以卸载是异步发起、下一次加载先等它）。用 `Single` 加载又不管，她的胶囊和场景里的 Player 会留到后面的锚点/步行测试里 —— 一次就撞出 4 个假失败（`anchor A2 overlaps Player`、`seat S1 overlaps Monkia`）。
    - **场景里是解包副本，不是 prefab 实例**：`Main.unity` 的 `Monkia` GameObject 是 `m_PrefabInstance: {fileID: 0}`（`Main.unity:8587`，Transform 同 `:8611`），整个头像被内联进场景（该文件因此多了 1.4 万行，15,372 行 vs 上一版 2,078），另有 90 处直接引用 `Assets/_Local/avatars/Monkia.vrm` 的 `guid: 790ccffb…`（贴图、三个 mesh、`Vrm` 数据、Humanoid `m_Avatar`）。原因是 VRM 由 ScriptedImporter 产出、不是 Prefab，`Object.Instantiate` 只能得到解包副本。代价：**没有该资产时场景里的她只剩骨骼与内联组件，mesh/材质/贴图全丢**，且每次 `tools/unity.ps1 player` 重建都会整只重烤（改模型必须重建整个场景）。而 `apps/unity/.gitignore` 忽略 `/Assets/_Local/`（17 MB 的 VRoid 模型不再分发）。所以**每个依赖她的夹具**（`AvatarSceneTests` 6 处、`AvatarRigTests`、`AvatarSeatTests`、`AvatarIdleTests`、`AvatarMoodTests`）不再各写一份 `File.Exists`，而是在加载 prefab 前统一走 `AvatarPrefabFixture.Require()`（`PrefabPath` 常量 + 用 `Application.dataPath` 推工程根 + `File.Exists`，缺件就 `Assert.Ignore("缺 Assets/_Local/avatars/Monkia.vrm…")`）：干净 clone 上只跳过，不会把没模型的机器判成坏代码。实测（2026-11-03）：有模型 `test -Mode PlayMode` **59 total / 58 passed / 0 failed / 1 skipped**（跳过的仍是 `[Explicit] UiSceneShot`）；把 `Monkia.vrm` 与 `Monkia.vrm.meta` 一起移出 `Assets/_Local/avatars/` 后 **59 total / 30 passed / 0 failed / 29 skipped**（22 个 avatar 用例 + 6 个场景用例 + 1 个 `[Explicit]`），`result="Failed"` 一条都没有。
    - **曝光配平（2026-11-03 已修）**：出生视角（`play-01`）曾把她渲染成过曝的高饱和色（上衣发黄、袜子发洋红），其她四机位正常。根因不是灯光参数，而是**两套曝光**：房间表面走 `Mas3D/RoomSurface`（以及 `RoomMirror`），自带 `_RoomExposure`（`2^-1.2 = 0.435`）与 ACES 曲线；VRM 头像走 MToon，对这两个全局量一无所知。同一盏灯下她比周围亮 `1/0.435 ≈ 2.3` 倍，正面受光即削顶，暖光（`0xffb877` / `0xffdcb8`）下被削成黄与洋红。修法是 `RoomLook.LightScale = 0.5`：`RoomDress.CreateLights` 把每盏灯（moon / lamp / 两盏 ceiling-glow / floor-bounce / bounce）与 Trilight 环境光都乘上它，`RoomLook.Exposure` 反过来把 `_RoomExposure` 除以它（`0.435 / 0.5 = 0.87`），所以房间的乘积逐项不变、头像降到房间的水平。
      实测核对（1280×720，同一帧内取样）：墙角 `(150,200)` 修正前 `[129,93,113]` → 修正后 `[128,93,113]`，右墙 `(1050,200)` `[171,163,175] → [170,163,175]`，地板 `[140,86,88] → [139,85,87]`，即房间在 1/255 量化噪声内没变；她身上原本削顶的像素从 `[252,234,0]`（纯黄）回到 `[127,106,123]`。`avatar-seated` 与 `play-02/03/04` 的颜色仍与验收版一致。
      新增诊断开关 `-diagLights 1`（`CliPlayer.Capture`）：按灯组（moon / lamp / glow / floor / bounce）逐个关掉后各渲染一张 `Shots/diag-off-<group>.png`，并用 `[play] lights` 打印每盏灯的名字与强度，用于把某一路偏色定位到具体灯。定位过程留下的反例：关掉 moon 后她整张脸变成 `[0,86,84]`（只剩 bounce 的蓝），说明主光确实是关键光、不是多余的补光。
    - **已知观感（非缺陷）**：出生视角里她的躯干偏灰，是因为主光（moon）从窗户一侧来、她正对摄像机的那面本来就背光，MToon 落在 shade 色上；换机位（`avatar-seated`）同一套参数下颜色正常。这是物理一致的结果，不再用提高亮度掩盖。
    - **已知债（未修）**：房间资产 `Assets/_Local/room/mita-room.glb` 同样被 ignore，而 `PlayerWalkTests`、`RoomAnchorTests`、`AvatarSeatTests.BuildRoom` 没有等价的守卫，缺 room 时它们会硬红；真正「什么都没有的干净 clone」不是全绿，只有「有 room、缺 VRM」才是 59/30/0/29 那种形态。

### 5.20 P4 四件动作（2026-11-04 实测）

- **五个 id**（`Assets/Scripts/Actions/ActionId.cs`，`All` 里就这五个）：`hands` 双手牵手、`pat-her` 我摸她头、`pat-me` 她摸我头、`kiss` 亲吻、`sit-together` 并肩坐床。入口是纯函数 `ActionDirector.TryEnterAction(string id)`，只回 `bool`，拒绝原因落在 `LastFaults`（"需要蹲下" / "需要跪下" / "她够不到" / "已经有个动作在演"）；出口只有一个 `Cancel()`，Esc 与 `OnDisable` 都走它。场景侧由 `RoomBootstrap.Boot()` 末尾建 `Actions` 物体（挂在 bootstrap 自己下面，随场景卸载）并把 `Actor / Player / Camera(Rig)` 三个引用接上，`Boot()` 不替玩家定姿势也不改位置——姿势由 `TryEnterAction` 解出的 plan 决定。`ActionSceneTests` 三例钉这条接线：`TheSceneCarriesADirectorReadyToPerform`、`EnteringAnActionFromTheSceneMovesThePlayerToTheMark`、`CancellingFromTheSceneReturnsThePlayerToTheSpawn`。
- **判据**（`ContactError` 是每帧实测的代理点残差，`Held` 只在 `ContactError <= ContactTolerance (0.02 m)` 时累加；门槛集中在 `ActionDirector` 的常量区，测试直接读它们，不各写一份魔数）：
  - `hands`：玩家站立、站在她座心正前方 `HandsOffset = 0.62` m；两掌到她掌心 ≤ 0.02 m，`Held` ≥ 3.0 s，后 1 s 掌心位置方差 ≤ (1 mm)²（呼吸不许把手抖出去），`Cancel` 后左肩回 `1°` 内。出图那次 90 帧里 gap = 0.000。
  - `pat-her`：她低头把头顶送到玩家抬起的掌心（`PlayerReach.Raised(false)`），残差 ≤ 0.03 m；头-颈夹角 > 20°，头骨相对入场 ≥ 3° 的受力反馈；`Cancel` 后头回 `1°` 内。
  - `pat-me`：玩家必须蹲（站立时 `LastFaults = "需要蹲下"`）；她的手到玩家头顶 ≤ 0.03 m，左肩不许跟着动。
  - `kiss`：玩家必须跪（站立/蹲都拒，"需要跪下"）；头心距 ≤ 0.06 m、面部朝向偏差 ≤ 15°、`Held` ≥ 2.0 s、`AvatarIdle.EyesClosed` 为真（`Blink` 权重 1.0）；`Cancel` 后头回 `2°` 内且闭眼关回。实测 gap 0.0134 m、align 3.4°。
  - `sit-together`：两人髋的水平距 ≥ 0.40 m（实测 0.70）；六根腿骨 y ≥ `topY − 0.01`；髋高 = `seat.topY + HipsGap (0.14)` ± 0.01；玩家眼 = 床面 + 0.50 ± 0.02；朝向 ± 2°。
- **演出态锁**：进场一次性做完，顺序固定——快照 `_wasSpot / _wasYaw / _wasPosture / _wasKeyboardInput / 相机 yaw,pitch` → `Player.KeyboardInput = false`（人走不动）→ `SetPosture(plan.Posture, plan.SeatTopY)` → `Teleport(plan.Spot, plan.Yaw)` → `Camera.SetOrbit(...)` → `ActionPose.Begin(Rig)`。`Cancel()` 逆序还回去（先还键盘，再关闭眼，再 `ClearPose`，最后姿势/位置/相机），`try/finally` 保证 `_current / _held / _contactError` 一定归零。`Update` 走 `Tick(Mathf.Min(deltaTime, MaxUpdateStep = 0.1))`：卡一帧不许把动作跳过去；测试关掉 `DriveInUpdate` 手动喂 `1/60`。锁只锁轨道与移动；唯一改玩法相机参数的例外是亲吻的取景（见下），`Cancel` 会把它一并还原，之后玩家能立刻继续走。
- **动作层分工**（一层一个职责，改一层不用碰别的）：
  - `ActionDirector`：状态机、`ResolvePlan`（座位 → 站位、朝向、姿势、相机）、每帧 `Drive*`；
  - `ActionPose`：骨层，`Begin` 快照 / `Apply` 表 / `Restore` 逆序还原 / `Forget`，只写 `Writable` 名单里的骨（Hips/Spine/Chest/Neck/Head/双臂），腿归 `AvatarPose`（所以 `sit-together` 的腿表和它不打架）；
  - `TwoBoneIk`：解双臂，`DriveArm` 先解 wrist = target − approach × 0.08 再转手骨；
  - `ActionGeometry`：量她那侧的接触点（`HerPalm / HerHeadTop / HerHeadCenter`）；
  - `ActionSeats.BedPair(seat, 0.70)`：床上第二个座位槽，沿座位右手边复制 `y / topY / facingY / mesh`，id = `S2-pair`（实测 x 2.441，在床 AABB x 0.353…3.129 内）；
  - `PlayerReach`：量玩家那侧的代理点。
- **`PlayerReach` 的数值**（玩家没有骨骼：一根胶囊 + 一个 `Head`，掌心/头顶/脸只能从头推）：`Palm = head + forward × 0.24 + right × (±0.22) + up × (−0.62)`；`Raised = head + forward × 0.30 + right × (±0.12) + up × (−0.32)`；`Crown = head + up × 0.16`；`Face = head + body.forward × 0.10 − body.up × 0.04`（亲吻用，比眼低 4 cm，两张脸才不至于在眉心相撞）。0.24/0.22 是出图之后改的：旧的 0.35/0.16 在 0.62 m 站位下把两掌放到她肚子上、前臂合拢成抱拳，改后接触点离座心 0.38 m、两手相距 0.44 m，两臂才前伸张开；`PalmDrop 0.62` 不许动，`PlayerPostureTests` 钉着 `eye − 0.62`。
- **两条已知限制**（布局事实，不是缺陷）：
  1. `S2` 是床面中线槽（x 1.741 / z −0.025 / topY 0.906），不是床沿——`anchors.json` 的生成器只给了这一个床锚点，所以「并肩坐床」是坐床面。要坐床沿得先按 §5.15 补一个量过穿模的槽，不在本阶段。
  2. `S2` 的 note 写明床顶是羽绒被：`topY 0.906` 是臀落被面的高度，不是床垫高度。所有判据（髋高、腿骨下限、玩家眼高）都相对这个数，换被子要重测。
- **亲吻的取景（2026-11-04 修）**：其他动作的摇臂放在玩家自己的视线上，0.62 m 的站位刚好容得下 0.20 m 的避障球；kiss 只隔 0.31 m，她的胶囊表面离他的头 0.115 m，避障球一开始就压在她身上，Cinemachine 把摇臂压到 **0.085 m**——整幅头发（cam `(-0.29,1.28,-0.04)`、pivot `(-0.21,1.28,-0.02)`、`obstacle=Monkia`）。所以 kiss 单独换一套取景：`KissOrbitYaw = 90`（相对玩家朝向转四分之一圈，摇臂横过房间，肩部偏置朝她反方向，画面变成两人侧面）、`KissCameraRadius = 0.06`（那个距离上墙都在几米外，没有要躲的东西）、`KissCameraDistance = 1.9`（2.8 m 会把两人拍成房间里的小岛）。`Cancel` 还回 0.20 / 原距离。`ActionKissTests.TheKissFramesItsCloseUpInsteadOfSittingInHerHair` 钉死这三个数（写的是字面量而不是常量本身，否则把常量调回 0.20 照样全绿——实测过：三项一起改回旧值时唯一红的就是它，`Expected: 0.06 But was: 0.2`）。
- **拍照不等于玩法**（`-actions 1`，`CliPlayer.CaptureActions`）：五张 `Shots/action-<id>.png` 是判据之外的目视证据，`hands / pat-her / kiss` 另存一张 `-gamecam.png` 记游戏机位；`[act]` 行同时打出 `dist / rigYaw / obstacle`，相机被挤扁时能立刻看出是被谁挤的。批处理没有 player loop，所以出图要显式 `actions.Tick(1/60) × 90` 并同步 `Actor.Idle.Tick`；改完骨还要 `RefreshSkin`（重开 SkinnedMeshRenderer，批处理的蒙皮调色板缓存停在开场景那一刻）与 `PushLids`（直接写 `Fcl_EYE_Close*` 的 blend shape，VRM10 的表情权重是在它自己的 Update 里推的）。玩家没有身体网格：任何一张图里都看不到玩家的手和脸，「两张脸并排」只能在游戏里看到——**代理点判据不等于好看，好看靠这五张图**。
- **整支评审收口（2026-11-04）**：评审结论 Spec MATCHES / Quality CONCERNS / READY WITH CONDITIONS，五个条件全部修完——
  1. **演出期间相机被玩家鼠标抢走**：`PlayerCameraRig.Update` 只要 `MouseInput` 为真且 `Output` 非空就 `AddOrbit` + 滚轮改 `Distance`，演出中动一下鼠标，kiss 的 0.06 m 避障半径就被打回 0.20 m。旧测试抓不到是因为夹具从不 `Build()`（`Output` 为 null 直接 return）。现在进场快照 `_wasMouseInput / _wasCameraRadius` 并 `MouseInput = false`，`Cancel` 按快照还原（半径不再硬写 0.20）。
  2. **床姿失败是静默的**：`AvatarSeat.SitOnBed` / `AvatarActor.SitOnBed` 改返回 bool，`ActionDirector` 在动玩家之前 `Refuse("sit-together: she could not sit on the bed")`；`ABedSheCannotSitOnIsRefusedNotHalfEntered` 用「拆掉 `AvatarSeat` 组件」钉住拒绝路径（玩家不位移、不转、键盘不锁）。
  3. **`Main.unity` 的 8500 行纯重序列化**：评审按 YAML 块逐项比对确认零语义（672/672 文档、215 个 transform 签名、195 组 MonoBehaviour 参数、`m_Script` guid 多重集全同）。场景回退到 HEAD 版并撤出暂存，`.gitattributes` 加 `*.unity text eol=lf`；`tools/unity.ps1 player` 的 fileID 噪声是独立问题，另起 chore 处理。
  4. **`-actions 1` 被拒仍退 0**：`CaptureActions` 返回拒绝数，进循环前清掉旧的 `action-*.png` 并打 `[act] cleared N stale action shots`，`Capture` 以 `exit 2` 报告拒绝。
  5. **`SceneFlow` 卸载 fire-and-forget**：`UnloadMainAsync` 的句柄存进静态字段，`LoadMain` 先等它结束，避免上一支场景的卸载和新场景的加载交错。
  另修 Minor：床对槽断言改成字面量 `seat.x + 0.70`（原先拿 `BedPair` 自比较，0.70 改 0.45 也绿）、`ActionPoses.Hands` 删掉两个被 IK 整根覆盖的 `UpperArm` 键、进场失败路径 `RestoreEntering` 回滚、`PlannedPoint` 改用 `Player.EyeHeight`、`ActionDirectorTests` 真建一台相机（否则鼠标通道永不被走过）、`ActionSceneTests` 的等待加上限、`AvatarIdle.OnDisable` 释放玩家。
- 验收（2026-11-04 终态）：`test -Mode PlayMode` **163 total / 162 passed / 0 failed / 1 skipped**（跳过的是 `[Explicit] BridgeSmokeTests.UiSceneShot`），`test -Mode EditMode` **44/44**，`-actions 1` exit 0 且五张动作图重出，`smoke` 一回合 `[smoke] turn ok turnId=ed3088c9-f231-441e-af29-3a26bb66d466`。收口轮**没有**再跑 `player`（它会把场景重新序列化一遍）；`player` 的上一次验证在 Task 8。
  判别力（变异，均已还原）：同时删掉 `Camera.MouseInput = false` 与席位检查的返回值 → `163 total / 160 passed / 2 failed`，红的正是 `TheKissFramesItsCloseUpInsteadOfSittingInHerHair` 与 `ABedSheCannotSitOnIsRefusedNotHalfEntered`。

### 5.21 P5 回归（2026-11-04 实测）

- **两条命令的分工**：`tools/unity.ps1 smoke` 只跑选定的两条链路（两次 Unity 批处理，串行）；`tools/check.ps1` 是六步全回归（web → bridge → unity-edit → unity-play → smoke → player），第五步就是前者（这一节写于 P5，第六门 `player` 是 P6 加的，见 §5.22）。日常改动用 `smoke`（约 3 分钟），交付前跑 `check.ps1`（约 9–12 分钟）。
- **`smoke` 的两个阶段**（各自一次 `unity test --mode PlayMode --filter <用例全名>`，各留一份 XML 与 marker）：

| 阶段 | 用例 | marker 文件 | marker 必须有的行 |
| --- | --- | --- | --- |
| 桥接 | `Mas3D.Tests.PlayMode.BridgeSmokeTests.OneTurnAgainstTheRealMockBridge` | `apps/unity/Logs/smoke-marker.txt` | `[smoke] turn ok`（同一行带 `turnId=`） |
| 动作 | `Mas3D.Tests.ActionSmokeTests.EveryActionPlaysOnceThroughTheScene` | `apps/unity/Logs/smoke-actions-marker.txt` | `[smoke] action <id>` 五行，id 就是 `ActionId.All` 那五个 |

- **动作冒烟测的是接线与复位，不是几何精度**：五件动作各演 `1.5 s`（`Step = 1/60`、`StepsPerAction = 90`），每件只断言 `Held ≥ MinHeld (1.4)`、`ContactError ≤ ContactTolerance`、marker 五行齐全，另外三条「确实演了」的判别（进场后玩家离出生点 > 0.05 m、四个手臂动作演完她右手位移 > 0.03 m、kiss 的 `Chest` 局部旋转变化 > 5°），然后 `Cancel()` 并确认 `Current == null`、键盘锁还回去、座位回到 S1（髋位水平距离 < 0.02 m）。掌心残差 `≤ 0.02 m`、`Held ≥ 3.0 s`、腿骨下限、髋高这些几何判据在 `Action*Tests` 里逐条钉——冒烟只证明「五件动作都能从场景入口进、都能干净退出」。marker 每行都带当轮的 `held` 与 `gap` 实测值，文件是 `apps/unity/Logs/smoke-actions-marker.txt`，**每次跑都会重写，以当轮文件为准**（`gap` 随呼吸相位在 0.000–0.016 m 之间浮动）。这两个数只记录、不当几何判据——冒烟要证明的是「进得去、退得干净」，几何精度由 `Action*Tests` 逐条钉。
- **判据不信任 CLI 退出码**：Unity CLI 对不存在的 `--filter` 不报错——实测（Task 2 的红证，filter 写成 `NoSuchCase`）退出码 0、XML 是 `testcasecount="0" result="Passed" total="0" failed="0"`，即「通过，但一个用例都没跑」。所以 `smoke` 每个阶段额外查三件事：XML 里没有 `result="Failed"` / `failed="[1-9]`、`total` 不是 0（空跑守卫）、marker 先删后跑且要求的行逐条命中；任一不满足即 `exit 1`，两条链路互不短路（第一条坏了第二条照跑）。`check.ps1` 反过来只管子进程退出码、不 grep 日志：上面三件事已经在被调脚本里判过，日志只留给人看。
- **五步的真实数字**（收口前的最终跑，`apps/unity/Logs/check-lead-full-p5.log`，`summary: 5/5 ok`；bridge 用 `-Python` 指向本机解释器）：

| 步骤 | 结果 | 数字 | 耗时 | 证据 |
| --- | --- | --- | --- | --- |
| `web` | PASS exit=0 | 190 tests / 21 files（`packages/protocol` 51 + `packages/motion` 9 + `apps/web` 130，三份 `Tests N passed` 都绿） | 11.2 s | `check-web.log` |
| `bridge` | PASS exit=0 | 169 passed | 11.1 s | `check-bridge.log`（只有 169 个点，见下） |
| `unity-edit` | PASS exit=0 | **44 total / 44 passed / 0 failed / 0 skipped** | 147.3 s | `TestResults/EditMode.xml` |
| `unity-play` | PASS exit=0 | **164 total / 163 passed / 0 failed / 1 skipped**（跳过的是 `[Explicit] BridgeSmokeTests.UiSceneShot`） | 174.6 s | `TestResults/PlayMode.xml` |
| `smoke` | PASS exit=0 | 桥接 1/1 + 动作 1/1，marker 五行齐全 | 184.6 s | `check-smoke.log` |

- **四个 `-Skip*` 与 SKIP 语义**：`-SkipWeb` / `-SkipBridge` / `-SkipUnity` / `-SkipSmoke`（`-SkipUnity` 只跳两条 Unity 测试，smoke 有自己的开关），另有 `-Python <path>` 与 `-Timeout <秒>`。SKIP 既不算通过也不进分母，末尾多一行 `note: skipped …`；实测 `-SkipWeb -SkipUnity -SkipSmoke` 一轮是 `note: skipped web, unity-edit, unity-play, smoke (not counted as passes)` + `summary: 1/1 ok (4 skipped)`、EXIT=0（`apps/unity/Logs/check-lead-skip-combo.log`；`note` 在 `summary` 之前，末行恒为 `summary`）。
- **耗时**：完整一轮约 **9 分钟**，Unity 三段占 500 s 上下。另一轮（`apps/unity/Logs/check-lead-full.log`，bridge 因没设 `MAS3D_PYTHON` 而 SKIP）是 `summary: 4/4 ok (1 skipped)`：web 11.5 s / unity-edit 147.4 s / unity-play 176.7 s / smoke 183.8 s。**跑前必须关掉编辑器**：Unity 单实例，撞锁只会让 Unity 段以非 0 退出码 FAIL，不会去读上一轮的旧 XML 假装全绿。
- **两个已知摩擦**（都不影响判据）：
  1. `apps/bridge/pytest.ini` 的 `addopts = -q` 叠加命令里显式写的 `-q` 成了双 quiet：`apps/unity/Logs/check-bridge.log` 里只有 169 个点和 `[100%]`，**没有「169 passed」摘要行**。想看摘要就临时去掉 `-q` 跑同一命令（`apps/unity/Logs/check-bridge-verbose.log`，实测 `169 passed in 9.97s`）；判红仍然只看退出码。
  2. bridge 用哪个解释器由 `$env:MAS3D_PYTHON` 传入（`-Python` 优先，其次环境变量，最后 PATH 里的 `python`，每个候选先 `-c "import sys"` 实跑验证）；都没配好就打 `bridge SKIP (no python; set MAS3D_PYTHON)` 并进 `note:` 行，**不算失败**。所以读 `summary: 5/5 ok` 之前先看分母——有 SKIP 时会变成 `3/3`、`1/1` 甚至 `0/0`。

### 5.22 P6 出包（2026-11-05 实测）

- **一条命令出包**：`powershell -ExecutionPolicy Bypass -File tools/unity.ps1 build`。`-Out` 默认 `build/Mas3D.exe`（相对仓库根），`-Target` 只接受 `StandaloneWindows64`（别的值直接 throw，`CliBuild` 里的设置本来就是 Windows-only），`-dev` 透传开发版，`-Timeout` 照旧管这一批。入口是 `apps/unity/Assets/Editor/CliBuild.cs` 的 `Mas3D.Editor.CliBuild.Build()`——`unity build` 子命令不收 `-executeMethod`，所以走 `run -- -executeMethod`。脚本跑完自己 `Test-Path` 两个产物（exe 与 `<name>_Data/il2cpp_data`），缺任一条打印 `build arm: player artifacts missing: …` 并 `exit 1`：退出码和产物是两道独立的门，编辑器说成功不算数。
- **产物**（2026-11-05 首建）：`build/Mas3D.exe` 667,136 B；`build/Mas3D_Data/` 186,630,142 B / 19 个文件；整包（不含 `Mas3D_BackUpThisFolder_ButDontShipItWithYourGame`）28 个文件 **259.4 MB**。大件：`GameAssembly.dll` 37,518,848、`UnityPlayer.dll` 38,724,016、`sharedassets0.assets` 138,500,252。`Mas3D_Data/app.info` 是两行 `Mas3D`（productName / companyName）。
- **IL2CPP 的证据**：`GameAssembly.dll` 在、`MonoBleedingEdge/` 不在、`Mas3D_Data/il2cpp_data` 存在（4 个文件 11,315,062 B）。旧 Mono 包正好相反：`_Data/Managed/` 里 30+ 个 dll（含 `mscorlib.dll` 4,632,064），`il2cpp_data` 不存在。
- **PlayerSettings 由 CliBuild 写**（不是手改 `.asset`）：`productName` / `companyName` = `Mas3D`，1280×720 窗口、`resizableWindow`、`runInBackground`，`scriptingBackend` 的 `Standalone: 1`（IL2CPP）、`managedStrippingLevel` 的 `Standalone: 1`（Low）、`il2cppCompilerConfiguration` 的 `Standalone: 1`（Release），随后 `AssetDatabase.SaveAssets()`。`ProjectSettings.asset` 的就是 `git diff --cached --numstat` 里的 13 增 10 删（含 `fullscreenMode` 1→3、`defaultScreenWidth/Height` 1280/720、`m_ActiveColorSpace` 不动）；`apiCompatibilityLevelPerPlatform` 保持 `{}`——Unity 6 默认 .NET Standard 2.1，枚举里已经没有 `NET_Standard_2_1` 这个名字。
- **耗时**：首建墙钟 **3 分 54 秒**（19:26:35 → 19:30:29），其中 CliBuild 自己记 `elapsed=184.7`（编辑器启动 + 脚本重编译约 50 s）；不动脚本的复跑增量 `elapsed=11.5`。所以「改一行 C# 再看包」是一分钟级的事，只有第一次和清缓存后才是四分钟。
- **换机的第一条前提**：IL2CPP 模块不在编辑器默认安装里，缺了构建只报 `Error building Player: Currently selected scripting backend (IL2CPP) is not installed.`。装它：`unity install-modules -e 6000.6.7f1 -m windows-il2cpp --accept-eula -y --no-elevate --retries 2`。本机实测下载 258.93 MB、落地 1.04 GB（日志 `apps/unity/Logs/p6-install-il2cpp.log`）。这是**机器级**改动、不在仓库里，换机器与 CI 都要重跑；注意 `WindowsStandaloneSupport/Variations/il2cpp` 只是编辑器侧引擎 DLL，不代表 player 变体已装。
- **输出目录不许跨后端复用**：同一个 `-Out` 目录里放过 Mono 包，再切 IL2CPP 会 `BuildFailedException: Build path contains a project previously built with the Mono2x scripting backend`。脚本不自动清目录（怕误删），换后端时先换目录或手工清空。
- **启动顺序（P6 的核心承诺）**：先让 AstrBot 起来（插件自起 `127.0.0.1:8790`），再启动 exe。反了不会崩、也不会黑屏：提示条走失败态 `连不上桥接 127.0.0.1:8790（第 n 次，x s 后重试）· 先启动 AstrBot，确认 bridge 正在监听`（`ConnectionBanner.cs:28`），后台按 `0.5 / 1 / 2 / 4 / 8` s 退避重连（`BridgeSettings.BackoffSeconds`，用完一直按 8 s），桥接一起来就自动连上、`attempt` 归零。这是纯重连逻辑，不需要重启 exe。
- **打包件的令牌（双击就能连，P6 Task 5 修的缺陷）**：`DialogueBridge` 按 `BridgeSettings.RuntimeTokenCandidates()` 的顺序逐个试，**取第一个非空**，成功时打 `[dialogue] token <- <路径>`；`-token` 给的仍然最高优先（来源记成 `-token`）。顺序由一个纯函数 `BridgeSettings.TokenCandidates(exeDir, repoRoot, persistentDir)` 决定，EditMode 有 5 条测试钉住：
  1. `<仓库>/apps/bridge/data/mas3d/token`——编辑器里，以及 exe 就在仓库 `build/` 里时（player 的仓库根 = **exe 所在目录的上一级**，不再是 `Application.dataPath` 往上三层）；
  2. `<exe 目录>/mas3d-token`——免安装整包拷贝；
  3. `<persistentDataPath>/token`——装到仓库外时用户自己放一份。
  修之前只认第一条，而且仓库根用 `Application.dataPath` 往上三层推：编辑器里 `apps/unity/Assets` → 仓库根（对），打包件里 `build/Mas3D_Data` → 盘符根（错），所以双击 `build\Mas3D.exe` 找不到令牌（`player-run.log` 里 `[voice] 没有令牌（D:\apps\bridge\data\mas3d\token）`、marker 里没有 `token src=`）。线上红证：`task5-red-1.log` 里 `player FAIL (online) exit=3 45.6s` + `bridge timeout elapsed=45.0`——不传 `-token` 时客户端根本不建。
  一个候选都没有时不建客户端，状态行「桥接未配置令牌」，警告里列出**试过的全部路径**；房间、走动、动作、离线兜底都照常，断的只有桥接。
- **三个启动参数**（`Scripts/Boot/BootArgs.cs`；编辑器和打包件都认，其余参数一律忽略，`-batchmode` / `-logFile` 这类 Unity 自己的参数认不出来不算错）：
  - `-bridge host:port` 或 `-bridge port`：只给端口时主机是 `127.0.0.1`；端口非法只打一条警告并回落 `127.0.0.1:8790`，不把「地址写错」伪装成「连上了但没反应」。
  - `-token <str>`：临时令牌，优先于磁盘那份（日志只打字节数，不打内容）。
  - `-selftest <path>`：自检模式的落盘路径。只有**命令行带这个参数**才挂 `PlayerSelfTest`（`RoomBootstrap.cs:133` 只看参数非空），所以打包件双击、编辑器日常运行、既有测试都不受影响；但编辑器里真去带 `-selftest` 会走到 `PlayerSelfTest.cs:280-281` 的兜底 `Application.Quit` / `Environment.Exit`，可能直接结束编辑器进程——那是给打包件用的开关，别在编辑器里用。
  启动时 `RoomBootstrap` 打一行 `[boot] … bridge=… token=disk|set`，排查先看它。
- **玩家包自检**（`tools/player-smoke.ps1`，跑的是 `build/Mas3D.exe`，不是编辑器）：
  - 在线（默认）：脚本从 8791 起找一个空闲端口，自起 `node tools/mock-bridge.mjs --port <p> --scenario happy`，再用 `-bridge 127.0.0.1:<p> -selftest <marker>` 起 exe——**不传 `-token`**，走的正是双击那条路；判据 = 退出码 0 + marker **五行**（`token src=<路径> len=64` / `bridge ok` / `reply ok len=<n>` / `action hands held=<x.xx> ok` / `done`）。实测 **exit=0 / 2.5 s**，marker 第一行 `token src=D:\Mas3D\apps\bridge\data\mas3d\token len=64`。`-WithToken` 保留一条覆盖 `-token` 的用例（判据换成 `token src=-token`），默认关。
  - 短回复：`-Reply "嗯。"` 透传成 `mock-bridge.mjs --reply`，用来钉住「回没回」不靠面板变长——面板从 12 字占位文案变成 `你好\n嗯。`（5 字，比占位文案还短）也算回答。实测 **exit=0 / 2.4 s / `reply ok len=5`**。
  - `-Offline`：不给桥接，判据 = 退出码 3 + `bridge timeout elapsed=<s>`。实测 **exit=3 / 45.6 s**（`elapsed=45.0`）。这条比在线那条更值钱：它证明连不上时会在 45 s 内自己退出，不会把全回归拖死。
  - 其它：`-Exe`（默认 `build/Mas3D.exe`）、`-Timeout 150`、`-Reply <text>`、`-WithToken`；结果码 `0 PASS / 1 FAIL / 2 SKIP`（缺 exe，或在线模式缺 node）；`Process.Start` 抛异常时也打一行 `player FAIL (launch: …)` 再 `exit 1`，不会无声炸掉。从属日志 `player-run.log`（exe 自己）与 `player-mock.log`（mock）。
  - **退出码只有一条路能给**：打包 player 里 `Application.Quit(code)` 根本不让进程退出，单独 `Environment.Exit(code)` 也不退（实测 45 s / 90 s 仍活着）；`PlayerSelfTest` 因此走 kernel32 `ExitProcess(code)`，3/4/5 才真交到 OS 手里。改这段之前先读 `tools/README.md` 那两条实测。
- **check 的第六门**：`tools/check.ps1` 现在是六道门 `web → bridge → unity-edit → unity-play → smoke → player`，`player` 门调 `player-smoke.ps1`（日志 `check-player.log`），只按子进程退出码判；`-SkipPlayer` 跳过，缺 `build/Mas3D.exe` 或子进程给 2 都记 SKIP（进 `note: skipped …`，不进分母，不伪装成通过）。实测单开 player 门 `player PASS exit=0 6.7s` + `summary: 1/1 ok (5 skipped)`；门序 web+player `2/2 ok (4 skipped)`。完整一轮因此从约 9 分钟涨到 9–12 分钟。
- **已知限制**：`-Target` 只有 Windows 一条腿（`CliBuild` 硬编码 `StandaloneWindows64` 与 `SetScriptingBackend(Standalone, IL2CPP)`）；产物里带 `Mas3D_BackUpThisFolder_ButDontShipItWithYourGame` 符号备份，发版别带；输出目录跨后端复用会炸（见上）。

### 5.23 P7 语音 · 走位 · 玩家身体（2026-11-06 实测）

- **一次只发一句**：`POST /mas3d/api/voice/synth`，body 是 `{"text":"<当前这一句>"}`，**不带 `sentence` 下标**。桥接先中译英、再按**英文句**切分取下标（`apps/bridge/tts.py:363-368`），中英句数对不齐：早期实现送中文句下标，句数少时尾部不播、句数多时拿到 400 被当成「播完了」静默收尾（`UnityWebRequestTtsTransport.cs:156-162` 的注释记的就是这次教训）。
- **音频与令牌**：拿到 `audioUrl` 再 `GET`；**只有同源（scheme/host/port 全同）才带 `Authorization: Bearer`**（`UnityWebRequestTtsTransport.cs:115-142`），绝对 CDN 地址裸取——不把桥接令牌发给别人。桥接侧另认 `?token=`（`apps/bridge/web_api.py:112`）。
- **交出去的音频是传输层自己拷的一份**：`DownloadHandlerAudioClip.GetContent` 给的 clip 归下载 handler 所有，`using` 一结束 request 就被销毁，Unity 没承诺 clip 还活着。`CopyClip`（`:79-89`）按 `samples × channels` 拷 float 样本另建 `AudioClip`（名字带 `-owned`），拷不动（压缩格式）才退回原件。用 `file://` 小 WAV 走真 `DownloadHandlerAudioClip` 的 PlayMode 用例 `TtsTransportTests.TheClipHandedUpIsAnOwnedCopyThatOutlivesTheRequest` 钉住这条。
- **字幕按句推进**：`TtsSentences.Split` 按 `。！？.!?` 切句，单句上限 `MaxSentenceChars = 200`（`TtsSentences.cs:18`），硬切时走 `SafeCut`：切点落在代理对中间就把整个字符留给下一句（emoji 不会被劈成两个乱码），纯标点片段丢掉；一句一个 `durationMs`，最多 `MaxSentences = 40` 句（`ReplySpeaker.cs:26`），面板只留最近 `MaxSubtitleLines = 6` 句（`:35`），更早的折成首行「…」（`:255`）。
- **失败一律降级**：任何非 2xx（含 400）都 `TtsReply.Unavailable` → `Degrade`（`UnityWebRequestTtsTransport.cs:163-165`、`ReplySpeaker.cs:170-174`），文案 `ReplySpeaker.FallbackCopy = "语音暂时不可用，先只看字幕。"`（`:29`），字幕照常显示；错误不会伪装成正常结束。
- **打断**：开新一轮先 `Stop()`（`ReplySpeaker.cs:97`），连接断开时 `ConversationController.OnClientFailed` 也 `Stop()`（`Conversation/ConversationController.cs:245-263`）——停声排在 `Panel == null` 早退之前，面板没接上也不许继续出声。`Stop()` 同时清掉「这一轮说过了」的记号（`ReplySpeaker.cs:127-129`），停播后同一段文本能重新播；重复 frame 的去重只发生在 `Speak` 入口那一次判断。`_generation` 让还在飞的协程在检查点退出，已交付但被丢弃的 `AudioClip` 直接 `Destroy`（`:184-198`、`:327-332`，`ClipsDestroyed` 计数），不泄漏。
- **计数**：EditMode **75/75/0/0**、PlayMode **203/202/0/1**（跳过 `BridgeSmokeTests.UiSceneShot`）。比交付时各多 3 条 / 2 条，是评审之后补的：切句不劈代理对、`CopyClip` 样本一致、`-token -nobody` 不把开关当令牌（EditMode）；真 `DownloadHandlerAudioClip` 交出来的是自有拷贝、`Stop` 后同一轮同一句能重播（PlayMode）。

- **锚点图，不用 NavMesh**：`Scripts/Room/EdgeTable.cs` 是 **9 个节点 / 10 条无向边 / 总长 16.4054 m**（日志按 F3 打 `16.405 m`）。节点 A1–A5 取自 `Anchors/anchors.json`；座节点 S1（长凳，站立点 +0.70 m）、S2（床，站立点 −1.30 m，只连 A1/A4，即只能从床脚下来）；另两个手写拐点 W1/W2 专门绕开床（`EdgeTable.cs:101-124`）。最窄净空 **+0.04 m**（A1–S2、S2–A4 贴床南侧），`Path(from, to)` 走 Dijkstra。
- **走位参数**（`Scripts/Avatar/AvatarWalker.cs`）：`Speed = 1.1` m/s、`TurnRate = 180`°/s，到位吸附属 `ArriveAngle = 5°`（`:36`）、髋位误差 **≤0.05 m**（`.superpowers/sdd/unity-p7-voice-walking-body/task-2-report.md`）；`Phase` 为 Thinking/Answered 时 `GoTo` 拒绝——说话不走动（`:489-493`）；单帧步长上限 `MaxUpdateStep = 0.1` s，卡一帧也不会瞬移（`:39`）。
- **玩家挡路只停下、不推人**：`OverlapCapsuleNonAlloc` 只把 `CharacterController` 当障碍（`:504-513`）。
- **半路停走是诚实的**：`StopQuietly` 把 `At` 清空并记 `_betweenNodes`（`:383-394`），恢复后不去猜最近的节点，要 `ResnapToNearestNode()` 显式认路（`:408-421`）——宁可说「不知道」，也不把没扫掠过的直线当成走过的路。
- **座位姿势**：S1 用椅表（髋 = 座面 + 0.14），S2 走 `AvatarActor.SitOnBed` + `AvatarPose.BedSitting`（髋 = 床面 + 0.14 m；实测 1.046 m，椅坐 1.056 m）。**不读 `actor.SeatId`**——它只在 `Spawn` 写一次，床坐之后仍写着 `"S1"`；认座靠 `SeatUnderneath()` 用 root 的 (x,z) 与各座比距离，`SeatWindow = 0.05` m 内才认（`:44-49`、`:463-487`；`NodeWindow = 0.5` m 管的是节点）。
- **站/坐共用同一根胶囊**：`AvatarActor.FitBody` 站立不再删胶囊，坐/站切换不换实例。
- **三条已知限制**：① **没有生产调用方**——`RoomBootstrap.cs:147-155` 只创建并绑定 Walker，全仓 `GoTo(` 只出现在 `AvatarWalkTests`；② `ActionDirector` 的 sit-together 与 Cancel 直接 `SitOnBed`/`SitAt`，**不同步 walker**（`ActionDirector.cs:209`、`:308`），将来接动作要先补编排与测试；③ **没有走路动画**——只有根位移 + 原地 idle，腿不会迈。

- **玩家身体**（`Scripts/PlayerBody.cs`）：把 `Assets/_Local/avatars/Me.vrm` 的实例挂在玩家根上（不是头、不是相机），缩放到 `TargetHeight = 1.70 m`（`:42`，scale 0.839），脚底贴合玩家脚下（`:166-176`）；蹲/坐按眼睛高度整体下移（`:150`）。跟随误差 **≤0.02 m / 1°**（`PlayerBodyTests.cs:123`、`:145`、`:150`）。
- **相机臂 < `NearDistance = 1.2` m 时藏头**（`:35`、`:209`），≥1.2 m 保持第三人称可见——留着只会让屏幕被后脑勺填满。
- **模型自带 collider 全剥掉**（`:121-122`、`:227-231`；当前资产实测 0 个 spring-bone collider，规则留作保险），玩家移动权威仍是 `CharacterController`。
- `-nobody 1` 关掉身体（`BootArgs.cs:42` 的 `NoBody`、预扫 `:72-87`；`-token` 的值若像开关则按没给令牌处理 `:126-132`）；找不到模型时打一条 `[body] 找不到 …` 后优雅降级（`PlayerBody.cs:105-108`），不抛异常。
- **证据**：`apps/unity/Shots/p7-body-front.png`（A1 站立）、`p7-mirror.png`（镜前机位），1280×720；机位参数见 `.superpowers/sdd/unity-p7-voice-walking-body/task-3-report.md` §4。
- **四条已知限制**：① **打包版没有身体**——`LoadPrefab` 只有 `#if UNITY_EDITOR` 分支（`:238-240`），非编辑器返回 null 并 `Debug.LogError`；要上包必须把 `Me.vrm` 烘进场景（`Prefab` 字段）或放进 `Resources`。② 蹲/坐是**整体下沉**，脚会入地；站立只有 `AvatarPose.ArmsDown` 静态姿势，没有 idle 微动。③ 镜子**没有平面反射**——那张图是「站在镜子前」的机位，不是镜中倒影。④ 头部识别是启发式（Renderer 名含 face/hair，或包围盒底 ≥ 62% 身高，`:45`、`:184`），换模型会先红而不是静默错。
- **红证（可复核）**：编译红 `apps/unity/Logs/p7-task3-red-1.log`（CS0246 `PlayerBody` 找不到 + CLI exit 6）；身体单点变异 195/193/1/1、唯一失败 `TheHeadIsHiddenWhenTheCameraComesClose`（计数与用例名在 `.superpowers/sdd/unity-p7-voice-walking-body/task-3-report.md` §「红证」；`Logs/p7-task3-red-mutant-1.log` 只留了 CLI 的 `exit 8`，变异 XML 未留档——见下条）；走位两个变异 `p7-task2-fix2b-mut-i1.xml`（201/198/2/1，`ABedSitBySomeoneElseIsRecognisedAsTheBed`、`ASeatSheIsNotActuallyOnLeavesHerOffTheGraph`）与 `p7-task2-fix2b-mut-i2.xml`（201/199/1/1，`ADisabledMidWalkComesBackOffTheGraph`：`Expected: null` / `But was: "A2"`）；语音变异红在 `.superpowers/sdd/unity-p7-voice-walking-body/task-1-report.md`（用例 `ReplySpeakerTests.ALongReplyIsAskedOneShortSentenceAtATimeAndTheSubtitleStaysBounded`，`:364`；`Logs/p7-task1-fix2-red-play-2.log` 同样只留了 CLI 退出码）。**只有 Task 2 的两个变异 XML 是真落盘的**，Task 1/Task 3 的红证只能追到报告文字——下次做变异先把 XML 另存。
- **评审轮的变异红（这次 XML 都留了）**：① 去掉 `CopyClip`（`onOk(downloaded)`）→ PlayMode **203/201/1/1**，唯一失败 `TtsTransportTests.TheClipHandedUpIsAnOwnedCopyThatOutlivesTheRequest`（`Logs/p7-review-mut-clip.xml`）；② 去掉 `Stop()` 里的 `_hasSpoken = false` → PlayMode **203/201/2/1**，失败 `AStoppedLineCanBeSaidAgainInTheSameTurn`（等了 10 s 没再合成）+ 同轮 `VoiceSceneTests` 因失败用例留下的面板未清而连带红（`Logs/p7-review-mut-replay.xml`）；③ 硬切不避代理对 + 去掉令牌形似开关的守卫 + 直接 `onOk(downloaded)` → EditMode **75/73/2/0**，失败 `ALongSentenceIsNeverCutThroughTheMiddleOfOneCharacter`、`ATokenThatLooksLikeAFlagIsIgnoredInsteadOfBeingSent`（`Logs/p7-review-mut-edit.xml`）。还原后 EditMode 75/75/0/0、PlayMode 203/202/0/1。
- **六门终跑**（评审修完之后重跑，`summary: 6/6 ok`，exit 0）：web 12.3 s / bridge 17.2 s / unity-edit 25.4 s / unity-play 58.7 s / smoke 61.8 s / player 6.2 s。`player` 门跑的是这一轮重编的 IL2CPP 包（`build/GameAssembly.dll` 37,611,520 B @ 12:34:02、`global-metadata.dat` 10,902,128 B @ 12:33:41；`Mas3D.exe` launcher 仍是 11-05 那份 667,136 B），即 `PlayerBody` 在打包版里打一条 `Debug.LogError` 后降级、没有把门弄红。同轮 `TestResults/EditMode.xml` = **75/75/0/0** @ 12:36:22、`TestResults/PlayMode.xml` = **203/202/0/1** @ 12:37:20（跳过 `BridgeSmokeTests.UiSceneShot`）。Task 2 的两个变异 XML 仍在 `Logs/p7-task2-fix2b-mut-i1.xml` / `-i2.xml`，评审轮三个在 `Logs/p7-review-mut-*.xml` 供复核。

### 5.24 观感与操作对齐（2026-11-06 实测）

用户 m16063 报了六条实机问题（画面发灰、打字常开、走位时镜头晃、脚下有光、滚轮缩放不对、输入框字符错序），并给出白底立绘作为判据基准（`docs/superpowers/specs/assets/2026-11-06-reference.png`，542×1030）。设计见 `docs/superpowers/specs/2026-11-06-art-and-controls-alignment-design.md`，实施计划见 `docs/superpowers/plans/2026-11-06-{rendering,controls}-alignment.md`。

**管线（发灰的根因，不是调色能救的）**
- `ProjectSettings.asset:49` 是 **Gamma**，改成 Linear（`m_ActiveColorSpace: 1`）。Gamma 下所有 PBR 值与贴图都在错误的色彩空间里运算，之前房间靠 `RoomSurface.shader` 手写 ACES 自救，而角色（MToon）完全没过 tonemap——两边永远不会一致。
- 调色统一到 **Volume**：`RoomLook.Apply()` 首次调用时用代码建全局 Volume（`Mas3D-Look`，`isGlobal`、priority 1000），ACES + ColorAdjustments(postExposure **−0.15** / contrast 18 / saturation 15 / colorFilter **(0.98,1.00,1.02)**) + WhiteBalance(−16/+4) + Bloom(0.12@1.1) + Vignette(0.15)。**手写 ACES 从 shader 里删干净**（`RoomSurface.shader`、`RoomMirror.shader`），否则同一帧里会有两次 tone mapping。
- 相机开后处理：`Main.unity` 的 `m_RenderPostProcessing: 1`、`CliPlayer` 里 `UniversalAdditionalCameraData.renderPostProcessing = true`。出图日志 `[play] volume Mas3D-Look … colourAdjustments=True post=-0.32` 就是这条链路的证据。
- **调色入口只能有一处数值**：`RoomLook.PostExposure` 与 `RoomDress.Tuning.postExposure` 必须同源（后者默认值直接引用前者）。曾只改前者，编辑器出图走的是后者（`CliPlayer.BuildScene` → `RoomDress.Apply`），于是曝光改了没生效。
- **最贵的一处坑：`postProcessData` 是 null**。`Assets/Settings/Mas3D-URP-Renderer.asset` 从 `CreateInstance<UniversalRendererData>()` 生成起就写着 `postProcessData: {fileID: 0}`，URP 遇到 null **静默跳过整条后处理**：把 `RoomLook.PostExposure` 从 −0.45 改到 −3.0，出图逐像素不变，而 Volume、相机开关、VolumeManager 栈全是对的。写回 `com.unity.render-pipelines.universal` 包内 `PostProcessData.asset`（guid `41439944d30ece34e96484bdb6645b55`）之后 ACES 才第一次真正生效，整幅压暗一档、脸的灰绿脏感消失。回归用例 `RoomLookTests.TheRendererIsWiredToPostProcessData`。**在此之前的所有观感扫参都作废。**
- **SSAO 关掉了**：实测关 SSAO 前后，房间床脚交界的 p05/p25/median 三档完全一致（44~45 / 81 / 135~136），而脸部 p05 从 10 抬到 26、脏感明显减轻；**SSAO 的 feature 已从 `Mas3D-URP-Renderer.asset` 里删除**（不是关掉）：只写 `m_Active: 0` 时 feature 仍在列表里，玩家构建里 URP 照样构造它，而它的 shader 因「未启用」被剥离 → `ScreenSpaceAmbientOcclusion.TryPrepareResources()` 抛 `NullReferenceException`，`CreatePipeline()` 每帧失败，exe 黑屏像打不开（2026-11-06 23:0x `build/play.log` 实测；编辑器内与六门都抓不到，player 门只跑 `-selftest`）。`OutlineTests` 现在断「SSAO 必须不存在」。
- `RoomLook.LightScale` 从 0.5 改回 **1.0**：那是为了补偿「房间有 ACES、角色没有」的 2.3 倍差，管线归位后它是新的偏差源。

**灯**
- moon 平行光从冷蓝 `0xbcd6ff` 改暖白 `0xffe2d2`、强度 1.1；lamp 台灯改成 **Soft** 阴影（原来 None）；**删掉 y=0.32 的 floor-bounce 点光**（用户看到的"脚下有奇怪光源"就是它和两盏无阴影吊顶光在每物体 4 盏附加光上限里轮换的结果）。
- 吊顶光从 2 盏 → **4 盏**（`ceiling-glow-1..4`，x 偏移 ±0.33/±0.11 × 房间宽、y = 檐口 −0.55、range 10、不投影、强度 = glow×0.5）。降到 eave−0.55 是因为贴到 −0.35 时 1/d² 会在吊顶上烧出四道亮条。
- URP 资产四个开关一起开：`m_AdditionalLightShadowsSupported`、`m_SoftShadowsSupported`、`m_PrefilteringModeAdditionalLightShadows`、`m_PrefilterSoftShadows`——只翻前两个，变体会被 prefiltering 剥掉，界面正常、出图零影子。

**角色材质与描边**
- **MToon 明暗带**（`Scripts/Avatar/AvatarShading.cs`）：VRM 导入的 `_ShadingToonyFactor` = 1.0，`mtoon_linearstep(-1+1, 1-1, x)` 上下界重合恒返回 1；加上 `_MTOON_PARAMETERMAP` 开着会采空 shift 贴图再 +1。结果整张脸永远只有 litColor。改成 shift **−0.15** / toony **0.9** 并 `DisableKeyword("_MTOON_PARAMETERMAP")`；**写的是场景副本**（`Object.Instantiate(material)` 后写回 `renderer.sharedMaterials`），不改 VRM 资产，否则重新导入即丢。
- **描边**：VRM10 包自带 `MToonOutlineRenderFeature`（Unity 6000+ 走 URP 内置 `RenderObjectsPass`，shaderTags `MToonOutline`，Render Graph 下能画，不需要自己写 `RecordRenderGraph`），已追加进 `Apps/unity/Assets/Settings/Mas3D-URP-Renderer.asset` 并保留原有 SSAO。宽度按像素反算：1080p / fov 42 / 2.5 m 下 1 m ≈ 563 px，故身体 `_OutlineWidth = 0.0040`（≈2.25 px）、布料/鞋袜 0.0032，脸/眼/眉/嘴/睫毛/牙齿/舌/耳 恒 0（`AvatarOutlines.WidthFor`）。同时必须置 `_OutlineWidthMode = 1`，否则 MToon 的描边 pass 直接跳过。
- **阴影真的落地**：`RoomSurface.shader` 加 `_MAIN_LIGHT_SHADOWS*/_ADDITIONAL_LIGHT_SHADOWS/_SHADOWS_SOFT*` 五组关键字，frag 里 `TransformWorldToShadowCoord` + `GetMainLight(shadowCoord)` 并乘 `shadowAttenuation`（无参 `GetMainLight()` 恒返回 1，只改灯不会有一像素影子）。

**操作**
- **输入门**（`Scripts/InputModes.cs` + `PlayerMotor.WantsKeyboard`）：`KeyboardInput` 是总闸，`WantsKeyboard = KeyboardInput && (Panel == null || !Panel.IsInputFocused)` —— **门只认一件事：焦点在不在输入框**。门清的是**行走意图**（wish 换 0），速度仍按 Acceleration 收敛到 0，不是瞬停。E 只负责**进入**输入框（打字时 E 就是一个字母，不能抢焦点），退出交给 Esc 与点面板外；演出中（`CanTakeKeys` 为假）E 与 Esc 都不抢焦点。
- **曾经有过第三态，已删除**：Esc 退出打字时把走路键盘「锁」上、等焦点回输入框才解。界面上没有任何提示，实机表现就是 YG m17495 第 4 条「方向键控制一会儿之后又失去控制」。现在锁与 `FocusChanged` 订阅一起删掉，门每帧直接问面板。
- **输入框顺序与 IME**（`DialoguePanel`）：所有写值只走 `WriteInput`（`SetValueWithoutNotify` + `SetCaret`），补全/语音/发送后复位都走 `AppendToInput`/`ApplyComposition`/`WriteInput`，**没有第二条写值路**——「你以站起来吗可」那类错序就是两条写值路各插一次光标造成的。组合态用 `Input.compositionString`（`IMECompositionEvent` 在 6000.6.7f1 里不公开），组合期间 Enter 不发送、由 `ApplyComposition` 收尾；而且**不许 `StopPropagation`** —— 在 trickle 阶段掐断会把这一次候选上屏吞掉，字就跑到句尾或被丢掉（YG m17495 第 1 条「你以站起来吗可」）。
- **相机骨架**（`PlayerCameraRig`）：新增 `CameraRigRoot`（只跟 head 位置、每帧世界旋转写回 identity），Pivot 挂它下面 —— 玩家的转身到这一层就断了，Cinemachine 在 LateUpdate 采样时不会再看到「本帧后半段才转的那几度」。`Apply()` 仍留在 `Update`（Brain 只在 LateUpdate 取景，晚写只会读到上一帧，270°/s 下每帧差 4.5°，更抖），同一帧最后一次转身由 `PlayerMotor.Tick` 尾部的 `_rig.Apply()` 补写。`TurnRate` 默认 720 → **270**（`Awake` 把序列化里的 720 迁移成 270）；`ReadWish` 改用 `rig.IntentYaw`，不再读渲染相机的欧拉角（那带着阻尼滞后，机器在追自己）。
- **真第一人称**（`PlayerCameraRig`）：`Distance` 下限 0.6 m、`IsFirstPerson` = ≤1.2 m；`EaseFollow` 在 **0.15 s** 内把 CameraDistance/竖臂/肩位/CameraSide 一起收（肩位不收会让相机偏到脸颊外侧，实测残差 0.284 m），FP 时阻尼归零；`PlayerBody.NearDistance = 1.25` 比 1.2 松 5 cm，极端帧率下也不会「已经第一人称、后脑勺还在画面里」。滚轮灵敏度只在 `AddOrbit` 里乘一次，上推抬高镜头（加号）。

**判据与实测（`tools/look-compare.mjs` / `tools/shadow-diff.mjs`）**
- 修好 post 链后的实测：脸颊 B/R **0.69**（限 ≤0.90）、脸峰值 **0.743**（限 ≤0.92）、房间对比度 **0.675**（限 ≥0.35）、描边 **3 px**（720p 折算限 1.0，1080p 档 ≈2.25 px）、脚下亮度比 **1.273**（限 ≤1.30）、阴影像素差 **下半幅 5.54%**（限 ≥3%，全画面 2.77%、最深 68.2%），六项全 PASS、`look-shoot.ps1` exit 0。一条命令跑完：`tools/look-shoot.ps1`（player → capture -noShadows 1 → 归档 `.superpowers/look/<date>/` → 两个检查，exit = 失败项数）。
- **三处测量口径是量出来定的，不是放宽阈值**：① 脸峰值取 99.5 分位而非 max（单像素高光与木家具暖色像素不该决定脸糊没糊）；② 描边取「长度 ≤10 px 暗带的 75 分位」而非 max（家具宽边会把最大长度顶成假数）；③ 阴影像素差的分母取**下半幅**（影子只落在照得到直射光的地板与床，上半幅是吊顶与墙，用全画面当分母等于让吊顶像素摊薄地板影子）。脚下光斑改代码级判据（`RoomLightTests` 断言 0.6 m 以下无点光、四盏吊顶光不投影），八角度绕圈的地面极差会被几何主导。
- 计数：EditMode **89/89/0/0**（14:06:54Z）、PlayMode **225/224/0/1**（14:07:27Z，跳过 `BridgeSmokeTests.UiSceneShot`）；六门终跑 `summary: 6/6 ok`（web 10.2 s / bridge 11.1 s / unity-edit 29.3 s / unity-play 62.7 s / smoke 68.7 s / player 6.1 s，日志 `apps/unity/Logs/s-round4-check.log`）。IL2CPP 包在 20:22 重编过（`build/GameAssembly.dll` 37,650,944 B、`global-metadata.dat` 10,917,172 B），player 门跑的就是这个新包。
- **URP 会自己重写 prefiltering 开关**：`m_PrefilterSoftShadows` 在某一轮测试后被 URP 序列化回 0（软阴影变体被剥），表现是用例红而画面看着没事（主光阴影不走这条 prefilter）。用例因此改成断「不是 Disabled（0）」而不是「恰好等于 1」，同时把开关显式写回 1。`m_PrefilteringModeAdditionalLightShadows` 现在是 **2（Auto）**，也合法。
- **红证**：渲染侧 `RoomLightTests` 四条先全红再实现（`apps/unity/TestResults/EditMode.xml` 4 failed）；controls Task 3 编译红 `Logs/c3-task3-red-compile.log`（CS1061 `AxisInput`/`IntentYaw`）、行为红 `Logs/s1-task3-red.xml`（223/219/**3**/1：绕圈 Pivot 残差 0.64°、Wish 方向 (1,0) 应为 (0,−1)、AMouseLocked 残差 −120°）、Task 4 红 `Logs/c4-task4-red.log`（226/222/**3**/1：Pitch −10、Distance 0.9、NearDistance 1.2 不小于 1.2）；变异红 `Logs/c3-task3-mutant-2.xml`（去掉 Tick 里的 `Apply` → 绕圈残差符号翻转 1 次）、`Logs/c4-task4-mutant-1.xml`（EyeGap 0.05→0.45 → 相差 0.450 m）。
- **两条已知限制**：① 变异「把 Pivot 挂回 head」**没被抓住**（223/222/0/1 全绿，`Logs/c3-task3-mutant-1.xml`）——转身后补写 `Apply` 已经把父节点转身造成的漂移抹平，结构本身在批处理里不可观察，用例真正钉住的是那行 `Apply`；② controls Task 4 的严格 TDD 不成立（FP 字段在 Task 3 的整文件重写里已落地，红证靠回退 0.9/减号造出），真实缺陷是首轮绿跑抓到的两条（灵敏度乘两遍、FP 肩位未归零 0.284 m）。

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
- **P3 语音**：/mas3d/api/asr + 按住说话 + 文本回填。（2026-11-03 已跑通，明细见 §5.19）
- **P4 四件动作**：按第 6 节逐件做到判据。
- **P5 回归与归档**：smoke 覆盖到四件动作（含第 6 节判据）；全绿后把 **web 版降级归档**（单开一步，不混在功能提交里）。
- **P6 打包**：IL2CPP 出包，双击可跑；桥接与 AstrBot 的启动顺序、失败提示。
- **P7 语音与身体**：TTS 逐句播放 + 字幕；锚点间走位（边表 + 胶囊扫掠）；玩家身体 Me.vrm（编辑器/PlayMode/截图）。明细见 §5.23。
- **P8 观感与操作对齐**：管线归位（Linear + 全局 Volume + 删手写 ACES）、灯与阴影、MToon 明暗与描边、输入门与 IME 字符顺序、相机骨架与真第一人称，判据由 `tools/look-compare.mjs`/`shadow-diff.mjs` 数值化。明细见 §5.24。

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

## 11. web 版的归档（2026-11-04）

- **归档点**：2026-11-04，P5 回归全绿（`tools/check.ps1` 一轮 `summary: 5/5 ok`，见 §5.21）之后同一天落地。自这一天起生效的边界：
  - `apps/unity` 是唯一在维护的主线；`apps/web` 冻结，保留可运行，作为协议与观感的对照资产，不再是产品入口。
  - web 端不接新功能、不修观感、不跟进新协议字段（§5.11 的纯增量可选字段由 Unity 侧按需消费，web 端可以忽略就忽略）。例外只有安全与阻断性 bug：修完仍须 `& pnpm.cmd -r test` 全绿。
  - 落点：`apps/web/ARCHIVE.md` 写这条边界与 web 版的跑法；README 首段、目录表与快速开始同步改成 Unity 主线。
  - 不动的部分：`apps/bridge`、`packages/protocol`、`packages/motion` 仍是两套客户端共享的语义出处，bridge 只加不改（§10 的回退承诺不变）。
- **为什么保留测试**：`pnpm.cmd -r test` 的 190 条（`apps/web` 130 + `packages/protocol` 51 + `packages/motion` 9）不是历史包袱，是**第二条实现**的对照基线——bridge 加字段若把老客户端弄坏，`tools/check.ps1` 的第一步（web 门）先红，不必等 Unity 的 C# 镜像契约测试去发现；web 门还是强门禁，缺 `pnpm.cmd` 直接 FAIL，不会静默跳过。观感同理：`apps/web/e2e/smoke.mjs`（8 项）与 `room-angles.mjs` 的四机位是「她该长什么样」的既有参照——§1 里「画面已经让 YG 满意」指的就是这套画面，Unity 的 MToon 曝光配平（§5.20）要对齐的基准在它这里。
- **要复活 web 版需要做什么**（按顺序；缺一条都只会得到「能起但不可信」的版本）：
  1. **解归档**：删 `apps/web/ARCHIVE.md`，README 首段与目录表把主线改回 web；spec 追加一节复活记录，不改 §11 的历史结论（归档这件事不重写）。
  2. **恢复主线性与门禁**：web 从「对照」升回「产品」，新用例与 Unity 同步增长，`ARCHIVE.md` 里那三条「不再做」作废；`tools/check.ps1` 的 web 步保持为必须绿。
  3. **补齐新协议字段**：以 `packages/protocol/src/frames.ts` 的当前版本为准，把归档之后 bridge / Unity 新增的字段在 web 端消费或显式忽略（P3 的 ASR 是独立 HTTP 端点 `POST /mas3d/api/asr`，不进 WS 帧）；跑 `& pnpm.cmd --filter @mas3d/web test`、`node e2e/wire.mjs`、`node e2e/smoke.mjs`。
  4. **重跑观感四机位**：`node e2e/room-angles.mjs --room /assets/room/mita-room.glb --dir <输出目录>`（在 `apps/web` 下跑）与 `node e2e/smoke.mjs` 的 8 项，和归档前的图逐张比；房间资产若在归档期变过，按 §5.20 的 `RoomLook.LightScale` 思路重新配平曝光。
  5. **真链路一轮**：`& pnpm.cmd --filter @mas3d/web dev` 连真桥接 8790，走 `say → ack → thinking → reply → intent`，确认 HAST-SEC 与语音的降级契约（`bridge_down` → Tier 0/1、TTS 503 → 只显示字幕）没被归档期的改动破坏。
  五步都过才把 web 版标回主线；任何一步不过，维持归档。