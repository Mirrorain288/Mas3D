# P4 四件动作 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让她和玩家之间能演四件事——牵手、摸头（双向）、并肩坐床、亲吻——每件事在演出态里被运行时 IK 解到接触点，并用世界坐标判据验收，不靠肉眼。

**Architecture:** 加一层薄薄的 `Mas3D.Actions`：`ActionDirector` 是唯一入口与状态机（`TryEnterAction(id)` / `Cancel()` / `Tick(dt)`），它先把玩家摆到每个动作的固定标记点并锁住移动，再让 `ActionPose`（她的姿势表）和 `TwoBoneIk`（解析两骨 IK）把接触点解到 `ActionGeometry` 量出来的目标上，最后每帧算一次 `ContactError`。她原有的五层（rig / seat / idle / mood / actor）一行不改，只在 `AvatarIdle` 上加一个 `EyesClosed` 开关给亲吻用；动作层写手臂、颈、头，并在 pat-her / pat-me / kiss 里写 Spine/Chest 做前倾或俯身——这两根骨 `AvatarIdle` 也在呼吸，两者靠 `ActionPose.Claimed` 与 `AvatarIdle.BoneClaim` 仲裁（被认领的骨呼吸跳过，退场时姿势表先交还）。

**Tech Stack:** Unity 6000.6.7f1 + URP 17.6 + UniVRM 0.131.4（VRM 10）+ Cinemachine 6.6 + NUnit/UnityTest（PlayMode）。解析 IK，不引 Animation Rigging。

**Spec:** `docs/superpowers/specs/2026-11-01-unity-rebuild-design.md` §6（四件动作的判据）与 §8.1–8.2（演出态入口是纯函数 `TryEnterAction(id)`；不做实时驱动手臂 IK；不做 NavMesh）。执行者要同时读这两节。

## Global Constraints

- **模型是 VRM 0.0 on disk，导入时 `VrmScriptedImporter` 迁移成 VRM 1.0**（`MigrateToVrm1 = true`）。骨名用 `HumanBodyBones`，表情用 `ExpressionKey`；asmdef 早已引 `VRM10` 与 `UniHumanoid`，不要再动。
- **玩家没有手臂**：`PlayerMotor` 只有一根胶囊和一个 `Head`。手/头顶一律用 `PlayerReach` 的代理点，任何测试与实现都读同一份代理，不许自己再算一套。
- **不做实时驱动手臂 IK**：动作只在演出态里解一次并保持，玩家鼠标不参与（spec §8.1）。入口是纯函数 `bool TryEnterAction(string id)`，PlayMode 直接调用。
- **不做 NavMesh、不改协议**：她的位姿是 Unity 本地状态；桥接帧一个字段都不加。
- **不写 AnimationClip、不加包**：姿势表是数据常量（`AvatarPose` 的既有做法），IK 是解析解。
- **不 commit、不 push**：实现者只 `git add -A apps/unity`（外加本计划与 spec 的文档改动），提交由 Lead 在 YG 确认后做。
- **PlayMode 基线**：当前 `135 total / 134 passed / 0 failed / 1 skipped`（skip 是 `[Explicit] BridgeSmokeTests.UiSceneShot`），EditMode `44/44`。每个 Task 的数字写在自己的 Steps 里，跑出来对不上就是没完成。
- **Unity 只能在单实例里跑**：一次只开一个 `unity.ps1 test`；输出必须 `*>` 落文件再 `Select-String`（PowerShell 会把 Unity 的 stderr 进度行变成 NativeCommandError，导致假 exit 1）。日志写 `apps/unity/Logs/<task>.log`。
- **测试夹具必须自带守卫**：任何 `AvatarActor.Spawn` / `AvatarRig.Spawn` 的夹具第一行写 `AvatarPrefabFixture.Require()`（缺 `Assets/_Local/avatars/Monkia.vrm` 时 `Assert.Ignore`，不许硬红）。房间资产不是必需：动作测试只依赖 anchors.json（已提交）与她，不加载 `Main.unity`。
- **`tools/unity.ps1` 保持纯 ASCII**（PS 5.1 读无 BOM 的 UTF-8 中文会误报「switch 语句的子句中缺少语句块」）。新增子命令一律走既有的 `run -- -executeMethod ...` 形式，除非该 Task 明确要求加别名。
- **中文文案照抄 spec**：四件动作的提示语在本计划的 Task 8 给出，逐字节写死，不许换同义词。

## Review Focus

1. **够不着就必须拒绝，不许拉伸**：玩家站在 1.4 m 外按 E 时，手臂不能像橡皮糖一样抻长去够；`TryEnterAction` 返回 false、姿势和相机一个字节都不动。测试在 Task 3（`AnUnreachableEntryIsRefused`）。
2. **演出态里 Esc 必须完整还原**：玩家姿势、相机轨道、她的手臂/颈/头都要回到入场前的值；半套 IK 留在骨头上比不演更糟。测试在 Task 4（`CancelRestoresTheArmsAndThePlayer`）。
3. **`AvatarIdle` 一直在跑**：呼吸每帧重写 Chest/Spine，动作每帧重写四肢与颈头；两边都不能把对方的基线吃掉。测试在 Task 4（连续 3 s 接触误差不增长）。
4. **干净 clone 没有 VRM**：所有新夹具靠 `AvatarPrefabFixture.Require()` 跳过，不许把缺资产变成红。测试在 Task 1（`TheFixtureSkipsInsteadOfFailingWithoutTheModel` 是人工核对的判据，不写成用例）。
5. **重复入场不许叠加**：动作进行中再调 `TryEnterAction` 必须被忽略，不能把姿势表叠两遍或把玩家锁两次。测试在 Task 3（`ASecondEntryIsIgnoredWhileOneRuns`）。

---

### Task 1: 玩家姿势与代理点（`PlayerPosture` / `PlayerReach`）

**Files:**
- Create: `apps/unity/Assets/Scripts/PlayerPosture.cs`
- Create: `apps/unity/Assets/Scripts/PlayerReach.cs`
- Modify: `apps/unity/Assets/Scripts/PlayerMotor.cs`（加 `Posture` / `SetPosture`）
- Test: `apps/unity/Assets/Tests/PlayMode/PlayerPostureTests.cs`

**Interfaces:**
- Consumes: `PlayerMotor.EyeHeight` / `CapsuleHeight` / `Head`（既有）。
- Produces: `enum PlayerPosture { Standing, Crouching, Sitting }`；`PlayerMotor.Posture { get; }`、`void PlayerMotor.SetPosture(PlayerPosture)`、`static (float eye, float capsuleHeight, float capsuleCentre) PlayerMotor.Metrics(PlayerPosture)`；`static Vector3 PlayerReach.Palm(PlayerMotor player, bool left)`、`PlayerReach.Raised(...)`、`PlayerReach.Crown(PlayerMotor)`、`PlayerReach.Facing(PlayerMotor)`。

