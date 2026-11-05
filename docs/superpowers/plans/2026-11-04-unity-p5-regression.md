# P5 回归与归档 — 实施计划

> **For agentic workers:** 按 Task 顺序执行；每个 Task 先写会红的用例/检查，再实现，再跑绿。步骤用 `- [ ]` 勾选。

## Goal

把「一次完整回归」变成两条命令：

1. `tools\unity.ps1 smoke` —— 从「桥接一回合」扩到「桥接一回合 + 五件动作在真实场景里各演一次并复位」，任一环节红即 exit 1。
2. `tools\check.ps1` —— 一条命令跑完 web（pnpm）、桥接（pytest）、Unity EditMode、Unity PlayMode、smoke，汇总成一张表，任一失败 exit 1。

全绿之后把 **web 版降级归档**（文档标注 + 入口改指向 Unity），归档单独一步，不混进功能提交。

## Architecture

- **动作冒烟不复用逐动作用例的夹具**：加载真实场景 `Main`（additive + finally 卸载），用场景里 `RoomBootstrap.Boot()` 接好的真导演，按 hands → pat-her → pat-me → kiss → sit-together 各进场一次，手动 `Tick(1/60) × 90`，结束后 `Cancel` 并核对玩家回原位、键盘恢复、她回 S1。这条用例测的是**接线与复位**，不是几何精度（精度由逐动作用例负责）。
- **判据不靠 CLI 退出码**：`Debug.Log` 不进 NUnit XML，CLI 也把测试进程日志收在别处；成功标记落 `apps/unity/Logs/smoke-actions-marker.txt`，脚本必须读到每一行 `[smoke] action <id> ... ok` 才算过。
- `tools/unity.ps1 smoke` 跑**两次串行调用**（两次 `--filter`），各自一条 XML、一个 marker；不用单次多过滤器（Unity CLI 的 filter 语义没在文档里钉死）。
- `tools/check.ps1` 只做编排：**不重写**任何测试逻辑，只调用既有入口并把 exit code 汇总。
- web 归档只动文档与措辞：`apps/web/src`、测试、锁文件全部保留，`pnpm -r test` 继续跑（归档不等于删码）。

## Tech Stack

Unity 6.6.7f1 + URP + Unity Test Framework（PlayMode）；Windows PowerShell 5.1；pnpm workspace；pytest（AstrBot 自带 python）。

## Spec

`docs/superpowers/specs/2026-11-01-unity-rebuild-design.md`：§7 的 P5 定义、§6 的判据表、§5.20 的动作层合同（五个 id、复位顺序、`Cancel` 语义）。

## Global Constraints

- **不自行 `git commit` / `git push`**：实现者只 `git add -A apps/unity tools docs`（Task 5 另加 `README.md`），提交由 Lead 统一做。
- `tools/*.ps1` 必须**纯 ASCII**：PS 5.1 读无 BOM 的 UTF-8 中文会报「switch 语句的子句中缺少语句块」。
- **Unity 串行**：同一时刻只能有一个编辑器或批处理；开跑前确认没有残留 `Unity.exe`（`Get-CimInstance Win32_Process` 里 `CommandLine -like "*Mas3D*"` 为空）。
- 一切 Unity 输出必须 `*> <log>` 落盘再 `Select-String`；直接管道会把 stderr 的进度行变成 NativeCommandError，制造假 exit 1。
- 编译失败时 `TestResults/*.xml` **不重写**，只认 `apps/unity/Logs/Editor.log` 里的 `error CS`。
- 出图类步骤不加 `-nographics`；测试与冒烟可以加。
- **基线**（本计划开始前）：PlayMode 163 total / 162 passed / 0 failed / 1 skipped；EditMode 44/44；web 16 files / 130 tests；bridge 169 passed。Task 1 完成后 PlayMode 应为 **164 / 163 / 0 / 1**。
- 常数与文案只从 spec 与既有代码抄，不许新造；不许改既有断言来迁就实现；不许改 `anchors.json`（生成物）。
- 测试里不许调 `boot.Boot()`（`Start` 已调过，重跑会重挂碰撞体）。
- 场景 `Main.unity` 不许重新生成（`tools/unity.ps1 player` 会把 15k 行 fileID 全部重写）；本计划不需要重建场景。

