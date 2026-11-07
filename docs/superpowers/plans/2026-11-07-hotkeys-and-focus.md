# 交互键与焦点修复 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 Esc 在任何焦点层级下都能退出输入框、并让 1–5 / C / Z / X 变成能用的实机键，够不着时有回声。

**Architecture:** 焦点修复落在 `DialoguePanel`（深扫真实焦点元素 + 面板根 TrickleDown 兜底 + 变化日志）；按键落在新组件 `ActionHotkeys`，它只做「按键 → 接缝」的翻译，与 `InputModes` 并列、共用同一条键盘门；`RoomBootstrap` 负责把两者接起来。

**Tech Stack:** Unity 6000.6.7f1（URP 17.6）、C#、老 Input Manager、UI Toolkit、NUnit + UnityTest。

**Spec:** `docs/superpowers/specs/2026-11-07-hotkeys-and-focus-design.md`（实现前通读；它带 file:line 与三条反目标）

## Global Constraints

- 工程目录 `D:/Mas3D/apps/unity`；编辑器 `D:/Unity_Editor/6000.6.7f1/Editor/Unity.exe`。
- 调 Unity 一律走 `powershell -ExecutionPolicy Bypass -File D:/Mas3D/tools/unity.ps1 <action>`（`test` / `player` / `capture`）。同一工程**同时只能有一个编辑器**：开跑前 `Get-Process Unity -ErrorAction SilentlyContinue`，跑完等它退出。
- 绿不绿看 XML 不看退出码：`apps/unity/TestResults/EditMode.xml`、`PlayMode.xml` 的 `total / passed / failed`。CLI exit 6 通常只是项目被占。
- **不许 commit、不许 push**（AGENTS.md）：做完只 `git add`，提交由 Lead 问过 YG 再说。
- 计数只许增不许减：EditMode **95** 起、PlayMode **225** 起（跳过 `BridgeSmokeTests.UiSceneShot`）。
- 批处理里合成不了真实按键：所有行为都要能从接缝驱动（`PressAction(1)`、`PressCrouch()`…），`Update` 只做翻译。
- 测试里不许「按帧数等」：等状态用 `while (条件 && elapsed < N) { yield return null; elapsed += Time.deltaTime; }`。
- 注释与日志用简体中文，与仓库现有风格一致；不引新包、不改 asmdef。

## Review Focus

1. **组词中途按 Esc**：期望失焦、且不会把半截拼音发出去（`ConversationController.SendSay` 只看 `InputText`）。
2. **走路时按 1–5**：期望她拒绝并说原因，玩家不位移、不传送、不卡住。
3. **附近没有座面时按 X**：期望姿态不变，面板里留一行原因，而不是进入一个悬空坐姿。
4. **点面板内部再走路**：期望走路立刻恢复（点面板内不该抓焦点），只有点输入框才进入打字。
5. **演出中按动作键 / 姿态键**：期望被忽略，动作继续；Esc 仍能取消动作（`ActionDirector` 既有路径）。

---

## Task 1: Esc 真的把焦点拿走

**Files:**
- Modify: `apps/unity/Assets/Scripts/UI/DialoguePanel.cs:199-206`（`BlurInput`）、绑定后的根节点注册
- Test: `apps/unity/Assets/Tests/PlayMode/TypingGateTests.cs`（追加 3 条）

**Interfaces:**
- Produces: `DialoguePanel.BlurInput()` 语义加强（仍无返回值）；新增私有 `DeepBlur()`、`LogFocus(string reason)`；根节点监听 Escape（TrickleDown）；只读访问器 `public TextField InputField => _input;`。
- Consumes: `DialoguePanelTests.DialoguePanelHarness.Create()`（真 UXML + PanelSettings，`TypingGateTests` 已在用）。

- [ ] **Step 1: 写三条会红的用例**（`TypingGateTests.cs` 末尾，沿用现有 `Build()`）