- [ ] **Step 1: 写失败的用例**

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mas3D.Tests
{
    public class PlayerPostureTests
    {
        static PlayerMotor MakePlayer(Vector3 feet, float yaw)
        {
            var go = new GameObject("Player");
            go.AddComponent<CharacterController>();
            var motor = go.AddComponent<PlayerMotor>();
            motor.KeyboardInput = false;
            motor.EnsureReady();
            motor.Teleport(feet, yaw);
            return motor;
        }

        [UnityTest]
        public IEnumerator CrouchingLowersTheEyeAndShortensTheCapsule()
        {
            var player = MakePlayer(Vector3.zero, 0f);
            try
            {
                player.SetPosture(PlayerPosture.Crouching);
                yield return null;
                var controller = player.GetComponent<CharacterController>();
                Assert.AreEqual(1.05f, player.Head.localPosition.y, 0.001f, "蹲下后眼高必须是 1.05 m");
                Assert.AreEqual(1.10f, controller.height, 0.001f, "蹲下后胶囊高度必须是 1.10 m");
                Assert.AreEqual(0.55f, controller.center.y, 0.001f, "胶囊中心必须跟着降到 0.55 m");
            }
            finally { Object.DestroyImmediate(player.gameObject); }
        }

        [UnityTest]
        public IEnumerator StandingBackRestoresTheMetrics()
        {
            var player = MakePlayer(Vector3.zero, 0f);
            try
            {
                player.SetPosture(PlayerPosture.Sitting);
                player.SetPosture(PlayerPosture.Standing);
                yield return null;
                Assert.AreEqual(1.62f, player.Head.localPosition.y, 0.001f, "站起来眼高必须回到 1.62 m");
                Assert.AreEqual(1.70f, player.GetComponent<CharacterController>().height, 0.001f, "胶囊高度必须回到 1.70 m");
            }
            finally { Object.DestroyImmediate(player.gameObject); }
        }

        [UnityTest]
        public IEnumerator ThePalmSitsThirtyFiveCentimetresAheadAndSixtyTwoBelowTheEye()
        {
            var player = MakePlayer(new Vector3(1f, 0f, 2f), 90f);   // 面向 +X
            try
            {
                yield return null;
                var eye = player.Head.position;
                var left = PlayerReach.Palm(player, true);
                var right = PlayerReach.Palm(player, false);
                Assert.AreEqual(0.35f, left.x - eye.x, 0.01f, "掌心必须在眼前 0.35 m");
                Assert.AreEqual(eye.y - 0.62f, left.y, 0.01f, "掌心必须在眼下 0.62 m");
                Assert.AreEqual(0.32f, right.z - left.z, 0.01f, "两只手必须左右分开 0.32 m");
                Assert.AreEqual(1.00f, left.y, 0.01f, "站姿掌心高度约 1.00 m：她坐着正好够");
            }
            finally { Object.DestroyImmediate(player.gameObject); }
        }

        [UnityTest]
        public IEnumerator StandingPutsTheCrownOutOfHerReach()
        {
            var player = MakePlayer(Vector3.zero, 0f);
            try
            {
                player.SetPosture(PlayerPosture.Crouching);
                yield return null;
                Assert.Less(PlayerReach.Crown(player).y, 1.40f, "蹲下后头顶必须低于 1.40 m：她坐着才够得到");

                player.SetPosture(PlayerPosture.Standing);
                yield return null;
                Assert.Greater(PlayerReach.Crown(player).y, 1.74f, "站立头顶 1.78 m 超出她坐姿伸手能到的 1.74 m：pat-me 必须先蹲下");
            }
            finally { Object.DestroyImmediate(player.gameObject); }
        }
    }
}
```

> 1.74 m 的来历：她坐 S1 时肩高约 1.24 m，上臂 0.28 m + 前臂 0.24 m ≈ 0.52 m，伸手能到的极限约 1.76 m。这条用例把 Task 5 里「pat-me 必须先蹲下」的理由钉成数字，而不是钉成注释。
- [ ] **Step 2: 跑用例确认是红的**

```powershell
powershell -ExecutionPolicy Bypass -File D:\Mas3D\tools\unity.ps1 test -Mode PlayMode *> D:\Mas3D\apps\unity\Logs\task1-red.log
Select-String -Path D:\Mas3D\apps\unity\Logs\task1-red.log -Pattern "error CS|PlayerPosture|PlayerReach"
```

Expected: `error CS0103: The name "PlayerPosture" does not exist` 与 `error CS0103: The name "PlayerReach" does not exist`（编译失败时 `TestResults/PlayMode.xml` 不重写，只认 `Logs/Editor.log` 里的 `error CS`）。

- [ ] **Step 3: 实现**

`PlayerPosture.cs`：枚举三个值。

`PlayerMotor` 里加：`public PlayerPosture Posture { get; private set; } = PlayerPosture.Standing;`，`public void SetPosture(PlayerPosture posture)` 依次设 `EyeHeight`、`_controller.height`、`_controller.center` 与 `Head.localPosition`（`EnsureReady` 之后再设一次 `Head.localPosition = new Vector3(0f, EyeHeight, 0f)`）。`public static (float eye, float capsuleHeight, float capsuleCentre) Metrics(PlayerPosture posture)` 返回 `Standing (1.62, 1.70, 0.85)`、`Crouching (1.05, 1.10, 0.55)`、`Sitting (1.10, 1.15, 0.575)`。

`PlayerReach.cs`：四个静态方法，全部读 `player.Head` 与 `player.transform` 的 `forward`/`right`；`Palm` 用 `forward * 0.35 + right * (left ? 0.16 : -0.16) + up * -0.62`，`Raised` 用 `forward * 0.30 + right * (left ? 0.12 : -0.12) + up * -0.32`，`Crown` 用 `up * 0.16`，`Facing` 返回 `player.transform.forward`。`player` 或 `Head` 为 null 时返回 `Vector3.zero`（不抛）。

- [ ] **Step 4: 跑绿**

```powershell
powershell -ExecutionPolicy Bypass -File D:\Mas3D\tools\unity.ps1 test -Mode PlayMode *> D:\Mas3D\apps\unity\Logs\task1-green.log
```

Expected: `total 124 / passed 123 / failed 0 / skipped 1`（比基线多 4 条）。

- [ ] **Step 5: 暂存**

```powershell
git -C D:\Mas3D add -A apps/unity
git -C D:\Mas3D status --porcelain
```

Expected: 只有本 Task 的三个脚本、一个测试与其 `.meta`。

---

### Task 2: 解析两骨 IK（`TwoBoneIk`）

**Files:**
- Create: `apps/unity/Assets/Scripts/Avatar/TwoBoneIk.cs`
- Test: `apps/unity/Assets/Tests/PlayMode/TwoBoneIkTests.cs`

**Interfaces:**
- Consumes: `Transform` 三件（upper/lower/末端），无其它依赖。
- Produces: `static bool TwoBoneIk.Solve(Transform upper, Transform lower, Vector3 target, Vector3 pole)`（够得着返回 true 并把末端放到 2 cm 内；够不着返回 false，手臂对准目标但保持伸直、不拉伸）；`static float TwoBoneIk.Reach(Transform upper, Transform lower)` = 两段长度之和；`static bool TwoBoneIk.CanReach(Transform upper, Transform lower, Vector3 target)`。

- [ ] **Step 1: 写失败的用例**

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Mas3D.Avatar;

namespace Mas3D.Tests
{
    public class TwoBoneIkTests
    {
        static (Transform upper, Transform lower) Chain(Vector3 root, float upperLength, float lowerLength)
        {
            var a = new GameObject("upper").transform;
            a.position = root;
            var b = new GameObject("lower").transform;
            b.SetParent(a, false);
            b.localPosition = new Vector3(0f, -upperLength, 0f);
            var tip = new GameObject("tip").transform;
            tip.SetParent(b, false);
            tip.localPosition = new Vector3(0f, -lowerLength, 0f);
            return (a, b);
        }

        static Transform Tip(Transform lower) => lower.GetChild(0);

        [UnityTest]
        public IEnumerator AReachableTargetIsHitWithinTwoCentimetres()
        {
            var (upper, lower) = Chain(Vector3.zero, 0.30f, 0.28f);
            try
            {
                yield return null;
                var target = new Vector3(0.20f, -0.30f, 0.15f);
                Assert.IsTrue(TwoBoneIk.Solve(upper, lower, target, Vector3.back), "0.42 m 的目标在 0.58 m 臂长内，必须够得着");
                Assert.Less(Vector3.Distance(Tip(lower).position, target), 0.02f, "末端到目标必须 ≤ 2 cm");
            }
            finally { Object.DestroyImmediate(upper.gameObject); }
        }

        [UnityTest]
        public IEnumerator AnUnreachableTargetIsRefusedNotStretched()
        {
            var (upper, lower) = Chain(Vector3.zero, 0.30f, 0.28f);
            try
            {
                yield return null;
                var target = new Vector3(0f, -2f, 0f);
                Assert.IsFalse(TwoBoneIk.Solve(upper, lower, target, Vector3.back), "两米外的目标必须报够不着");
                Assert.AreEqual(0.30f, Vector3.Distance(upper.position, lower.position), 0.002f, "上段长度不许变");
                Assert.AreEqual(0.28f, Vector3.Distance(lower.position, Tip(lower).position), 0.002f, "下段长度也不许变");
                Assert.Greater(Vector3.Dot((Tip(lower).position - upper.position).normalized, (target - upper.position).normalized), 0.99f, "够不着时至少要沿目标方向伸直");
            }
            finally { Object.DestroyImmediate(upper.gameObject); }
        }

        [UnityTest]
        public IEnumerator ThePoleChoosesWhichSideTheElbowBendsTo()
        {
            var (upper, lower) = Chain(Vector3.zero, 0.30f, 0.28f);
            try
            {
                yield return null;
                var target = new Vector3(0f, -0.40f, 0f);
                TwoBoneIk.Solve(upper, lower, target, Vector3.forward);
                var frontElbow = lower.position;
                TwoBoneIk.Solve(upper, lower, target, Vector3.back);
                var backElbow = lower.position;
                Assert.Greater(frontElbow.z, 0.05f, "pole 朝前时肘部必须往前弯");
                Assert.Less(backElbow.z, -0.05f, "pole 朝后时肘部必须往后弯");
            }
            finally { Object.DestroyImmediate(upper.gameObject); }
        }

        [UnityTest]
        public IEnumerator AMissingBoneReturnsFalseInsteadOfThrowing()
        {
            var upper = new GameObject("upper").transform;
            try
            {
                yield return null;
                Assert.IsFalse(TwoBoneIk.Solve(upper, null, Vector3.zero, Vector3.forward), "缺下段要返回 false，不抛");
                Assert.AreEqual(0f, TwoBoneIk.Reach(upper, null), 0.0001f, "缺骨头的臂长是 0");
            }
            finally { Object.DestroyImmediate(upper.gameObject); }
        }
    }
}
```

- [ ] **Step 2: 跑红**

```powershell
powershell -ExecutionPolicy Bypass -File D:\Mas3D\tools\unity.ps1 test -Mode PlayMode *> D:\Mas3D\apps\unity\Logs\task2-red.log
Select-String -Path D:\Mas3D\apps\unity\Logs\task2-red.log -Pattern "error CS"
```

Expected: `error CS0103: The name "TwoBoneIk" does not exist`。

- [ ] **Step 3: 实现余弦定理求解**

`TwoBoneIk.Solve` 的步骤：`a = upper.position`、`b = lower.position`、`c = Tip` 的当前位置；三段长度 `l1 = |b-a|`、`l2 = |tip-b|`；`d = |target-a|`。`CanReach` 要求 `d <= (l1 + l2) * 0.999f`（留 1 mm 余量，避免浮点边界两侧抖动）。够不着时把 upper 朝 `target - a` 转、lower 保持 0 度、返回 false。够得着时：用余弦定理算 `cos(a)`、求出肘部应处的位置，把 upper 的 `rotation` 设成 `LookRotation(elbowDirection, pole)` 之类（实现自由，只要满足四条用例）；末端到目标 ≤ 2 cm。**不要**改 `localScale`，也不要碰 `lossyScale`。

- [ ] **Step 4: 跑绿**

Expected: `total 128 / passed 127 / failed 0 / skipped 1`。

- [ ] **Step 5: 暂存**（同 Task 1 的 `git add` 形式）

---

### Task 3: 演出态状态机（`ActionDirector`）

**Files:**
- Create: `apps/unity/Assets/Scripts/Actions/ActionId.cs`
- Create: `apps/unity/Assets/Scripts/Actions/ActionDirector.cs`
- Test: `apps/unity/Assets/Tests/PlayMode/ActionDirectorTests.cs`

**Interfaces:**
- Consumes: `PlayerMotor`（Task 1 的 `SetPosture`）、`PlayerReach`（Task 1）、`PlayerCameraRig.SetOrbit`、`AvatarActor` / `AvatarSeat` / `AvatarRig`（既有）、`AnchorTable.Seat(id)`。
- Produces: `static class ActionId { const string Hands = "hands"; const string PatHer = "pat-her"; const string PatMe = "pat-me"; const string Kiss = "kiss"; const string SitTogether = "sit-together"; static readonly string[] All; }`；`ActionDirector`：`public AvatarActor Actor; public PlayerMotor Player; public PlayerCameraRig Camera; public bool KeyboardInput = true; public bool DriveInUpdate = true; public string Current { get; } public float Held { get; } public float ContactError { get; } public bool TryEnterAction(string id); public void Cancel(); public void Tick(float dt);`。

