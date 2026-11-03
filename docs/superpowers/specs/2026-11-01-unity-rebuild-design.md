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
19. **P4 前置：角色接入的五层与判据**（2026-11-03 实测，`tools/unity.ps1 player|capture|test` 全绿）：她现在是 `Main.unity` 里的常驻物件（`Actors` 空物体下的 `Monkia`），不再由 P4 临时生成。
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