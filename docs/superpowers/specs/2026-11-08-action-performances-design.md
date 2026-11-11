# 动作表演的三段结构（进场 / 保持 / 退出）· 设计

- 日期：2026-11-08
- 状态：待 YG 批准（未批准前不写产品代码）
- 决策来源：m21783「有过程的表演」、m21796「开始表演动画 / 轻微动态的保持 / 按 ESC 的退出动画」、m21800「三段里我和她都要有可见动画」、m21803 选 C（先把自己生成的整段表演烘成 clip，再补程序化节拍表）
- 规模：项目级（多阶段，不是一次小改）

## 1. 意图与为什么

现在五个热键动作（hands / pat-her / pat-me / kiss / sit-together）**没有一个动作是"演"出来的**：按下热键的那一帧，玩家被 `Player.Teleport` 瞬移到标记点、相机瞬移到机位、她的骨骼被一次性写成常量姿势；之后每一帧只是把这个常量姿势重写一遍，外加 IK 把接触点压进 2 cm。按 ESC 又是瞬移回去。

所以观感是：**闪现 → 定格 → 闪现**。用户要的是连续的三段：

1. **进场表演**：从当前站的地方走到位、抬手、她转身/抬头，两具身体都动。
2. **保持**：进到位之后**不要定格**，要有呼吸之外的轻微动态（重心、头、手指），但接触不能散。
3. **退出表演**：按 ESC 之后不是瞬移，而是松开、退开、把相机还回去。

这件事的技术难点不在"播一段动画"，而在**产品里根本没有动画通道**：

- `apps/unity/Assets/Scripts/Avatar/AvatarRig.cs:92` 把 `Animator.enabled = false`，全仓没有 AnimationClip / AnimatorController。
- 五个动作的姿势是 `apps/unity/Assets/Scripts/Actions/ActionPose.cs` 的常量表（Euler 偏移 + 快照基准），逐帧重写。
- `ActionDirector.TryEnterAction`（`apps/unity/Assets/Scripts/Actions/ActionDirector.cs:179`）是"原子入场"：查计划 → 锁键盘 → 设姿势 → `Teleport` → 锁相机 → 快照 → 置 `_current`；`Cancel()`（:290）反向还原。中间**没有时间轴**。
- 她的走位（`AvatarWalker`）是程序化摆腿，玩家的腿从来没有被摆过（`AvatarPose` 只有 `ArmsDown`）。

因此这次不是"加几个动作"，是**给动作系统装一条时间轴**，并把已经验证过的生成动作（Kimodo → retarget → IK）接进产品。

## 2. 目标 / 非目标 / 反目标

**目标**

1. 五个动作全部具备三段：进场 / 保持 / 退出；每段都有可测的时长与进度（`Phase`、`PhaseTime`、`PhaseWeight`）。
2. 每一段里**两个人都动**：她至少有可观测的骨角度变化，他至少有可观测的位移或上身/腿部变化。
3. hands 的三段由**生成动作**驱动（Kimodo 生成 → 现有 retarget → 烘成 clip → 打包进产品），证明"生成动作能上线"这条通道。
4. 保持段永不定格：≥ 2 s 的保持窗口内，她至少一根骨的角度持续变化 ≥ 0.3°、身体重心有可见微移；同时 `ContactError ≤ 0.02` 仍然成立。
5. ESC 在**任何阶段**都安全：进场中按 ESC 立刻转成退出（从当前进度反着走，不回跳）；退出完成才 `Cancel()` 的旧还原逻辑。
6. 退化可用：clip 缺失/被剥离时，同一动作退回程序化进场（结构与时长不变），日志一行说明。
7. 现有判据全部保绿：`Held` 语义、`ContactError`、`ClearPoseCount == 1`、`-nobody 1`、六门 `tools/check.ps1`。

**非目标**