> **As-built 注（2026-11-04，Task 3 已交付，已回写本计划）**：下面的代码块是骨架，**不是逐字副本**。落盘版
> `ActionDirectorTests.cs` 154 行、6 条用例（计数不变），额外带：夹具以 `KeyboardInput = true` 起手，
> 且 `CancelRestoresThePlayerAndTheCamera` 末尾补「true → 进场 → Cancel → true」往返（否则「归还」与
> 「什么都不做」同值，删掉快照/归还两行仍全绿）；m1 真建 `PlayerCameraRig` 并断言 orbit 还原；m2 先蹲下再挪位置；
> m13 `Tick(0.5f)` 断言 `Held == 0.5`。接口额外暴露只读 `ClearPoseCount`（诊断用，`ClearPose` 只在非空闲调用）；
> `Refuse()` 只写 `LastFaults`（`busy: <id>` / `unknown action: <id>` / `out of reach`），不改任何状态；
> `MaxUpdateStep = 0.1f` 只在 `Update()` 入口生效，`Tick(float)` 保持线性。

- [ ] **Step 1: 写失败的用例**

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Mas3D;
using Mas3D.Actions;
using Mas3D.Avatar;

namespace Mas3D.Tests
{
    public class ActionDirectorTests
    {
        ActionDirector _director;
        AvatarActor _actor;
        PlayerMotor _player;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            AvatarPrefabFixture.Require();
            _actor = AvatarActor.Spawn();
            var go = new GameObject("Player");
            go.AddComponent<CharacterController>();
            _player = go.AddComponent<PlayerMotor>();
            _player.KeyboardInput = true;   // 起手必须能走：否则「Cancel 归还」与「什么都不做」同值
            _player.EnsureReady();
            var dir = new GameObject("Actions");
            _director = dir.AddComponent<ActionDirector>();
            _director.Actor = _actor;
            _director.Player = _player;
            _director.KeyboardInput = false;
            _director.DriveInUpdate = false;
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_director != null) Object.Destroy(_director.gameObject);
            if (_actor != null) Object.Destroy(_actor.gameObject);
            if (_player != null) Object.Destroy(_player.gameObject);
            yield return null;
        }

        [Test]
        public void EveryActionHasAnId()
        {
            CollectionAssert.AreEquivalent(
                new[] { "hands", "pat-her", "pat-me", "kiss", "sit-together" },
                ActionId.All, "五个动作 id 必须齐");
        }

        [UnityTest]
        public IEnumerator EnteringHandsLocksThePlayerAndMarksTheSpot()
        {
            yield return null;
            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands), "她坐在 S1 上，牵手必须能进场");
            Assert.AreEqual(ActionId.Hands, _director.Current, "Current 必须是 hands");
            Assert.IsFalse(_player.KeyboardInput, "演出态必须锁住键盘");
            var seat = AnchorTable.Load().Seat("S1");
            var want = new Vector3(seat.x, 0f, seat.z) + Quaternion.Euler(0f, seat.facingY, 0f) * Vector3.forward * 0.62f;
            Assert.AreEqual(want.x, _player.transform.position.x, 0.02f, "玩家必须被摆到她正前方 0.62 m");
            Assert.AreEqual(want.z, _player.transform.position.z, 0.02f, "x/z 都要对上");
            Assert.AreEqual(90f, _player.transform.eulerAngles.y, 1f, "玩家必须面向她（她朝 -X，所以玩家朝 +X）");
        }

        [UnityTest]
        public IEnumerator CancelRestoresThePlayerAndTheCamera()
        {
            yield return null;
            var wasYaw = _player.transform.eulerAngles.y;
            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands));
            _director.Cancel();
            yield return null;
            Assert.IsNull(_director.Current, "Cancel 之后必须回到 idle");
            Assert.IsTrue(_player.KeyboardInput, "Cancel 之后键盘必须交还");
            Assert.AreEqual(PlayerPosture.Standing, _player.Posture, "Cancel 之后站姿必须还原");
            Assert.AreEqual(wasYaw, _player.transform.eulerAngles.y, 1f, "朝向不能被 Cancel 留在动作机位");

            // 起手是 true 的往返：只有「快照 + 归还」能过，强制 true / 完全不还原都会红
            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands));
            Assert.IsFalse(_player.KeyboardInput, "进场必须锁住键盘");
            _director.Cancel();
            Assert.IsTrue(_player.KeyboardInput, "进场前能走，Cancel 后就必须还能走");
        }

        [UnityTest]
        public IEnumerator AnUnknownIdIsRefusedAndChangesNothing()
        {
            yield return null;
            Assert.IsFalse(_director.TryEnterAction("dance"), "未知动作必须拒绝");
            Assert.IsNull(_director.Current, "被拒绝时不许进入任何状态");
            Assert.IsTrue(_player.KeyboardInput, "被拒绝时不许锁键盘");
        }

        [UnityTest]
        public IEnumerator ASecondEntryIsIgnoredWhileOneRuns()
        {
            yield return null;
            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands));
            Assert.IsFalse(_director.TryEnterAction(ActionId.Kiss), "动作进行中再按别的动作必须被忽略");
            Assert.AreEqual(ActionId.Hands, _director.Current, "Current 不许被第二次调用改掉");
        }

        [UnityTest]
        public IEnumerator AnUnreachableEntryIsRefused()
        {
            yield return null;
            // 把她放到房间另一头：任何手臂都够不着她，入场必须被拒绝而不是把她拖过来
            _actor.transform.position += new Vector3(4f, 0f, 0f);
            yield return null;
            Assert.IsFalse(_director.TryEnterAction(ActionId.Hands), "够不着时必须返回 false");
            Assert.IsNull(_director.Current, "被拒绝后仍是 idle");
            Assert.IsTrue(_player.KeyboardInput, "被拒绝后玩家还能走");
        }
    }
}
```

- [ ] **Step 2: 跑红**

```powershell
powershell -ExecutionPolicy Bypass -File D:\Mas3D\tools\unity.ps1 test -Mode PlayMode *> D:\Mas3D\apps\unity\Logs\task3-red.log
Select-String -Path D:\Mas3D\apps\unity\Logs\task3-red.log -Pattern "error CS"
```

Expected: `error CS0246: The type or namespace name "ActionDirector" could not be found`（以及 `ActionId`）。

- [ ] **Step 3: 实现状态机**

`ActionDirector` 字段与方法如上。`TryEnterAction` 的骨架：`if (Current != null) return false;` → 查 `ActionId.All`，不认识就 `return false` → `ResolvePlan(id, out plan)`（本 Task 只实现 `hands` 的标记点与 `0.62` 偏移，其它 id 在 Task 4–7 里接上，先 `return false` 并在 `LastFaults` 里写 `not implemented`）→ 前置条件不满足（够不着 / 姿势不对）也 `return false`，**不改任何状态**（`Refuse()` 只写 `LastFaults`，连键盘都不碰）→ 全部检查通过之后才改状态：先快照 `_wasKeyboardInput = Player.KeyboardInput` 与 `_yaw/_pitch/_posture/_position`，再 `Player.KeyboardInput = false`、`Player.SetPosture(plan.posture)`、`Player.Teleport(plan.spot, plan.yaw)`、`Camera.SetOrbit(plan.orbitYaw, plan.orbitPitch)`、`Current = id`（`Current` 必须在这些写入之前赋值也不许：它同时是「有进场」的判据，先置位才能在写入抛异常时靠 `Cancel()` 兜底）。`Cancel()` 顺序固定为：`if (Current == null) return;` 早退 → 先还键盘 `Player.KeyboardInput = _wasKeyboardInput` → `try { ClearPose(); 姿势 → 位置 → 相机 }` → `finally { Current/Held/ContactError 清零 }`（键盘是唯一不可恢复项，必须最先还；姿势还原排在位置之前，避免控制器半高与位置错配）。`OnDisable()` 里 `if (Current != null) Cancel();`。`ClearPose()`（本 Task 里是空实现，Task 4 起由 `ActionPose` 接管）必须在空闲时绝不被调用，并配一个只读 `ClearPoseCount` 供测试观察「恰好一次」；`ClearPose()` 还必须幂等（连按两次 Cancel 不报错、不重复还原）。

`Update()`：`if (KeyboardInput && Input.GetKeyDown(KeyCode.Escape)) Cancel();`，然后 `if (DriveInUpdate) Tick(Mathf.Min(Time.deltaTime, MaxUpdateStep));`（`MaxUpdateStep = 0.1f`：卡顿帧不许把动作时钟一次推过门槛；`Tick(float)` 本身保持线性、不做封顶，供测试精确驱动）。`Tick` 用 `if (!(dt > 0f)) return;` 挡 NaN 与负值（`Mathf.Clamp` 对 NaN 原样返回，靠不住）。`DriveInUpdate` 默认 true，测试里一律置 false 再手动 `Tick(1f/60f)`——否则测试与 `Update` 会各推进一次，`Held` 直接翻倍，用例的 3 s 门槛就名存实亡。`Tick(dt)` 在 `Current == null` 时直接返回。

- [ ] **Step 4: 跑绿**

Expected: `total 135 / passed 134 / failed 0 / skipped 1`（Task 2 修复轮加了 1 条内死区用例，全计划 T4–T8 的期望值一并 +1）。

- [ ] **Step 5: 暂存**

---

### Task 4: 牵手（`ActionGeometry` / `ActionPose` / 双臂 IK）

**Files:**
- Create: `apps/unity/Assets/Scripts/Actions/ActionGeometry.cs`
- Create: `apps/unity/Assets/Scripts/Actions/ActionPose.cs`（含 `ActionPoses` 姿势表）
- Modify: `apps/unity/Assets/Scripts/Actions/ActionDirector.cs`（`hands` 分支 + `ClearPose`）
- Test: `apps/unity/Assets/Tests/PlayMode/ActionHandsTests.cs`

**Interfaces:**
- Consumes: `AvatarRig.Bone(HumanBodyBones)`、`AvatarRig.EnsureBound()`、`PlayerReach.Palm`（Task 1）、`TwoBoneIk.Solve`（Task 2）、`ActionDirector.TryEnterAction`（Task 3）、`AvatarIdle.Tick`（既有，只在测试里当背景噪声）。
- Produces: `static Vector3 ActionGeometry.HerPalm(AvatarRig rig, bool left)`、`HerHeadCenter`、`HerHeadTop`、`HerFacing`、`static float ActionGeometry.FaceAlign(AvatarRig rig, Vector3 toward)`；`static void ActionPose.Begin(AvatarRig rig)`、`ActionPose.Apply(AvatarRig rig, IReadOnlyDictionary<HumanBodyBones, Vector3> angles)`、`ActionPose.Restore(AvatarRig rig)`、`static bool ActionPose.Active`；`static class ActionPoses { Hands / PatHer / PatMe / Kiss / SitTogether }`（每张表是 `IReadOnlyDictionary<HumanBodyBones, Vector3>`，值是**相对入场基线的欧拉偏移**）。

- [ ] **Step 1: 写失败的用例**

```csharp
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Mas3D;
using Mas3D.Actions;
using Mas3D.Avatar;

