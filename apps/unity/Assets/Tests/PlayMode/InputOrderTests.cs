using System.Collections;
using System.Collections.Generic;
using Mas3D.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Mas3D.Tests
{
    /// <summary>
    /// 输入框的文字顺序与光标（controls Task 2）：所有程序化写值都走面板的唯一写入口，
    /// 值写进去光标就落到末尾；发送期输入框不再被 SetEnabled(false) 冻住；IME 组合中的 Enter
    /// 只确认候选、不发送；Shift+Enter 换行不发送。
    ///
    /// 用例钉的都是面板这一层的接缝（AppendToInput / InputText / ApplyComposition / CursorIndex），
    /// 真实 IME 与真实按键在批处理里合成不出来，那部分只能在编辑器里手工验（报告 §6）。
    /// </summary>
    public class InputOrderTests
    {
        DialoguePanelTests.DialoguePanelHarness _panel;
        readonly List<string> _submitted = new List<string>();
        TextField _input;

        [TearDown]
        public void TearDown()
        {
            if (_panel != null) _panel.Dispose();
            _panel = null;
            _input = null;
            _submitted.Clear();
        }

        IEnumerator Build()
        {
            _panel = DialoguePanelTests.DialoguePanelHarness.Create();
            yield return null;   // 让 UIDocument 把 UXML 挂上并跑一帧布局
            _submitted.Clear();
            _panel.Panel.Submitted += _submitted.Add;
            _input = _panel.Document.rootVisualElement.Q<TextField>("input");
            Assert.IsNotNull(_input, "Dialogue.uxml 里必须有 input");
        }

        /// <summary>
        /// 逐字追加 20 个不同的字：值必须一字不差、顺序不变，写完光标在末尾。
        /// 用 20 个不同的字（不是同一个字重复）才能钉住“顺序”，否则乱序也看不出来。
        /// </summary>
        [UnityTest]
        public IEnumerator TwentyTypedCharactersStayInOrderAndLeaveTheCursorAtTheEnd()
        {
            yield return Build();

            const string typed = "甲乙丙丁戊己庚辛壬癸子丑寅卯辰巳午未申酉";
            for (var i = 0; i < typed.Length; i++) _panel.Panel.AppendToInput(typed[i].ToString());

            Assert.AreEqual(typed.Length, _panel.Panel.InputText.Length, "20 次追加之后长度必须是 20");
            Assert.AreEqual(typed, _panel.Panel.InputText, "字符顺序必须与输入顺序完全一致");
            Assert.AreEqual(typed.Length, _panel.Panel.CursorIndex, "写完光标必须落在末尾");
            Assert.IsTrue(_panel.Panel.InputFieldEnabled, "输入框任何时候都不该被冻住");
        }

        /// <summary>
        /// 发送等待期（Interactable = false）玩家继续打字：字必须落进框里，焦点与可编辑性都不许被抢走；
        /// 同时这一期按 Enter 不能重复提交。
        /// 这里直接置 Interactable，不走 ConversationController 的 Submit 往返 —— 面板层行为由这条钉住，
        /// “真提交一次再投递 reply 帧”的整链证据在 ConversationTests（那条覆盖传输层，不覆盖这里）。
        /// </summary>
        [UnityTest]
        public IEnumerator TypingDuringSendKeepsTheFieldUsable()
        {
            yield return Build();

            _panel.Panel.Interactable = false;   // 控制器在 Submit 里就是这么做的
            _panel.Panel.AppendToInput("草");
            _panel.Panel.AppendToInput("稿");
            yield return null;

            Assert.IsTrue(_panel.Panel.InputFieldEnabled, "发送期输入框仍必须可编辑（不再 SetEnabled(false)）");
            Assert.AreEqual("草稿", _panel.Panel.InputText, "发送期打的字不许掉");
            Assert.AreEqual(2, _panel.Panel.CursorIndex, "发送期打完字光标仍在末尾");

            DialoguePanelTests.DialoguePanelHarness.PressEnter(_input);
            Assert.AreEqual(0, _submitted.Count, "发送期按 Enter 不许重复提交");
            Assert.AreEqual("草稿", _panel.Panel.InputText, "被挡住的那次 Enter 不许清掉草稿");
        }

        /// <summary>
        /// IME 组合期间按 Enter 只确认候选：不发送、草稿留在框里；组合结束（空串）之后再按 Enter 才发送。
        /// </summary>
        [UnityTest]
        public IEnumerator EnterWhileComposingDoesNotSend()
        {
            yield return Build();

            _panel.Panel.InputText = "站起来";
            _panel.Panel.ApplyComposition("站起来");

            DialoguePanelTests.DialoguePanelHarness.PressEnter(_input);
            Assert.AreEqual(0, _submitted.Count, "组合中按 Enter 只确认候选，不许发送");
            Assert.AreEqual("站起来", _panel.Panel.InputText, "组合中的那次 Enter 不许清掉草稿");

            _panel.Panel.ApplyComposition(string.Empty);
            DialoguePanelTests.DialoguePanelHarness.PressEnter(_input);
            Assert.AreEqual(1, _submitted.Count, "组合结束后回车要能发出去");
            Assert.AreEqual("站起来", _submitted[0], "发出去的是组合出来的那一句");
            Assert.AreEqual(string.Empty, _panel.Panel.InputText, "发送之后输入框清空");
        }

        /// <summary>
        /// Shift+Enter 只换行：不发送、草稿留着，而且之后普通 Enter 仍能把它发出去
        /// （证明 Shift 分支只“让路”，没有把面板留在某个半截状态里）。
        /// 与 DialoguePanelTests 里那条同名断言不重复：那条只看「没发出去」，这条还看后续能否发送。
        /// 合成事件不带 character，所以“换行本身有没有插进文本”由 TextField 负责，批处理里测不到。
        /// </summary>
        [UnityTest]
        public IEnumerator ShiftEnterLeavesTheDraftAlone()
        {
            yield return Build();

            _panel.Panel.InputText = "还在打字";
            DialoguePanelTests.DialoguePanelHarness.PressEnter(_input, true);

            Assert.AreEqual(0, _submitted.Count, "Shift+Enter 不许发送");
            Assert.AreEqual("还在打字", _panel.Panel.InputText, "Shift+Enter 之后草稿必须留着");

            DialoguePanelTests.DialoguePanelHarness.PressEnter(_input);
            Assert.AreEqual(1, _submitted.Count, "换行之后普通 Enter 必须照常发送");
            Assert.AreEqual("还在打字", _submitted[0], "发出去的还是那句草稿");
        }

        /// <summary>
        /// 程序化整串写值（控制器的清空/写回、语音的覆盖）也必须把光标搬到末尾：
        /// 否则下一次真按键会插在旧光标处 —— 那就是“末字错位”的来源。
        /// </summary>
        [UnityTest]
        public IEnumerator SettingTheTextProgrammaticallyMovesTheCaretToTheEnd()
        {
            yield return Build();

            _panel.Panel.InputText = "你好世界";
            Assert.AreEqual(4, _panel.Panel.CursorIndex, "整串写值之后光标在末尾");

            _panel.Panel.AppendToInput("！");
            Assert.AreEqual("你好世界！", _panel.Panel.InputText);
            Assert.AreEqual(5, _panel.Panel.CursorIndex, "追加之后光标跟着到新的末尾");
        }
    }
}
