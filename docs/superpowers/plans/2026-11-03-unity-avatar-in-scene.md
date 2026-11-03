# Unity Avatar In The Room Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: use `subagent-driven-development` (or `executing-plans`) to implement this plan task by task. Every Task is one dispatchable unit: write the failing test, watch it fail, make it pass, then stop and hand back. Steps use `- [ ]` checkboxes.

**Goal:** 把 Monkia 从「能导入的 VRM」变成「坐在 S1 长凳上、会呼吸会眨眼、跟着对话状态换表情」的场景角色，成为 P4 四件动作的主体。

**Architecture:** 五层，一层只做一件事。
- `AvatarRig` 认得这具 `Vrm10Instance`：给骨骼、给表情、给包围盒。它不知道房间，也不知道对话。
- `AvatarPose` + `AvatarSeat` 只负责「按 anchors.json 的 S1 把她摆成坐姿」。姿态是数据（每根骨的 localRotation 表），不是散落在代码里的魔法角度。
- `AvatarIdle` 只在姿态之上叠偏移：呼吸、微微摇摆、眨眼、视线跟头。它不拥有骨骼基线。
- `AvatarMood` 只把 `ConversationController.Phase` 翻成表情权重，不碰眨眼与口型。
- `AvatarActor` 是场景里唯一的门面：握四个组件，绑视线、绑对话、挂胶囊。

分层是为了可测：每层都能在「没有场景、没有桥接、没有玩家」的情况下单独跑 PlayMode 测试。

**Tech Stack:** Unity 6000.6.7f1 + URP 17.6 + UniVRM `com.vrmc.vrm` 0.131.4（VRM 1.0 runtime）+ Cinemachine 6.6 + Unity Test Framework（PlayMode / NUnit 3）。

**Spec:** `docs/superpowers/specs/2026-11-01-unity-rebuild-design.md` — 第 3 节（模型事实，本计划修正为 VRM 1.0）、5.15（锚点判据）、5.17（S1 座面）、5.18（P2 场景与运行时约束）、7（P3/P4 定位）。

## Global Constraints

- 编辑器是 `D:/Unity_Editor/6000.6.7f1/Editor/Unity.exe`，一切都走包装脚本。执行前先 `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force`。
  - 测试：`powershell -ExecutionPolicy Bypass -File tools/unity.ps1 test -Mode PlayMode`
  - 重建场景：`powershell -ExecutionPolicy Bypass -File tools/unity.ps1 player`
  - 截图：`powershell -ExecutionPolicy Bypass -File tools/unity.ps1 capture`
  - （命令行里正斜杠在 PowerShell 与 Unity CLI 下都可用；写成反斜杠也行，但别改动脚本本身的 ASCII 属性。）
- **不要 git commit / git push**（AGENTS.md）。每个 Task 收尾只 `git add -A apps/unity`，把暂存区留给 Lead 复核后统一提交。新建 `.cs` 首次导入后 Unity 会生成同名 `.meta`，一并 `git add`。
- `tools/unity.ps1` 必须保持纯 ASCII（PS 5.1 读无 BOM 的 UTF-8 中文会报「switch 语句的子句中缺少语句块」）。
- 读 Unity 输出必须落文件再挑行：`... test -Mode PlayMode *> Logs/plan-test.log`，再 `Select-String -Path Logs/plan-test.log -Pattern 'error CS|failed|passed'`。把 stderr 接进管道会把进度行变成 NativeCommandError，让整条命令假报 exit 1。
- 渲染与截图类调用**不加** `-nographics`（SkinnedMesh + MToon 要 GPU）。
- 资产只按路径引用、不进仓库：`Assets/_Local/avatars/Monkia.vrm`、`Assets/_Local/avatars/Me.vrm`（`apps/unity/.gitignore` 的 `/Assets/_Local/`），截图目录 `apps/unity/Shots/`。
- **模型事实**（2026-11-03 两轮实测，口径以这条为准）：磁盘上的 `.vrm` 是 **VRM 0.0**（GLB `extensionsUsed` 含 `VRM`、`specVersion 0.0`、VRoid Studio 1.21、骨骼 `hips` 共 54 根）；导入器 `VrmScriptedImporter` 默认 `MigrateToVrm1 = true`，**导入后的运行时是 VRM 1.0**：`Vrm10Instance` + `UniHumanoid.Humanoid` + `Animator` 各一个，骨骼 `J_Bip_*`。Monkia：57 blendShape / 17 材质（`VRM10/Universal Render Pipeline/MToon10`）/ T-pose 包围盒 1.37 x 1.61 x 0.42（身高 1.61 m），迁移后 17 个表情键齐全（happy / angry / sad / relaxed / surprised / aa..oh / blink / blinkLeft / blinkRight / lookUp..Left / neutral）。asmdef 需要 `VRM10` **与** `UniHumanoid` 两个引用（Task 1 已加）。
- 座位只认 `Anchors/anchors.json` 的 `S1`：x 0.105 / y 0.614（座面 topY 0.604）/ z -0.024 / facingY -90。S2 仍是「不可用」，本计划不碰。
- 现有基线不许退：**EditMode 44 通过、PlayMode 28（27 通过 + 1 个 `[Explicit] UiSceneShot`）**。不许为了变绿删改旧断言。
- 界面中文文案（待机、她坐在书桌前等你先开口。等）一个字都别动。