namespace Mas3D.Tests
{
    public class ActionHandsTests
    {
        AvatarActor _actor;
        PlayerMotor _player;
        ActionDirector _director;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            AvatarPrefabFixture.Require();
            _actor = AvatarActor.Spawn();
            var go = new GameObject("Player");
            go.AddComponent<CharacterController>();
            _player = go.AddComponent<PlayerMotor>();
            _player.KeyboardInput = true;   // 起手必须能走：否则「Cancel 归还」与「什么都不做」同值
            _player.EnsureReady();
            var dir = new GameObject("Actions");
            _director = dir.AddComponent<ActionDirector>();
            _director.Actor = _actor;
            _director.Player = _player;
            _director.KeyboardInput = false;
            _director.DriveInUpdate = false;
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_director != null) Object.Destroy(_director.gameObject);
            if (_actor != null) Object.Destroy(_actor.gameObject);
            if (_player != null) Object.Destroy(_player.gameObject);
            yield return null;
        }

        float PalmGap(bool left) => Vector3.Distance(
            ActionGeometry.HerPalm(_actor.Rig, left), PlayerReach.Palm(_player, left));

        [UnityTest]
        public IEnumerator BothHandsMeetTheirPalms()
        {
            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands), "她坐在 S1 上，牵手必须能进场");
            _director.Tick(1f / 60f);
            yield return null;
            Assert.Less(PalmGap(true), 0.02f, "左手掌心到她坐姿掌心的距离必须 ≤ 2 cm");
            Assert.Less(PalmGap(false), 0.02f, "右手掌心也必须 ≤ 2 cm");
        }

        [UnityTest]
        public IEnumerator TheHandsHoldForThreeSeconds()
        {
            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands));
            for (var i = 0; i < 240; i++)   // 4 s @60 fps
            {
                _director.Tick(1f / 60f);
                yield return null;
            }
            Assert.GreaterOrEqual(_director.Held, 3.0f, "接触必须稳定保持 3 s 以上");
            Assert.LessOrEqual(_director.ContactError, 0.02f, "全程接触误差不得超过 2 cm");
        }

        [UnityTest]
        public IEnumerator TheContactDoesNotCreepWhileSheBreathes()
        {
            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands));
            var chest = _actor.Rig.Bone(HumanBodyBones.Chest);
            var chest0 = chest.localRotation;
            var samples = new List<Vector3>();
            for (var i = 0; i < 180; i++)   // 3 s：呼吸周期 3.0 s，正好一整周
            {
                _director.Tick(1f / 60f);
                yield return null;
                if (i >= 120) samples.Add(_actor.Rig.Bone(HumanBodyBones.LeftHand).position);
            }
            Assert.Greater(Quaternion.Angle(chest0, chest.localRotation), 0.2f, "呼吸必须真的在跑，否则这条用例测的是静物");
            var mean = Vector3.zero;
            foreach (var s in samples) mean += s;
            mean /= samples.Count;
            var variance = 0f;
            foreach (var s in samples) variance += (s - mean).sqrMagnitude;
            variance /= samples.Count;
            Assert.Less(variance, 0.000001f, "最后 1 s 手的位置方差必须 ≤ (1 mm)²：呼吸不许把手抖出去");
        }

        [UnityTest]
        public IEnumerator CancelRestoresTheArms()
        {
            var upper = _actor.Rig.Bone(HumanBodyBones.LeftUpperArm);
            var before = upper.localRotation;
            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands));
            for (var i = 0; i < 30; i++) { _director.Tick(1f / 60f); yield return null; }
            Assert.Greater(Quaternion.Angle(before, upper.localRotation), 10f, "进场后左臂必须真的抬起来，否则 Cancel 没有东西可还原");
            _director.Cancel();
            yield return null;
            Assert.Less(Quaternion.Angle(before, upper.localRotation), 1f, "Cancel 之后左肩必须回到入场前 1° 以内");
        }

        [UnityTest]
        public IEnumerator TheActionNeverMovesTheFeet()
        {
            var hips = _actor.Rig.Bone(HumanBodyBones.Hips).position;
            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands));
            for (var i = 0; i < 60; i++) { _director.Tick(1f / 60f); yield return null; }
            Assert.AreEqual(hips.y, _actor.Rig.Bone(HumanBodyBones.Hips).position.y, 0.005f, "牵手只写上半身，髋高不许动");
            Assert.AreEqual(hips.x, _actor.Rig.Bone(HumanBodyBones.Hips).position.x, 0.005f, "髋位也不许动");
        }
    }
}
```

- [ ] **Step 2: 跑红**

```powershell
powershell -ExecutionPolicy Bypass -File D:\Mas3D\tools\unity.ps1 test -Mode PlayMode *> D:\Mas3D\apps\unity\Logs\task4-red.log
Select-String -Path D:\Mas3D\apps\unity\Logs\task4-red.log -Pattern "error CS"
```

Expected: `error CS0103: The name 'ActionGeometry' does not exist` 与 `'ActionPose' does not exist`。

- [ ] **Step 3: 实现**

`ActionGeometry`：四个静态量取 `AvatarRig.Bone` 的世界位置——`HerPalm` = `Hand.position + Hand.forward * 0.08`；`HerHeadCenter` = `Head.position + Head.up * 0.10`；`HerHeadTop` = `Head.position + Head.up * 0.20`；`HerFacing` = `Head.forward`；`FaceAlign(rig, toward)` = `Vector3.Angle(HerFacing(rig), toward - HerHeadCenter(rig))`，`toward` 与头部重合时返回 0。骨头为 null 时全部返回 `Vector3.zero` / 180f。

`ActionPose`：`Dictionary<HumanBodyBones, Quaternion> _base` + `HashSet<HumanBodyBones> _taken`。`Begin(rig)` 只对**本动作将要写的骨头**（`LeftUpperArm/LeftLowerArm/LeftHand/RightUpperArm/RightLowerArm/RightHand/Neck/Head/Hips/Chest/Spine` 里实际存在且还没记过的那些）记 `localRotation`，已记过的不覆盖（一次演出只快照一次）。`Apply(rig, angles)`：对表里每一根 `bone.localRotation = _base[bone] * Quaternion.Euler(offset)`——**永远基于基线，不叠加**（这是 `AvatarPose` 已经付过学费的教训）。`Restore(rig)` 把 `_base` 原样写回并清空表（幂等：没快照时什么都不做）。

`ActionPoses.Hands`：`LeftUpperArm (0,0,70)`、`RightUpperArm (0,0,-70)`、`LeftLowerArm (0,65,0)`、`RightLowerArm (0,-65,0)`、`LeftHand/RightHand` 不写（留给 IK）。这张表只负责把手臂抬到身前，精确落点由 IK 解。

`ActionDirector` 里 `hands` 的 `Tick`：`ActionPose.Apply(rig, ActionPoses.Hands)` → 左臂 `TwoBoneIk.Solve(UpperArm, LowerArm, PlayerReach.Palm(Player, true), pole)`，pole 用 `player.transform.right * -1f + player.transform.forward * 0.2f`（肘往身体外侧、略朝前）；右臂对称。每只手解完把 `Hand` 骨朝目标点转向（`Hand.rotation = Quaternion.LookRotation(target - Hand.position, player.transform.up)`），掌心才算真的贴上。然后 `ContactError = max(两只手的距离)`；`ContactError <= 0.02f` 时 `Held += dt`，否则 `Held = 0f`。`ClearPose()` 改成调 `ActionPose.Restore(_actor.Rig)`。

`TryEnterAction` 的 `hands` 前置条件：`ActionGeometry.HerPalm` 到对应 `PlayerReach.Palm` 的距离必须在 `TwoBoneIk.CanReach` 之内（肩到掌心 ≤ `Reach * 0.999`）；不满足就 `return false` 并写 `LastFaults`，状态一律不动。

- [ ] **Step 4: 跑绿**

Expected: `total 141 / passed 140 / failed 0 / skipped 1`。

> **As-built（Task 4 收口）**：`ActionHandsTests` 多一条 `ADestroyedActorForgetsTheSnapshot`（演员先销毁再 `ClearPose`，断言 `ActionPose.Active == false`）。它钉住评审 I1：静态快照在拆卸期必须 `Forget()`，否则下一个演员继承死掉那具的基线。数量因此从 140 变成 141，Task 5–8 的期望值全部 +1。

- [ ] **Step 5: 暂存**

> **这条判据能证明什么**：它证明她的手骨到了 `PlayerReach.Palm` 定义的那个点，**不证明画面像牵手**。`HerPalm` 的 0.08 m、`HerHeadTop` 的 0.20 m 都是手工校准常数（没有可靠的骨骼长度来源），Task 8 出图目视核对；若图上差得明显，改常数并重跑 Task 4/5 的用例——常数本身不会被这几条用例抓到。

---

### Task 5: 摸头，双向（`pat-her` / `pat-me`）

**Files:**
- Modify: `apps/unity/Assets/Scripts/Actions/ActionPose.cs`（加 `PatHer` / `PatMe` 两张表）
- Modify: `apps/unity/Assets/Scripts/Actions/ActionDirector.cs`（两个分支）
- Test: `apps/unity/Assets/Tests/PlayMode/ActionPatTests.cs`

**Interfaces:**
- Consumes: Task 4 的 `ActionGeometry` / `ActionPose` / `ActionPoses`、`PlayerReach.Raised` / `PlayerReach.Crown`、`TwoBoneIk`。
- Produces: `ActionPoses.PatHer`（她被摸时的姿势表：颈 +10°、头 +6°、右肩抬）、`ActionPoses.PatMe`（她伸手摸玩家时的姿势表：右臂抬到身前）；`ActionDirector` 里 `pat-her` 与 `pat-me` 两个可进场分支，`pat-me` 要求 `Player.Posture == PlayerPosture.Crouching`。

- [ ] **Step 1: 写失败的用例**

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Mas3D;
using Mas3D.Actions;
using Mas3D.Avatar;

namespace Mas3D.Tests
{
    public class ActionPatTests
    {
        AvatarActor _actor;
        PlayerMotor _player;
        ActionDirector _director;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            AvatarPrefabFixture.Require();
            _actor = AvatarActor.Spawn();
            var go = new GameObject("Player");
            go.AddComponent<CharacterController>();
            _player = go.AddComponent<PlayerMotor>();
            _player.KeyboardInput = true;   // 起手必须能走：否则「Cancel 归还」与「什么都不做」同值
            _player.EnsureReady();
            var dir = new GameObject("Actions");
            _director = dir.AddComponent<ActionDirector>();
            _director.Actor = _actor;
            _director.Player = _player;
            _director.KeyboardInput = false;
            _director.DriveInUpdate = false;
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_director != null) Object.Destroy(_director.gameObject);
            if (_actor != null) Object.Destroy(_actor.gameObject);
            if (_player != null) Object.Destroy(_player.gameObject);
            yield return null;
        }

        [UnityTest]
        public IEnumerator PattingHerBringsTheHandToHerHeadTop()
        {
            Assert.IsTrue(_director.TryEnterAction(ActionId.PatHer), "站在她面前 0.62 m 处伸手摸头必须能进场");
            for (var i = 0; i < 30; i++) { _director.Tick(1f / 60f); yield return null; }
            var raised = PlayerReach.Raised(_player, false);
            var gap = Vector3.Distance(raised, ActionGeometry.HerHeadTop(_actor.Rig));
            Assert.Less(gap, 0.03f, "玩家指尖到她头顶必须 ≤ 3 cm（她可以低头凑过来）");
        }

        [UnityTest]
        public IEnumerator HerHeadTakesThePat()
        {
            var head = _actor.Rig.Bone(HumanBodyBones.Head);
            var before = head.localRotation;
            Assert.IsTrue(_director.TryEnterAction(ActionId.PatHer));
            for (var i = 0; i < 30; i++) { _director.Tick(1f / 60f); yield return null; }
            Assert.Greater(Quaternion.Angle(before, head.localRotation), 3f, "被摸时头部必须有 ≥ 3° 的受力反馈，不能僵硬不动");
            _director.Cancel();
            yield return null;
            Assert.Less(Quaternion.Angle(before, head.localRotation), 1f, "Cancel 之后头部必须回到入场前 1° 以内");
        }

        [UnityTest]
        public IEnumerator PattingMeNeedsACrouchedPlayer()
        {
            Assert.IsFalse(_director.TryEnterAction(ActionId.PatMe), "站姿头顶 1.78 m 超出她坐姿伸手的 1.74 m，必须拒绝");
            Assert.IsNull(_director.Current, "被拒绝时不许进入任何状态");
            _player.SetPosture(PlayerPosture.Crouching);
            yield return null;
            Assert.IsTrue(_director.TryEnterAction(ActionId.PatMe), "蹲下后头顶降到 1.21 m，必须能进场");
        }

        [UnityTest]
        public IEnumerator HerHandFindsTheCrownWhenCrouched()
        {
            _player.SetPosture(PlayerPosture.Crouching);
            yield return null;
            Assert.IsTrue(_director.TryEnterAction(ActionId.PatMe));
            for (var i = 0; i < 30; i++) { _director.Tick(1f / 60f); yield return null; }
            var gap = Vector3.Distance(ActionGeometry.HerPalm(_actor.Rig, false), PlayerReach.Crown(_player));
            Assert.Less(gap, 0.03f, "她的手到玩家头顶必须 ≤ 3 cm");
        }

        [UnityTest]
        public IEnumerator TheFreeArmKeepsItsBaseline()
        {
            var free = _actor.Rig.Bone(HumanBodyBones.LeftUpperArm);
            var before = free.localRotation;
            Assert.IsTrue(_director.TryEnterAction(ActionId.PatHer));
            for (var i = 0; i < 30; i++) { _director.Tick(1f / 60f); yield return null; }
            Assert.Less(Quaternion.Angle(before, free.localRotation), 1f, "摸头只用右手，左肩不许跟着动");
        }
    }
}
```