- 不做走路动画（腿不迈步；位移是滑行 + 上身/腿部姿势变化）。玩家仍无 walk cycle。
- 不做表情/口型/视线与对话同步（她已有眨眼与程序化视线）。
- 不改五个动作的**接触几何**（标记点距离、`HandsOffset`、`KissOffset` 等常量不动）；三段化只改"怎么到那儿"。
- 不改 `AvatarWalker`、不改房间、不改语音/桥接。
- 不做 Unity 的 `Animator` / PlayableGraph / Timeline 集成（理由见 §4）。
- 不做其余四个动作的生成版（它们留在程序化；通道留好，后续可逐个升级）。

**反目标（技术上跑通也算失败）**

- 用"瞬移 + 播完再定格"冒充三段：必须有中间帧的位置/角度证据。
- 保持段靠"每秒重放同一帧"装作在动：必须有连续的角度变化量。
- 让 ESC 在进场中把玩家瞬移回原位（应该是从当前进度退回去）。
- 为了省事直接把 `Animator.enabled` 打开：会与 `AvatarIdle`、`AvatarSeat`、IK 抢骨骼（§5.2 记录原因）。

## 3. 约束（不可协商）

- **资产要能进包**：`apps/unity/Assets/_Local/` 被 `.gitignore` 忽略（`apps/unity/.gitignore:34`）。烘好的 clip 必须放在**受版本管理**的目录（`apps/unity/Assets/Motion/`），否则干净 clone 上五个动作只剩退化路径。
- **Unity 单实例**：任何 `unity.ps1` 动作前必须关编辑器（`tools/unity.ps1` 的既有约定）。
- **不自行提交**：AGENTS.md「不要自行 git commit / git push，需要提交时先问我」。设计批准后先写计划，实施完由 YG 拍板提交。
- **确定性**：判据要能在无头 `unity test` 里跑；`Tick(float dt)` 必须保持线性（`MaxUpdateStep` 的钳位只在 Update 入口），随机数一律不用（微动用 `sin`，相位由 `PhaseTime` 决定）。
- **性能**：每帧额外开销上限 = 一次 clip 采样（约 20 根骨的 `localRotation` 写入）+ 一次路径求值；不得每帧分配数组（复用缓冲）。
- **她与他的 IK 权威不变**：接触点最终仍由 `TwoBoneIk` / `JoinHands` 那套解算压进 `ContactTolerance`。

## 4. 三条路线与取舍（头脑风暴结论）

| 路线 | 做法 | 优点 | 代价/风险 |
|---|---|---|---|
| A 程序化节拍表 | 只写代码：路径 + 关键帧表 + 缓动，不引入 clip | 无资产、无 retarget 风险、可完全单测 | 动作质量上限 = 手写曲线，观感仍"机械" |
| B 整段生成 | 用 Kimodo 生成"整段表演"（进场+保持+退出）烘成一个 clip，两个人都由生成驱动 | 观感最好、一次到位 | 一次生成两个人的动作不可靠（现有 9 个样本里只有 1 个保持直立）；整段无法中途打断；保持段的循环点难做 |
| **C 先 B 后 A（选定）** | hands 的三段用生成 clip（她的身体），他的位移/上身走程序化节拍表 + 现有 IK；保持段用程序化微动；其余四个动作先走 A，通道留好 | 先把"生成动作能上线"这条最难的通道打通；两人协作用程序化保证接触；打断/保持可控 | 两套驱动并存（clip 通道 + 程序化通道），需要清晰的层序（§5.2） |

**选 C**：整段生成解决不了"保持不定格"和"中途打断"（m21796 的两个硬要求），而纯程序化达不到用户想要的表演质量。C 把两者放在正确的层：**她的身体用生成，时间与接触用程序化**。

## 5. 定下的做法

### 5.1 阶段状态机（ActionDirector）

新增：

```csharp
public enum ActionPhase { None, Enter, Hold, Leave }
```

`ActionDirector` 增加只读属性 `Phase`、`PhaseTime`、`PhaseWeight`（0→1 的段内进度，缓动前）、`PhaseDuration`。