## Review Focus

1. **膝盖朝后、腿穿进床** — 坐姿最容易把大腿转成反方向。判据：Task 2 的 `SittingPutsTheKneesInFrontOfTheHips` 与 `Shots/avatar-pose-side.png`。
2. **髋部压进座面** — 正面图看不出来，裙摆与大腿可能已经嵌进长凳。判据：Task 2 的 `NothingSinksIntoTheBench`（BakeMesh 采样长凳足迹）。
3. **表情预设不是标准的** — 本机 Monkia 迁移后 17 键齐全，但 `SetWeight` 对不存在的键**静默忽略**（不报错、不动画面），换一具模型就会静默不动。判据：Task 4 的 `MissingPresetsDoNotThrow` 必须用 `ExpressionKey.CreateCustom("sulk")` 这种模型上确实没有的键，不许拿 `Happy` 假装缺失。
4. **Idle 与 VRM 运行时抢骨骼** — `Vrm10Instance.UpdateType` 默认 LateUpdate，视线与 SpringBone 也在写 Transform；若 Idle 直接给 `bone.localRotation` 赋偏移，会把坐姿抹掉。判据：Task 3 的 `TheIdleBaselineSurvivesTicks`。
5. **她的碰撞体影响玩家** — 出生点 A2 就在床边。判据：Task 5 的 `ThePlayerSpawnStaysClear` 与全套走位回归。

---

## Task 1 — AvatarRig：把 Vrm10Instance 认出来

**Files**
- Create `apps/unity/Assets/Scripts/Avatar/AvatarRig.cs`（目录连同 `Assets/Scripts/Avatar/` 一起新建，Unity 导入后会生成 `.meta`）
- Modify `apps/unity/Assets/Scripts/Mas3D.Runtime.asmdef`（references 追加 `VRM10`）
- Modify `apps/unity/Assets/Tests/PlayMode/Mas3D.Tests.PlayMode.asmdef`（references 追加 `VRM10`）
- Create `apps/unity/Assets/Tests/PlayMode/AvatarRigTests.cs`

**Interfaces**
- Consumes: UniVRM 的 `Vrm10Instance` / `UniHumanoid.Humanoid` / `ExpressionKey`。
- Produces: `Mas3D.Avatar.AvatarRig`
  - `public const string DefaultPrefabPath = "Assets/_Local/avatars/Monkia.vrm";`
  - `public Vrm10Instance Instance { get; }`、`public Animator Animator { get; }`、`public Transform Root { get; }`
  - `public Transform Bone(HumanBodyBones bone)` — 找不到返回 `null`，不抛
  - `public bool HasExpression(ExpressionKey key)`、`public float ExpressionWeight(ExpressionKey key)`、`public void SetExpression(ExpressionKey key, float weight)`
  - `public static AvatarRig Spawn(GameObject prefab, string name = "Monkia")`
  - `public Bounds Measure()`

**Steps**

- [ ] **1.1 写失败测试** `apps/unity/Assets/Tests/PlayMode/AvatarRigTests.cs`，三个用例。

```csharp
TheImportedAvatarCarriesTheVrmRuntime
var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(AvatarRig.DefaultPrefabPath);
Assert.IsNotNull(prefab, "Monkia.vrm 没导入：" + AvatarRig.DefaultPrefabPath);
var rig = AvatarRig.Spawn(prefab);
try
{
    Assert.IsNotNull(rig.Instance, "Spawn 之后必须拿到 Vrm10Instance");
    Assert.IsNotNull(rig.Animator, "Animator 是 Humanoid 骨骼的入口");
    Assert.IsNotNull(rig.Bone(HumanBodyBones.Hips), "hips 必须存在");
    Assert.IsNotNull(rig.Bone(HumanBodyBones.LeftLowerLeg), "膝盖是坐姿的判据");
    Assert.IsNotNull(rig.Bone(HumanBodyBones.RightUpperArm), "手臂要能放下");
}
finally { Object.DestroyImmediate(rig.gameObject); }
```

```csharp
SheIsOneMetreSixtyOneTallAndArmsOut
var size = rig.Measure().size;
Assert.That(size.y, Is.InRange(1.50f, 1.75f), "她该有 1.61 m 高，实测 " + size.y);
Assert.That(size.x, Is.GreaterThan(1.0f), "T-pose 的手臂是张开的，宽度应超过 1 m");
```

```csharp
AnUnknownExpressionIsReportedNotThrown
var key = ExpressionKey.CreateFromPreset(ExpressionPreset.surprised);
Debug.Log("surprised present=" + rig.HasExpression(key) + " weight=" + rig.ExpressionWeight(key));
rig.SetExpression(key, 0.5f);                       // 不存在也必须安静地什么都不做
Assert.LessOrEqual(rig.ExpressionWeight(key), 1f);  // 只钉「查询与越界写入都不炸」
```

