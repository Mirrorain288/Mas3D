# P7 — 语音、走动与身体（Unity 侧）

## 这一阶段要什么

P6 之后这个包已经能双击跑一轮对话与一件动作，但三件事还是空的：

1. **她不会出声**。`reply.voice` 在协议里早就有（`packages/protocol/src/frames.ts:14-19`，字段 `audioUrl / durationMs / sentenceStartsMs / englishText`），Unity 侧 `Net/ServerFrame.cs:38` 也解析了，但从来没人播。桥接**不在 reply 帧里塞 voice**（`apps/bridge/*.py` 里没有 `"voice"` 的出生点），TTS 是客户端驱动的：`POST /mas3d/api/voice/synth {text, sentence}` → `{ok, voice}` → `GET <audioUrl>`（Bearer 或 `?token=`）。web 版的参考实现在 `apps/web/src/chat/voice.ts:60-117`。
2. **她不会走动**。`AvatarActor` 只有 `SitAt` / `SitOnBed`，锚点表（`AnchorTable`，5 个站位 + S1/S2）只有坐标没有边。spec §8.2 已经拍板「锚点 + 锚点间过渡，不做 NavMesh，说话中不位移」。
3. **玩家没有身体**。第一人称只有相机（`PlayerCameraRig`），`Assets/_Local/avatars/Me.vrm` 已导入但没人加载，镜子（`Mesh_0014out`）里照不出人。

P7 就是把这三件补上，仍然不碰协议语义、不引入第三方包。

## 权威

- `docs/superpowers/specs/2026-11-01-unity-rebuild-design.md`：§6 判据、§7 阶段、§8.2 锚点走动裁决、§5.15 防穿模、§5.22 P6 实测（现状）。
- `apps/web/src/chat/voice.ts`、`apps/web/src/avatar/lipsync.ts`：web 版 TTS 与口型时间线的既有行为，Unity 侧照它的**可观察行为**对齐，不照抄实现。
- `apps/bridge/web_api.py:210-271`：`/mas3d/api/voice/synth` 与 `/mas3d/api/voice/{voice_id}.wav`，都要 token；`require_token` 同时收 `Authorization: Bearer` 与 `?token=`。

## 全局约束

- 不自行 `git commit` / `git push`。每个 Task 结束时把改动留在工作区（未提交），由 Lead 汇总后问 YG。
- `openspec/` 不动；需求以 spec 为准，本计划只补 P7 的空白。
- **不改协议**：`packages/protocol` 一个字节不改。TTS 走既有 HTTP 端点；她走位是 Unity 本地状态。
- **不引第三方包**：Unity 侧不装新包（HTTP 用 `UnityWebRequest`，JSON 用现有的 Newtonsoft/JsonUtility 二选一，跟着仓库现状）。
- 测试先写：每个 Task 都要有会红的用例再实现（`test-driven-development`）。
- 本机 `127.0.0.1:8790` 是 YG 的真 AstrBot，**不许 kill、不许当测试依赖**；测试一律用 `tools/mock-bridge.mjs` 或注入的假实现。
- `.ps1` 必须纯 ASCII；本机只有 Windows PowerShell 5.1，跑脚本一律 `powershell -ExecutionPolicy Bypass -File …`。
- Unity 无头跑测试 `tools/unity.ps1 test -Mode EditMode|PlayMode`；无头**截图/渲染**不能加 `-nographics`（走 `-Gpu`，见 `tools/unity.ps1` 的 capture/player 分支）。
- 收尾判据：`tools/check.ps1` 六门全绿（web / bridge / unity-edit / unity-play / smoke / player），且不降低任何既有断言。

## Task 1 — TTS：让她出声，字幕按句走

**目标行为**

- reply 到达后，若本机桥接可用：把回复切成**中文字句**，逐句 `POST /mas3d/api/voice/synth`（body 只有 `{"text": "<这一句>"}`，**不带 `sentence`**），拿到 `VoiceRef` 后 `GET base+audioUrl` 播出来；播放期间字幕按这一句的 `durationMs` 逐句出现，而不是整段一次性铺开。
- **为什么不照 web 的 `sentence` 下标**：桥接先把它翻成英文再按英文句切分（`apps/bridge/tts.py:363-368`），下标是英文句的；中文句数与英文句数不等时，`N_zh > N_en` 会少播尾部，`N_zh < N_en` 会拿到 `400 句子下标越界`。而且 `sentenceStartsMs` 也是英文句的时间线，无法驱动中文字幕。只送一句、不带下标，服务端就只会合成这一句，`durationMs` 就是这句的真时长。这是**有意偏离 web 与端点原设计**，理由记进 Task 4 文档。
- 因此任一非 2xx（400 含超长/空句、401、404、503）都算失败 → 整段字幕 + 状态栏提示，不再把 400 当「没有更多句子」。
- 任何一环失败（401 / 503 / 网络错 / 音频解码失败）：**只显示字幕**，状态栏一句话 `语音暂时不可用，先只看字幕。`，不抛异常、不阻塞输入、不影响下一轮。
- 同一轮只播一次；新一轮开始时上一轮的音频立刻停（不叠音）。