- `TryEnterAction` 不再直接 `Teleport`：它仍然做"能不能进"的全部校验（`ResolvePlan` / `Reachable` / sit-together 的座位），成功后把 `Phase = Enter`、`PhaseTime = 0`，锁定键盘与相机，快照姿势（`ActionPose.Begin`），并**记录他当前的 spot/yaw**（已有 `_wasSpot/_wasYaw`）。
- `Tick(dt)` 变成 `AdvancePhases(dt)` + 现有 `Drive*`：
  - `Enter`：`PhaseTime += dt`；驱动他的路径与姿势、她的 clip/程序化、相机缓动；到 `EnterSeconds` 时 `Phase = Hold`，`Held = 0`。
  - `Hold`：`Held += dt` 的现有判据（接触 ≤ 容差）**只在 Hold 段累计**；同时叠加微动。
  - `Leave`：`PhaseTime += dt`；把他的路径反向求值、她的 clip 反向采样、相机反向缓动；到 `LeaveSeconds` 时调用现有 `Cancel()` 的还原分支（键盘、姿势、相机、`ClearPoseCount` 只加一次）。
- `Cancel()`（ESC 或外部调用）语义改为：**第一次**在 Enter/Hold → 切 `Leave`（从当前进度）；在 Leave → 忽略；离开完成后才真正还原。`Cancel()` 仍是唯一"结束动作"的公开入口，兼容现有测试。
- `OnDisable` 直接走立即还原（不播 Leave）：卸载场景时没有观众。

时长表（`PerformanceTiming` 静态类，集中在 `ActionDirector` 旁边）：

| 动作 | Enter | Hold | Leave |
|---|---|---|---|
| hands（生成 clip） | 2.6 s | 不定长 | 1.6 s |
| pat-her / pat-me | 0.9 s | 不定长 | 0.7 s |
| kiss | 1.4 s | 不定长 | 1.0 s |
| sit-together | 2.2 s | 不定长 | 1.4 s |

时长是设计决定，不是可调参数表；测试只断言"有明确值且 > 0"，不断言具体秒数（避免每次调时都改测试）。

### 5.2 每帧的层次（谁写哪根骨）

固定的层序，从底到顶：

1. **基础姿势**：`AvatarSeat` / `AvatarPose`（她坐着/他站立）+ `AvatarPose.ArmsDown`。
2. **动作层**：`ActionPose.Apply(rig, table, weight)` —— 现有常量表加一个 `weight`（`Quaternion.Slerp(identity, offset, weight)`），进场从 0→1、退出从 1→0。
3. **生成层（hands 的她）**：clip 采样写 `localRotation`（绝对），把写到的骨列入 `Claimed`；同一根骨时**生成层覆盖动作层**。
4. **微动层（Hold）**：程序化低频偏移，只写它自己声明的骨（Head、Spine、Hand），叠加在 3 之后。
5. **IK 层**：`TwoBoneIk` / `JoinHands` 把接触压进容差（这一层永远最后，所以微动不会破坏接触）。**IK 带权重**：Enter 的前 70% 权重为 0（让她的 clip/姿势自己走），后 30% 用 smoothstep 从 0→1 闭合最后几厘米；Hold 恒 1；Leave 前 30% 从 1→0 松手。过渡期 `ContactError` 只记录、不累加 `Held`。
6. **玩家身体同步**：`PlayerBody.ApplyNow()`（已有，LateUpdate）。

换成一句话：**时间轴决定"多重"，层序决定"谁赢"，IK 决定"贴不贴"**。

### 5.3 他的三段：路径 + 姿势（程序化）

新增 `PlayerPath`（`apps/unity/Assets/Scripts/Actions/PlayerPath.cs`）：