- [ ] **1.2 跑红**：`tools/unity.ps1 test -Mode PlayMode`，期望编译错误 `The type or namespace name 'AvatarRig' could not be found`。

- [ ] **1.3 实现** `AvatarRig.cs`（namespace `Mas3D.Avatar`）：
  - `Spawn(prefab, name)`：`Instantiate(prefab)`，改名，`AddComponent<AvatarRig>()` 后 `Attach`。
  - `Attach`：`Instance = root.GetComponentInChildren<Vrm10Instance>(true)`；`Animator = Instance ? Instance.GetComponent<Animator>() : root.GetComponentInChildren<Animator>(true)`；`Root = Instance ? Instance.transform : root.transform`。
  - 设 `Animator.applyRootMotion = false`、`Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate`、`Animator.enabled = false`（人形骨骼由我们与 UniVRM 驱动，不跑状态机）。**不要**把 VRM 根重新挂到别的父节点下（SpringBone 与 Animator 的偏移会错）。
  - `Bone(...)` 走 `Instance.Humanoid.GetBoneTransform(bone)`，取不到兜底 `Animator.GetBoneTransform(bone)`。
  - 表情三方法包 `Instance.Runtime.Expression`（`HasExpression` 用 `GetWeights().ContainsKey(key)`，这个类没有 HasKey；`SetExpression` 未知键 no-op 并 `Clamp01`）。`Instance.Runtime` 的 getter 是懒建，**活实例上永远非 null**，别把 null 检查当「未初始化」信号（销毁后它抛 `MissingReferenceException`）。
  - `Measure()`：把所有 `Renderer.bounds` 求并集（空则返回 `new Bounds(root.position, Vector3.zero)`）。
  - 两个 asmdef 的 `references` 加 `VRM10`。

- [ ] **1.4 跑绿**：`AvatarRigTests` 3 通过，PlayMode 总通过 30，0 failed。

- [ ] **1.5 暂存**：`git add -A apps/unity`（含新 `.meta`），不提交。

---

## Task 2 — AvatarPose / AvatarSeat：坐到 S1 上，且不穿模

**Files**
- Create `apps/unity/Assets/Scripts/Avatar/AvatarPose.cs`
- Create `apps/unity/Assets/Scripts/Avatar/AvatarSeat.cs`
- Create `apps/unity/Assets/Editor/CliAvatarPose.cs`（探针：坐姿正面 + 侧面各一张图）
- Modify `tools/unity.ps1`（ValidateSet 与分发里各加一条 `avatarpose`，照 `player` 的写法调 `Mas3D.Editor.CliAvatarPose.Probe`）
- Create `apps/unity/Assets/Tests/PlayMode/AvatarSeatTests.cs`

**Interfaces**
- Consumes: `AvatarRig`（Task 1）、`Mas3D.AnchorTable.SeatEntry`。
- Produces:
  - `Mas3D.Avatar.AvatarPose` — `public static void Sitting(AvatarRig rig)`、`public static void Standing(AvatarRig rig)`、`public static IReadOnlyDictionary<HumanBodyBones, Quaternion> Capture(AvatarRig rig)`
  - `Mas3D.Avatar.AvatarSeat : MonoBehaviour` — `public AvatarRig Rig`、`public const float HipsGap = 0.14f`（Lead 裁决 2026-11-03：这具模型的臀面在髋关节下 0.141 m，0.05 会让臀部陷进 0.604 的座面 8 cm；0.14 让臀面落在 0.613）、`public bool Seated { get; }`、`public Vector3 HipsWorld { get; }`、`public void Sit(SeatEntry seat)`、`public void Stand(Vector3 feet, float facingY)`

**Steps**

- [ ] **2.1 写失败测试** `AvatarSeatTests.cs`。夹具分两种：
  - 只用她本人：`AssetDatabase.LoadAssetAtPath<GameObject>(AvatarRig.DefaultPrefabPath)` + `AnchorTable.Load()`，每例 `try/finally` 销毁；把 `AvatarSeat` 之外的时间驱动全部关掉。
  - `NothingSinksIntoTheBench` 还要一间房：照 `PlayerWalkTests` 的做法载 `Assets/_Local/room/mita-room.glb`、把房间归零到地板 y=0 与 xz 居中，否则 `GameObject.Find("Mesh_0001out")` 找不到长凳。

```csharp
SittingPutsTheHipsOnTheBench
var s1 = table.Seat("S1");
var seat = rig.gameObject.AddComponent<AvatarSeat>(); seat.Rig = rig;
seat.Sit(s1);
yield return null;                              // 等一帧，让 UniVRM 的 LateUpdate 跑完再量
Assert.AreEqual(s1.y + AvatarSeat.HipsGap, seat.HipsWorld.y, 0.02f, "髋部没落在座面上");
Assert.AreEqual(s1.x, seat.HipsWorld.x, 0.03f, "横向偏出座面");
Assert.AreEqual(s1.z, seat.HipsWorld.z, 0.03f, "纵深偏出座面");
Assert.IsTrue(seat.Seated);
```