- [ ] **Step 2: 跑红**

```powershell
powershell -ExecutionPolicy Bypass -File D:\Mas3D\tools\unity.ps1 test -Mode PlayMode *> D:\Mas3D\apps\unity\Logs\task5-red.log
Select-String -Path D:\Mas3D\apps\unity\Logs\task5-red.log -Pattern "Failed|error CS"
```

Expected: 五条全红，失败语分别是「必须能进场」「必须 ≤ 3 cm」「必须 ≥ 3°」等——本 Task 的表与分支还不存在，`TryEnterAction` 一律返回 false。

- [ ] **Step 3: 实现**

`ActionPoses.PatHer`（as-built）：`Spine (24,0,0)`、`Chest (12,0,0)`、`Neck (16,0,0)`、`Head (6,0,0)`，**手臂两行已删除**——原稿的 `RightUpperArm (0,0,-46)` 把她前臂送到自己耳边，出图读作「捂头」而不是「被摸头」，她保持坐姿基线的放手姿势。计划原稿的 Spine 3 只把头顶送到 0.2923 m，唯一授权旋钮 Neck 8–20 只到 0.2491 m（0.62 m 标记点在她左侧 0.122 m，pitch 补不回横向差），实测颈根最低 0.2157 m @Spine 30 —— 所以低头必须由躯干承担。配合 `DrivePatHer` 的 head aim（把 Head 的 up 轴对准掌心，`|d - 0.20| = 1.6 cm` 落进 3 cm 判据），30 帧后残差 0.0163 m。**呼吸会抢 Chest/Spine**：`ActionPose` 在 `Apply` 时把写过的骨记进 `Claimed`，`AvatarIdle.BoneClaim` 让呼吸跳过被认领的骨（见下），否则首帧的前倾会被呼吸按 0.25 s 去抖窗口拉直。

`ActionPoses.PatMe`（as-built）：`Spine (20,0,0)`、`Chest (10,0,0)`、`RightUpperArm (0,0,-64)`、`RightLowerArm (0,-24,0)`；她坐姿肩高 1.24 m，蹲姿玩家头顶 1.21 m。落点仍由 IK 解：`TwoBoneIk.Solve(RightUpperArm, RightLowerArm, PlayerReach.Crown(Player), pole)`，pole 取 `player.transform.forward`。

**标记点必须留在 0.62 m（Lead 裁决，不许改小到 0.48）**：长凳 `Mesh_0001out` 的 AABB 是 x∈[−0.189, 0.399]，玩家胶囊半径 0.30。0.62 → 玩家中心 x=−0.515、胶囊前沿 −0.215（离凳 2.6 cm）；0.48 → 中心 −0.375、前沿 −0.075，**胶囊嵌进长凳 11.4 cm**，第一人称会看到膝盖穿进坐垫。下限 0.60（临界 0.594）。0.62 直立时 shoulder→wrist 0.5678 > 臂长 0.4708，够不着由躯干前倾解决：`ActionDirector.ShoulderUnder(table)` 临时把 Spine/Chest 摆到表上的角度、量出前倾后的肩，再 `TwoBoneIk.CanReachFrom(upper, lower, shoulder, wrist)` 问能不能够到——**站位可达性判断必须用前倾后的肩，否则会把能做到的动作判成 out of reach**。实测前倾后 shoulder=(−0.002,1.093,0.058)、wrist=(−0.437,1.197,−0.014)、d=0.453 < reach 0.471。

`ActionDirector`：`pat-her` 的标记点与 `hands` 相同（她正前方 0.62 m、yaw 90、站姿）；`pat-me` 要求 `Player.Posture == PlayerPosture.Crouching`，否则 `return false` 并在 `LastFaults` 写 `需要蹲下`——**不许自己偷偷把玩家按下去**，姿势是玩家的状态。两个分支的 `ContactError` 分别用「Raised 掌心 ↔ HerHeadTop」「HerPalm(right) ↔ Crown」。

- [ ] **Step 4: 跑绿**

Expected: `total 146 / passed 145 / failed 0 / skipped 1`。

> **As-built 补充**：① `ActionPose.Claimed` + `AvatarIdle.BoneClaim`（呼吸跳过被认领的 Chest/Spine，`AvatarIdle.Claimed` 在写骨前查）；② `PattingHerBringsTheHandToHerHeadTop` 在 30 帧之外加了「前五帧残差 ≤ 3 cm」断言（钉住 0.25 s 去抖窗口里前倾不被拉直）；③ `TryEnterAction` 的失败语在测试里带上 `_director.LastFaults`，失败时能直接看到拒绝原因。

- [ ] **Step 5: 暂存**

---
### Task 6: 亲吻（`kiss`，含闭眼开关）

**Files:**
- Modify: `apps/unity/Assets/Scripts/Avatar/AvatarIdle.cs`（加 `EyesClosed`）
- Modify: `apps/unity/Assets/Scripts/PlayerReach.cs`（加 `Face`）
- Modify: `apps/unity/Assets/Scripts/Actions/ActionPose.cs`（`Kiss` 表）
- Modify: `apps/unity/Assets/Scripts/Actions/ActionDirector.cs`（`kiss` 分支）
- Test: `apps/unity/Assets/Tests/PlayMode/ActionKissTests.cs`

**Interfaces:**
- Consumes: Task 4/5 的动作层；`AvatarIdle.BlinkWeight` / `SetExpression` 通路（既有）；`AvatarRig.ExpressionWeight(ExpressionKey)`。
- Produces: `AvatarIdle.EyesClosed { get; set; }`（true 时每 tick 把 Blink 写死 1.0 并跳过包络；false 立刻恢复包络）；`static Vector3 PlayerReach.Face(PlayerMotor player)` = 眼 + 前 0.10 m − 上 0.04 m；`ActionPoses.Kiss`；`ActionId.Kiss` 可进场分支。

- [ ] **Step 1: 写失败的用例**

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UniVRM10;
using Mas3D;
using Mas3D.Actions;
using Mas3D.Avatar;