- 由两点构成：`From`（进场前的脚点）→ `To`（plan.Spot）；朝向从 `FromYaw` 到 `Yaw`。
- `Evaluate(u, out Vector3 feet, out float yaw)`：`u` 用 smoothstep 缓动；`feet` 沿直线；`yaw` 走最短角。
- **两种进场（按 spot 是否可站立判定）**：`Approach`（hands / pat-her / pat-me）真的走；`Settle`（kiss / sit-together）原地演。判定用 `Physics.CheckCapsule` 在目标姿势下逐段探测，目标点被家具占住就自动降为 `Settle`（kiss 的 spot 与长椅面差 0.30 m、sit-together 的 spot 整个落在床的 AABB 内，`CharacterController` 永远进不去；今天能成立全靠 `Teleport` 先关控制器，见 `PlayerMotor.cs:233-236`）。
- `Approach` 的起点：`Staging = To` 沿进场方向后退，直到"胶囊站得下且到目标无遮挡"，上限 `MaxApproach = 2.5 m`；玩家本来就在可行走区间内则从当前位置起手，`0` 位移时 Enter 只演姿势（判据 1 的夹具必须从 ≥1.2 m 外起手）。
- 每帧通过新方法 `PlayerMotor.MoveTo(Vector3 feet, float yaw, float dt)` 移动：内部用 `CharacterController.Move`（尊重碰撞）而不是改 transform；`dt` 用于把重力/贴地走一遍。禁止直接 `Teleport` 逐帧（会穿墙）。走完仍差 ≤ 0.05 m 时由 `Teleport` 补最后一下（补位量写进日志）。
- `Settle`：进场第一帧就 `Teleport` 到 plan.Spot（与今天一致），三段的可观感来自姿势渐变、她的 clip、相机缓动；`Enter` 期间**没有位移**。
- 姿势：进场时 `SetPosture` 仍立即生效（眼高/胶囊先到位），但**腿部与上身**由 `ActionPose` 的新表 + `PlayerLegs` 渐变：
  - 新表 `ActionPoses.StandToCrouch` / `Kneel` / `Sit`：只写 `LeftUpperLeg/LeftLowerLeg/LeftFoot` 及其右侧（现有 `Writable` 列表要扩到腿部六根骨）。
  - 渐变：进场 0→1 期间腿从站姿插值到目标姿势；退出反向。**这就顺手还了玩家腿部姿势的债**（现在蹲/跪/坐腿是直的）。
- 他的抬手：hands 用手臂表 + IK；其余动作沿用现有 `Drive*` 的手臂目标，但把"瞬移到目标"改成"每帧朝目标插值 `ReachBlend`"。

### 5.4 她的三段：clip 通道 + 程序化退化

**clip 通道（hands 先上）**

- 资产格式：**不用 `AnimationClip`，用烘好的 `TextAsset`（JSON）**，`apps/unity/Assets/Motion/hands-enter.json` / `hands-leave.json`（约 20 根骨 × 78 帧 × 4 分量，200–400 KB）。理由：`Animator` 会接管整棵骨架并与 `AvatarIdle`/`AvatarSeat` 抢写；`AnimationClip.SampleAnimation` 会把整个 clip 的骨一次性写下去、还要读回来做权重混合，行为依赖引擎内部；自己存四元数数组可以逐骨 `Slerp`、可单测、打包时只靠字段引用随包走。源数据 `apps/unity/Motion/source/reach42.json`（**从 `Assets/_Local/motion/` 复制出来并纳入版本管理**，作为可复现的出处）。
- 采样器 `apps/unity/Assets/Scripts/Actions/MotionTrack.cs`：字段 `public TextAsset Data; public bool Reverse;`；`Sample(float t)` 按 `t × fps` 在相邻两帧四元数之间 `Slerp` 写进复用缓冲；`Apply(AvatarRig rig, float weight)` 按 `Slerp(快照/静置, clip, weight)` 写入并 `Claim`。解析在 `Awake` 一次完成（帧数/骨名表），每帧零分配。
- 烘制：新编辑器方法 `Mas3D.Editor.MotionBaker.BakeHands()`（无参数，路径常量在类里），复用 retarget（镜像 X、分段朝向前向、脊柱折线 `spineAxis=1 / spineUp=0.35`）。**前提改动**：把 `CliMotion.PoseTo(rig, frame)`（`apps/unity/Assets/Editor/CliMotion.cs:604`，现在是 private）抽成 `MotionRetarget.PoseTo(...)`，`CliMotion` 与 `MotionBaker` 共用；抽出的验收是 `CliMotion` 现有出图逐像素不变。烘制流程 = 实例化 `Me`/`Monkia` 到固定座位 → 每帧 `PoseTo` → 读 20 根骨的 `localRotation` → 写 JSON（`AssetDatabase.Refresh`）。命令行：
  ```
  unity.ps1 run -- -executeMethod Mas3D.Editor.MotionBaker.BakeHands
  ```