```csharp
SittingPutsTheKneesInFrontOfTheHips
var forward = Quaternion.Euler(0f, s1.facingY, 0f) * Vector3.forward;
var hips  = rig.Bone(HumanBodyBones.Hips).position;
var knee  = rig.Bone(HumanBodyBones.LeftLowerLeg).position;
var ahead = Vector3.ProjectOnPlane(knee - hips, Vector3.up);
Assert.Greater(Vector3.Dot(ahead.normalized, forward), 0.15f, "膝盖朝后了：大腿转反了方向");
```

```csharp
NothingSinksIntoTheBench
// 长凳 = 渲染器 Mesh_0001out 的世界包围盒（anchors.json 的 S1.mesh 同源）；只取落在其 xz 足迹内的顶点
var bench = GameObject.Find("Mesh_0001out").GetComponent<Renderer>();
var b = bench.bounds;
float lowest = float.PositiveInfinity;
foreach (var smr in rig.GetComponentsInChildren<SkinnedMeshRenderer>())
{
    var mesh = new Mesh(); smr.BakeMesh(mesh, true);
    try
    {
        foreach (var v in mesh.vertices)
        {
            var w = smr.transform.TransformPoint(v);
            if (w.x < b.min.x - 0.02f || w.x > b.max.x + 0.02f) continue;
            if (w.z < b.min.z - 0.02f || w.z > b.max.z + 0.02f) continue;
            lowest = Mathf.Min(lowest, w.y);
        }
    }
    finally { Object.DestroyImmediate(mesh); }
}
Assert.GreaterOrEqual(lowest, b.max.y - 0.02f, "有身体顶点穿到座面以下：" + lowest);
Assert.GreaterOrEqual(rig.Measure().min.y, -0.01f, "脚不能穿地板");
```

```csharp
StandingRestoresThePose
var standingHips = rig.Bone(HumanBodyBones.Hips).position.y;
seat.Sit(s1); yield return null;
seat.Stand(new Vector3(0.105f, 0f, -0.024f), s1.facingY); yield return null;
Assert.AreEqual(standingHips, rig.Bone(HumanBodyBones.Hips).position.y, 0.01f, "站起来没恢复");
Assert.IsFalse(seat.Seated);
```

- [ ] **2.2 跑红**：编译失败（`AvatarSeat` 不存在）。

- [ ] **2.3 实现 AvatarPose**：一张 `(HumanBodyBones, Vector3 euler)` 表 + 基线缓存（`Capture` 存 prefab 的 T-pose，`Sitting` 永远从基线算起，**不许在上一帧结果上叠乘**）。
  初始值如下（**符号大概率要改，改表不改断言**；Unity 里绕 X 负值把腿送向 +z 前方，左臂骨指向 +x，所以 Z 取负才是「放下」）：

```
| 骨 | euler |
|---|---|
| Hips | (-6, 0, 0) |
| Spine | (4, 0, 0) |
| Chest | (2, 0, 0) |
| LeftUpperLeg | (-78, 0, 4) |
| RightUpperLeg | (-78, 0, -4) |
| LeftLowerLeg | (76, 0, 0) |
| RightLowerLeg | (76, 0, 0) |
| LeftFoot | (6, 0, 0) |
| RightFoot | (6, 0, 0) |
| LeftUpperArm | (0, 0, -62) |
| RightUpperArm | (0, 0, 62) |
| LeftLowerArm | (0, 0, -12) |
| RightLowerArm | (0, 0, 12) |
```

- [ ] **2.4 实现 AvatarSeat**（`[DisallowMultipleComponent]`，`Rig` 为空时 `Awake` 里 `GetComponent<AvatarRig>()`）：
  - `Sit(seat)`：根设到 `(seat.x, 0, seat.z)` 与 `Quaternion.Euler(0, seat.facingY, 0)` → `AvatarPose.Sitting(Rig)` → 量髋部世界高度 `h` → 根整体 `y += (seat.y + HipsGap) - h`（**只抬根，不动骨**）→ `Seated = true`。
  - `HipsWorld` = `Rig.Bone(HumanBodyBones.Hips).position`。
  - `Stand(feet, facingY)`：`AvatarPose.Standing(Rig)` → 根归位到 `feet` 与 `facingY` → `Seated = false`。

- [ ] **2.5 写探针** `CliAvatarPose.Probe()`：载 prefab → `Spawn` → `Sit(S1)` → 一盏平行光 + 相机（fov 30）：
  - 正面：`S1 - forward * 2.4 + up * 1.05`，看向 `S1 + up * 0.75`（`forward = Quaternion.Euler(0, facingY, 0) * Vector3.forward`）
  - 侧面：`S1 + right * 2.4 + up * 1.05`，`right = Quaternion.Euler(0, facingY + 90, 0) * Vector3.forward`
  - 720x960 存 `Shots/avatar-pose-front.png`、`Shots/avatar-pose-side.png`，日志 `[pose] wrote ...`。
  - `tools/unity.ps1` 加 `avatarpose` 动作（`Invoke-Batch` 会自带 `-batchmode` 与 `-logFile`，不要再传）。

