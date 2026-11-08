# 玩家身体进包（Player Body in Build）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让打包版（build/Mas3D.exe）里的玩家也有身体——把 Me.vrm 烘进 Main.unity，运行时由 PlayerBody 收编（adopt）场景里那份实例，而不是去 Instantiate 一个编辑器里才存在的资产。

**Architecture:** CliPlayer.BuildScene 在编辑器里实例化 Assets/_Local/avatars/Me.vrm，挂到 Player 下、命名 Me、局部 TRS 归零、SetActive(false)，写进 PlayerBody.Baked；构建出来的场景因此自带一份 inactive 的身体。PlayerBody.Spawn() 的来源顺序变成 Baked（收编，不 Instantiate）→ Prefab（Instantiate，给 B 路线留口）→ LoadPrefab()（仅编辑器）。打包版自检多写一行 body ok renderers=… height=… shader=…，tools/player-smoke.ps1 的判据从五行变六行。

**Tech Stack:** Unity 6000.6.7f1（URP 17.6、VRM10、glTFast）、C# Editor 脚本、NUnit PlayMode 测试、tools/unity.ps1（Unity CLI 1.0.0-beta.13 包装）、PowerShell 5.1 冒烟脚本。

**Spec:** docs/superpowers/specs/2026-11-08-player-body-in-build-design.md（方案 A：场景内联）

## Global Constraints