- 绑定：`ActionDirector` 是 `RoomBootstrap` 运行时 `AddComponent` 出来的（`RoomBootstrap.cs:141-158`），**场景里没有它的字段可填**。所以 `RoomBootstrap` 新增 `public TextAsset HandsEnterData; public TextAsset HandsLeaveData;`（`RoomBootstrap` 本身在 `Main.unity` 里，引用随场景进包），创建 `ActionDirector` 时转交给 `MotionTrack`。缺失时 `null` → 退化。
- 分解：`reach42.json` 的 120 帧里，前 ~55 帧是"坐直 → 抬手"，取 `0..78`（2.6 s）作 enter；leave 用同一段**倒放**（`MotionTrack` 支持 `reverse` 标志），不额外生成。理由：倒放的抬手在观感上就是"放下手"，且与 enter 天然首尾相接，不会出现两段动作对不上。

**程序化退化（其余四个动作，以及 clip 缺失时）**

- 她的进场 = 现有常量表按 `weight` 0→1 应用 + 朝向/头部 aim 插值；退出反向。没有新资产，观感提升来自"有时间过程"而不是"动作更复杂"。
- 日志一行：`[stage] hands enter=clip leave=clip reversed` 或 `[stage] hands enter=procedural (clip missing)`。

### 5.5 保持段：轻微动态

- 只在她与他的**已声明骨**上叠加（不碰 IK 之后的手/头接触点）：她 `Spine` 的 ±0.8° 侧倾（0.11 Hz）、`Head` 的 ±1.2° 缓慢环视（0.07 Hz，两个不同相位）、手指 `LeftHand/RightHand` 每 3.7 s 一次 2° 的轻微收紧-放松；他 `Spine` 的 ±0.6° 呼吸性前后（0.16 Hz，与 `AvatarIdle` 的呼吸错开相位）。
- 全部由 `sin(2π f PhaseTime + φ)` 计算，无随机、无状态累积；幅度上限写进常量，测试断言"连续两帧的差 > 0 且 ≤ 上限"。
- 接触保证：微动在 IK **之前**，IK 会把手/头拉回接触点 → `ContactError` 不变；测试直接复用现有容差断言。
  - 手部收紧落在手腕自己的滚转轴上（`LookRotation` 之后后乘 `Euler(0,0,θ)`）：它转掌不挪掌，`HerPalm` 的 position 与 forward 逐位不变，接触代价恒为 0 m。
  - **例外：`kiss` 没有 IK** —— 偏移之后没有任何东西把她拉回去，而起始间隙只有 1.34 cm（容差 2 cm）。所以 kiss 的三个通道整体乘 `KissScale = 0.6`：100 s 周期内实测最大间隙 1.83 cm，仍在容差内。kiss 的测试必须同时钉「不超容差」与「间隙仍在变化」，只钉上限会被「倍频幅 = 0」（等于冻住）满足。
  - 已知盲区：`pat-her` 的 `BowHeadTo` 每帧把头 aim 回他的手，头部微动被它吃掉；脊柱与手不受影响。这一条不修（要修就得让 aim 也走 base 叠加），记在 T4 报告的偏差表里。

### 5.6 相机

- 进场：从当前 `Yaw/Pitch/Distance` 在 `EnterSeconds` 内 ease 到 plan 的目标（`SetOrbit` + `SetDistance` 每帧写）；`MouseInput = false` 期间不变。
- 退出：从当前位置反向 ease 回 `_wasCameraYaw/_wasCameraPitch/_wasCameraDistance`。
- 特例保留：kiss 的 `KissCameraRadius = 0.06 / KissCameraDistance = 1.9` 在进场第一帧就设（它防的是相机臂塌进她身体，不能等缓动），退出时与旧逻辑一样还原。

### 5.7 打断、异常与幂等