- [ ] **2.6 校准**：跑 `tools/unity.ps1 avatarpose`，看侧面图——目标是大腿接近水平、小腿竖直、手臂自然垂在身侧、臀面贴住座面。**「脚掌贴地」已废弃（Lead 裁决 2026-11-03）**：座面 0.604 时髋关节被钉在 0.754，小腿 0.407 m 竖直够地需要髋 ≈0.506，二者相差 0.25 m —— 让脚触地只能把臀部埋进座面 11 cm。正确判据是「踝部自然下垂、鞋底离地 0.15–0.19 m 且不穿地板」（实测踝 0.255 / 趾根 0.196），AvatarSeatTests 用一条脚高断言把这个悬空钉住。

- [ ] **2.7 跑绿**：4 个新用例通过，PlayMode 总通过 34，0 failed。

- [ ] **2.8 暂存**。

---

## Task 3 — AvatarIdle：呼吸、眨眼、看着玩家

**Files**
- Create `apps/unity/Assets/Scripts/Avatar/AvatarIdle.cs`
- Create `apps/unity/Assets/Tests/PlayMode/AvatarIdleTests.cs`

**Interfaces**
- Consumes: `AvatarRig`、`AvatarPose.Capture`。
- Produces: `Mas3D.Avatar.AvatarIdle : MonoBehaviour`
  - `public AvatarRig Rig`、`public float BreathPeriod = 3.6f`、`public float BreathDegrees = 1.1f`、`public float SwayDegrees = 0.8f`、`public float BlinkPeriod = 4.2f`、`public float BlinkSeconds = 0.12f`
  - `public void Tick(float dt)` — 唯一时间入口，测试逐帧驱动
  - `public bool Blinking { get; }`、`public float BlinkWeight { get; }`、`public Transform GazeTarget { get; }`
  - `public void SetGaze(Transform target)` — 设 `Rig.Instance.LookAtTarget = target` 与 `LookAtTargetType = VRM10ObjectLookAt.LookAtTargetTypes.SpecifiedTransform`

**Steps**

- [ ] **3.1 写失败测试** `AvatarIdleTests.cs`（每个用例先 `AvatarPose.Sitting(rig)`，再把组件 `enabled = false` 后手动 `Tick`）。

```csharp
BreathingMovesTheChestAndReturnsToTheBaseline
var chest = rig.Bone(HumanBodyBones.Chest);
var rest = chest.localRotation;
idle.Tick(0.9f);
Assert.Greater(Quaternion.Angle(rest, chest.localRotation), 0.15f, "胸口没动");
idle.Tick(1.8f); idle.Tick(1.8f);
Assert.Less(Quaternion.Angle(rest, chest.localRotation), 0.35f, "呼吸没有回到基线");
```

```csharp
BlinksThenOpens
float peak = 0f;
for (int i = 0; i < 150; i++) { idle.Tick(1f / 30f); peak = Mathf.Max(peak, idle.BlinkWeight); }
Assert.Greater(peak, 0.5f, "五秒内该眨一次眼");
Assert.LessOrEqual(peak, 0.9f, "眨眼不该把眼睛全合上（会穿模）");
Assert.Less(idle.BlinkWeight, 0.05f, "眨完要睁开");
```

```csharp
GazeFollowsThePlayerHead
var head = new GameObject("HeadProbe").transform;
try
{
    idle.SetGaze(head);
    Assert.AreSame(head, idle.GazeTarget);
    Assert.AreSame(head, rig.Instance.LookAtTarget);
    Assert.AreEqual(VRM10ObjectLookAt.LookAtTargetTypes.SpecifiedTransform, rig.Instance.LookAtTargetType);
}
finally { Object.DestroyImmediate(head.gameObject); }
```

```csharp
TheIdleBaselineSurvivesTicks
var thigh = rig.Bone(HumanBodyBones.LeftUpperLeg);
var sitRotation = thigh.localRotation;
for (int i = 0; i < 60; i++) idle.Tick(1f / 30f);
Assert.Less(Quaternion.Angle(sitRotation, thigh.localRotation), 3f, "Idle 把坐姿抹掉了");
```

- [ ] **3.2 跑红**：编译失败。

- [ ] **3.3 实现**：`Awake`/`OnEnable` 里快照 **rig 当前的**骨骼 localRotation（**不是** `AvatarPose.Capture(Rig)`——那返回的是站立静止基线，会在第一帧抹掉坐姿表写在 `Chest`/`Spine` 上的 +2°/+4° 前倾，Lead 裁决 2026-11-03）；`Tick` 内每根被驱动的骨写 `snapshot[bone] * Quaternion.Euler(offset)`（**永远基于快照，不叠乘**）。呼吸驱动 `Chest`（绕 X 正负 `BreathDegrees`），摇摆驱动 `Spine`（绕 Z 的 sin 微幅），眨眼按 `BlinkPeriod` 周期在 `BlinkSeconds` 内做 0→1→0 三角包络，上限 0.85，仅当 `Rig.HasExpression(ExpressionKey.Blink)` 才写。`Update()` 调 `Tick(Time.deltaTime)`。
  `SetGaze` 必须同时设 `LookAtTargetType`（默认值不是 `SpecifiedTransform`）。