- 只改 `apps/unity/` 与 `tools/`；web 侧、PlayerMotor、PlayerCameraRig、动作系统（ActionDirector 四个件）、相机参数一律不动。
- 提交按 AGENTS.md：**每一步提交前问用户**，不自行 `git push`。每个任务给出的 commit 命令只是格式，执行者必须先征得同意。
- Unity 是单实例：任何 `unity.ps1` / `unity test` 调用之前，编辑器必须已关闭，否则 CLI 会连到正在跑的那一个。
- `tools/*.ps1` 必须保持纯 ASCII（PS 5.1 读无 BOM 的 UTF-8 中文会报「switch 语句的子句中缺少语句块」）。C# 里可以写中文注释。
- `Assets/_Local/avatars/Me.vrm` 不存在时：`CliPlayer.BuildScene` 只 `Debug.LogWarning`，**不许** `EditorApplication.Exit(1)`；任何读该资产的测试一律 `Assert.Ignore`，不许悄悄变绿。
- 玩家身体高度必须是 1.70 ± 0.02 m（`PlayerBody.TargetHeight`）；着色器名字为空即整条自检失败。
- `-nobody 1` 优先级最高：不 `Spawn()`，烘好的那份实例保持 inactive。
- 统一跑单个 PlayMode 用例的命令（下文简称「聚焦跑」）：

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
$unity  = Join-Path $env:LOCALAPPDATA 'Unity\bin\unity.exe'
$filter = 'Mas3D.Tests.PlayerBodyTests.SpawnAdoptsTheBakedBodyInsteadOfInstantiatingAnother'
& $unity test D:\Mas3D\apps\unity --mode PlayMode --output D:\Mas3D\apps\unity\TestResults\body.xml --report-format nunit --filter $filter
```

  换用例只改 `$filter`；判定看 `apps/unity/TestResults/body.xml` 与进程退出码。

## Review Focus

1. **`-nobody 1` 撞上烘好的身体**：截图机位、离线对比都靠它把玩家变回空胶囊。期望：身体保持 inactive、`Spawned == false`、画面里没有 Me。钉在 Task 1 的 `ABakedBodyStaysAsleepUntilSpawnIsCalled`。
2. **干净 clone 里没有 Me.vrm**：期望构建照样写出场景，只是没有 Baked、玩家仍是胶囊，而不是整条构建挂掉。这台机器上造不出缺资产，由 Task 2 的 `BakeBody` 早退分支（`LogWarning` 而非 `Exit(1)`）保证，复核靠日志 `[player] body missing at`。
3. **IL2CPP 把 URP 着色器剥掉**：烘进场景的材质在包里找不到 shader 时会画成粉色，自检若还报 ok 就等于假装成功。期望自检明确失败。钉在 Task 3 的 `BodySummarySaysFailedWhenTheMaterialLostItsShader`。
4. **VRM 自带的 spring-bone collider 被序列化进场景**：胶囊会从内部撞自己的四肢。期望 `Spawn()` 之后层级里 0 个 collider。钉在 Task 1、Task 2 两个 collider 断言。
5. **Player 上出现两个 PlayerBody**：`[DisallowMultipleComponent]` 只能挡住同一物体重复添加，挡不住「场景里一个 + RoomBootstrap 又 Add 一个」。期望 RoomBootstrap 找到场景里那个、整棵玩家层级只有一个 PlayerBody、身体只有一个 Me。钉在 Task 2 的 `TheSceneCarriesABakedBodyTheBuildCanAdopt`。

---


### Task 1: PlayerBody 收编烘进场景的身体

**Files:**
- Modify: `apps/unity/Assets/Scripts/PlayerBody.cs:49-138`（新增 `Baked` 字段、把 Spawn 拆成「取来源 → 统一收尾」）
- Test: `apps/unity/Assets/Tests/PlayMode/PlayerBodyTests.cs`（追加两个用例）

**Interfaces:**
- Consumes: 现有的 `AvatarRig.Spawn(GameObject prefab, string name)`、`AvatarRig.Measure()`、`AvatarPose.ArmsDown(AvatarRig)`、`StripColliders(GameObject)`、`FitToHeight()`、`CollectHeadRenderers()`、`ApplyNow()`。
- Produces: `public GameObject Baked;`（场景里那份 inactive 的 Me，CliPlayer 写、Spawn() 读）；`Spawn()` 语义变为 Baked 优先。后续任务只依赖这两点。

- [ ] **Step 1: 写失败测试**

在 `PlayerBodyTests` 的 `AttachAndSpawn()` 后面追加两个用例：

```csharp
/// <summary>
/// 打包版没有 AssetDatabase，身体只能是场景里烘好的那一份。收编的意义全在「不 Instantiate」：
/// 再造一个 Me 就会有两套骨骼、两倍 draw call，位置还对不上。
/// </summary>
[UnityTest]
public IEnumerator SpawnAdoptsTheBakedBodyInsteadOfInstantiatingAnother()
{
    RequireBodyAsset();

    var source = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(PlayerBody.BodyPrefabPath);
    Assert.IsNotNull(source, "缺 " + PlayerBody.BodyPrefabPath);

    // 模拟 CliPlayer 烘进场景的那一份：Player 的子物体、局部 TRS 归零、inactive。
    var baked = Object.Instantiate(source, _player.transform);
    baked.name = PlayerBody.BodyName;
    baked.transform.localPosition = Vector3.zero;
    baked.transform.localRotation = Quaternion.identity;
    baked.transform.localScale = Vector3.one;
    baked.SetActive(false);

    _body.Baked = baked;
    _body.Attach(_motor, _motor.Head, _rig);

    Assert.IsTrue(_body.Spawn(), "有 Baked 时 Spawn() 必须成功");
    yield return null;

    Assert.AreSame(baked, _body.Avatar.gameObject, "收编不许再造一个 Me");
    Assert.IsTrue(baked.activeSelf, "收编后身体要被打开");
    Assert.AreEqual(0, baked.GetComponentsInChildren<Collider>(true).Length,
        "烘进场景的 spring collider 也要在 Spawn 里清掉");
    Assert.AreEqual(1, _player.GetComponentsInChildren<AvatarRig>(true).Length,
        "玩家下面只该有一个身体");
}

/// <summary>
/// -nobody 1 与所有截图机位都指望这一条：没人 Spawn 的时候，场景里那份身体是睡着的。
/// </summary>
[Test]
public void ABakedBodyStaysAsleepUntilSpawnIsCalled()
{
    var baked = new GameObject(PlayerBody.BodyName);
    baked.transform.SetParent(_player.transform, false);
    baked.SetActive(false);

    _body.Baked = baked;
    _body.Attach(_motor, _motor.Head, _rig);

    Assert.IsFalse(baked.activeSelf, "没人 Spawn 之前，烘好的身体必须保持 inactive");
    Assert.IsFalse(_body.Spawned, "Spawned 只该在 Spawn() 之后为真");
}
```

- [ ] **Step 2: 跑红**

Run（聚焦跑，`$filter = 'Mas3D.Tests.PlayerBodyTests.SpawnAdoptsTheBakedBodyInsteadOfInstantiatingAnother'`）
Expected: 编译失败 `error CS0117: 'PlayerBody' does not contain a definition for 'Baked'`（两条用例一起红）。

- [ ] **Step 3: 实现**

在 `PlayerBody.cs` 的 `Prefab` 字段后面加：

```csharp
[Tooltip("Optional: the body instance baked into the scene by CliPlayer. Adopted, never re-instantiated.")]
public GameObject Baked;
```

把 `Spawn()` 的取来源那一段（原 `:106-119`）改成：

```csharp
AvatarRig rig = null;
if (Baked != null)
{
    // Baked body: the scene already carries the model, so adopting it is the only thing left to do.
    // SetActive first: an inactive object's Awake has not run, so the rig would stay unbound.
    Baked.SetActive(true);
    rig = Baked.GetComponent<AvatarRig>();
    if (rig == null) rig = Baked.AddComponent<AvatarRig>();
    rig.EnsureBound();
}
else
{
    var prefab = Prefab != null ? Prefab : LoadPrefab();
    if (prefab == null)
    {
        Debug.LogError("[body] 找不到 " + BodyPrefabPath + "：玩家只能继续当胶囊。"
            + "打包时要先把模型烘进场景（Baked 字段）。");
        return false;
    }

    rig = AvatarRig.Spawn(prefab, BodyName);
}

