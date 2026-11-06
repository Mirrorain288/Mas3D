# 操作与观感对齐 · 控制面实现计划（S1 输入门 + S3 相机）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** 让打字时 WASD 不再走进输入框、让输入框的文字顺序与光标永远跟随玩家、让镜头在移动时不再左右抖、并让第一人称的滚轮与俯仰方向符合直觉。

**Architecture:** 三个互不耦合的接缝：① PlayerMotor 的键盘门由 DialoguePanel.IsInputFocused 决定，面板自己提供 FocusInput/BlurInput；② DialoguePanel 成为输入框文字的唯一写入口（AppendToInput → 值 + 光标一起搬），并停止用 SetEnabled(false) 冻结输入框；③ PlayerCameraRig 的 Pivot 迁到一根不随角色旋转的 CameraRigRoot 上、在 LateUpdate 统一 Apply，PlayerMotor 的朝向改用它暴露的 IntentYaw。

**Tech Stack:** Unity 6000.6.7f1、URP 17.6、Cinemachine 6.6、UI Toolkit、NUnit PlayMode、unity CLI（tools/unity.ps1）。

**Spec:** docs/superpowers/specs/2026-11-06-art-and-controls-alignment-design.md（G2/G3 与判据表是验收依据；渲染部分见同目录渲染计划）

## Global Constraints

- 不改 Any 公开签名除非本计划写明；PlayerMotor.Tick(float, Vector2, bool) 与 ReadWish() 保持可用（既有测试在用）。
- 全部注释与文档用中文，代码标识符用英文；不新增第三方包。
- 每个 Task 结束都要能单独编译通过（unity.ps1 selfcheck 或 unity test -Mode EditMode）。
- 六门跑法固定：powershell -ExecutionPolicy Bypass -File tools\check.ps1 -Python D:\Mas3D\.worktrees\add-monika-space-classroom\.venv\Scripts\python.exe
- 不自行 git commit / git push；每个 Task 末尾只把改动 staged，提交由 Lead 汇总后问 YG。
- 输入轴名沿用 Unity 旧 Input Manager（Horizontal / Vertical / Mouse X / Mouse Y / Left Shift），不迁 Input System。

## Review Focus

- 焦点在输入框但玩家按了 Shift+Enter：必须换行，不能发送。
- 玩家在发送等待期继续打字：字符必须落在框里且顺序正确（不许被 SetEnabled 抢走焦点）。
- 玩家用微软拼音选词时按 Enter：只确认候选，不发送；选完再按 Enter 才发送。
- 镜头贴墙、距离压到最小：角色不该出现后脑勺挡住整个屏幕（藏头阈值与距离联动）。
- 半路 Esc：走位与打字都要能干净退出，不留「键盘被永久锁死」的状态。


---

### Task 1: 打字时不再走路（输入门 + 焦点进出）

**Files:**
- Modify: apps/unity/Assets/Scripts/PlayerMotor.cs（:21 附近字段、:109-113 Update）
- Modify: apps/unity/Assets/Scripts/UI/DialoguePanel.cs（:92-104 IsInputFocused 已有、新增 FocusInput/BlurInput、:136-143 Awake/OnEnable）
- Create: apps/unity/Assets/Scripts/InputModes.cs
- Modify: apps/unity/Assets/Scripts/RoomBootstrap.cs（Boot 里挂 InputModes、给 Motor 接 Panel）
- Test: apps/unity/Assets/Tests/PlayMode/TypingGateTests.cs

**Interfaces:**
- Consumes: DialoguePanel.IsInputFocused（已存在）、DialoguePanel.InputText（已存在）
- Produces: PlayerMotor.Panel（public DialoguePanel，可空）、PlayerMotor.WantsKeyboard（public bool，只读：KeyboardInput && (Panel == null || !Panel.IsInputFocused)）、DialoguePanel.FocusInput()、DialoguePanel.BlurInput()、InputModes.KeyboardLocked（public bool，Esc 锁死时 true）