- ESC：Enter/Hold → `Leave`（不改 `_was*` 快照）；Leave 中再按 → 忽略；Leave 完成 → 还原并 `_current = null`。
- **两个出口，不是一个**：`Cancel()` 保持**瞬时还原**（现有 18 处调用、自检、打包退出、`OnDisable` 全部不变）；ESC 走新的 `RequestLeave()` —— 播完 Leave 才调用 `Cancel()`。演出动画只挂在 ESC 这条路上，`Cancel()` 永远是"立刻放手"。
- 因为 `Cancel()` 不变，`ClearPoseCount == 1`、相机/位置/姿态的旧断言全部原样成立；判据 3 改用 `RequestLeave()`。
- 幂等：`ClearPoseCount` 每次表演只 +1（现有测试钉着）；重复 `Cancel()` 无操作。
- 异常：进场中途抛异常 → 现有 `RestoreEntering` 分支保留（它已经覆盖键盘/姿势/相机/座位），新增的 `Phase` 一并归 `None`。

### 5.7.1 退场怎么收尾

- Leave 只在原地演：松手、收回上身、相机反向缓动，时长按表（1.6 / 0.7 / 1.0 / 1.4 s），**不沿进屋路径倒着走**（1.6 s 走不完 2.5 m，倒放节奏也对不上）。
- Leave 最后一帧做一次**瞬时还原**：位置/朝向回到 `_wasSpot/_wasYaw`（与今天的 `Cancel()` 完全一致），相机同理。这一次跳切由退场动画遮住，是本次明确接受的代价。
- 位置与相机在 Leave 期间**不动**（除姿势）：测试可以在 50% 处断言"还没跳"，在结束后断言"已经回位"。
### 5.8 边界裁决（外部实现者提出）

**他的腿：谁摆、脚怎么贴地**

- 他的骨架与她的不共享快照栈：`ActionPose` 是静态的、绑定 `Begin(rig)` 传入的那副骨架。新增 `apps/unity/Assets/Scripts/Actions/PlayerLegs.cs`：拥有自己的 `Base/Claimed`（按 `AvatarRig` 实例隔离），只写 `LeftUpperLeg / LeftLowerLeg / LeftFoot` 及其右侧。不扩 `ActionPose.Writable`。
- 腿长补偿：弯腿会让"髋→脚"的竖直距离变短。`PlayerLegs` 用 `PlayerBody` 骨架实测的 thigh/shin 长度，按当前髋/膝角算竖直缩短量，回给 `PlayerBody` 作额外下沉修正（或上抬），保证**脚底离地 ≤ 1.5 cm**。这是十几行，不引入腿部 IK。
- 判据 5 拆两条：`PlayerLegs` 的表本身做纯函数单测（不需要骨骼，本机与干净 clone 都能跑）；`FootStaysOnTheFloor` 需要真身体，按现有 `PlayerBody` 测试的规矩 `Assert.Ignore`（干净 clone 跳过，本机必跑）。
- 不在范围：走路时的脚部落点、脚尖弯折、腿部碰撞。

**sit-together 的时机**

- 她仍然在 `TryEnterAction` 里立刻 `SitOnBed`（今天的行为）；`Settle` 让他也被放到床面、姿态在 Enter 内渐变，**没有位移**，所以不会出现"坐着滑"。
- 她"爬上床"的过渡本次不做（需要另生成一段动作），列为已知限制。

**打包后怎么证明 clip 真的在**

- 自检 marker 从 `stage ok enter=…` 扩展为 `stage ok enter=2.601 clip=clip`：`clip=clip|procedural` 由 `ActionDirector` 实际拿到的 track 决定。
- `tools/player-smoke.ps1` 断言 `clip=clip`（不是任意值）：漏装资产 → 红。判据 6（缺失回退）只在编辑器测试里把字段置空模拟，不靠"日志里有 procedural"来证明。
### 5.9 可观测（日志与自检）

- 每次阶段切换一行：`[stage] hands enter->hold at 2.600s contact=0.004`、`[stage] hands hold->leave at 5.213s`、`[stage] hands done held=2.613`。
- 打包版自检（`PlayerSelfTest`）新增一行 marker：`stage ok enter=2.601 hold=1.002 leave=1.600`。`tools/player-smoke.ps1` 的 `$wants` 从六行加到七行（正则要求三个时长都 > 0 且 ≤ 3.0）。
- 现有 marker `action hands held=1.00 ok` 保持：它现在会在**保持段**里被打印（`Held ≥ 1` 后立刻返回），等待逻辑相应延长到 `enter + 1.5 s`。

