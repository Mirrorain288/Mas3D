# 玩家身体进包（方案 A：场景内联）· 设计

- 日期：2026-11-08
- 状态：待 YG 批准（未批准前不写产品代码）
- 决策来源：m21144「先1后2」（1=身体进包，2=动作管线接进产品）、m21145 三方案比较、m21156 选 A
- 规模：小变更（短设计，不走完整 spec）

## 1. 意图与为什么

「我走进这个房间、有身体」是 §2 目标 3，也是反目标「变成 Unity 壳 + 内嵌浏览器」的另一半：没有身体，第一人称就只是一个悬空的镜头。

现在打包版里这件事**不成立**，且不是「没做好」而是**通道从未接上**：

- `apps/unity/Assets/Scripts/PlayerBody.cs:240-248`：`LoadPrefab()` 整个函数体在 `#if UNITY_EDITOR` 内，非编辑器分支直接 `return null`。
- 同一文件 `:49-50` 早就留了 `public GameObject Prefab;`（注释写着「baked into the scene」），但**全仓没有任何地方给它赋值**：`CliPlayer.Cs` 只用 `body.Avatar` 做判据（`:373`），`Main.unity` 里 `Me.vrm` 出现 0 次。
- 于是打包版走到 `:109` 的 `Debug.LogError("[body] 找不到 …")`，玩家退回胶囊。P6 的 player 门只跑 `-selftest`（桥接 + 一轮对话），碰不到这条路径。

结论：不是修 bug，是**把一条已经设计好、却只接了编辑器一侧的通道接到打包版**。

## 2. 目标 / 非目标 / 反目标

**目标**

1. 由 `tools/unity.ps1 build` 产出的 `build/Mas3D.exe`双击运行后，玩家脚下有身体（`Me.vrm`），第三人称可见、近距藏头照旧。
2. 身体的材质必须**真的可渲染**：不能出现 `shader == null` 的粉红/不可见（§5.24 SSAO 剥离导致 exe 黑屏是同一类事故）。
3. 有一条**自动化判据**能在打包版上抓住「身体没进包」（现在的盲区正是「没人验」）。
4. 编辑器里的既有行为不变：`-nobody 1` 仍然无身体；截图/批处理不因新增内容而改变取景。

**非目标**

- 不做蹲/坐的膝盖弯曲（现在蹲坐是整体下沉、脚入地）；那是新写一层玩家腿部姿势，另开一次。
- 不做走路动画（腿不会迈）、不做换装/换模型。
- 不做 B（存真 Prefab）与 C（StreamingAssets 运行时导入）；理由见 §4。
- 不改 `PlayerMotor` 的移动权威、不改相机、不改动作系统。

**反目标（技术上跑通也算失败）**

- 把模型预生成到 `Resources` 里、包里塞一份编辑器才有的路径。
- 用「打包后不崩」冒充「身体在」：必须有 renderer 数、身高、shader 名的实测值。
- 为了省事把 `#if UNITY_EDITOR` 直接删掉、让非编辑器去 `AssetDatabase.LoadAssetAtPath`（编译不过或运行时崩）。

## 3. 约束（不可协商）

- `Assets/_Local/` 被 `apps/unity/.gitignore` 忽略 → `Me.vrm` 不进公开仓库。场景里内联的是**该资产的引用**，干净 clone 上只剩空壳（与 `Monkia` 同一笔已记录的债，见 §5.20 / §7）。
- Unity 单实例：任何 `unity.ps1` 动作前必须关编辑器。
- IL2CPP + Low stripping：只靠运行时 `Shader.Find` 的材质有被剥离风险；**场景引用**的材质会被自动收进包，这是选 A 的核心原因。
- 提交由 YG 拍板：AGENTS.md 明确「不要自行 git commit / git push」。

## 4. 三条路线与取舍

| 路线 | 做法 | 优点 | 代价/风险 |
|---|---|---|---|
| **A 场景内联（选定）** | `CliPlayer.BuildScene` 把 `Me.vrm` 实例化进 `Player` 下、**inactive**；`PlayerBody` 增加 `Baked` 引用，`Spawn` 优先**收编（adopt）**它 | 材质/骨骼/MToon 随场景序列化进包；`Monkia` 已证明这条机制在本工程可行（15,372 行场景、P4 起一直绿） | `Main.unity` 再涨一万余行；干净 clone 只剩空壳 |
| B 存真 Prefab | `PrefabUtility.SaveAsPrefabAsset` 存 `Assets/_Local/avatars/Me.prefab`，填现有 `Prefab` 字段 | 场景不膨胀、加载可增量 | VRM 表达式/骨骼引用跨资产保存后是否失效未验；要额外 spike |
| C 运行时载入 | `Vrm10.LoadPathAsync` 读 StreamingAssets | 资产不进 Asset 数据库 | 运行时实例化的材质靠 `Shader.Find`，MToon 变体可能被剥离；IL2CPP 下风险最高 |

**选 A**：唯一在现场验证过、不引入新失败面的方案。B 需要一个不确定的 spike，C 撞上本工程已经付过代价的那类事故（SSAO 被剥离 → 每帧 `CreatePipeline()` 失败 → exe 黑屏）。场景膨胀是**已知且可度量**的代价，换来的是「材质一定进包」。