## Review Focus

1. **用例必须能红**：把 `director.Cancel()` 注释掉、把 `TryEnterAction` 换成恒 `true`、把 `Tick` 换成空实现，三种变异各自必须让新用例失败（实现者要贴出红证原文）。
2. **marker 是唯一判据**：`tools/unity.ps1 smoke` 不许只看 CLI 退出码；marker 缺行、XML 有 Failed 都必须 exit 1。
3. **失败传播**：`tools/check.ps1` 任一子步骤失败必须 exit 1，并打印是哪一步、原始 exit code。
4. **归档只是文档**：`git status` 里不该出现 `apps/web/src` 的改动。
5. **不写死机器路径**：脚本里不许出现 `D:\Mas3D`、`Rain zhou`、`Astrbot` 之类绝对路径；python 走 `$env:MAS3D_PYTHON` 覆盖 + 探测，仓库根由 `$PSScriptRoot` 推。

---

## Task 1 — 场景内一镜到底的动作冒烟

**Files**
- Create `apps/unity/Assets/Tests/PlayMode/SmokeMarker.cs`（+ `.meta`）：`internal static class SmokeMarker { internal static void Write(string relative, string line) }`
- Create `apps/unity/Assets/Tests/PlayMode/ActionSmokeTests.cs`（+ `.meta`）
- Modify `apps/unity/Assets/Tests/PlayMode/BridgeSmokeTests.cs`：`WriteMarker(string line)` 改成一行委托 `SmokeMarker.Write(MarkerRelative, line)`，路径与格式一字不改
- Modify `apps/unity/Assets/Tests/PlayMode/SceneFlow.cs`：新增 `internal static IEnumerator WaitForBoot(RoomBootstrap boot)`
- Modify `apps/unity/Assets/Tests/PlayMode/ActionSceneTests.cs`：三处私有 `WaitForBoot` 调用改成 `SceneFlow.WaitForBoot(boot)`，删掉私有版本

**Interfaces**
- 消费：`SceneFlow.Require()/LoadMain()/UnloadMainIfLoaded()`、`RoomBootstrap.Actions/Player/Actor`、`ActionDirector.TryEnterAction(string)/Current/Held/ContactError/Cancel()/LastFaults/DriveInUpdate/ContactTolerance`、`PlayerMotor.SetPosture(PlayerPosture)/Posture/KeyboardInput/transform`、`ActionId.All`。
- 产出：`apps/unity/Logs/smoke-actions-marker.txt`，每个 id 一行 `[smoke] action <id> held=<f2> gap=<f3> ok`；Task 2 的脚本读它。

**Steps**

- [ ] 1. 先把 marker 写法抽成一个共用小类（两条冒烟用例要写同一种文件，复制一份会被评审当缺陷）：新建 `SmokeMarker.cs`：

```csharp
using System.IO;
using UnityEngine;

namespace Mas3D.Tests
{
    /// <summary>
    /// The one channel that survives to the PowerShell side: Debug.Log does not reach the NUnit XML,
    /// and the CLI keeps the batchmode console elsewhere, so a smoke run proves itself by the lines it
    /// writes here. Paths are relative to the project root (Application.dataPath's parent).
    /// </summary>
    internal static class SmokeMarker
    {
        internal static void Write(string relative, string line)
        {
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", relative));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.AppendAllText(path, line + System.Environment.NewLine);
            Debug.Log(line);
        }
    }
}
```

  然后让 `BridgeSmokeTests.cs:225-231` 的 `WriteMarker(string line)` 变成一行委托（`SmokeMarker.Write(MarkerRelative, line);`），删掉它自己的 `MarkerPath()`；marker 路径与行格式一字不改，桥接那条用例必须仍然绿。

- [ ] 2. 先写会红的新用例：新建 `ActionSmokeTests.cs`，内容如下（常数照抄，不要改）：

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Mas3D;
using Mas3D.Actions;

