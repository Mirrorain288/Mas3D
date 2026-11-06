using System.Collections;
using Mas3D.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mas3D.Tests
{
    /// <summary>
    /// 输入门的开关（controls Task 1）：焦点在输入框里，WASD 不许再驱动走路；焦点离开，键盘回来；
    /// Esc 能把键盘锁死，但焦点一旦回到输入框就必须解锁 —— 不许留下一个按不动的键盘。
    ///
    /// 键盘不合成：InputModes.Update 只负责把按键翻译成 FocusInput/BlurInput/Lock 这几个接缝，
    /// 用例直接调接缝，和 VoiceInputTests 直接调 Press/Release 是同一套口径（批处理里没有真实按键可发）。
    /// 面板用 DialoguePanelTests 的真 UXML + PanelSettings 搭：IsInputFocused 读的是 UI Toolkit 的
    /// focusController，手搓一个假面板证明不了“焦点”这件事。
    /// </summary>
    public class TypingGateTests
    {
        DialoguePanelTests.DialoguePanelHarness _panel;
        GameObject _playerObject;
        GameObject _modesObject;
        PlayerMotor _motor;
        InputModes _modes;

        [TearDown]
        public void TearDown()
        {
            if (_modesObject != null) UnityEngine.Object.Destroy(_modesObject);
            if (_playerObject != null) UnityEngine.Object.Destroy(_playerObject);
            if (_panel != null) _panel.Dispose();
            _modesObject = null;
            _playerObject = null;
            _panel = null;
            _motor = null;
            _modes = null;
        }

        /// <summary>
        /// 真面板 + 真 PlayerMotor（CharacterController 是硬依赖）+ 一个只走接缝的 InputModes。
        /// motor.enabled 关掉：Update 不跑，于是既不会去读真实的 WASD 轴，也不会在两次断言之间偷偷移动。
        /// 这条用例钉的是门本身（WantsKeyboard 决定这一帧的行走意图），位移正确性归 PlayerWalkTests 的 Tick 接缝。
        /// </summary>
        IEnumerator Build()
        {
            _panel = DialoguePanelTests.DialoguePanelHarness.Create();
            yield return null;   // 让 UIDocument 把 UXML 挂上并跑一帧布局

            _modesObject = new GameObject("TypingGateTests/InputModes");
            _modes = _modesObject.AddComponent<InputModes>();
            _modes.KeyboardInput = false;   // 真按键分支只在 Update 里走，这里用 Lock/Unlock 接缝

            _playerObject = new GameObject("TypingGateTests/Player");
            _playerObject.AddComponent<CharacterController>();
            _motor = _playerObject.AddComponent<PlayerMotor>();
            _motor.enabled = false;
            _motor.KeyboardInput = true;

            _modes.Bind(_motor, _panel.Panel);   // 与 RoomBootstrap.Boot 里同一套接线
            Assert.IsTrue(_motor.WantsKeyboard, "前置条件：没焦点、没锁，键盘应该可用");
        }

        [UnityTest]
        public IEnumerator AFocusedInputStopsTheKeyboardFromWalking()
        {
            yield return Build();

            _panel.Panel.FocusInput();
            yield return null;

            Assert.IsTrue(_panel.Panel.IsInputFocused, "焦点必须真的落在输入框上，否则这条用例什么也没证明");
            Assert.IsFalse(_motor.WantsKeyboard, "焦点在输入框里时，键盘不许驱动走路");
        }

        [UnityTest]
        public IEnumerator LeavingTheInputRestoresTheKeyboard()
        {
            yield return Build();

            _panel.Panel.FocusInput();
            yield return null;
            Assert.IsFalse(_motor.WantsKeyboard, "前置条件：聚焦时键盘是停的");

            _panel.Panel.BlurInput();
            yield return null;

            Assert.IsFalse(_panel.Panel.IsInputFocused, "失焦之后焦点不该还留在输入框");
            Assert.IsTrue(_motor.WantsKeyboard, "失焦之后键盘必须回来");
        }

        /// <summary>
        /// Esc 退出打字之后，走路键盘必须立刻回来 —— 不许留下“界面上看不出、按了却没反应”的隐形锁。
        /// 旧实现把 Esc 当成锁（等焦点回到输入框才解），实机表现就是 m17495 第 4 条
        /// 「方向键控制一会儿之后又失去控制」。
        /// </summary>
        [UnityTest]
        public IEnumerator EscapeAfterTypingHandsTheKeyboardBackAtOnce()
        {
            yield return Build();

            _panel.Panel.FocusInput();
            yield return null;
            Assert.IsFalse(_motor.WantsKeyboard, "前置：在打字，走路键盘本来是停的");

            _modes.PressEscape();
            yield return null;

            Assert.IsFalse(_panel.Panel.IsInputFocused, "打字中按 Esc：焦点必须离开输入框");
            Assert.IsTrue(_motor.WantsKeyboard, "退出打字当帧走路键盘就得回来，不许再有隐形锁");

            // 只断言谓词不够：真的走一步才算回来。
            var before = _playerObject.transform.position;
            for (var i = 0; i < 10; i++) _motor.Tick(1f / 60f, Vector2.up);
            Assert.Greater((_playerObject.transform.position - before).magnitude, 1e-3f,
                "键盘回来之后 Tick 必须真的推动她，而不是只在谓词上返回 true");
        }

        /// <summary>
        /// 门关的是“意图”，不是 Tick 本身：聚焦时 Update 仍要跑重力与贴地，只把 wish 换成 0。
        /// 旧实现直接 return，空中按 E 会连重力一起停住 = 悬停。
        /// 批处理里合成不了 WASD 轴，所以先用 Tick 接缝把速度喂起来，再让 Update 自己把它收回去。
        /// </summary>
        [UnityTest]
        public IEnumerator FocusedGateZeroesIntentWithoutStoppingPhysics()
        {
            yield return Build();

            // 先给一段平面速度；此刻 motor 还是关的，Update 不会抢先把这段速度衰减掉。
            for (var i = 0; i < 20; i++) _motor.Tick(1f / 60f, Vector2.right);
            Assert.Greater(_motor.PlanarVelocity.magnitude, 1.5f,
                "前置：先有一段速度，否则“聚焦后收敛到 0”什么也没证明");

            _panel.Panel.FocusInput();
            yield return null;
            Assert.IsFalse(_motor.WantsKeyboard, "前置：聚焦后门必须是关的");

            _motor.enabled = true;   // 从这里起，位移只由 Update 产生
            var beforeY = _playerObject.transform.position.y;
            // 按**模拟时间**等，不按帧数：批处理里一帧的 Time.deltaTime 可能小到几十微秒，
            // 数 300 帧只推进几毫秒，速度根本来不及收敛（这条用例曾经因此假红）。
            var elapsed = 0f;
            var frames = 0;
            while (_motor.PlanarVelocity.magnitude > 0.05f && elapsed < 2f && frames++ < 20000)
            {
                yield return null;
                elapsed += Time.deltaTime;
            }

            Assert.Less(_motor.PlanarVelocity.magnitude, 0.05f,
                "聚焦时行走意图必须是 0：门只清意图，Tick 还得继续跑");
            Assert.Less(_playerObject.transform.position.y, beforeY - 1e-4f,
                "聚焦时重力仍在跑：旧实现把 Tick 一起停掉，胶囊会悬停");

            _panel.Panel.BlurInput();
            yield return null;
            Assert.IsTrue(_motor.WantsKeyboard, "失焦后门必须重新打开，否则“门写死成关”也能绿");
            var beforeBlurY = _playerObject.transform.position.y;
            var blurElapsed = 0f;
            while (blurElapsed < 0.1f)
            {
                yield return null;
                blurElapsed += Time.deltaTime;
            }
            Assert.Less(_playerObject.transform.position.y, beforeBlurY - 1e-5f,
                "失焦后 Tick 照跑（同样在掉），说明门只在“意图”这一层");
        }

        /// <summary>
        /// E 只负责“进”。打字时再按 E 不能把焦点抢走 —— 拼音里 e 满地都是，
        /// 抢一次就打断正在组的词（m17495 第 1 条的字序错乱有这一份）。
        /// </summary>
        [UnityTest]
        public IEnumerator PressingEAgainKeepsTheFocusWhereItIs()
        {
            yield return Build();

            _modes.PressFocus();
            yield return null;
            Assert.IsTrue(_panel.Panel.IsInputFocused, "第一次按 E：焦点必须进输入框");
            Assert.IsFalse(_motor.WantsKeyboard, "进输入框时走路键盘是停的");

            _modes.PressFocus();
            yield return null;
            Assert.IsTrue(_panel.Panel.IsInputFocused, "已经聚焦时再按 E：焦点必须留着，不许打断组词");
            Assert.IsFalse(_motor.WantsKeyboard, "焦点仍在框里：走路键盘仍然该停");
        }

        /// <summary>
        /// 换 motor 时旧 motor 的反向引用必须清空，否则它会一直指着这个还活着的组件。
        /// </summary>
        [Test]
        public void RebindingAMotorClearsTheOldMotorsBackReferences()
        {
            var first = new GameObject("TypingGateTests/OldPlayer", typeof(CharacterController), typeof(PlayerMotor));
            var second = new GameObject("TypingGateTests/NewPlayer", typeof(CharacterController), typeof(PlayerMotor));
            var modesObject = new GameObject("TypingGateTests/BindTwice");
            try
            {
                var modes = modesObject.AddComponent<InputModes>();
                var oldMotor = first.GetComponent<PlayerMotor>();
                var newMotor = second.GetComponent<PlayerMotor>();

                modes.Bind(oldMotor, null);
                modes.Bind(newMotor, null);

                Assert.IsNull(oldMotor.Modes, "换 motor 后旧 motor 的 Modes 必须清空");
                Assert.IsNull(oldMotor.Panel, "换 motor 后旧 motor 的 Panel 也必须清空");
                Assert.AreSame(modes, newMotor.Modes, "新 motor 必须接上");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(modesObject);
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
            }
        }

        /// <summary>Esc 只有一种语义：退出输入框。退出之后键盘立刻可用（不再有第二态）。</summary>
        [UnityTest]
        public IEnumerator EscapeWhileIdleChangesNothing()
        {
            yield return Build();

            Assert.IsFalse(_panel.Panel.IsInputFocused, "前置：本来就没在打字");

            _modes.PressEscape();
            yield return null;

            Assert.IsFalse(_panel.Panel.IsInputFocused, "空闲按 Esc：焦点状态不变（没有东西可退）");
            Assert.IsTrue(_motor.WantsKeyboard, "空闲按 Esc 不许把键盘弄丢 —— 隐形锁正是这么来的");
        }

        /// <summary>点面板外面 = 失焦；这条只管“外面”那一侧。</summary>
        [UnityTest]
        public IEnumerator ClickingOutsideThePanelBlursAndUnlocks()
        {
            yield return Build();

            _panel.Panel.FocusInput();
            yield return null;
            Assert.IsTrue(_panel.Panel.IsInputFocused, "前置：焦点得先在框里");
            var outside = new Vector2(-10000f, -10000f);
            Assert.IsFalse(_panel.Panel.ContainsScreenPoint(outside), "前置：这个点必须在面板外面");

            _modes.PressOutsideClick(outside);
            yield return null;

            Assert.IsFalse(_panel.Panel.IsInputFocused, "点面板外面：焦点必须离开");
            Assert.IsTrue(_motor.WantsKeyboard, "点面板外面：走路键盘必须立刻恢复");
        }

        /// <summary>
        /// 点面板里面 = 什么都不做：焦点留着（PressOutsideClick 的另一半）。
        /// UIElements 只给了 Screen→Panel 的反向换算，没有现成的正向，所以扫一遍找落在面板里的点。
        /// </summary>
        [UnityTest]
        public IEnumerator ClickingInsideThePanelLeavesTheFocusAlone()
        {
            yield return Build();

            _panel.Panel.FocusInput();
            yield return null;
            Assert.IsTrue(_panel.Panel.IsInputFocused, "前置：焦点得先在框里");
            Assert.IsFalse(_panel.Panel.ContainsScreenPoint(new Vector2(-10000f, -10000f)),
                "前置：面板矩形得是可判的，否则下面的扫描会退化成空转");

            var inside = FindPointInsidePanel();
            _modes.PressOutsideClick(inside);
            yield return null;

            Assert.IsTrue(_panel.Panel.IsInputFocused, "点面板里面：焦点必须留着");
            Assert.IsFalse(_motor.WantsKeyboard, "点在面板里不该顺手把走路键盘解出来");
        }

        /// <summary>扫屏找一个落在面板矩形里的屏幕点；一个都找不到就直接判失败，别静默退化成“外面”那条分支。</summary>
        Vector2 FindPointInsidePanel()
        {
            var width = Screen.width > 0 ? Screen.width : 1920;
            var height = Screen.height > 0 ? Screen.height : 1080;
            for (var y = 0; y < height; y += 16)
            {
                for (var x = 0; x < width; x += 16)
                {
                    var point = new Vector2(x, y);
                    if (_panel.Panel.ContainsScreenPoint(point)) return point;
                }
            }

            Assert.Fail("扫遍全屏也没找到落在面板里的点：ContainsScreenPoint 可能一直在返回 false");
            return Vector2.zero;
        }

        /// <summary>
        /// 演出期间 Motor.KeyboardInput 被关掉，E/Esc 不该抢焦点。
        /// 本条只钉判据层：真正挡住按键的是 InputModes.Update 里 CanTakeKeys 的早退，
        /// 而 batch 合成不了 Input.GetKeyDown，那一步只能在编辑器里手工验。
        /// </summary>
        [Test]
        public void PerformingDisablesTheKeys()
        {
            var player = new GameObject("TypingGateTests/Performing", typeof(CharacterController), typeof(PlayerMotor));
            var modesObject = new GameObject("TypingGateTests/PerformingModes");
            try
            {
                var motor = player.GetComponent<PlayerMotor>();
                var modes = modesObject.AddComponent<InputModes>();
                modes.Bind(motor, null);

                modes.KeyboardInput = true;
                motor.KeyboardInput = false;
                Assert.IsFalse(modes.CanTakeKeys, "演出中（Motor.KeyboardInput=false）E/Esc 必须被挡住");

                motor.KeyboardInput = true;
                Assert.IsTrue(modes.CanTakeKeys, "演出结束后按键立刻回来");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(modesObject);
                UnityEngine.Object.DestroyImmediate(player);
            }
        }
    }
}