namespace Mas3D.Tests
{
    public class ActionKissTests
    {
        AvatarActor _actor;
        PlayerMotor _player;
        ActionDirector _director;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            AvatarPrefabFixture.Require();
            _actor = AvatarActor.Spawn();
            var go = new GameObject("Player");
            go.AddComponent<CharacterController>();
            _player = go.AddComponent<PlayerMotor>();
            _player.KeyboardInput = true;   // 起手必须能走：否则「Cancel 归还」与「什么都不做」同值
            _player.EnsureReady();
            _player.SetPosture(PlayerPosture.Crouching);
            var dir = new GameObject("Actions");
            _director = dir.AddComponent<ActionDirector>();
            _director.Actor = _actor;
            _director.Player = _player;
            _director.KeyboardInput = false;
            _director.DriveInUpdate = false;
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_director != null) Object.Destroy(_director.gameObject);
            if (_actor != null) Object.Destroy(_actor.gameObject);
            if (_player != null) Object.Destroy(_player.gameObject);
            yield return null;
        }

        [UnityTest]
        public IEnumerator TheKissClosesToSixCentimetres()
        {
            Assert.IsTrue(_director.TryEnterAction(ActionId.Kiss), "蹲在她面前时亲吻必须能进场");
            for (var i = 0; i < 20; i++) { _director.Tick(1f / 60f); yield return null; }
            var gap = Vector3.Distance(ActionGeometry.HerHeadCenter(_actor.Rig), PlayerReach.Face(_player));
            Assert.Less(gap, 0.06f, "两张脸最近处必须 ≤ 6 cm");
        }

        [UnityTest]
        public IEnumerator TheFacesAlignWithinFifteenDegrees()
        {
            Assert.IsTrue(_director.TryEnterAction(ActionId.Kiss));
            for (var i = 0; i < 20; i++) { _director.Tick(1f / 60f); yield return null; }
            var align = ActionGeometry.FaceAlign(_actor.Rig, PlayerReach.Face(_player));
            Assert.Less(align, 15f, "她的面部朝向与她对玩家的连线偏差必须 ≤ 15°");
        }

        [UnityTest]
        public IEnumerator TheEyesAreShutDuringTheKiss()
        {
            Assert.IsTrue(_director.TryEnterAction(ActionId.Kiss));
            for (var i = 0; i < 20; i++) { _director.Tick(1f / 60f); yield return null; }
            Assert.IsTrue(_actor.Idle.EyesClosed, "亲吻期间必须把 EyesClosed 打开");
            Assert.GreaterOrEqual(_actor.Rig.ExpressionWeight(ExpressionKey.Blink), 0.9f, "闭眼强度必须 ≥ 0.9（Idle 的眨眼上限只有 0.85，所以这条只有强制闭眼才能过）");
            _director.Cancel();
            for (var i = 0; i < 20; i++) { _director.Tick(1f / 60f); yield return null; }
            Assert.IsFalse(_actor.Idle.EyesClosed, "松开后必须把 EyesClosed 关回");
            Assert.LessOrEqual(_actor.Rig.ExpressionWeight(ExpressionKey.Blink), 0.86f, "关回之后回到眨眼包络（上限 0.85）");
        }

        [UnityTest]
        public IEnumerator TheKissHoldsTwoSecondsAndThenLetsGo()
        {
            var head = _actor.Rig.Bone(HumanBodyBones.Head);
            var before = head.localRotation;
            Assert.IsTrue(_director.TryEnterAction(ActionId.Kiss));
            for (var i = 0; i < 156; i++) { _director.Tick(1f / 60f); yield return null; }   // 2.6 s
            Assert.GreaterOrEqual(_director.Held, 2.0f, "接触必须稳定保持 2 s 以上");
            Assert.Greater(Quaternion.Angle(before, head.localRotation), 10f, "进场后她必须真的俯身，否则这条测的是静物");
            _director.Cancel();
            yield return null;
            Assert.Less(Quaternion.Angle(before, head.localRotation), 2f, "Cancel 之后头部必须回到入场前 2° 以内");
            Assert.IsNull(_director.Current, "Cancel 之后必须回到 idle");
        }
    }
}
```

- [ ] **Step 2: 跑红**

```powershell
powershell -ExecutionPolicy Bypass -File D:\Mas3D\tools\unity.ps1 test -Mode PlayMode *> D:\Mas3D\apps\unity\Logs\task6-red.log
Select-String -Path D:\Mas3D\apps\unity\Logs\task6-red.log -Pattern "error CS"
```

Expected: `error CS1061: 'AvatarIdle' does not contain a definition for 'EyesClosed'` 与 `'PlayerReach' does not contain a definition for 'Face'`。

- [ ] **Step 3: 实现**

`AvatarIdle`：加 `public bool EyesClosed { get; set; }`，在 `TickBlink` 的最前面判——`true` 时 `Rig.SetExpression(ExpressionKey.Blink, 1f)` 并且 `BlinkWeight = 1f`，直接返回（**不许**只改 `BlinkWeight` 属性，那只是内部状态，VRM 脸不会变）；`false` 时走原来的包络，`BlinkWeight` 上限仍是 `BlinkCap = 0.85`。

`PlayerReach.Face`：`Head.position + transform.forward * 0.10f - transform.up * 0.04f`（鼻子高度，比眼低 4 cm）。这是玩家头部的代理点，和 Task 1 的掌心/头顶一样属于「没有骨架就拿代理点」的同一套约定。

**as-built（2026-11-04，计划原稿的蹲姿方案几何不成立，已由 Lead 裁决改姿势）**

`ActionPoses.Kiss` = `Spine (13,0,0)` / `Chest (7,0,0)` / `Neck (20,0,0)` / `Head (14,0,0)`（合计 54°，低头多于折腰），双臂 `(0,0,±34)`。

`PlayerPosture.Kneeling`（新增，enum 末尾）= `(eye 1.28, capsule 1.32, centre 0.66)`；`PlayerReach.Face` 在跪姿下 y = 1.2400。**为什么必须加姿势**：她的坐姿头心 1.3368 被现有三种姿势夹在中间（站 1.62 / 蹲 1.05 / 坐 1.10），蹲姿的 Face 1.0100 与她差 0.33 m，而 60° 脊柱前倾最多只买到 0.21 m —— 实测 60° 表 gap 0.2275 m、align 25.4°；把 60° 全给躯干仍有 0.1162 m 垂直差；要 2 cm 需 76° 且 k=0.574，那画面是她整个人趴上来。

`KissOffset = 0.31f`：按 `(L, k, gap)` 扫描定值（30/0.3055/0.1160、36/0.2964/0.0839、42/0.3000/0.0565、48/0.2992/0.0353、**54/0.3110/0.0140**、60/0.3201/0.0037 为假解——k=0.32 时视线已越过玩家的脸）。最终 gap 0.0134 m、align 3.4°、Held 2.6 s。

**胶囊与长凳重叠 0.284 m，这是明确批准的**：`Mesh_0001out` AABB x∈[−0.189,0.399]，r=0.30 的胶囊在 spot.x=−0.205 处伸到 x=0.095；面对面两个点要重合，任何姿势都绕不开。第一人称看不到身体网格，且跪姿 eye 1.28 比座面 0.604 高 0.676 m —— **底线 `EyeHeight > seatTopY + 0.40`（1.28 > 1.004）写进了 `ActionKissTests`**，否则镜头会掉进坐垫。

`ActionDirector`：`kiss` 前置 `Posture == Kneeling`（`LastFaults = "需要跪下"`；pat-me 仍是 `Crouching` / `"需要蹲下"`，两者不是同一个姿势）；进场 `EyesClosed = true`，`Cancel`/`OnDisable` 置回 false。`ContactError` 用 `HerHeadCenter ↔ Face`。

- [ ] **Step 4: 跑绿**

Expected: `total 151 / passed 150 / failed 0 / skipped 1`（比计划的 150 多 1：`PlayerPostureTests` 新增 `KneelingPutsTheEyeAt128AndClearsTheSeat`，因为 `Kneeling` 是计划里没有的姿势）。

- [ ] **Step 5: 暂存**

---

### Task 7: 并肩坐床（`sit-together`）

**Files:**
- Modify: `apps/unity/Assets/Scripts/Avatar/AvatarPose.cs`（加 `BedSitting`）
- Modify: `apps/unity/Assets/Scripts/Avatar/AvatarSeat.cs`（加 `SitOnBed`）
- Modify: `apps/unity/Assets/Scripts/Avatar/AvatarActor.cs`（加 `SitOnBed(string seatId)`）
- Create: `apps/unity/Assets/Scripts/Actions/ActionSeats.cs`（`BedPair`）
- Modify: `apps/unity/Assets/Scripts/PlayerMotor.cs`（`SetPosture` 加 `seatTopY`）
- Modify: `apps/unity/Assets/Scripts/Actions/ActionDirector.cs`（`sit-together` 分支）
- Test: `apps/unity/Assets/Tests/PlayMode/ActionSitTogetherTests.cs`

**Interfaces:**
- Consumes: `AnchorTable.Seat("S2")`（`x 1.741 / y 0.916 / z -0.025 / topY 0.906 / facingY 0`）、`AvatarSeat.HipsGap`、`AvatarActor.Body`。
- Produces: `static void AvatarPose.BedSitting(AvatarRig rig)`；`void AvatarSeat.SitOnBed(SeatEntry seat)`；`void AvatarActor.SitOnBed(string seatId)`；`static SeatEntry ActionSeats.BedPair(SeatEntry seat, float offset = 0.70f)`（沿她的右手方向偏移，复制 `topY/facingY`，`id = seat.id + "-pair"`）；`void PlayerMotor.SetPosture(PlayerPosture posture, float seatTopY = 0f)`（Sitting 时 `eye = seatTopY + 0.50f`，`seatTopY == 0` 退回 1.10）。

- [ ] **Step 1: 写失败的用例**

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Mas3D;
using Mas3D.Actions;
using Mas3D.Avatar;

namespace Mas3D.Tests
{
    public class ActionSitTogetherTests
    {
        AvatarActor _actor;
        PlayerMotor _player;
        ActionDirector _director;
        AnchorTable _table;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            AvatarPrefabFixture.Require();
            _table = AnchorTable.Load();
            _actor = AvatarActor.Spawn();
            var go = new GameObject("Player");
            go.AddComponent<CharacterController>();
            _player = go.AddComponent<PlayerMotor>();
            _player.KeyboardInput = true;   // 起手必须能走：否则「Cancel 归还」与「什么都不做」同值
            _player.EnsureReady();
            var dir = new GameObject("Actions");
            _director = dir.AddComponent<ActionDirector>();
            _director.Actor = _actor;
            _director.Player = _player;
            _director.KeyboardInput = false;
            _director.DriveInUpdate = false;
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_director != null) Object.Destroy(_director.gameObject);
            if (_actor != null) Object.Destroy(_actor.gameObject);
            if (_player != null) Object.Destroy(_player.gameObject);
            yield return null;
        }

        static readonly HumanBodyBones[] Legs =
        {
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
            HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
        };

        [UnityTest]
        public IEnumerator TheBedPoseKeepsEveryLegBoneAboveTheMattress()
        {
            var seat = _table.Seat("S2");
            _actor.SitOnBed("S2");
            yield return null;
            foreach (var bone in Legs)
            {
                var p = _actor.Rig.Bone(bone).position;
                Assert.GreaterOrEqual(p.y, seat.topY - 0.01f, bone + " 陷进床垫了：腿必须在床面之上");
            }
        }

        [UnityTest]
        public IEnumerator SittingOnTheBedSnapsTheHips()
        {
            var seat = _table.Seat("S2");
            _actor.SitOnBed("S2");
            yield return null;
            Assert.AreEqual(seat.topY + AvatarSeat.HipsGap, _actor.Seat.HipsWorld.y, 0.01f, "髋高必须等于床面 + HipsGap（±1 cm）");
            Assert.AreEqual(seat.x, _actor.Seat.HipsWorld.x, 0.02f, "髋位必须落在床沿槽位上");
            Assert.AreEqual(seat.z, _actor.Seat.HipsWorld.z, 0.02f, "z 也要对上");
        }

        [UnityTest]
        public IEnumerator ThePlayerSitsOnTheSecondSlot()
        {
            Assert.IsTrue(_director.TryEnterAction(ActionId.SitTogether), "床沿两个槽位都空着时，并肩坐必须能进场");
            yield return null;
            var seat = _table.Seat("S2");
            var pair = ActionSeats.BedPair(seat);
            Assert.AreEqual(PlayerPosture.Sitting, _player.Posture, "玩家必须切成坐姿");
            Assert.AreEqual(seat.topY + 0.50f, _player.Head.position.y, 0.02f, "坐下后眼高必须是床面 + 0.50 m");
            Assert.AreEqual(pair.x, _player.transform.position.x, 0.02f, "玩家必须坐在第二个床沿槽位上");
            Assert.AreEqual(pair.z, _player.transform.position.z, 0.02f, "z 也要对上");
            Assert.AreEqual(seat.facingY, _player.transform.eulerAngles.y, 2f, "两人必须朝同一个方向坐");
        }

        [UnityTest]
        public IEnumerator TheTwoBodiesStayFortyCentimetresApart()
        {
            Assert.IsTrue(_director.TryEnterAction(ActionId.SitTogether));
            yield return null;
            var hers = _actor.Seat.HipsWorld;
            var his = _player.transform.position;
            var gap = new Vector2(hers.x - his.x, hers.z - his.z).magnitude;
            Assert.GreaterOrEqual(gap, 0.40f, "两人髋位的水平间距必须 ≥ 0.40 m：胶囊半径 0.30 + 她身体 0.20，再近就穿模了");
        }
    }
}
```