namespace Mas3D.Tests
{
    /// <summary>
    /// 一次回归里最贵的那一段：真实场景 + 真实导演，把五件动作各演一遍再各复位一次。
    /// 它不重复逐动作用例的几何判据（接触误差另有专门的用例），它证明的是「接线没断、
    /// 复位没漏」——这正是发布前最容易坏、又最不容易被单点用例发现的两处。
    ///
    /// 标记落 Logs/smoke-actions-marker.txt：Debug.Log 不进 NUnit XML，Unity CLI 也把测试进程
    /// 日志收在别处，文件是唯一活到脚本那一侧的通道。
    /// </summary>
    public class ActionSmokeTests
    {
        const string MarkerRelative = "Logs/smoke-actions-marker.txt";
        const float Step = 1f / 60f;
        const int StepsPerAction = 90;    // 1.5 s：三个动作的判据都要 >= 1 s，取 1.5 s 留余量
        const float MinHeld = 1.4f;

        [UnityTest]
        public IEnumerator EveryActionPlaysOnceThroughTheScene()
        {
            SceneFlow.Require();
            try
            {
                yield return SceneFlow.LoadMain();
                var boot = Object.FindAnyObjectByType<RoomBootstrap>();
                yield return SceneFlow.WaitForBoot(boot);

                var director = boot.Actions;
                var player = boot.Player;
                var spawn = player.transform.position;
                director.DriveInUpdate = false;   // 手动 Tick：Update 也推一次会让 1.5 s 变成 3 s

                var steps = new (string Id, PlayerPosture Posture)[]
                {
                    (ActionId.Hands, PlayerPosture.Standing),
                    (ActionId.PatHer, PlayerPosture.Standing),
                    (ActionId.PatMe, PlayerPosture.Crouching),
                    (ActionId.Kiss, PlayerPosture.Kneeling),
                    (ActionId.SitTogether, PlayerPosture.Standing),
                };

                // 加第六个动作时这张表必须跟着长：否则冒烟永远「五个都 ok」而新动作从未跑过。
                var ids = new string[steps.Length];
                for (var i = 0; i < steps.Length; i++) ids[i] = steps[i].Id;
                CollectionAssert.AreEquivalent(ActionId.All, ids);

                var hand = boot.Actor.Rig.Bone(HumanBodyBones.RightHand);
                var s1 = AnchorTable.Load().Seat("S1");

                foreach (var step in steps)
                {
                    // 站姿是前置条件：pat-me 要蹲、kiss 要跪，导演会拒绝而不是硬塞。
                    player.SetPosture(step.Posture);
                    yield return null;

                    // 驱动的证据要「进场前 vs 演完」比：取消后再比就被复位抹平了。
                    var handBefore = hand.position;
                    var isKiss = step.Id == ActionId.Kiss;
                    var chest = isKiss ? boot.Actor.Rig.Bone(HumanBodyBones.Chest) : null;
                    var chestBefore = isKiss ? chest.localRotation : Quaternion.identity;

                    Assert.IsTrue(director.TryEnterAction(step.Id),
                        step.Id + " 在真实场景里进不了场：" + director.LastFaults);
                    Assert.AreEqual(step.Id, director.Current, "进场后 Current 必须是 " + step.Id);
                    Assert.Greater(Vector3.Distance(spawn, player.transform.position), 0.05f,
                        step.Id + " 进场后玩家必须离开出生点（证明 plan 真的被摆过）");

                    for (var i = 0; i < StepsPerAction; i++) director.Tick(Step);

                    Assert.GreaterOrEqual(director.Held, MinHeld,
                        step.Id + " 只保持了 " + director.Held.ToString("F2") + " s，接触没稳住");
                    Assert.LessOrEqual(director.ContactError, ActionDirector.ContactTolerance,
                        step.Id + " 的接触误差超了 " + ActionDirector.ContactTolerance + " m");

                    // Held 只证明「时间过去了」，它不证明驱动层跑过：删掉任何一个 DriveX 分支，
                    // Held 照常涨到 1.5 s、ContactError 保持 0、marker 照写 ok。所以这里必须看见
                    // 骨骼真的被搬动过。
                    if (isKiss)
                        Assert.Greater(Quaternion.Angle(chestBefore, chest.localRotation), 10f,
                            "kiss 期间她的躯干必须被摆过（驱动层没跑的话这里不会动）");
                    else if (step.Id != ActionId.SitTogether)
                        Assert.Greater(Vector3.Distance(handBefore, hand.position), 0.03f,
                            step.Id + " 期间她的右手必须被搬动过（驱动层没跑的话这里不会动）");

                    SmokeMarker.Write(MarkerRelative, "[smoke] action " + step.Id + " held=" + director.Held.ToString("F2") +
                        " gap=" + director.ContactError.ToString("F3") + " ok");

                    director.Cancel();
                    yield return null;

                    Assert.IsNull(director.Current, "Cancel 之后必须回到 idle");
                    Assert.IsTrue(player.KeyboardInput, "Cancel 之后键盘必须还给玩家");
                    Assert.Less(Vector3.Distance(spawn, player.transform.position), 0.05f,
                        "Cancel 之后玩家必须回到进场前的位置 5 cm 以内");
                    Assert.AreEqual(step.Posture, player.Posture, "Cancel 要把进场前那个姿势还回来");
                }

                // Seated 在 S1 与床垫上同为 true，断言它等于什么都没说：必须看髋部落在哪。
                var hips = boot.Actor.Seat.HipsWorld;
                Assert.Less(new Vector2(hips.x - s1.x, hips.z - s1.z).magnitude, 0.02f,
                    "五个动作跑完，她必须回到 S1（坐在床垫上也算 Seated）");
            }
            finally { SceneFlow.UnloadMainIfLoaded(); }
        }

    }
}
```

- [ ] 3. 把 `WaitForBoot` 提进 `SceneFlow`（体不变，见 `ActionSceneTests.cs:16-21`，需要 `internal static IEnumerator`），并改三处调用（`:38/:54/:73`），同时把断言文案里的「等了 2 s」改成「等了 120 帧」（界是 120 次 `yield return null`，不是倒计时）；跑 `test -Mode PlayMode`，确认新用例在**没有任何实现改动**的情况下就能绿——它测的是既有的接线。
- [ ] 4. 变异红证（三条，逐条贴原文，跑完必须还原且 `grep MUTATION` 为 0）：
  - 注释掉 `ActionSmokeTests` 里那一行 `director.Cancel();` → 第二条 id 起会 `busy` 拒绝进场（`Assert.IsTrue` 红）。
  - 把 `SceneFlow.WaitForBoot` 里的上限从 120 改成 0 → 断言红（证明这条等待不是摆设）。
  - 把 `ActionSmokeTests` 里的 `director.Tick(Step)` 注释掉 → `Held` 恒 0，`MinHeld` 断言红。
  - 把 `ActionDirector.cs` 里 `case ActionId.Hands: DriveHands();` 改成 `break;`（只改这一处，跑完还原）→ 她右手不动，新增的位移断言红。这条证明 marker 的 `ok` 不是「什么都没做也算过」。
- [ ] 5. 跑绿并记录：`test -Mode PlayMode` 应为 `164 total / 163 passed / 0 failed / 1 skipped`；EditMode 仍 `44/44`。日志写 `apps/unity/Logs/task1-*.log`。
- [ ] 6. `git add -A apps/unity`，报告里贴出 marker 文件的前五行与 XML 计数。

---

## Task 2 — `tools/unity.ps1 smoke` 覆盖两条链路

**Files**
- Modify `tools/unity.ps1`（`smoke` 分支）

**Interfaces**
- 消费：`TestResults/Smoke.xml`（桥接）、`TestResults/SmokeActions.xml`（动作）、`Logs/smoke-marker.txt`、`Logs/smoke-actions-marker.txt`。
- 产出：`smoke` 动作的退出码（0 全绿 / 1 任一失败），并打印两侧的 `[smoke]` 行。

**Steps**

- [ ] 1. 先看红：把 `smoke` 分支里动作那条用例的 filter 写成不存在的名字（例如 `Mas3D.Tests.ActionSmokeTests.NoSuchCase`），跑 `smoke` 必须 exit 1（说明失败会被发现）；还原。
- [ ] 2. 改造 `smoke` 分支（保持纯 ASCII，结构与既有 `capture` 分支同形）：
  - 两个 XML：`TestResults/Smoke.xml`、`TestResults/SmokeActions.xml`；两个 marker：`Logs/smoke-marker.txt`、`Logs/smoke-actions-marker.txt`；开跑前都删掉。
  - 两次串行 `Invoke-Unity`：先 `--filter Mas3D.Tests.PlayMode.BridgeSmokeTests.OneTurnAgainstTheRealMockBridge`，再 `--filter Mas3D.Tests.ActionSmokeTests.EveryActionPlaysOnceThroughTheScene`；两次都用 `try/catch` 记 `$bad`，不要因第一次失败就跳过第二次（两条链路互不依赖）。
  - 每次跑完检查 XML 存在且不含 `result="Failed"|failed="[1-9]`。
  - marker 检查：桥接那条至少一行 `[smoke] turn ok`，动作那条必须**五个 id 各一行** `[smoke] action <id>`；缺任何一条 `$bad = $true` 并打印缺的是哪个 id。
  - 末尾 `if ($bad) { exit 1 }`。