Avatar = rig;
var root = Avatar.transform;
root.SetParent(transform, false);
root.localPosition = Vector3.zero;
root.localRotation = Quaternion.identity;

Finish(root);
```

原来的 `springColliders` 到 `return true;` 那一段（`:120-137`）整体搬进新的私有方法：

```csharp
/// <summary>Shared tail of both spawn paths: strip, scale, stand, collect, place, log.</summary>
void Finish(Transform root)
{
    var springColliders = Avatar.GetComponentsInChildren<Collider>(true).Length;
    StripColliders(Avatar.gameObject);
    FitToHeight();
    AvatarPose.ArmsDown(Avatar);
    CollectHeadRenderers();
    _visibilityApplied = false;
    ApplyNow();

    Debug.Log(string.Format("[body] spawned '{0}' source={1} renderers={2} head={3} height={4:F3} scale={5:F3} feetOffset={6:F3} strippedColliders={7}",
        BodyName, Baked != null ? "baked" : "prefab", Avatar.GetComponentsInChildren<Renderer>(true).Length,
        _headRenderers.Count, Avatar.Measure().size.y, root.localScale.x, _feetOffset, springColliders));
}
```

- [ ] **Step 4: 跑绿**

Run: 同上命令
Expected: PASS，日志出现 `[body] spawned 'Me' source=baked`。

- [ ] **Step 5: 提交（先问用户）**

```bash
git add apps/unity/Assets/Scripts/PlayerBody.cs apps/unity/Assets/Tests/PlayMode/PlayerBodyTests.cs
git commit -m "feat(player): adopt the body baked into the scene"
```

---


### Task 2: BuildScene 把 Me.vrm 烘进 Main.unity

**Files:**
- Modify: `apps/unity/Assets/Editor/CliPlayer.cs:60`（BuildPlayer() 之后调用烘焙）、`:187-204`（BuildPlayer() 里补 PlayerBody 组件）、新增 `BakeBody(GameObject player)`
- Test: 新建 `apps/unity/Assets/Tests/PlayMode/BakedBodyTests.cs`

**Interfaces:**
- Consumes: `PlayerBody.Baked`、`PlayerBody.BodyPrefabPath`（Task 1）；`SceneFlow.Require()` / `SceneFlow.LoadMain()` / `SceneFlow.UnloadMainIfLoaded()`（既有）。
- Produces: 构建日志行 `[player] body baked renderers=<n> height=<h:F3>`（或 `[player] body missing at <path>`）；Main.unity 里 Player 上带一个 PlayerBody，其 Baked 指向 inactive 的 Me。

- [ ] **Step 1: 写失败测试**

新建 `BakedBodyTests.cs`：

```csharp
using System.Collections;
using Mas3D.Avatar;
using Mas3D.Boot;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mas3D.Tests
{
    /// <summary>
    /// 打包版没有 AssetDatabase：身体只能靠场景里烘好的那份。这一条用例同时钉住三件事——
    /// 场景真的烘了身体、运行时收编的是同一份实例（不是又 Instantiate 一个）、玩家身上只有一个 Me。
    /// </summary>
    public class BakedBodyTests
    {
        [UnityTest]
        public IEnumerator TheSceneCarriesABakedBodyTheBuildCanAdopt()
        {
            SceneFlow.Require();

            try
            {
                yield return SceneFlow.LoadMain();

                var boot = Object.FindAnyObjectByType<RoomBootstrap>();
                Assert.IsNotNull(boot, "Main.unity 必须带 RoomBootstrap");
                Assert.IsNotNull(boot.Player, "RoomBootstrap.Player 该指向 PlayerMotor");

                var player = boot.Player.gameObject;
                var body = player.GetComponent<PlayerBody>();
                Assert.IsNotNull(body, "Player 上该有 PlayerBody（CliPlayer 烘场景时加的）");
                Assert.IsNotNull(body.Baked, "场景里必须烘一份 Me（Baked 字段）");

                Assert.IsTrue(body.Spawned, "Boot() 该已经收编了身体");
                Assert.AreSame(body.Baked, body.Avatar.gameObject,
                    "收编必须是同一份实例——另 Instantiate 一个 Me 说明走错了分支");

                Assert.AreEqual(1, player.GetComponentsInChildren<AvatarRig>(true).Length,
                    "玩家层级里只该有一个身体");

                var named = 0;
                foreach (var t in player.GetComponentsInChildren<Transform>(true))
                    if (t.name == PlayerBody.BodyName) named += 1;
                Assert.AreEqual(1, named, "只该有一个 " + PlayerBody.BodyName);

                Assert.AreEqual(0, player.GetComponentsInChildren<Collider>(true).Length,
                    "除了 CharacterController，身体不许带 collider");

                var height = body.Avatar.Measure().size.y;
                Assert.AreEqual(PlayerBody.TargetHeight, height, 0.02f,
                    "身体高度该是 " + PlayerBody.TargetHeight.ToString("F2") + "，实测 " + height.ToString("F3"));
            }
            finally
            {
                SceneFlow.UnloadMainIfLoaded();
            }
        }
    }
}
```

- [ ] **Step 2: 跑红**

Run（聚焦跑，`$filter = 'Mas3D.Tests.BakedBodyTests.TheSceneCarriesABakedBodyTheBuildCanAdopt'`）
Expected: FAIL —— `场景里必须烘一份 Me（Baked 字段）`（Main.unity 目前 Me.vrm 出现 0 次）。

- [ ] **Step 3: 实现**

`BuildPlayer()` 末尾加一行（让场景里那个组件承载 Baked，RoomBootstrap 的 GetComponent 就会拿到它而不是再 Add 一个）：

```csharp
go.AddComponent<PlayerBody>();
```

`BuildScene()` 里 `var player = BuildPlayer();` 之后、`BuildCamera()` 之前插入：

```csharp
BakeBody(player);
```

新增：

```csharp
/// <summary>
/// Put the player's VRM into the scene as an inactive child of the Player. A build has no
/// AssetDatabase, so this instance is the only body the packaged player can ever have:
/// PlayerBody.Spawn() adopts it instead of instantiating anything.
/// </summary>
static void BakeBody(GameObject player)
{
    var body = player.GetComponent<PlayerBody>();
    if (body == null) body = player.AddComponent<PlayerBody>();

    var source = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerBody.BodyPrefabPath);
    if (source == null)
    {
        // A clean clone has no Assets/_Local. The scene still has to build: the player is then the
        // bare capsule, exactly like -nobody 1.
        Debug.LogWarning("[player] body missing at " + PlayerBody.BodyPrefabPath
            + " -> 打包版玩家只剩胶囊");
        return;
    }

    var instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
    instance.name = PlayerBody.BodyName;
    instance.transform.SetParent(player.transform, false);
    instance.transform.localPosition = Vector3.zero;
    instance.transform.localRotation = Quaternion.identity;
    instance.transform.localScale = Vector3.one;
    instance.SetActive(false);

    var rig = instance.GetComponent<AvatarRig>();
    if (rig == null) rig = instance.AddComponent<AvatarRig>();
    rig.EnsureBound();

    body.Baked = instance;
    Debug.Log(string.Format("[player] body baked renderers={0} height={1:F3}",
        instance.GetComponentsInChildren<Renderer>(true).Length, rig.Measure().size.y));
}
```

（用 `PrefabUtility.InstantiatePrefab` 而不是 `Object.Instantiate`：前者让材质保持对 Assets/_Local 下贴图的引用，而不是把贴图烘成场景内嵌副本。）

- [ ] **Step 4: 重建场景并跑绿**

Run:
```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
& D:\Mas3D\tools\unity.ps1 player
```
Expected: 日志出现 `[player] body baked renderers=` 与 `[player] scene written: Assets/Scenes/Main.unity`；`grep -c Me Assets/Scenes/Main.unity` 大于 0。

然后聚焦跑（`$filter = 'Mas3D.Tests.BakedBodyTests.TheSceneCarriesABakedBodyTheBuildCanAdopt'`）
Expected: PASS。

- [ ] **Step 5: 提交（先问用户）**

```bash
git add apps/unity/Assets/Editor/CliPlayer.cs apps/unity/Assets/Tests/PlayMode/BakedBodyTests.cs apps/unity/Assets/Scenes/Main.unity
git commit -m "feat(player): bake the player body into Main.unity"
```

---


### Task 3: 打包版自检报身体，player-smoke 判六行

**Files:**
- Modify: `apps/unity/Assets/Scripts/Boot/PlayerSelfTest.cs:99`（token 行之后插身体行）、新增 `public static string BodySummary(PlayerBody body)`
- Modify: `tools/player-smoke.ps1:246-252`（`$wants` 加一行）、`:11`（five → six）
- Test: 新建 `apps/unity/Assets/Tests/PlayMode/PlayerSelfTestBodyTests.cs`

**Interfaces:**
- Consumes: `PlayerBody.Spawned`、`PlayerBody.Avatar`（AvatarRig）、`AvatarRig.Measure()`。
- Produces: `public static string BodySummary(PlayerBody body)` 返回 `body ok renderers=<n> height=<h:F3> shader=<name>` 或 `body failed reason=<no-body|no-shader>`；marker 行前缀由 `Mark()` 统一加 `[selftest] `。

- [ ] **Step 1: 写失败测试**

新建 `PlayerSelfTestBodyTests.cs`：

```csharp
using Mas3D.Boot;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mas3D.Tests
{
    /// <summary>
    /// 打包版唯一的证据是 marker 文件里那几行。身体这一行必须把「有没有身体、多高、什么 shader」
    /// 一次说清：IL2CPP 把 URP 剥掉时材质会变粉，高度会漂，只有 shader 名字能把那种情况抓出来。
    /// </summary>
    public class PlayerSelfTestBodyTests
    {
        GameObject _player;

        [TearDown]
        public void TearDown()
        {
            if (_player) Object.DestroyImmediate(_player);
            _player = null;
        }

        PlayerBody NewBody()
        {
            _player = new GameObject("Player");
            _player.tag = "Player";
            _player.AddComponent<CharacterController>();
            var motor = _player.AddComponent<PlayerMotor>();
            motor.KeyboardInput = false;
            var rig = _player.AddComponent<PlayerCameraRig>();
            var body = _player.AddComponent<PlayerBody>();
            body.Attach(motor, motor.Head, rig);
            return body;
        }

        [Test]
        public void BodySummaryIsOkWithARealBody()
        {
            AvatarPrefabFixture.Require();

            var body = NewBody();
            Assert.IsTrue(body.Spawn(), "Spawn() 失败：" + PlayerBody.BodyPrefabPath);

            var line = PlayerSelfTest.BodySummary(body);

            StringAssert.StartsWith("body ok renderers=", line);
            StringAssert.Contains(" height=1.700 shader=", line);
            StringAssert.Contains(" shader=", line);
        }

        [Test]
        public void BodySummarySaysFailedWhenThereIsNoBody()
        {
            _player = new GameObject("Player");
            _player.tag = "Player";
            var body = _player.AddComponent<PlayerBody>();

            Assert.AreEqual("body failed reason=no-body", PlayerSelfTest.BodySummary(body));
        }

        [Test]
        public void BodySummarySaysFailedWhenTheMaterialLostItsShader()
        {
            AvatarPrefabFixture.Require();

            var body = NewBody();
            Assert.IsTrue(body.Spawn());

            // 只清 renderer 上的数组，不碰 sharedMaterials 指向的资产本身——改了 shader 名字会污染
            // 同一进程里后面所有用例（VRM 的材质是共享资产，不是这份实例的副本）。
            foreach (var renderer in body.Avatar.GetComponentsInChildren<Renderer>(true))
                renderer.sharedMaterials = new Material[0];

            Assert.AreEqual("body failed reason=no-shader", PlayerSelfTest.BodySummary(body));
        }
    }
}
```

- [ ] **Step 2: 跑红**

Run（聚焦跑，`$filter = 'Mas3D.Tests.PlayerSelfTestBodyTests'`）
Expected: 编译失败 `error CS0117: 'PlayerSelfTest' does not contain a definition for 'BodySummary'`。

- [ ] **Step 3: 实现**

`PlayerSelfTest.cs` 在 `Mark("token src=...")` 之后加：

```csharp
Mark(BodySummary(FindAnyObjectByType<PlayerBody>()));
```

并在类里加：

```csharp
/// <summary>
/// One marker line that says whether the packaged player has a body at all, how tall it came out,
/// and which shader drew it. A build that lost its URP shaders still renders — pink — so the shader
/// name is the only field that separates "body ok" from "body is a warning sign".
/// </summary>
public static string BodySummary(PlayerBody body)
{
    if (body == null || !body.Spawned || body.Avatar == null) return "body failed reason=no-body";

    var renderers = body.Avatar.GetComponentsInChildren<Renderer>(true);
    string shader = null;
    foreach (var renderer in renderers)
    {
        if (renderer == null) continue;
        foreach (var material in renderer.sharedMaterials)
        {
            if (material != null && material.shader != null && !string.IsNullOrEmpty(material.shader.name))
            {
                shader = material.shader.name;
                break;
            }
        }
        if (shader != null) break;
    }

    if (shader == null) return "body failed reason=no-shader";

    return string.Format("body ok renderers={0} height={1:F3} shader={2}",
        renderers.Length, body.Avatar.Measure().size.y, shader);
}
```

`tools/player-smoke.ps1` 的 `$wants` 里，在 action hands 与 done 之间插入一行（保持纯 ASCII）：

```powershell
        @{ Label = 'body ok'; Pattern = '\[selftest\] body ok renderers=[1-9][0-9]* height=1\.(6[89]\d|7[0-1]\d|720) shader=\S+' },