```csharp
// 1) 面板根的 Escape 必须能失焦：不经过 InputModes，证明 UI Toolkit 这条路自己也能兜住。
[UnityTest]
public IEnumerator EscapeOnThePanelRootBlursWithoutTheInputModesComponent()
{
    yield return Build();
    _panel.Panel.FocusInput();
    yield return null;
    Assert.IsTrue(_panel.Panel.IsInputFocused, "前置条件：焦点在输入框里");

    var root = _panel.Panel.Document.rootVisualElement;
    using (var evt = KeyDownEvent.GetPooled(new Event { keyCode = KeyCode.Escape, character = '\0' }))
    {
        evt.target = root;
        root.SendEvent(evt);
    }
    yield return null;
    Assert.IsFalse(_panel.Panel.IsInputFocused, "面板收到 Escape 就必须失焦");
    Assert.IsTrue(_motor.WantsKeyboard, "失焦当帧键盘就该回来");
}

// 2) 焦点落在 TextField 的子树里时，Blur 也必须把它拿走。
[UnityTest]
public IEnumerator BlurInputTakesTheFocusAwayFromAChildOfTheField()
{
    yield return Build();
    var field = _panel.Panel.InputField;
    var child = new VisualElement { focusable = true };
    field.Add(child);
    yield return null;
    child.Focus();
    yield return null;

    if (!_panel.Panel.IsInputFocused)
    {
        child.RemoveFromHierarchy();
        Assert.Ignore("这一版 TextField 不接受子元素焦点，本条不适用");
    }

    _panel.Panel.BlurInput();
    yield return null;
    Assert.IsFalse(_panel.Panel.IsInputFocused, "子元素持焦点时 BlurInput 也必须清干净");
    Assert.IsNull(field.panel.focusController.focusedElement, "失焦后焦点控制器里不该还留着元素");
    child.RemoveFromHierarchy();
}

// 3) 焦点变化要留痕，理由可读（日志是「过一会又进框」的唯一证人）。
[UnityTest]
public IEnumerator FocusChangesAreLoggedWithAReason()
{
    yield return Build();
    LogAssert.Expect(LogType.Log, new Regex(@"\[focus\] .* -> .* reason=focus"));
    _panel.Panel.FocusInput();
    yield return null;
    LogAssert.Expect(LogType.Log, new Regex(@"\[focus\] .* -> .* reason=escape"));
    _panel.Panel.BlurInput();
    yield return null;
}
```

- [ ] **Step 2: 跑一次，确认红**

Run: `powershell -ExecutionPolicy Bypass -File D:/Mas3D/tools/unity.ps1 test -Mode PlayMode`
Expected: 编译错 `InputField` / `LogFocus` 不存在，或三条行为红。XML 存 `apps/unity/Logs/h1-task1-red.xml`。

- [ ] **Step 3: 实现**

```csharp
public void BlurInput()
{
    EnsureBound();
    if (_input == null) return;
    DeepBlur();
    LogFocus("escape");
    var handler = FocusChanged;
    if (handler != null) handler(false);
}

void DeepBlur()
{
    var controller = _input.panel != null ? _input.panel.focusController : null;
    var focused = controller != null ? controller.focusedElement as VisualElement : null;
    if (focused != null && (focused == _input || _input.Contains(focused))) focused.Blur();
    _input.Blur();
    if (controller != null && controller.focusedElement is VisualElement still && _input.Contains(still)) controller.Blur();
}
```

`FocusInput` 末尾补 `LogFocus("focus")`；`InputModes.PressOutsideClick` 走 `BlurInput` 时理由为 `outside-click`（`BlurInput` 加可选参数 `string reason = "escape"`，只影响日志）。`LogFocus` 只在 `focusedElement` 真的变化时打一行：`[focus] <旧类型> -> <新类型> reason=<reason>`，元素名只取类型名，不带玩家文本。

绑定成功后注册一次（`_escapeHooked` 布尔守幂等）：

```csharp
_root.RegisterCallback<KeyDownEvent>(evt =>
{
    if (evt.keyCode != KeyCode.Escape || !IsInputFocused) return;
    BlurInput("escape");
    evt.StopPropagation();
}, TrickleDown.TrickleDown);
```

- [ ] **Step 4: 跑绿**

Run: `powershell -ExecutionPolicy Bypass -File D:/Mas3D/tools/unity.ps1 test -Mode PlayMode`
Expected: `PlayMode.xml` total = 228、failed = 0、skipped = 1；`EditMode.xml` 仍 95/0。

- [ ] **Step 5: 变异红证**：把 `DeepBlur()` 里第一段（`focused.Blur()`）注释掉，确认第 2 条红；还原后重跑绿。XML 存 `apps/unity/Logs/h1-task1-mut-blur.xml`。

- [ ] **Step 6: stage（不 commit）**

```bash
git add apps/unity/Assets/Scripts/UI/DialoguePanel.cs apps/unity/Assets/Scripts/InputModes.cs apps/unity/Assets/Tests/PlayMode/TypingGateTests.cs
```

---

## Task 2: 1–5 动作键与 C / Z / X 姿态键