- [ ] **3.4 跑绿**：PlayMode 总通过 38，0 failed。

- [ ] **3.5 暂存**。

---

## Task 4 — AvatarMood：把对话状态翻成表情

**Files**
- Create `apps/unity/Assets/Scripts/Avatar/AvatarMood.cs`
- Create `apps/unity/Assets/Tests/PlayMode/AvatarMoodTests.cs`

**Interfaces**
- Consumes: `AvatarRig`、`Mas3D.Conversation.ConversationController`（只读 `Phase`）。
- Produces: `Mas3D.Avatar.AvatarMood : MonoBehaviour`
  - `public AvatarRig Rig`、`public float ThinkingRelaxed = 0.20f`、`public float IdleRelaxed = 0.35f`、`public float HappyWeight = 0.65f`、`public float HappySeconds = 2.5f`、`public float SadWeight = 0.50f`、`public float SadSeconds = 3.0f`
  - `public void Apply(TurnPhase phase)` — 幂等
  - `public void Tick(float dt)` — 只推进计时与衰减

**Steps**

- [ ] **4.1 写失败测试** `AvatarMoodTests.cs`：

```csharp
ThinkingLooksRelaxedAndNotHappy
mood.Apply(TurnPhase.Thinking);
Assert.AreEqual(0.20f, rig.ExpressionWeight(ExpressionKey.Relaxed), 0.01f);
Assert.Less(rig.ExpressionWeight(ExpressionKey.Happy), 0.01f);
```

```csharp
AnAnswerSmilesThenSettlesBack
mood.Apply(TurnPhase.Answered);
Assert.AreEqual(0.65f, rig.ExpressionWeight(ExpressionKey.Happy), 0.01f);
mood.Tick(2.6f); mood.Tick(0.5f);
Assert.Less(rig.ExpressionWeight(ExpressionKey.Happy), 0.05f, "笑完要收回去");
```

```csharp
MoodNeverTouchesBlinkOrMouth
rig.SetExpression(ExpressionKey.Blink, 0.4f);
rig.SetExpression(ExpressionKey.Aa, 0.3f);
mood.Apply(TurnPhase.Answered);
Assert.AreEqual(0.4f, rig.ExpressionWeight(ExpressionKey.Blink), 0.02f, "表情不许动眨眼");
Assert.AreEqual(0.3f, rig.ExpressionWeight(ExpressionKey.Aa), 0.02f, "表情不许动口型");
```

  另外两例：`AFailedTurnDropsTheSmile`（`Failed` → Sad 0.5，3 s 后回 Relaxed）、`MissingPresetsDoNotThrow`（用 `ExpressionKey.CreateCustom("sulk")` 这种模型上确实不存在的键：`Apply` 全程无异常，`Relaxed` 不被写成负数）。

- [ ] **4.2 跑红**。

- [ ] **4.3 实现**：内部只维护「目标表情键 + 目标权重 + 剩余秒数」。`Apply` 先把上一轮的临时表情（Happy/Sad）清零，再设新目标；`Tick` 递减剩余时间，到点回落到 `Relaxed`（`Idle`/`Sending` 用 `IdleRelaxed`，`Thinking` 用 `ThinkingRelaxed`，`Answered`/`Failed` 结束后回 `IdleRelaxed`）。**只写 Relaxed / Happy / Sad 三个键**，每次写都过 `HasExpression`。
  `Update()` 里 `if (Controller != null && Controller.Phase != _last) { Apply(Controller.Phase); _last = Controller.Phase; }`，再 `Tick(Time.deltaTime)`。轮询而非事件：`ConversationController` 目前没有 Phase 事件，本计划不为表情去改它。

- [ ] **4.4 跑绿**：PlayMode 总通过 43，0 failed。

- [ ] **4.5 暂存**。

---

## Task 5 — AvatarActor：接进主场景，出图，补文档

**Files**
- Create `apps/unity/Assets/Scripts/Avatar/AvatarActor.cs`
- Modify `apps/unity/Assets/Scripts/RoomBootstrap.cs`（加 `public AvatarActor Actor;` 与 `public ConversationController Dialogue;`，`Boot()` 末尾 `BindGaze(Player.Head)` + `BindConversation(Dialogue ?? FindAnyObjectByType<ConversationController>())`）
- Modify `apps/unity/Assets/Scripts/Avatar/AvatarRig.cs`（加幂等 `public void EnsureBound()`，`Awake` 转调；编辑模式路径没有 `Awake`）
- Modify `apps/unity/Assets/Editor/CliPlayer.cs`（`BuildScene` 建 Actors 组并挂 `AvatarActor`；`Capture` 多拍一张 `Shots/avatar-seated.png`）
- Create `apps/unity/Assets/Tests/PlayMode/AvatarSceneTests.cs`
- Modify `docs/superpowers/specs/2026-11-01-unity-rebuild-design.md`（第 3 节改 VRM 1.0 事实；新增 5.19 决策记录）