- [ ] 3. 跑 `smoke`：两侧全绿、五个 id 都打印、exit 0；再故意把 `ActionSmokeTests` 里的 `MinHeld` 改成 `99f` 跑一次 `smoke`，必须 exit 1 且打印动作那条 XML 的失败（还原后复绿）。
- [ ] 4. 更新 `tools/unity.ps1` 头部注释的 `.EXAMPLE`，说明 `smoke` 现在覆盖两条链路。
- [ ] 5. `git add -A tools`。

---

## Task 3 — `tools/check.ps1`：一条命令全回归

**Files**
- Create `tools/check.ps1`（纯 ASCII）
- Modify `tools/README.md`（新增一节，见 Task 4 的边界：Task 3 只加 `check.ps1` 的用法，Task 4 才写 P5 记录）

**Interfaces**
- 参数：`-SkipWeb`、`-SkipBridge`、`-SkipUnity`、`-SkipSmoke`、`-Python <path>`、`-Timeout <秒>`。
- 退出码：0 全绿；1 任一步失败。
- 打印：每一步一行 `<step> <PASS|FAIL|SKIP> exit=<n> <秒>`，末尾一行 `summary: <passed>/<ran> ok`。

**Steps**

- [ ] 1. 先写红：新建 `tools/check.ps1`，只 `exit 1`，跑一次确认退出码非 0（脚本存在且可执行）。
- [ ] 2. 实现五步（顺序即依赖顺序，后一步不依赖前一步的结果，但全跑能省时间）：
  - **web**：`& pnpm.cmd -r test`（**不能**调 `pnpm.ps1`：执行策略会拦，之前踩过）；工作目录 `$repoRoot`。
  - **bridge**：python 解析顺序 `-Python` → `$env:MAS3D_PYTHON` → `python`（PATH）；都找不到时打印 `bridge SKIP (no python; set MAS3D_PYTHON)` 并**不计失败**。设 `$env:PYTHONPATH = Join-Path $env:TEMP "mas3d_pytest_lib"`、`$env:PYTHONIOENCODING="utf-8"`，跑 `-m pytest apps/bridge/tests -p no:cacheprovider --tb=no -q`。
  - **unity-edit**：`powershell -ExecutionPolicy Bypass -File tools/unity.ps1 test -Mode EditMode`，输出重定向到 `apps/unity/Logs/check-edit.log`；判定用子进程的 `ExitCode`（`Start-Process powershell -PassThru -Wait`），不要去解析日志文本。
  - **unity-play**：同上，`-Mode PlayMode`，日志 `check-play.log`。
  - **smoke**：`powershell -ExecutionPolicy Bypass -File tools/unity.ps1 smoke`，日志 `check-smoke.log`。