- [ ] **Step 1: 写失败用例**

    // apps/unity/Assets/Tests/PlayMode/TypingGateTests.cs
    [UnityTest] public IEnumerator AFocusedInputStopsTheKeyboardFromWalking()
    {  // 建 panel（用既有 DialoguePanelTests 的搭法）+ 一个 PlayerMotor；panel.FocusInput();
       // yield return null; Assert.IsFalse(motor.WantsKeyboard); Assert.IsTrue(panel.IsInputFocused); }
    [UnityTest] public IEnumerator LeavingTheInputRestoresTheKeyboard()
    {  // panel.FocusInput(); panel.BlurInput(); yield return null;
       // Assert.IsTrue(motor.WantsKeyboard); Assert.IsFalse(panel.IsInputFocused); }
    [UnityTest] public IEnumerator EscapeLocksTheKeyboardUntilTheInputIsFocusedAgain()
    {  // inputModes.Lock(); yield return null; Assert.IsFalse(motor.WantsKeyboard);
       // panel.FocusInput(); Assert.IsTrue(motor.WantsKeyboard); }

- [ ] **Step 2: 跑一遍确认是红的**

    powershell -ExecutionPolicy Bypass -File tools\unity.ps1 test -Mode PlayMode -Out apps\unity\Logs\s1-task1-red.xml
    期望：3 个用例编译失败或失败（WantsKeyboard / FocusInput / KeyboardLocked 尚未存在）。

- [ ] **Step 3: 实现**

    // PlayerMotor：在 :21 后加 public DialoguePanel Panel; 与
    // public bool WantsKeyboard => KeyboardInput && !(InputModes.KeyboardLocked) && (Panel == null || !Panel.IsInputFocused);
    // Update() 改成 if (!WantsKeyboard) return;（Tick 逻辑不动）
    // DialoguePanel：FocusInput() { EnsureBound(); if (_input == null) return; _input.Focus(); MoveCursorToEnd(); }
    //               BlurInput()  { EnsureBound(); if (_input != null) _input.Blur(); }
    // InputModes（MonoBehaviour，挂到 Boot 的 Session 物体上）：
    //   Update() 里 E → Panel.FocusInput(); Esc → Panel.BlurInput() 且 KeyboardLocked = true;
    //   鼠标左键按下且指针不在面板矩形内（panel.worldBound.Contains）→ BlurInput() 并解锁。
    //   public bool KeyboardLocked { get; private set; }（FocusInput 时复位 false）。

- [ ] **Step 4: 跑绿**

    powershell -ExecutionPolicy Bypass -File tools\unity.ps1 test -Mode PlayMode -Out apps\unity\Logs\s1-task1-green.xml
    期望：TypingGateTests 3/3 通过，且 PlayMode 总失败数不增加（基线 0 failed / 1 skipped）。

- [ ] **Step 5: 存盘**

    git add -A apps/unity/Assets && git status --short

**验收：** WantsKeyboard 在聚焦时为 false、失焦后为 true；Esc 后键盘锁死直到重新聚焦；六门里 unity-play 仍绿。

---

### Task 2: 输入框文字顺序与光标（唯一写入口 + IME + 发送期不冻结）

**Files:**
- Modify: apps/unity/Assets/Scripts/UI/DialoguePanel.cs（:67-86 InputText、:387-408 发送、:435-439 ApplyInteractable）
- Modify: apps/unity/Assets/Scripts/Voice/VoiceInput.cs（:283-287 整串重写那段）
- Modify: apps/unity/Assets/Scripts/Conversation/ConversationController.cs（:99 与 :161/:172 的 Interactable 语义）
- Test: apps/unity/Assets/Tests/PlayMode/InputOrderTests.cs

**Interfaces:**
- Consumes: DialoguePanel.InputText、DialoguePanel.Submitted、DialoguePanel.Interactable
- Produces: DialoguePanel.AppendToInput(string piece)（追加到末尾并把光标放到末尾）、DialoguePanel.CursorIndex（public int，读 textEdition）、DialoguePanel.Interactable 现在只影响发送按钮与发送动作，不再 SetEnabled 输入框

- [ ] **Step 1: 写失败用例**

    [UnityTest] public IEnumerator TwentyTypedCharactersStayInOrderAndLeaveTheCursorAtTheEnd()
    {  // 逐字 panel.AppendToInput("你")…共 20 字；
       // Assert.AreEqual(20, panel.InputText.Length); Assert.AreEqual(20, panel.CursorIndex); }
    [UnityTest] public IEnumerator TypingDuringSendKeepsTheFieldUsable()
    {  // panel.Interactable = false; panel.AppendToInput("草稿"); yield return null;
       // Assert.IsTrue(panel.InputFieldEnabled); Assert.AreEqual("草稿", panel.InputText); }
    [UnityTest] public IEnumerator EnterWhileComposingDoesNotSend()
    {  // panel.ComposeForTest("站起来"); 发 Enter → Submitted 计数 0；panel.ComposeForTest(""); 再 Enter → 计数 1。 }

- [ ] **Step 2: 跑红**（同上一条命令，输出 s1-task2-red.xml；期望 3 个用例红）

- [ ] **Step 3: 实现**

    // DialoguePanel：
    //   void AppendToInput(string piece)：InputText = InputText + piece; 然后 _input.cursorIndex = InputText.Length;（光标置末尾）
    //     —— 6000.6.7f1 没有公开的 TextField.textEdition；但 UnityEngine.UIElementsModule.dll 里确实有
    //     SelectRange / MoveTextEnd / textSelection / ITextSelection.cursorIndex。优先用 _input.SelectRange(len, len)（最稳），
    //     编不过再退 _input.textSelection.cursorIndex = len，最后退 ((ITextSelection)_input).cursorIndex = len；三者取第一个能编过的并写注释。
    //   public int CursorIndex => _input != null ? _input.cursorIndex : 0;
    //   public bool InputFieldEnabled => _input != null && _input.enabledSelf;
    //   OnInputKeyDown：开头加 if (_composing) return;  —— _composing 只能靠 IMECompositionEvent 维护（KeyDownEvent 没有 isComposing）：
    //     _input.RegisterCallback<IMECompositionEvent>(e => _composing = !string.IsNullOrEmpty(e.compositionString));
    //     组合态期间 Enter 只确认候选、不发送；空串回调后才恢复发送。
    //   ApplyInteractable：只 SetEnabled(_send)；输入框永远可编辑（发送期由 _interactable 拦住 SendFromInput）。
    // VoiceInput:283-287 改为 target.AppendToInput(text) 一处调用，不再 SetValueWithoutNotify 整串。
    // ConversationController:99 保留 Interactable=false（按钮禁用 + 防重复提交），删除任何 InputField 层面的禁用副作用。

- [ ] **Step 4: 跑绿并确认旧用例不回归**

    tools\unity.ps1 test -Mode PlayMode -Out apps\unity\Logs\s1-task2-green.xml
    期望：InputOrderTests 3/3、DialoguePanelTests / ConversationTests / VoiceInputTests 全绿。

- [ ] **Step 5: 存盘**（git add -A apps/unity/Assets）

**验收：** 20 字顺序 + 光标在末尾；发送期仍能打字；组合输入中的 Enter 不发送。

---

### Task 3: 相机骨架（把 Pivot 从那颗会转的头上摘下来）

**Files:**
- Modify: apps/unity/Assets/Scripts/PlayerCameraRig.cs（:39-50 Build、:85-92 Update、:122-126 Apply）
- Modify: apps/unity/Assets/Scripts/PlayerMotor.cs（:17 TurnRate、:116-124 ReadWish）
- Test: apps/unity/Assets/Tests/PlayMode/CameraStabilityTests.cs

**Interfaces:**
- Consumes: PlayerMotor.Head（:28）、PlayerCameraRig.Build(Transform head, Camera output)
- Produces: PlayerCameraRig.IntentYaw（public float：本帧镜头朝向）、PlayerCameraRig.CameraRigRoot（public Transform：不旋转的中介节点）、PlayerCameraRig.SetPitch(float)、PlayerMotor.TurnRate 默认 270

- [ ] **Step 1: 写失败用例**

    [UnityTest] public IEnumerator WalkingInACircleKeepsThePivotOnTheAngleWeAskedFor()
    {  // 建 motor + rig；每帧 motor.Tick(1/60, 单位圆切线方向)；记录
       // d = Mathf.DeltaAngle(rig.Pivot.eulerAngles.y, rig.Yaw)，两秒内 Mathf.Abs(d) 恒 < 0.5；
       // 一阶差分符号翻转次数 == 0。 }
    [UnityTest] public IEnumerator TheWishDirectionFollowsTheIntentYawNotTheRenderedCamera()
    {  // rig.SetOrbit(90, 0); motor.ReadWish() 用注入的 raw（1,0）→ 断言方向 ≈ rig.IntentYaw 对应轴；
       // 把 Camera.main 的 yaw 故意设成 0，结果不变。 }

- [ ] **Step 2: 跑红**（s1-task3-red.xml；期望 2 红）

- [ ] **Step 3: 实现**

    // PlayerCameraRig.Build：
    //   建 CameraRigRoot（无旋转，SetParent(transform.parent ?? transform)），Pivot 挂 CameraRigRoot（不再挂 head），
    //   VCam 仍挂 Pivot；每帧在 Update 里 CameraRigRoot.position = head.position，随后 Apply() 写 Pivot.rotation。
    //   **Apply 必须留在 Update，不要加 [DefaultExecutionOrder] 挪到 LateUpdate**：CinemachineBrain 在 LateUpdate 取景、
    //   没有可配的 order，晚于它写 Pivot 会让相机读上一帧朝向，270°/s 下每帧差 4.5°，比现状更抖。
    //   抖动真正的根因是 Pivot 被挂在会随 TurnRate 旋转的 head 下，拆开即可，顺序保持 Update → Brain。
    //   public float IntentYaw => Yaw;
    // PlayerMotor.ReadWish：yaw 改用 GetComponent<PlayerCameraRig>()?.IntentYaw ?? transform.eulerAngles.y；
    //   TurnRate 默认 720 → 270。

- [ ] **Step 4: 跑绿**（s1-task3-green.xml；另跑 PlayerWalkTests 确认没把移动测坏；特别确认 Update→Brain 的顺序没有被 execution order 改反）

- [ ] **Step 5: 存盘**

**验收：** 直线行走与绕圈时 Pivot 残差 <0.5 度、无符号翻转；W 的移动方向不再随渲染相机抖动。

---

### Task 4: 真第一人称（最近距离贴到头、俯仰同向）

**Files:**
- Modify: apps/unity/Assets/Scripts/PlayerCameraRig.cs（:94-120 SetOrbit/AddOrbit/SetDistance）
- Modify: apps/unity/Assets/Scripts/PlayerBody.cs（:35 NearDistance 与藏头判断处）
- Test: apps/unity/Assets/Tests/PlayMode/CameraStabilityTests.cs（追加用例）

**Interfaces:**
- Consumes: PlayerCameraRig.Distance / Pitch / SetDistance / AddOrbit、PlayerBody.NearDistance
- Produces: PlayerCameraRig.FirstPersonDistance = 0.6f（最小距离）、PlayerCameraRig.IsFirstPerson（public bool：Distance ≤ 1.2f）、AddOrbit 改成 Pitch + deltaPitch

- [ ] **Step 1: 写失败用例**

    [UnityTest] public IEnumerator MovingTheMouseUpToRaisesTheCamera()
    {  // rig.SetOrbit(0, 0); rig.AddOrbit(0, 10); yield return null;
       // Assert.Greater(rig.Pitch, 0); Assert.That(Mathf.Abs(rig.Pitch - 22f), Is.LessThan(0.5f));
       // （Sensitivity 2.2 → 10 单位 = 22 度，与设计判据 2.2 度/单位一致） }
    [UnityTest] public IEnumerator TheClosestDistancePutsTheCameraAtTheHead()
    {  // rig.SetDistance(0.6); yield return null;
       // Assert.IsTrue(rig.IsFirstPerson); Assert.Less(Vector3.Distance(rig.CameraPosition, motor.Head.position), 0.08f); }

- [ ] **Step 2: 跑红**（s1-task4-red.xml；期望 2 红）

- [ ] **Step 3: 实现**

    // AddOrbit：SetOrbit(Yaw + dYaw, Pitch + dPitch)（去掉减号）。
    // SetDistance：Clamp 下限 0.9 → FirstPersonDistance 0.6。
    // IsFirstPerson => Distance <= 1.2f；FirstPerson 时 _follow.CameraDistance = 0.05f、VerticalArmLength = 0、
    //   CameraSide = 0（贴头），并在 0.15 s 内插值（简单 Mathf.MoveTowards 到目标值即可，不需要 DOTween）。
    //   PlayerBody.NearDistance 由 1.2 改为 1.25 保证 FP 时头部一定藏起来。

- [ ] **Step 4: 跑绿**（s1-task4-green.xml；同时确认 PlayerBodyTests 仍绿）

- [ ] **Step 5: 存盘**

**验收：** 鼠标上移 → 仰角变大；最近距离相机在头附近 8 cm 内；角色进 FP 时不再看到后脑勺。

---

### Task 5: 六门与手工验收

- [ ] **Step 1:** 跑六门，落 apps/unity/Logs/s1-check.log，要求 summary: 6/6 ok 且无 SKIP
- [ ] **Step 2:** 用 tools\unity.ps1 capture 出四张房内图（front/left45/right45/wide），确认房间亮度未因相机改动而异常
- [ ] **Step 3:** 手工三条（写进 apps/unity/Logs/s1-manual.md）：① 点输入框打 20 字不乱序；② 打字时按 W 不位移、不吞字；③ 转圈走 5 秒画面不左右晃
- [ ] **Step 4:** git add -A apps/unity/Assets && git status --short（staged 待 Lead 统一提交）

**交付物：** 三个新测试文件、五个被改的运行时脚本、一份手工记录；提交建议拆分：输入门 / 输入顺序 / 相机骨架 / 第一人称。

---