## 5. 定下的做法（A 的落地形状）

1. **烘焙**：`CliPlayer.BuildScene` 在建成 `Player` 之后，用 `AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Local/avatars/Me.vrm")` 实例化一份，命名 `Me`，挂到 `Player` 下、`localPosition/rotation = zero`，**`SetActive(false)`**，并把它写进 `PlayerBody.Baked`。缺资产时只 `LogWarning`，场景照建（干净 clone 不炸）。
2. **收编**：`PlayerBody.Spawn()` 的三级来源顺序改为 **`Baked`（adopt）→ `Prefab`（Instantiate）→ `LoadPrefab()`（仅编辑器）**。
   - adopt 分支：`SetActive(true)` → `GetComponent<AvatarRig>()`（没有就 `AddComponent`）→ `EnsureBound()`；**不 Instantiate、不重挂父**（已经是 `Player` 的子物体，重挂会破 SpringBone 与 Animator 偏移，`AvatarRig.cs:89` 的注释记的正是这条）。
   - `AvatarRig` 已经能从自身子树里 `GetComponentInChildren<Vrm10Instance>` 绑上（`:62`），所以 adopt 后不需要额外接线。
3. **后续步骤一律复用**：`StripColliders` → `FitToHeight`（按 `TargetHeight = 1.70` 缩放、算 `_feetOffset`）→ `AvatarPose.ArmsDown` → `CollectHeadRenderers` → `ApplyNow`。adopt 与 Instantiate 两条路走**同一个收尾函数**，不许各写一份。
4. **`-nobody 1` 优先于一切**：BootArgs 已有 `NoBody`，行为不变——直接不调 `Spawn()`，烘焙实例保持 inactive。
5. **场景里的 inactive 实例不参与任何取景/判据**：它没有 renderer 被启用，`Camera` 看不见它；批处理截图不变。
6. **打包版自检加一行**：`PlayerSelfTest` 在 `done` 之前写 `body ok renderers=<n> height=<h:F3> shader=<name>`；`shader` 取第一个非空 `Renderer.sharedMaterial.shader.name`，为空则整条失败。`tools/player-smoke.ps1` 的判据从「五行」改成「六行」（`token src` / `bridge ok` / `reply ok` / `action hands` / `body ok` / `done`）。

## 6. 验收判据（可测，不靠肉眼）

| # | 判据 | 怎么测 |
|---|---|---|
| 1 | 场景里身体真的烘进去了 | `tools/unity.ps1 player` 后 `[player]` 日志有 `body baked`；`Main.unity` 里 `Me.vrm` 引用数 > 0 |
| 2 | 收编路径成立 | PlayMode 新用例 `TheSceneCarriesABakedBodyTheBuildCanAdopt`：加载 `Main`、`Player` 下存在 inactive 的 `Me`、`Spawn()` 后 `Spawned == true`、层级里只有一个 `Me`（没有 Instantiate 出第二份）、collider 数 = 0 |
| 3 | 打包版真的有身体 | `tools/unity.ps1 build` → `tools/player-smoke.ps1`：marker 六行齐全、`body ok` 行里 `renderers > 0`、`height` 落在 1.70 ± 0.02、`shader` 非空 |
| 4 | 旧行为不变 | 现有 `PlayerBodyTests`（跟随误差 ≤0.02 m / 1°、近距藏头、剥 collider、`-nobody` 降级）与 `CameraStabilityTests` 全绿；`-nobody 1` 的截图与今天逐像素同级 |
| 5 | 判据有牙（变异） | 删掉 adopt 分支 → 判据 3 红（打包版 marker 缺 `body ok`）；把 `Baked` 写成 null → 判据 1/2 红 |

六门终跑：`tools/check.ps1`（web / bridge / unity-edit / unity-play / smoke / player）一轮 `summary: 6/6 ok`；EditMode 与 PlayMode 的计数在报告里写实际数字，不写「全绿」。

## 7. 已知限制（本次不修，记录在案）

1. `Main.unity` 体积再涨（`Me` 16.6 MB 资产的引用 + 内联层级，预计 +1 万行量级）；`unity.ps1 player` 重烤时间会变长。
2. 干净 clone（无 `Assets/_Local/avatars/Me.vrm`）上身体是空壳，与 `Monkia` 同一笔债；本次只保证「打警告、不炸」。
3. 蹲/坐仍是整体下沉、脚入地；走路仍无迈步动画。
4. 打包版身体**没有表情/口型驱动**——`AvatarMood` 只管她，玩家的脸是静止的。

## 8. 留给实现者

- `Baked` 字段命名与 Inspector 提示文案；是否加 `[Tooltip]`。
- adopt 时是否需要 `AvatarRig.Attach` 的幂等二次调用（`EnsureBound` 已覆盖，但 `Spawn` 现在会 `Attach` 一次）。
- `Me` 实例在场景里的容器命名（`Me` 还是 `Body`）与是否加注释物体。
- 烘焙是否顺手把 `PlayerBody.Prefab` 也指上（本次不需要）。
- 自检行里 `height` 取 `Avatar.Measure().size.y` 还是 `root.localScale` 反算（建议前者，和 `FitToHeight` 同源）。