**Interfaces**
- Consumes: `AvatarRig` / `AvatarSeat` / `AvatarIdle` / `AvatarMood` / `AnchorTable` / `PlayerMotor.Head`。
- Produces: `Mas3D.Avatar.AvatarActor : MonoBehaviour`
  - `public AvatarRig Rig`、`public AvatarSeat Seat`、`public AvatarIdle Idle`、`public AvatarMood Mood`
  - `public void BindGaze(Transform head)`、`public void BindConversation(ConversationController controller)`、`public void SitAt(string seatId)`
  - `public static AvatarActor Spawn(string prefabPath = AvatarRig.DefaultPrefabPath, string seatId = "S1")` — 场景构建与测试共用

**Steps**

- [ ] **5.1 写失败测试** `AvatarSceneTests.cs`（照 `BridgeSmokeTests.UiSceneShot` 的范式，但**不加** `[Explicit]`，因为它只断言不截图）：

```csharp
// 载入方式照 AvatarSceneTests.cs 的 LoadMain/UnloadMain：additive 进 Main，finally 里卸载。
// 别用 Single——Single 会把 Main 的 Player 和她的胶囊留在测试场景里，撞
// RoomAnchorTests/AvatarSeatTests 的 overlap 断言（一次撞出 4 个假失败）。
TheShippedSceneSeatsHerOnTheBench
RequireAvatarPrefab();                    // 缺 Assets/_Local/avatars/Monkia.vrm 就 Assert.Ignore，绝不判红
if (!Application.CanStreamedLevelBeLoaded("Main")) Assert.Ignore("先跑 tools/unity.ps1 player 构建 Main.unity");
if (SceneManager.GetSceneByName("Main").isLoaded) yield return UnloadMain();
yield return SceneManager.LoadSceneAsync("Main", LoadSceneMode.Additive);
try
{
    yield return null;                              // 让 Boot 跑完
    var deadline = Time.realtimeSinceStartup + 10f;
    var actor = Object.FindAnyObjectByType<AvatarActor>();
    while ((actor == null || actor.Seat == null || !actor.Seat.Seated) && Time.realtimeSinceStartup < deadline)
    { yield return null; actor = Object.FindAnyObjectByType<AvatarActor>(); }
    Assert.IsNotNull(actor, "主场景里必须有 AvatarActor");
    Assert.IsTrue(actor.Seat.Seated, "她该已经坐在 S1 上");
    var s1 = AnchorTable.Load().Seat("S1");
    Assert.AreEqual(s1.y + AvatarSeat.HipsGap, actor.Seat.HipsWorld.y, 0.04f);
    Assert.IsNotNull(actor.Idle); Assert.IsNotNull(actor.Mood);
    Assert.AreSame(Object.FindAnyObjectByType<PlayerMotor>().Head, actor.Idle.GazeTarget, "视线该绑在玩家头上");
}
finally
{
    UnloadMainIfLoaded();                            // finally 不能 yield：只发起卸载，下一次 LoadMain 先等它
}
```

```csharp
ThePlayerSpawnStaysClear
RequireAvatarPrefab();
if (SceneManager.GetSceneByName("Main").isLoaded) yield return UnloadMain();
yield return SceneManager.LoadSceneAsync("Main", LoadSceneMode.Additive);
try
{
    yield return null;
    var a2 = AnchorTable.Load().Anchor("A2");
    var feet = new Vector3(a2.x, 0.1f, a2.z);
    foreach (var h in Physics.OverlapCapsule(feet + Vector3.up * 0.3f, feet + Vector3.up * 1.5f, 0.30f))
        Assert.AreNotEqual("Monkia", h.name, "她的碰撞体压到出生点了");
}
finally
{
    UnloadMainIfLoaded();
}
```

  **每个场景/预制体用例的第一行都是 `RequireAvatarPrefab()`**：`Main.unity` 里嵌着一份从 `Assets/_Local/avatars/Monkia.vrm` 解包出来的副本（`m_PrefabInstance: {fileID: 0}`，文件内 90 处直接引用该资产的 `guid: 790ccffb…`），而 `apps/unity/.gitignore` 忽略 `/Assets/_Local/`（17 MB 的 VRoid 模型不再分发）。缺件时 `Assert.Ignore`，干净 clone 上只跳过；有件但 prefab 引用坏了仍然要红。

  第三例 `TheSeatedAvatarLeavesTheAisleVolumeClear`（原名 `HerCapsuleDoesNotSwallowTheBenchIsle` 已改）：S1 正前方 0.7 m 的玩家胶囊体积不被她的胶囊占用。**这是体积断言，不是可达性断言**——家具 collider 与「从 A2 走过去是否真的走得通」都不在它的证明范围内，用例名与失败消息必须如实这么说。

- [ ] **5.2 跑红**：编译失败。