- [ ] 3. 失败传播实测：故意让某一步红（例如临时把 `apps/unity/Assets/Tests/PlayMode/ActionSmokeTests.cs` 的 `MinHeld` 改成 `99f`），`check.ps1` 必须 exit 1 且那行是 `FAIL`；还原后 `check.ps1` exit 0 且五行全 `PASS`。
- [ ] 4. `-SkipSmoke -SkipBridge` 也要能跑通（验证参数不互相打架）。
- [ ] 5. `git add -A tools`。

---

## Task 4 — 文档：P5 的验收记录

**Files**
- Modify `docs/superpowers/specs/2026-11-01-unity-rebuild-design.md`：新增 `### 5.21 P5 回归（2026-11-04 实测）`
- Modify `tools/README.md`：加 `check.ps1` 与 `smoke` 两条链路的用法段

**Steps**

- [ ] 1. spec §5.21 写清：两条命令的职责边界、`smoke` 两阶段（桥接 / 动作）与各自的 marker 文件、动作冒烟测的是接线与复位而不是几何精度、判据不信任 CLI 退出码的原因、五步回归的实际数字（PlayMode 164 / EditMode 44 / web 130 / bridge 169），以及 `check.ps1` 的参数与跳过语义。
- [ ] 2. `tools/README.md` 补用法：`powershell -ExecutionPolicy Bypass -File tools/unity.ps1 smoke`、`... tools/check.ps1`，并写明「`pnpm.ps1` 会被执行策略拦，脚本内部一律用 `pnpm.cmd`」。
- [ ] 3. 文档里的数字必须来自实跑（贴 `check.ps1` 的 summary 行），不许估。
- [ ] 4. `git add -A docs tools`。