```

同时把 `tools/player-smoke.ps1:11` 注释里的 `five marker lines` 改成 `six marker lines`。

- [ ] **Step 4: 跑绿**

Run（聚焦跑，`$filter = 'Mas3D.Tests.PlayerSelfTestBodyTests'`）
Expected: 3 条全 PASS。

- [ ] **Step 5: 提交（先问用户）**

```bash
git add apps/unity/Assets/Scripts/Boot/PlayerSelfTest.cs apps/unity/Assets/Tests/PlayMode/PlayerSelfTestBodyTests.cs tools/player-smoke.ps1
git commit -m "feat(boot): report the body in the player self-test"
```

---


### Task 4: 六门验收 + 变异红

**Files:**
- Modify: `docs/superpowers/specs/2026-11-01-unity-rebuild-design.md`（追加 §5.27：实测数字与判据）

**Interfaces:**
- Consumes: Task 1–3 的全部产物；`tools/check.ps1`、`tools/player-smoke.ps1`、`tools/unity.ps1` 的既有入口。
- Produces: 一份可复现的验收记录（日志行、marker 六行、变异红的证据）。

- [ ] **Step 1: 关编辑器，重建场景与玩家**

Run:
```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
& D:\Mas3D\tools\unity.ps1 player
& D:\Mas3D\tools\unity.ps1 build -Out build/Mas3D.exe
```
Expected: `[player] body baked renderers=` 与 `build arm: D:\Mas3D\build\Mas3D.exe + Mas3D_Data ready`。

- [ ] **Step 2: 打包版自检**

Run:
```powershell
& D:\Mas3D\tools\player-smoke.ps1
```
Expected: `player PASS`，六行 marker 齐全，其中 `body ok renderers=<n> height=1.7xx shader=<...>`。

- [ ] **Step 3: 全量六门**

Run: `& D:\Mas3D\tools\check.ps1`
Expected: web / bridge / unity-edit / unity-play / smoke / player 六门全 PASS（9–12 分钟）。

- [ ] **Step 4: 变异红**

把 `PlayerBody.Spawn()` 里 Baked 分支临时改成 `Baked = null;`，然后依次跑 `unity.ps1 build -Out build/Mas3D.exe` 与 `tools/player-smoke.ps1`。
Expected: **FAIL**，`marker missing lines: body ok`（打包版没有 AssetDatabase，身体回落到胶囊 → `body failed reason=no-body`）。恢复改动，重跑 Step 2 回到 PASS。

- [ ] **Step 5: 记录并提交（先问用户）**

在 `docs/superpowers/specs/2026-11-01-unity-rebuild-design.md` 末尾追加 §5.27 `Body in the build (P11)`：写 Step 1–4 的实测数字（renderers 数、height、shader 名、六门耗时、变异红那一行），以及 `-nobody 1` 与干净 clone 的行为。

```bash
git add docs/superpowers/specs/2026-11-01-unity-rebuild-design.md
git commit -m "docs(specs): record the player body in the build"
```