## 6. 判据（写代码前先能红）

PlayMode（`apps/unity/Assets/Tests/PlayMode/`）：

1. `ActionEnterApproachesInsteadOfTeleporting`（只针对 `Approach` 动作，夹具从 1.2 m 外起手）：`Tick` 手动推进；进场 0.2 s 时玩家位置与原 spot 的差 > 0.05 m 且 < 全程距离；进场结束时距 plan.Spot ≤ 0.02 m。**红**：改回 `Teleport` 后第一帧就 0.00 m 差。
1b. `SettleActionsDoNotTravel`：kiss / sit-together 进场期间玩家位移恒 ≤ 0.001 m，但姿势在 Enter 内单调变化（膝角或髋高至少一处 ≥ 5°）。防"为了省事把 Settle 也做成瞬移 + 不动"。
2. `HoldKeepsThePoseAlive`：进入 Hold 后连续推进 2 s，她 `Head` 的 `localRotation` 在首末两帧的夹角 ≥ 0.3°；同一窗口的 `ContactError ≤ ContactTolerance`。**红**：删掉微动层 → 夹角 0。
3. `EscapeDuringEnterLeavesFromHere`：进场 0.5 s 时 `RequestLeave()`（ESC），玩家位置**不回跳**（0.5 s 时点与 Leave 50% 时点的差 ≤ 0.02 m），退出结束后回到 `_wasSpot`（≤ 0.02 m）、yaw 差 ≤ 1°、`ClearPoseCount == 1`。
4. `HeldOnlyCountsAfterEnter`：进场期间 `Held` 恒 0；进入 Hold 后接触达标才开始增长。
5. `CrouchingBendsThePlayersLegs`：pat-me 进 Hold 时，玩家 `LeftUpperLeg` 相对站姿的屈曲 ≥ 25°、`LeftLowerLeg` ≥ 25°（现有债务的判据：现在恒 0）；同帧 `FootStaysOnTheFloor`（本机真身体）脚底离地 ≤ 1.5 cm。纯函数版 `PlayerLegsTableBendsBothKnees` 在干净 clone 上也必须绿。
6. `MissingClipFallsBackToThePoseTable`：把 `HandsEnter` 置 null，进场仍完成、`Phase` 走到 Hold、`Held` 正常增长、日志含 `procedural (clip missing)`。
7. `ClipTrackSamplesTheSamePoseTwice`（`MotionTrack` 单元级）：同一 `t` 采样两次结果一致；`weight=0` 时骨骼等于快照。防"采样有隐藏状态"。

出图判据（`[Explicit]` 截图测试，不进门）：

8. `HandsPerformanceShowsMovementInAllThreePhases`：在进场 1.3 s、保持 1.0 s、退出 0.8 s 各拍一张（同机位），用 sharp 度量相邻两张的像素差 > 3%（说明画面在动），并把三张拼成 `apps/unity/Shots/hands-phases.png` 供人眼复核。

回归：

9. 现有八个动作测试文件全绿（`ActionHandsTests` / `ActionPatTests` / `ActionKissTests` / `ActionSitTogetherTests` / `ActionDirectorTests` / `ActionHotkeysTests` / `ActionSceneTests` / `ActionSmokeTests`）—— 它们大多假设"进场即接触"，迁移规则见 §6.1。
10. `tools/check.ps1 -Python D:\Python\python.exe` 六门 6/6；smoke 七行 marker，且 `stage` 行必须是 `clip=clip`（漏装资产就红）。

变异红（证明判据有效）：把 `ActionDirector.Tick` 的 `Enter` 分支改成"直接 `Phase = Hold` 且 `Teleport`"→ 判据 1、8 必须红；删掉微动层 → 判据 2 必须红。

### 6.1 旧测试的迁移（这次改动的最大隐性成本）

现有 8 个动作测试文件（见上）默认"进场即接触"：例如 `ActionHandsTests.cs:66-73` 在 `TryEnterAction` 之后只 `Tick(1f/60f)` 一帧就断言两掌 ≤ 2 cm；`:76-86` 推进 4 s 后断言 `Held ≥ 3`。