- [ ] **Step 2: 跑红**

```powershell
powershell -ExecutionPolicy Bypass -File D:\Mas3D\tools\unity.ps1 test -Mode PlayMode *> D:\Mas3D\apps\unity\Logs\task7-red.log
Select-String -Path D:\Mas3D\apps\unity\Logs\task7-red.log -Pattern "error CS"
```

Expected: `error CS1061: 'AvatarActor' does not contain a definition for 'SitOnBed'`、`error CS1061: 'AvatarPose' does not contain a definition for 'BedSitting'`、`error CS0103: The name 'ActionSeats' does not exist`。

- [ ] **Step 3: 实现**

`AvatarPose.BedSitting`：大腿比坐凳抬得更高、小腿折回来让脚落在床面上——起点用 `Hips (-4,0,0)`、`Spine (2,0,0)`、`Chest (1,0,0)`、`UpperLeg (-52,0,±7)`、`LowerLeg (104,0,0)`、`Foot (-28,0,0)`、`UpperArm (0,0,±68)`、`LowerArm (0,±62,0)`。**这是起点不是答案**：床面 0.906 比凳面 0.604 高 0.30 m，髋抬上去之后脚必须落在床面之上而不是穿过它；用 `[bed] bone=<名字> y=<值> top=<值>` 的日志逐根打印（照 `AvatarSeatTests` 已经有的 `[bench]` 日志写法），按数字调 `LowerLeg`/`Foot`，直到六根腿骨全部 ≥ `topY - 0.01`。

`AvatarSeat.SitOnBed(SeatEntry seat)`：和 `Sit` 同一条路，只是换成 `AvatarPose.BedSitting` 并且在抬髋之前把 `Seated` 先置 false（`Sit` 里已有的守卫照抄）。`AvatarActor.SitOnBed(seatId)`：`Seat.SitOnBed(table.Seat(seatId))` → `Idle.CaptureBaseline()` → `FitBody()`（和 `SitAt` 一模一样的三步）。

`ActionSeats.BedPair(seat, offset = 0.70f)`：`var right = Quaternion.Euler(0f, seat.facingY, 0f) * Vector3.right;` 新槽位 `x = seat.x + right.x * offset`、`z = seat.z + right.z * offset`、`y = seat.y`、`topY` 与 `facingY` 照抄，`id = seat.id + "-pair"`、`mesh = seat.mesh`。不改 `anchors.json`（它是生成物，`player` 会覆盖它）。

`PlayerMotor.SetPosture(posture, seatTopY = 0f)`：`Sitting` 且 `seatTopY > 0` 时 `eye = seatTopY + 0.50f`，胶囊高度仍 1.15、中心仍 0.575（她坐的床 0.906 高，眼 1.406 才对得上）。

`ActionDirector` 的 `sit-together`：进场顺序必须是「先她坐下、再摆玩家」——`Actor.SitOnBed("S2")` → `Player.SetPosture(Sitting, seat.topY)` → `Teleport(pair 的 (x, 0, z), seat.facingY)` → `Camera.SetOrbit(seat.facingY + 200f, 10f)`（从两人侧前方看）。`ContactError` = 髋位三个分量的最大偏差（水平 / 竖直分别算，取 `HipsGap` 目标）。`Cancel` 要还原：她被 `SitAt("S1")` 放回凳子、玩家站起回到入场位置。

- [ ] **Step 4: 跑绿**

Expected: `total 157 / passed 156 / failed 0 / skipped 1`（基数 152 = 151 + fix-5 的 `AThreeFramePatLeavesNoLeanBehind`，加本任务 4 例，再加 Task 6 评审 I1 的 `KissingOnTwoFeetIsRefusedNotForcedDown`）。

- [ ] **Step 5: 暂存**

> **已知限制（两处，执行时必须照写进 spec §5.20）**：① spec §6 说「床沿两个座位槽」，而 `anchors.json` 的 `S2` 是床面中线（`z -0.025`，床体 `z -0.981..0.931`）、由 `tools/anchor-plan.mjs` 生成——真正的床沿没有现成槽位。本计划的取向是**坐在床面上**（腿折起来搁在被子上），因为重做床沿槽位要改生成器并重跑全部锚点，超出 P4 的范围；YG 若要字面的床沿，另开一个小计划改量测。② `S2` 的 `note` 早就写了「床顶是羽绒被，真正的床垫面要另量」。本 Task 用 `topY = 0.906` 当床面，她的臀会落在被子顶面而不是被压下去的位置，视觉上像坐在被子上。Task 8 的图里如果难看，改的是 `anchors.json` 的生成输入（`tools/anchor-plan.mjs` 的床体量测），不是这一层的代码。

---
### Task 8: 场景接线、四张动作图与文档

**Files:**
- Modify: `apps/unity/Assets/Scripts/RoomBootstrap.cs`（加 `public ActionDirector Actions;` 并在 `Boot()` 末尾接线）
- Modify: `apps/unity/Assets/Editor/CliPlayer.cs`（`-actions 1` 出四张图）
- Modify: `docs/superpowers/specs/2026-11-01-unity-rebuild-design.md`（新增 §5.20）
- Test: `apps/unity/Assets/Tests/PlayMode/ActionSceneTests.cs`

**Interfaces:**
- Consumes: Task 3 的 `ActionDirector`、`RoomBootstrap` 既有字段（`Room/Player/Rig/OutputCamera/SpawnAnchor/Actor/Dialogue/Voice`）、`CliPlayer.Capture` 既有的 `WriteShot`。
- Produces: `RoomBootstrap.Actions`（场景里的 `ActionDirector`，`Boot()` 保证非空且四个字段都接上）；`tools/unity.ps1 run -- -executeMethod Mas3D.Editor.CliPlayer.Capture -actions 1` 产出 `apps/unity/Shots/action-{hands,pat-her,pat-me,kiss,sit-together}.png`。

- [ ] **Step 1: 写失败的用例**

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Mas3D;
using Mas3D.Actions;

namespace Mas3D.Tests
{
    public class ActionSceneTests
    {
        static void RequireScene()
        {
            AvatarPrefabFixture.Require();
            if (!Application.CanStreamedLevelBeLoaded("Main"))
                Assert.Ignore("先跑 tools/unity.ps1 player 构建 Main.unity");
        }

        [UnityTest]
        public IEnumerator TheSceneCarriesADirectorReadyToPerform()
        {
            RequireScene();
            try
            {
                yield return SceneFlow.LoadMain();
                var boot = Object.FindAnyObjectByType<RoomBootstrap>();
                Assert.IsNotNull(boot, "Main.unity 必须带 RoomBootstrap");
                yield return new WaitUntil(() => boot.Actions != null);   // Boot() 在 Start 里跑；卡住就是没接线
                Assert.AreSame(boot.Actor, boot.Actions.Actor, "导演必须接上同一个她");
                Assert.AreSame(boot.Player, boot.Actions.Player, "导演必须接上场景里的玩家");
                Assert.AreSame(boot.Rig, boot.Actions.Camera, "导演必须接上同一个相机架");
            }
            finally { SceneFlow.UnloadMainIfLoaded(); }
        }

