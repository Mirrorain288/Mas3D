using System.Collections;
using Mas3D;
using Mas3D.Actions;
using Mas3D.Avatar;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Mas3D.Tests
{
    /// <summary>
    /// Spec hotkeys-and-focus 的按键层：数字键 1–5 找动作、C/Z/X 摆姿态，
    /// 人正在打字时整块让给输入框，演出中按键静默。
    ///
    /// 每条用例只按一下：接缝是同步的，帧只用来让 UIDocument 把面板挂上。
    /// 接缝也过 Ready()（总闸 / 打字 / 演出），所以 SetUp 打开总闸 —— 批处理里没有真实按键，
    /// 打开它也不会被硬件打断。
    /// </summary>
    public class ActionHotkeysTests
    {
        AvatarActor _actor;
        PlayerMotor _player;
        ActionDirector _director;
        DialoguePanelTests.DialoguePanelHarness _panel;
        ActionHotkeys _hotkeys;
        GameObject _playerGo;
        GameObject _actionsGo;
        GameObject _keysGo;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            AvatarPrefabFixture.Require();
            _actor = AvatarActor.Spawn();

            _playerGo = new GameObject("Player");
            _playerGo.AddComponent<CharacterController>();
            _player = _playerGo.AddComponent<PlayerMotor>();
            _player.KeyboardInput = true;
            _player.EnsureReady();

            _actionsGo = new GameObject("Actions");
            _director = _actionsGo.AddComponent<ActionDirector>();
            _director.Actor = _actor;
            _director.Player = _player;
            _director.KeyboardInput = false;
            _director.DriveInUpdate = false;

            _panel = DialoguePanelTests.DialoguePanelHarness.Create();
            yield return null;   // 让 UIDocument 把树挂上

            _keysGo = new GameObject("Hotkeys");
            _hotkeys = _keysGo.AddComponent<ActionHotkeys>();
            _hotkeys.Bind(_director, _player, _panel.Panel, AnchorTable.Load());
            _hotkeys.KeyboardInput = true;   // 接缝也走 Ready()：批处理没有真实按键，开着安全
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_director != null && _director.Current != null) _director.Cancel();
            if (_panel != null) _panel.Dispose();
            if (_keysGo != null) Object.Destroy(_keysGo);
            if (_actionsGo != null) Object.Destroy(_actionsGo);
            if (_actor != null) Object.Destroy(_actor.gameObject);
            if (_playerGo != null) Object.Destroy(_playerGo);
            yield return null;
        }

        [UnityTest]
        public IEnumerator SlotOneAsksForHands()
        {
            Assert.IsTrue(_hotkeys.PressAction(1), "站着按 1：牵手必须能进场");
            Assert.AreEqual(ActionId.Hands, _director.Current, "槽位 1 就是牵手");
            yield return null;
        }

        [UnityTest]
        public IEnumerator SlotFourAsksForTheKiss()
        {
            _player.SetPosture(PlayerPosture.Kneeling);   // 亲需要跪姿，门槛由 ActionDirector 定
            Assert.IsTrue(_hotkeys.PressAction(4), _director.LastFaults);
            Assert.AreEqual(ActionId.Kiss, _director.Current, "槽位 4 就是亲");
            yield return null;
        }

        [UnityTest]
        public IEnumerator AnUnreachableActionIsRefusedOutLoud()
        {
            // 计划里的掌心落在 S1 前面，跟玩家站在哪里无关；把她整个人挪开 3 m，她的肩膀离那只
            // 手掌就有 3 m —— 手臂只有 0.47 m，够不着。拒绝必须写在面板上，不能只是悄悄返回 false。
            _actor.transform.position += new Vector3(0f, 0f, 3f);
            var position = _player.transform.position;
            var posture = _player.Posture;

            Assert.IsFalse(_hotkeys.PressAction(1), "够不着的时候不许进场");
            Assert.IsNull(_director.Current, "被拒绝之后不许留下半个动作");
            StringAssert.Contains("够不着", _panel.Panel.LogText, "拒绝原因必须出现在对话记录里");
            StringAssert.DoesNotContain("out of reach", _panel.Panel.LogText, "玩家不该读到导演的内部英文标识");
            Assert.AreEqual(position, _player.transform.position, "被拒绝之后玩家不许被挪动");
            Assert.AreEqual(posture, _player.Posture, "被拒绝之后姿态不许变");
            yield return null;
        }

        [UnityTest]
        public IEnumerator CrouchTogglesBackToStanding()
        {
            Assert.IsTrue(_hotkeys.PressCrouch(), "站着按 C 必须蹲下");
            Assert.AreEqual(PlayerPosture.Crouching, _player.Posture);
            Assert.IsTrue(_hotkeys.PressCrouch(), "同一个键再按一次必须站起来");
            Assert.AreEqual(PlayerPosture.Standing, _player.Posture);
            yield return null;
        }

        [UnityTest]
        public IEnumerator SittingNeedsASeatWithinReach()
        {
            var seat = AnchorTable.Load().Seat("S1");

            _player.Teleport(new Vector3(5f, 0f, 0f), 0f);
            Assert.IsFalse(_hotkeys.PressSit(), "4 米外没有座面，按 X 不许凭空坐下");
            Assert.AreEqual(PlayerPosture.Standing, _player.Posture, "找不到座面时姿态不许变");
            StringAssert.Contains("没有能坐的地方", _panel.Panel.LogText, "拒绝原因必须写在面板上");
            Assert.AreEqual("坐不下", StatusText(), "状态行要写明为什么坐不下");

            _player.Teleport(Vector3.zero, 0f);
            Assert.IsTrue(_hotkeys.PressSit(), "站到 S1 跟前按 X 必须坐下");
            Assert.AreEqual(PlayerPosture.Sitting, _player.Posture);
            // 两边都是 topY + 0.5f，实际是精确相等；容差收到 0.002 是为了让「seat.topY 传 0」
            // 这种变异也能在 S1（0.604 → 1.104，与固定 1.10 只差 0.004 m）上红。
            Assert.That(_player.EyeHeight, Is.EqualTo(seat.topY + 0.5f).Within(0.002f),
                "坐下的眼高必须跟着座面走：座面 topY + 0.5 m（容差 2 mm）");
            Assert.AreEqual(string.Empty, StatusText(), "做成之后状态行必须复位，不许一直挂着上一句拒绝");
            yield return null;
        }

        [UnityTest]
        public IEnumerator ASuccessfulKeyLeavesSomeoneElsesStatusAlone()
        {
            // 状态行是会话的（思考中 / 发送中），热键只在上面写拒绝、也只清自己写的那句。
            _panel.Panel.SetStatus("思考中");
            Assert.IsTrue(_hotkeys.PressCrouch(), "站着按 C 必须蹲下");
            Assert.AreEqual("思考中", StatusText(), "成功按键不许把会话写的状态行抹掉");
            yield return null;
        }

        [UnityTest]
        public IEnumerator HotkeysAreDeafDuringAnAction()
        {
            Assert.IsTrue(_hotkeys.PressAction(1));
            Assert.AreEqual(ActionId.Hands, _director.Current);
            var log = _panel.Panel.LogText;

            Assert.IsFalse(_hotkeys.PressAction(2), "演出中按别的槽位必须被静默吃掉");
            Assert.IsFalse(_hotkeys.PressCrouch(), "演出中 C 也不许改姿态");
            Assert.AreEqual(ActionId.Hands, _director.Current, "演出不许被热键打断");
            Assert.AreEqual(PlayerPosture.Standing, _player.Posture, "演出中姿态不变");
            Assert.AreEqual(log, _panel.Panel.LogText, "静默就是静默：演出中不许往面板里写原因");

            // 槽位 5 是双人坐：演出中同样不许进场；PressSit 的那道 Busy 也要挡住。
            Assert.IsFalse(_hotkeys.PressSit(), "演出中 X 不许把人塞进座面");
            yield return null;
        }

        [UnityTest]
        public IEnumerator TypingKeepsTheNumberKeysForTheField()
        {
            _panel.Panel.FocusInput();
            yield return null;
            Assert.IsTrue(_panel.Panel.IsInputFocused, "前置：焦点确实在输入框里");

            Assert.IsFalse(_hotkeys.HandleKey(KeyCode.Alpha1), "打字时按 1 是数字，不是动作");
            Assert.IsNull(_director.Current, "打字时不许进场");

            _panel.Panel.BlurInput("outside-click");
            yield return null;
            Assert.IsFalse(_panel.Panel.IsInputFocused, "前置：焦点已经离开输入框");
            Assert.IsTrue(_hotkeys.HandleKey(KeyCode.Alpha1), "失焦以后同一个键必须能进动作");
            Assert.AreEqual(ActionId.Hands, _director.Current);
        }

        /// <summary>
        /// 键表到槽位的映射：HandleKey 收到的 KeyCode 必须落到它自己的那个 id 上，2/3 不许对调。
        /// </summary>
        [UnityTest]
        public IEnumerator HandleKeySendsEachKeyToItsOwnSlot()
        {
            Assert.IsTrue(_hotkeys.HandleKey(KeyCode.Alpha2), _director.LastFaults);
            Assert.AreEqual(ActionId.PatHer, _director.Current, "Alpha2 必须落在第二个槽位上");
            _director.Cancel();

            _player.SetPosture(PlayerPosture.Crouching);   // 摸头需要蹲姿
            Assert.IsTrue(_hotkeys.HandleKey(KeyCode.Alpha3), _director.LastFaults);
            Assert.AreEqual(ActionId.PatMe, _director.Current, "Alpha3 必须落在第三个槽位上");
            _director.Cancel();

            _player.SetPosture(PlayerPosture.Standing);
            Assert.IsTrue(_hotkeys.HandleKey(KeyCode.Alpha5), _director.LastFaults);
            Assert.AreEqual(ActionId.SitTogether, _director.Current, "Alpha5 必须落在第五个槽位上（并排坐）");
            _director.Cancel();

            Assert.IsTrue(_hotkeys.HandleKey(_hotkeys.KneelKey), "Z 必须能跪下");
            Assert.AreEqual(PlayerPosture.Kneeling, _player.Posture, "KneelKey 必须真的改成跪姿");
            yield return null;
        }

        /// <summary>
        /// 退出演出要把「坐在床沿」还原成坐在床沿：座面高度必须跟着姿态一起回来，否则眼高掉回
        /// 1.10 m，头沉进床垫（床面 0.906 + 0.5 = 1.406）。
        /// </summary>
        /// <summary>状态行（DialoguePanel 只写不读，这里从 UXML 的 status 标签上读回来）。</summary>
        string StatusText()
        {
            var document = _panel.Panel.GetComponent<UIDocument>();
            var root = document != null ? document.rootVisualElement : null;
            var label = root != null ? root.Q<Label>("status") : null;
            return label != null ? label.text : null;
        }

        [UnityTest]
        public IEnumerator CancelKeepsTheSeatHeight()
        {
            var bed = AnchorTable.Load().Seat("S2");
            _player.Teleport(new Vector3(bed.x, 0f, bed.z), 0f);
            Assert.IsTrue(_hotkeys.PressSit(), "站在床沿按 X 必须坐下");
            Assert.That(_player.EyeHeight, Is.EqualTo(bed.topY + 0.5f).Within(0.01f), "前置：坐着时眼高是床面 + 0.5 m");

            Assert.IsTrue(_hotkeys.PressAction(2), _director.LastFaults);
            Assert.AreEqual(ActionId.PatHer, _director.Current);
            Assert.AreEqual(PlayerPosture.Standing, _player.Posture, "前置：演出里她回到了站姿");

            _director.Cancel();
            Assert.AreEqual(PlayerPosture.Sitting, _player.Posture, "退出演出必须回到坐下之前的姿态");
            Assert.That(_player.EyeHeight, Is.EqualTo(bed.topY + 0.5f).Within(0.01f),
                "退出演出不许把床沿坐成空中坐：眼高必须还是床面 + 0.5 m");
            yield return null;
        }
    }
}