三段化之后这两条**必然红**——这是正确行为，不是回归。迁移规则：

- 新增测试工具 `apps/unity/Assets/Tests/PlayMode/ActionTestDrive.cs`：`public static int ToHold(ActionDirector d, float maxSeconds = 6f)` 按 1/60 s 推进直到 `Phase == Hold`（超时 `Assert.Fail` 并打印阶段）；`public static int ToLeaveEnd(ActionDirector d, float maxSeconds = 6f)` 推进到 `Current == null`；`public static int Hold(ActionDirector d, float seconds)`。
- 断言接触/姿势/`Held` 的用例：`TryEnterAction` 之后先 `ToHold()` 再断言；`Held` 类改为 `ToHold(); Hold(3f);`。
- 断言 `Cancel()` 立即还原的用例**不需要改**：`Cancel()` 仍是瞬时出口（§5.7），只有 ESC 走 `RequestLeave()`。
- 断言"进场即快照/即锁键盘/即置 `_current`"的用例（`ActionDirectorTests` 的原子性那组）**不需要改**：它们发生在 `TryEnterAction` 返回时，仍然成立。
- 计划必须逐个文件列出改动点数；实施后跑全量 PlayMode，不允许"测试改不动就跳过"。

## 7. 资产、提交与回滚

- 新增受版本管理的资产：`apps/unity/Assets/Motion/hands-enter.json`、`hands-leave.json`（烘好的四元数，预计各 200–400 KB）+ 源 `apps/unity/Motion/source/reach42.json`（约 250 KB，已在 `_Local` 有副本）。
- `apps/unity/Assets/Motion/*.json.meta` 一并提交；场景里 `ActionDirector` 的两个字段写 `TextAsset` 引用（`Main.unity` 会再涨几十行）。
- 回滚：字段置空即退化为程序化（判据 6 保证这条路可用），不需要回滚资产。
- 提交：实施完成、六门绿之后**询问 YG**再提交；本次不 push。

## 8. 已知限制（批准即接受）

- 玩家行走仍是滑行（无 walk cycle），进场看得到"平移"；这是非目标，等下一次。
- 她的生成 clip 来自单人提示词，**不是**针对"他站在 0.62 m 处"生成的；接触靠 IK 兜，观感上她的手可能有一小段"朝空气伸"。
- 保持段的微动是正弦叠加，时间长了会看出周期性（约 9 s 一个完整周期）。
- 只有 hands 有生成动作；其余四个的"表演感"只来自时长与缓动。
- kiss 与 sit-together 的 spot 被家具占住，进场是**原地演**（按下热键那一刻瞬移到位）；只有 hands / pat-her / pat-me 会走过去。
- 退出动画最后一帧有一次瞬时回位（`_wasSpot` / 相机），与今天一致；"走回去"不在本次。
- sit-together 里她是**瞬间**坐到床上的，没有"爬上床"的过渡。
- 他的腿只有旋转表 + 竖直补偿，没有脚部 IK；跳跃/走路时脚仍可能离地。
- clip 采样与 `AvatarIdle` 的呼吸可能在同一根骨上叠加两次（Chest/Spine）；计划里把 Idle 的 claim 尊重到底，冲突的骨以生成层为准。

## 9. 分阶段实施顺序（给 written plan 的骨架）

1. **S1 时间轴**：`ActionPhase` + 时长表 + `Tick` 分段 + ESC 语义；五个动作接到三段上（全部程序化）；判据 3/4。
2. **S2 他的三段**：`PlayerPath` + `MoveTo` + 腿部姿势表 + 判据 1/5。
3. **S3 保持微动**：微动层 + 判据 2。
4. **S4 她的 clip 通道**：`MotionTrack` + `MotionBaker` + hands 接 clip + 判据 6/7/8。
5. **S5 打包与回归**：marker 七行、六门、变异红、设计文档 §5.29 实测记录。

每个阶段独立可验收；S1–S3 完成即"五个动作都有三段"，S4 才让 hands 真正用上生成动作。