        [UnityTest]
        public IEnumerator EnteringAnActionFromTheSceneMovesThePlayerToTheMark()
        {
            RequireScene();
            try
            {
                yield return SceneFlow.LoadMain();
                var boot = Object.FindAnyObjectByType<RoomBootstrap>();
                yield return new WaitUntil(() => boot.Actions != null);
                Assert.IsTrue(boot.Actions.TryEnterAction(ActionId.Hands), "场景里的她坐在 S1 上，牵手必须能进场");
                yield return null;
                var seat = AnchorTable.Load().Seat("S1");
                var want = new Vector3(seat.x, 0f, seat.z) + Quaternion.Euler(0f, seat.facingY, 0f) * Vector3.forward * 0.62f;
                Assert.AreEqual(want.x, boot.Player.transform.position.x, 0.03f, "玩家必须被摆到她正前方 0.62 m");
                Assert.AreEqual(want.z, boot.Player.transform.position.z, 0.03f, "z 也要对上");
            }
            finally { SceneFlow.UnloadMainIfLoaded(); }
        }

        [UnityTest]
        public IEnumerator CancellingFromTheSceneReturnsThePlayerToTheSpawn()
        {
            RequireScene();
            try
            {
                yield return SceneFlow.LoadMain();
                var boot = Object.FindAnyObjectByType<RoomBootstrap>();
                yield return new WaitUntil(() => boot.Actions != null);
                var spawn = boot.Player.transform.position;
                Assert.IsTrue(boot.Actions.TryEnterAction(ActionId.Hands));
                yield return null;
                boot.Actions.Cancel();
                yield return null;
                Assert.Less(Vector3.Distance(spawn, boot.Player.transform.position), 0.05f, "Cancel 之后玩家必须回到出生点 5 cm 以内");
                Assert.IsNull(boot.Actions.Current, "Cancel 之后必须回到 idle");
            }
            finally { SceneFlow.UnloadMainIfLoaded(); }
        }
    }
}
```

> `SceneFlow` 是把 `apps/unity/Assets/Tests/PlayMode/AvatarSceneTests.cs` 里已有的三个私有 helper（`LoadMain()`、`UnloadMainIfLoaded()`、`RequireAvatarPrefab()` 的 `Application.CanStreamedLevelBeLoaded` 守卫）抽出来的 `internal static class`，放同一个 `Tests/PlayMode` 目录。additive 加载 + `finally` 卸载是硬要求：`LoadSceneMode.Single` 会把她的胶囊和场景 Player 漏到别的夹具，已经撞出过四个假失败。**不要**在测试里调 `boot.Boot()`——`Start()` 已经调过一次，第二次会把碰撞体重挂一遍。

- [ ] **Step 2: 跑红**

```powershell
powershell -ExecutionPolicy Bypass -File D:\Mas3D\tools\unity.ps1 test -Mode PlayMode *> D:\Mas3D\apps\unity\Logs\task8-red.log
Select-String -Path D:\Mas3D\apps\unity\Logs\task8-red.log -Pattern "error CS"
```

Expected: `error CS1061: 'RoomBootstrap' does not contain a definition for 'Actions'`（三处）。

- [ ] **Step 3: 实现**

`RoomBootstrap`：加字段 `public ActionDirector Actions;`；`Boot()` 最后（`Voice.Bind(...)` 之后）补一段：`if (Actions == null) { var go = new GameObject("Actions"); Actions = go.AddComponent<ActionDirector>(); }` 然后 `Actions.Actor = Actor; Actions.Player = Player; Actions.Camera = Rig;`。**不许**在 `Boot()` 里替玩家决定姿势或位置——那是 `TryEnterAction` 的事。

`CliPlayer.Capture`：加 `-actions 1` 分支：主相机出完 `play-01..04` 与 `avatar-seated` 之后，对 `ActionId.All` 里每个 id 依次 `boot.Actions.TryEnterAction(id)` → 连跑 90 帧 `Actions.Tick(1f/60f)` + `yield WaitForEndOfFrame` → `WriteShot(target, Path.Combine("Shots", "action-" + id + ".png"))` → `boot.Actions.Cancel()`。`pat-me` 与 `kiss` 需要蹲姿，进场前先 `boot.Player.SetPosture(PlayerPosture.Crouching)`（导演自己不会替你蹲，见 Task 5）。`sit-together` 会把玩家挪到床上，出完图要 `Cancel()` 再出下一张，否则后续动作的标记点会算在错误的位置上。

跑图命令（**不带 `-nographics`**，要 GPU）：

```powershell
powershell -ExecutionPolicy Bypass -File D:\Mas3D\tools\unity.ps1 run -- -executeMethod Mas3D.Editor.CliPlayer.Capture -actions 1 -logFile D:\Mas3D\apps\unity\Logs\task8-shots.log
Get-ChildItem D:\Mas3D\apps\unity\Shots\action-*.png | Select-Object Name, Length
```

Expected: 五张图，每张 > 100 KB；日志里有 `[cap] wrote` 与四次 `[act] <id> held=`。

- [ ] **Step 4: 目视五张图**

逐张打开 `apps/unity/Shots/action-*.png`，按这四条看：①她的手真的贴在玩家掌心/头顶的位置，不是悬空 20 cm；②`pat-her` 的头是低的、`pat-me` 的肘往前；③`kiss` 两张脸是并排靠近的，不是鼻子顶额头；④`sit-together` 两人并排坐、腿没有插进被子。任一条不对——先改常数（`HerPalm` 的 0.08、`HerHeadTop` 的 0.20、kiss 的 0.34、`BedSitting` 的角度），改完重跑本 Task 与对应动作 Task 的用例。**图不过就是没完成**：代码全绿只说明代理点对上了。

- [ ] **Step 5: 文档**

在 spec 里新增 `### 5.20 P4 四件动作`，写：五个 id 与各自的接触判据（2/3/6 cm、15°、2 s/3 s）、演出态锁（`KeyboardInput=false` + 姿势 + 标记点 + 相机轨道，Esc 还原）、动作层只写手臂/颈/头而 `AvatarIdle` 只写 Chest/Spine 的分工、`PlayerReach` 三个代理点的数值、`S2` 床面用 0.906 的已知限制、本计划的用例计数（**163 total / 162 passed / 1 skipped**，含 Task 6 评审 I1 补的 `KissingOnTwoFeetIsRefusedNotForcedDown`、交付时补的 `TheKissFramesItsCloseUpInsteadOfSittingInHerHair`，以及整支评审收口补的 `DisablingTheDirectorReleasesThePlayer` 与 `ABedSheCannotSitOnIsRefusedNotHalfEntered`）与 `turnId`，以及「代理点判据不等于好看，好看靠 Task 8 的图」。

- [ ] **Step 6: 跑绿并暂存**

```powershell
powershell -ExecutionPolicy Bypass -File D:\Mas3D\tools\unity.ps1 test -Mode PlayMode *> D:\Mas3D\apps\unity\Logs\task8-green.log
powershell -ExecutionPolicy Bypass -File D:\Mas3D\tools\unity.ps1 test -Mode EditMode *> D:\Mas3D\apps\unity\Logs\task8-edit.log
powershell -ExecutionPolicy Bypass -File D:\Mas3D\tools\unity.ps1 smoke *> D:\Mas3D\apps\unity\Logs\task8-smoke.log
git -C D:\Mas3D add -A apps/unity docs
```

Expected: PlayMode `163 total / 162 passed / 0 failed / 1 skipped`；EditMode `44/44`；smoke 打印 `[smoke] turn ok turnId=...`；三条都 exit 0。

- [x] Step 9（交付时补的 as-built）：**亲吻的玩法机位**。首个 160/159 的版本里 `action-kiss-gamecam.png` 是整幅头发——`[act]` 打点证实 `cam=(-0.29,1.25,-0.04)`、`pivot=(-0.21,1.28,-0.02)`、`obstacle=Monkia`：她在 0.31 m 外，胶囊表面离他的头 0.115 m，0.20 m 的避障球一开始就压在她身上，Cinemachine 把 2.8 m 的摇臂压到 0.085 m。修法是给 kiss 单开一套取景：`KissOrbitYaw = 90`（相对玩家朝向转四分之一圈，肩部偏置朝她反方向，摇臂横过房间成侧面机位）、`KissCameraRadius = 0.06`、`KissCameraDistance = 1.9`，`Cancel` 还回 0.20 与原距离；`PlayerCameraRig` 为此新增 `CameraRadius` 字段与 `SetCameraRadius()`。出图复验 `cam=(-0.48,1.79,1.82) obstacle=none`，侧面构图里她俯身闭眼、头发分开。`ActionKissTests.TheKissFramesItsCloseUpInsteadOfSittingInHerHair` 用**字面量** 0.06 / 1.9 / 180° 钉住（写常量会让「把它调回旧值」照样全绿，实测三项一起改回去唯一红的就是这条：`Expected: 0.06 But was: 0.2`）。`CliPlayer` 的 `[act]` 行同时加打 `dist / rigYaw / obstacle`，相机被挤扁时不用再猜。

---

## 交付口径

- 全部 8 个 Task 完成后由 Lead 跑一遍完整矩阵（PlayMode / EditMode / smoke / `-actions 1` 出图），再问 YG 是否提交。
- 提交信息建议 `feat(actions): let her hold hands, pat and kiss`，正文列出五个 id 与判据数值。
- 若某个 Task 的常数在校准中改动，**必须**在同一个 Task 里把用例的期望值一起改并重跑，不许留下「用例绿但常数已换」的树。

## 不在本计划内

- 语音命令触发动作（「牵我的手」）：P3 只回文字，接动作要另开一层意图解析。
- 动画片段（`AnimationClip` / 逐帧手 K）：本阶段全部是程序化姿势 + 解析 IK。
- 玩家模型长出手臂：等 `Me.vrm` 真正进场景再谈，届时 `PlayerReach` 的代理点换成人骨。
- 行走时自动避障到动作标记点（NavMesh）：spec §8.2 明确不做，`TryEnterAction` 直接把人摆过去。
- `S2` 床垫面的重新量测（见 Task 7 的已知限制）。