- [ ] **5.3 实现 AvatarActor**：`Spawn` 载 prefab → `AvatarRig.Spawn` → 加 `AvatarSeat`/`AvatarIdle`/`AvatarMood` → `Seat.Sit(AnchorTable.Load().Seat(seatId))` → 加 `CapsuleCollider`（`radius 0.20f`、`height 1.05f`、`center = 髋部 + 0.2 m`、非触发器）。`BindGaze` 转 `Idle.SetGaze`；`BindConversation` 转 `Mood`，场景里没有对话对象时不抛。

- [ ] **5.4 接进 CliPlayer.BuildScene**：在 Session 之后建一个 `Actors` 空物体（内联 GameObject，不是预制体），上挂 `AvatarActor` 并 `Spawn()`；写进 `RoomBootstrap.Actor`；**保存前确认 `AvatarSeat.HipsWorld.y` 落在 `seat.y + AvatarSeat.HipsGap` ± 0.02（S1 = 0.754）**。不要检查根节点的 y：坐姿下根 y ≈ −0.11（0.754 减去站立髋高 0.866），因为落座数学只抬根、骨骼保持相对姿势；任何把 Actor 根当作「脚底/地面高度」的代码（胶囊 center、场景定位）都必须改读 `HipsWorld`（Lead 裁决 2026-11-03，plan 原判据「根 y 0.55–0.75」在本数学下不可能成立）。

  **落盘姿态（实现时的裁决，2026-11-03 19:00）**：场景存**站姿**并把 Actor 停在 A5，落座放在 `AvatarActor.Start()` 而不是 `EditorApplication` 的保存态——`AvatarSeat.Seated` 是运行期字段（载入即 false），`AvatarPose` 又把首次见到的姿势记为静息，存坐姿会在载入时把腿折两遍；放 `Start` 而非 `Awake` 是因为 `AvatarRig` 在自己的 `Awake` 里绑 `Vrm10Instance`，同物体 `Awake` 顺序不确定（在 `Awake` 里 `Bone(Hips)` 为 null，Unity 直接把 PlayMode 记为失败）。为此 `AvatarRig` 增加幂等的 `public void EnsureBound()`（`Awake` 转调），供编辑模式路径（`CliPlayer.Capture`，没有 `Awake`）显式绑定。

- [ ] **5.5 Capture 加第五张图**：相机在 `S1 + forward * 2.4 + right * 1.1 + up * 1.35`、看向 `S1 + up * 0.37`、fov 38（**订正**：原式 `S1 - forward * 2.2 + up * 1.15` 在 S1 的 `facingY = -90`（forward = −X）下是背面机位，首拍只有后脑勺且髋部被裁；实际出图用前 3/4 机位），存 `Shots/avatar-seated.png`，日志 `[play] wrote ... avatar-seated.png`。先 `player` 再 `capture`，**人眼看图**：她坐在长凳上、腿在身前、手臂垂下、没有穿模。

- [ ] **5.6 补文档**：spec 第 3 节**已由 Lead 改写**为「磁盘是 VRM 0.0，导入时 `MigrateToVrm1` 迁移为运行时 VRM 1.0，装 `com.vrmc.vrm` 0.131.4，asmdef 需 VRM10 + UniHumanoid」，并写入 161 骨 / 57 blendShape / 1.61 m 实测值——本轮只需补 5.19，不要把它改回「磁盘上是 VRM 1.0」；新增 5.19「P4 前置：角色接入的五层与判据」，记录层级划分、S1 落座参数（HipsGap 0.14、髋 0.754、臀面 0.613、踝 0.255 / 鞋底离地）、只写三键的表情策略与「不为了表情改 ConversationController」的理由。

- [ ] **5.7 全量回归**：`test -Mode EditMode`（44 通过）、`test -Mode PlayMode`（**实交验收 59 总数 / 58 通过 / 0 failed / 1 skipped**，跳过的是 `[Explicit] UiSceneShot`；本行原写 53/52，是 fix-5 加 4 个用例之前的数）、`smoke`（桥接一回合仍绿）。

- [ ] **5.8 暂存**：`git add -A apps/unity docs`，把改动交回 Lead。

---

## 交付口径

Task 1–4 是纯代码与测试，Task 5 才动场景与文档。Lead 会：
1. 自己跑 EditMode / PlayMode / smoke / capture（不信实现者的一面之词）。
2. 目视 `Shots/avatar-pose-side.png` 与 `Shots/avatar-seated.png`。
3. 复核 diff 里没有为变绿而删改的旧断言，没有把 `Assets/_Local` 或 `Shots` 加进版本库。
4. 问用户是否提交；提交后推送。

## 不在本计划内

- 四件动作（坐下/起身/躺下/起身）的动画与 IK —— 本计划只让它「坐得住、看得出活着」。
- 口型与语音（P3）。
- 玩家角色 `Me.vrm` 的接入。
- 给 `ConversationController` 加 Phase 事件（本计划用轮询）。
- SpringBone 的碰撞体调优（发丝与裙摆穿透）。