**落地形态（由实现者定细节，但接口要能测）**

- 一个 `ReplySpeaker`（`Assets/Scripts/Voice/`）挂在 Boot 上，由 `ConversationController` 在 `reply` 分支把 `(turnId, text)` 交给它；`ConversationController` 只加一个事件或一个字段，不改既有 Phase 语义。
- HTTP 抽成接口（`ITtsTransport` 之类），PlayMode 用假实现，真实现只用 `UnityWebRequest`；URL 拼接、Bearer 头、句切分、`sentenceStartsMs` 调度全部是纯函数或有注入点。
- 句子切分与 web 对齐：`apps/web/src/chat/voice.ts` 的 `VOICE_MAX_SENTENCES` 与 `synth(text, index)` 语义（同一段文本的第 index 句；`400` 表示没有更多句子）。

**验收**

- EditMode：URL 拼接（相对 → 绝对、`ws://`→`http://`）、Bearer 头（只对同源 audio URL 带）、句切分（中英混排、无标点、超长句切成 ≤200 字）、请求体里**没有** `sentence` 且 `text` 等于当前这一句。
- EditMode：700 字的回复切成多句后，每次请求都 ≤200 字（这条取代「超过 600 字整轮静默」的旧缺陷）。
- EditMode：`400` / `401` / `503` 三种非 2xx 都走降级，不当作「播完了」。
- PlayMode（假 transport）：reply 到达 → `ReplySpeaker` 收到同一段文本 → 字幕按句逐条出现（用假音频时长驱动）→ 结束后输入可用、Phase 仍是 `Answered`。
- PlayMode（假 transport）：第二轮 `Speak` 打断第一轮 —— 第一轮的第 3 句之后不再露、第二轮从头开始。
- PlayMode（假 transport）：`error` 帧到达时正在播的那一轮被停掉（音频停、字幕清）。
- PlayMode（假 transport 返回 503）：状态栏出现 `语音暂时不可用，先只看字幕。`，字幕仍是完整回复，输入可用。
- 不回归：`tools/unity.ps1 test -Mode PlayMode` 全绿（当前 168 passed / 1 skip）。

## Task 2 — 锚点间走位

**目标行为**

- 她能在锚点之间走过去：`AvatarWalker.GoTo("A3")` 之类的入口，按预定义边表走直线段（不做 NavMesh、不做 A*，图小到可以直接写边）。
- 行走速度约 `1.1 m/s`，转身按 `180°/s` 限速；到达后朝向锚点朝向（`anchors.json` 里每个锚点已有 `facing`），误差 ≤ 5°。
- **说话中不位移**：`ConversationController.Phase` 是 `Thinking` / `Answered` 时不启动新的走位；已经走起来的允许走完，但不接新目标。
- 走位中只做原地 idle（不叠加说话时的表情动作冲突）；到达后回到既有 idle。
- 身体胶囊跟着走，玩家的移动仍由 `PlayerMotor` 负责，两者互不推挤（她撞到玩家时停下，不把玩家顶飞）。

**必须复用已有的身体子系统（P5 已落地，别新造一套）**

- `Scripts/Avatar/AvatarActor.cs`：`BodyRadius 0.20 / BodyHeight 1.05 / BodyLift 0.20`（`:19-29`）、`SeatId`、`SitAt`(`:126`)、`SitOnBed`(`:157`)、`FitBody`(`:187`)、`Spawn`(`:220`)。
- `Scripts/Avatar/AvatarSeat.cs`：`Seated`(`:26`)、`HipsWorld`(`:32`)、`Sit`(`:50`)、`SitOnBed`(`:92`)、**`Stand(Vector3 feet, float facingY)`(`:150`)** —— 站起来这条路已经有了，走位从它接。
- `Scripts/Avatar/AvatarIdle.cs`：`CaptureBaseline`(`:105`)、`Tick`(`:161`)、`SetGaze`(`:218`)；姿态一动就要 `CaptureBaseline` 重定基线。
- `Scripts/Avatar/AvatarRig.cs`：`Measure()`(`:148`)、`Bone()`(`:96`)、`EnsureBound()`(`:53`)。
- **已知缺口（本 Task 要补）**：`FitBody` 在 `!Seat.Seated` 时**删掉胶囊**（`:193-203`），所以站着走的时候她身上没有碰撞体。走位需要「站立态胶囊」——要么给 `FitBody` 加站立分支（按 `BodyRadius` 与站姿髋高放胶囊），要么由 walker 自己挂一个只在走位期间存在的胶囊。选前者，理由是这样胶囊的位置口径只有一份。
- **没有走路动画**：`Assets/_Local/avatars/*.vrm` 里的 Monika 没有 walk clip，仓库也没有 AnimatorController。本 Task 不造假动画：根节点位移 + 既有 idle 呼吸，腿不迈。这是**已知观感缺陷**，要写进 Task 4 文档，不要用「播放行走动画」含糊过去。