**Files:**
- Create: `apps/unity/Assets/Scripts/ActionHotkeys.cs`
- Modify: `apps/unity/Assets/Scripts/RoomBootstrap.cs`（Boot 里建 `Input` 那段，`Modes.Bind(...)` 之后）
- Test: `apps/unity/Assets/Tests/PlayMode/ActionHotkeysTests.cs`

**Interfaces:**
- Consumes: `ActionDirector.TryEnterAction(string) / Current / LastFaults`、`PlayerMotor.SetPosture(PlayerPosture, float seatTopY) / Posture`、`DialoguePanel.SetStatus / SetError / LogText / IsInputFocused`、`AnchorTable.seats / Seat(string)`、`SeatEntry.topY`。
- Produces: `public sealed class ActionHotkeys : MonoBehaviour`：字段 `Director / Player / Panel / Anchors`、`KeyCode[] ActionKeys`（默认 `Alpha1..Alpha5`）、`CrouchKey=C / KneelKey=Z / SitKey=X`、`float SeatReach = 1.0f`、`bool KeyboardInput = true`；接缝 `bool PressAction(int slot)`、`bool PressCrouch()`、`bool PressKneel()`、`bool PressSit()`；`void Bind(ActionDirector, PlayerMotor, DialoguePanel, AnchorTable)`。

- [ ] **Step 1: 写 7 条用例（先红）**

```csharp
[UnityTest] public IEnumerator SlotOneAsksForHands()              // PressAction(1) -> Current == ActionId.Hands
[UnityTest] public IEnumerator SlotFourAsksForTheKiss()           // PressAction(4) -> Current == ActionId.Kiss
[UnityTest] public IEnumerator AnUnreachableActionIsRefusedOutLoud()  // 玩家挪到 6 m 外：返回 false、Current 空、LogText 含「够不着」
[UnityTest] public IEnumerator CrouchTogglesBackToStanding()      // PressCrouch() -> Crouching -> Standing
[UnityTest] public IEnumerator SittingNeedsASeatWithinReach()     // 4 m 外：Posture 不变、LogText 含原因
[UnityTest] public IEnumerator HotkeysAreDeafDuringAnAction()     // 进场后 PressCrouch() 无效果、Current 不变
[UnityTest] public IEnumerator TypingKeepsTheNumberKeysForTheField() // 聚焦输入框后 PressAction 走 Update 分支不触发（用 KeyboardInput=true + 面板聚焦断言）
```

脚手架照抄 `ActionHandsTests`：`AvatarPrefabFixture.Require()` + `AvatarActor.Spawn()` + `CharacterController` 的 `PlayerMotor` + `ActionDirector{DriveInUpdate=false}`；面板用 `DialoguePanelTests.DialoguePanelHarness.Create()`；`_hotkeys.KeyboardInput = false` 后只调接缝。

- [ ] **Step 2: 跑一次，确认全红**（编译错 `ActionHotkeys` 不存在也算红），XML 存 `Logs/h2-task2-red.xml`。

- [ ] **Step 3: 实现 `ActionHotkeys`**

```csharp
void Update()
{
    if (!CanTakeKeys) return;                              // KeyboardInput && (Player == null || Player.KeyboardInput)
    if (Panel != null && Panel.IsInputFocused) return;      // 打字时 1–5 是数字
    for (var i = 0; i < ActionKeys.Length; i++)
        if (Input.GetKeyDown(ActionKeys[i])) { PressAction(i + 1); return; }
    if (Input.GetKeyDown(CrouchKey)) { PressCrouch(); return; }
    if (Input.GetKeyDown(KneelKey))  { PressKneel();  return; }
    if (Input.GetKeyDown(SitKey))    { PressSit();    return; }
}

public bool PressAction(int slot)
{
    if (Director == null || slot < 1 || slot > ActionId.All.Length) return false;
    if (Director.Current != null) return false;             // 演出中：静默忽略，动作本身在屏幕上
    if (Director.TryEnterAction(ActionId.All[slot - 1])) return true;
    Say(Director.LastFaults);                               // 具体原因（够不着 / 缺座 / 忙碌）
    return false;
}

public bool PressSit()
{
    if (Player == null) return false;
    if (Player.Posture == PlayerPosture.Sitting) { Player.SetPosture(PlayerPosture.Standing); return true; }
    var seat = NearestSeat();
    if (seat == null) { Say("1 米内没有能坐的地方"); return false; }
    Player.SetPosture(PlayerPosture.Sitting, seat.topY);
    return true;
}
// PressCrouch / PressKneel 同构：当前 == 目标 -> Standing，否则 SetPosture(目标)。
// NearestSeat()：遍历 Anchors.seats，取 XZ 距离最近且 <= SeatReach 的；Anchors 为 null 返回 null。
// Say(text)：Panel.SetStatus("够不着") + Panel.SetError(text)；Panel 为 null 只 Debug.Log。
```