---

## Task 5 — web 版降级归档（单独一步）

**Files**
- Create `apps/web/ARCHIVE.md`
- Modify `README.md`（顶部「目录」与首段：主线指向 Unity，web 标为归档）
- Modify `docs/superpowers/specs/2026-11-01-unity-rebuild-design.md`：新增 `## 11. web 版的归档（2026-11-04）`

**Steps**

- [ ] 1. `apps/web/ARCHIVE.md` 写四段：归档日期、归档理由（Unity 版是唯一在维护的主线，web 版保留可运行）、仍然怎么跑（`pnpm --filter @mas3d/web dev` + mock bridge）、不再做什么（不加功能、不修观感、不跟进新协议字段；安全与阻断性 bug 例外）。
- [ ] 2. `README.md`：首段改成「Unity 版是主线」，`apps/web` 一行标注「已归档（见 `apps/web/ARCHIVE.md`）」，`apps/unity` 加一行说明与 `tools/unity.ps1` 入口；快速开始补 Unity 两条命令。**不要**删除任何 web 的说明段（许可、换模型那些仍然有效）。
- [ ] 3. spec 新增 §11：归档点（日期、随之生效的边界）、为什么保留测试、以及「要复活 web 版需要做什么」。
- [ ] 4. 验证归档没有动代码：`git status --porcelain apps/web` 只能出现 `ARCHIVE.md`；`& pnpm.cmd -r test` 仍 130 passed。
- [ ] 5. `git add -A apps/web README.md docs`，在报告里给出 `git diff --cached --stat`。

---

## 交付口径（Lead 收口时用）

- `tools/unity.ps1 smoke` exit 0，打印桥接 `[smoke] turn ok` 与五条 `[smoke] action <id> ... ok`。
- `tools/check.ps1` exit 0，五行 `PASS`：web 130 / bridge 169 / EditMode 44 / PlayMode 164·163 / smoke。
- `git status` 干净；`apps/web/src` 零改动。
- spec §5.21 与 §11 的数字与实跑一致。
- 提交分两次：`chore(tools): one command for the whole regression`（Task 1–4）与 `docs: archive the web build`（Task 5）。

## 不在本计划内

- 让 Unity 播放 TTS 语音、锚点间行走、AnimationClip、玩家身体网格（各自另开计划）。
- `tools/unity.ps1 player` 的 fileID 抖动根治。
- 发布打包（P6）与 CI 接入。
- 改任何既有断言或既有动作判据。