**边表必须先证明不穿模**

- 用 `apps/unity/Assets/Scripts/RoomColliders.cs` 已有的碰撞体数据，在 PlayMode 里对每条边做胶囊扫掠（半径 `0.30`，高度 `0.30` 与 `1.00` 两个采样高度），断言与家具/墙无交集。
- 任何一条边过不去就改边表，不是放宽断言。床体两侧、书桌正面、门洞扫掠区照 §5.15 的否决区。

**验收**

- EditMode/PlayMode：边表连通性（5 个锚点 + S1/S2 至少各有一条进路）、每条边扫掠净空、走位时长与 `距离/速度` 误差 ≤ 10%。
- PlayMode：`Phase == Thinking` 时 `GoTo` 被拒（返回 false 或不启动）；`Phase == Idle` 时启动并到达，髋位落在锚点 `xz` 误差 ≤ 0.05 m，朝向误差 ≤ 5°。
- PlayMode：走到一半把 `Phase` 置 `Answered`，她走完当前段就停，不接新目标。
- PlayMode：从 S1 坐着到 A3 站着再回 S1 坐下 —— 三段之后 `Seat.Seated` / `AvatarActor.Body` / `Idle` 基线三者一致（站起时胶囊按站立髋高重放、坐下时按座面重放，不残留上一姿态的胶囊）。

## Task 3 — 玩家身体

**目标行为**

- `Assets/_Local/avatars/Me.vrm` 作为玩家身体实例化，挂在相机装配体（`PlayerCameraRig`）的根上，随玩家一起移动/转向。
- 第一人称下看不到自己的头/脸（否则镜头被头发糊住）：头骨以下可见，头的 `SkinnedMeshRenderer` 关掉或按 VRM 的 first-person 设置隐藏；低头能看到躯干与手。
- 身体不能挡玩家自己的移动：不加 `CharacterController`/实体碰撞体，或碰撞体设成 `isTrigger` 且不与 `PlayerMotor` 的 layer 冲突。
- 镜子（`Mesh_0014out`）里能看到身体——用一次固定机位截图人工确认，不进自动门。

**已核实可用的接口（别新造）**

- `Assets/_Local/avatars/Me.vrm` 已在盘上，`Assets/Editor/CliAvatar.cs:29` 扫这个目录导入 `*.vrm`；`Scripts/Avatar/AvatarRig.cs:24` 的 `Spawn(GameObject prefab, string name)` 接任意 VRM prefab（默认参只是 Monkia）。
- `Scripts/PlayerCameraRig.cs:39 Build(Transform head, Camera output)`、`:34 Pivot`、`:33 VCam`；`Scripts/PlayerMotor.cs:28 Head`、`:155 Teleport`。
- VRM 1.0 模型在 Unity 里朝 +z（`Assets/Editor/CliAvatar.cs:227` 的注释），身体朝向跟玩家 yaw 对齐时要算进这一步。
- 第一人称隐藏：`AvatarRig.Instance`（`:111`）是 `Vrm10Instance`，头骨用 `AvatarRig.Bone(HumanBodyBones.Head)`（`:96`）取；把头部与头发所在的 `SkinnedMeshRenderer` 关掉比改 VRM 的 first-person 标注更好测（能断言 `enabled == false`）。

**验收**

- PlayMode：实例存在、`transform.parent` 是相机装配体根、位置与玩家脚底一致（误差 ≤ 0.02 m）。
- PlayMode：头部的 renderer 在相机为第一人称时被禁用；关闭第一人称（或切到第三人称调试）时恢复。
- PlayMode：玩家从 A2 走到 A3 的过程中，身体位置与 `PlayerMotor` 的 `transform` 误差 ≤ 0.02 m，且不产生额外碰撞（移动距离与无身体时一致，误差 ≤ 1%）。
- 截图：`tools/unity.ps1 capture` 出一张镜子方向的图，落 `apps/unity/Shots/p7-mirror.png`，人工看一眼。

## Task 4 — 文档

- spec 新增 `### 5.23 P7 语音、走动与身体（2026-11-05 实测）`：TTS 端点契约、句切分与调度、失败降级文案、走位速度/边表/净空判据、玩家身体的第一人称隐藏方式，以及每条实测数字的出处。
- 根 `README.md` 的「现在能做什么」补三行（会说话、会在锚点间走、有身体）。
- `tools/README.md` 若有新参数（例如 capture 的机位开关）同步。
- 不写没实测过的数字；测不出来就写「未实测」并说明原因。

## 收尾

- Lead 跑 `tools/check.ps1`（六门）与 PlayMode/EditMode 全量，把真实数字写进 §5.23；
- 整支评审（vs P6 的 `35abb8a`）；
- 问 YG 是否提交。