`RoomBootstrap.Boot` 里 `Modes.Bind(...)` 之后：

```csharp
// 与 Modes 同一处创建、同样挂在 Boot 名下（Main 卸载后不许还在 Update 里摸已销毁的对象）。
if (Hotkeys == null)
{
    Hotkeys = modesGo.AddComponent<ActionHotkeys>();
}
Hotkeys.Bind(Director, Player, dialoguePanel, Anchors);   // Anchors 是 RoomBootstrap 既有属性（:64/:79）
```

- [ ] **Step 4: 跑绿**

Run: `powershell -ExecutionPolicy Bypass -File D:/Mas3D/tools/unity.ps1 test -Mode PlayMode`
Expected: `PlayMode.xml` total = 235、failed = 0、skipped = 1。

- [ ] **Step 5: 两条变异红证**：`PressSit` 去掉 `SeatReach` 判断 → 座位用例红；`PressAction` 去掉 `Current != null` 早退 → 演出用例红。XML 存 `Logs/h2-task2-mut-seat.xml`、`Logs/h2-task2-mut-busy.xml`，随后还原。

- [ ] **Step 6: 重建场景并 stage**：`powershell -ExecutionPolicy Bypass -File D:/Mas3D/tools/unity.ps1 player` → 确认日志里有 `[hotkeys]` 绑定行；`Main.unity` 与代码一起 stage。

```bash
git add apps/unity/Assets/Scripts/ActionHotkeys.cs apps/unity/Assets/Scripts/ActionHotkeys.cs.meta apps/unity/Assets/Scripts/RoomBootstrap.cs apps/unity/Assets/Tests/PlayMode/ActionHotkeysTests.cs apps/unity/Assets/Tests/PlayMode/ActionHotkeysTests.cs.meta apps/unity/Assets/Scenes/Main.unity
```

---

## Task 3: 文档与总闸

**Files:**
- Modify: `docs/superpowers/specs/2026-11-01-unity-rebuild-design.md`（§5.26 追加、§7 加 P10）
- Modify: `docs/superpowers/plans/2026-11-07-hotkeys-and-focus.md`（回填实测数字）

- [ ] **Step 1: 写 §5.26**：键位表、Esc 的四条防御与「真因待日志」、`ActionHotkeys` 的接缝、两个新计数、两条已知限制（数字键与将来热键冲突；焦点回流若来自 UI Toolkit 内部的退路是改成模态输入条）。
- [ ] **Step 2: 跑六门**：`powershell -ExecutionPolicy Bypass -File D:/Mas3D/tools/check.ps1 -Python D:/Mas3D/.worktrees/add-monika-space-classroom/.venv/Scripts/python.exe`，要求 `6/6 ok`，日志 `apps/unity/Logs/h-check.log`。
- [ ] **Step 3: 问 YG 是否重编 IL2CPP 包与提交**（不自行执行）。

---

## 实测回填（2026-11-07 收口）

- **计数**：EditMode **97/97/0/0**、PlayMode **240/239/0/1**（跳过 BridgeSmokeTests.UiSceneShot），XML：Logs/h3-status-owner-edit.xml、Logs/h3-status-owner-play2.xml；基线 95 / 225 只增不减，达成。
- **计划偏差（已并入 §5.26）**：① SeatReach 从 1.0 收到 **0.6**（两座相距 1.636 m，站在中点按 X 会坐在离凳 0.8 m 的空气里）；② 多一道「状态行归属」——成功按键只清自己写过的那句，否则会把会话的「思考中」抹掉（用例 ASuccessfulKeyLeavesSomeoneElsesStatusAlone，变异 Logs/h3-mut-status-owner.xml）；③ 多两条 EditMode 键表用例（HotkeyTableTests），把「改两个键位仍然全绿」这条洞堵上；④ HandleKey(KeyCode) 是为了让 Update 与测试走同一条路而加的公开接缝。
- **评审两轮**：Task 1 复审 I1（LogFocus 二次去重吞掉「第二次失焦」）+ I2（外部抢焦点不可观测）必修；Task 2 首轮 4 Important（Cancel 丢座高 / 键表零证据 / seat.topY 零覆盖 / 英文 fault 上屏）+ 11 Minor，全部落地。
- **六门**：Logs/h-check.log，要求 6/6（本轮结果见 §5.26 与提交说明）。

