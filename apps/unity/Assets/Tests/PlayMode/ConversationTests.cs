using System;
using System.Collections;
using System.Diagnostics;
using System.Threading;
using Mas3D.Conversation;
using Mas3D.Net;
using Mas3D.UI;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;

namespace Mas3D.Tests
{
    /// <summary>
    /// 会话状态机：Submit 之后进 Thinking、reply 收尾成 Answered、error 收尾成 Failed。
    ///
    /// 为什么用真 BridgeClient 而不是假替身：BridgeClient 是 sealed，<c>SendSay</c> 还要求 socket 真的是
    /// Open（BridgeClient.cs:161），替身只能改事件不能改这个判据。所以这里按 brief 的“直接喂帧”走
    /// ScriptedBridge —— 真 WebSocket、按剧本回帧，状态机的输入与真实运行时完全同源。
    /// </summary>
    public class ConversationTests
    {
        const string TurnId = "turn-1";
        const float Deadline = 10f;

        [UnityTest]
        public IEnumerator ASayMovesThePhaseToThinking()
        {
            ScriptedBridge bridge = null;
            var client = new BridgeClient();
            DialoguePanelTests.DialoguePanelHarness harness = null;
            try
            {
                bridge = new ScriptedBridge(session =>
                {
                    var say = session.ReadText(5000);
                    if (say == null) return;
                    Debug.Log("[ConversationTests] 服务端收到：" + say);
                    // 故意不回这一轮：状态必须停在 Thinking，不许自己滑到 Answered。
                });

                client.Configure(new BridgeSettings { Url = bridge.Url });
                client.Start();

                harness = DialoguePanelTests.DialoguePanelHarness.Create();
                var controller = harness.AddController(client);

                yield return WaitUntil(() => client.State == BridgeState.Connected, client);

                Assert.IsTrue(controller.Submit("晚上好"), "连上之后提交必须真的发出去");
                Assert.AreEqual(TurnPhase.Thinking, controller.Phase, "发出 say 之后进入 Thinking");
                Assert.IsFalse(harness.Panel.Interactable, "等这一轮回复时输入框要锁住（Review Focus：重复提交防护）");
                StringAssert.Contains("晚上好", harness.Panel.LogText, "发出的话要进消息流");

                // 上一句没回来之前不许再发：SendSay 在 Waiting 里本来就返回 false。
                Assert.IsFalse(controller.Submit("再来一句"), "等回复期间不许发第二句");
                Assert.AreEqual(TurnPhase.Thinking, controller.Phase, "被挡下的第二句不该改状态");
            }
            finally
            {
                client.Dispose();
                if (bridge != null) bridge.Dispose();
                if (harness != null) harness.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator TheReplyEndsTheTurn()
        {
            ScriptedBridge bridge = null;
            var client = new BridgeClient();
            DialoguePanelTests.DialoguePanelHarness harness = null;
            try
            {
                const string replyText = "挺好的，你呢？";
                bridge = new ScriptedBridge(session =>
                {
                    var say = session.ReadText(5000);
                    if (say == null) return;
                    session.SendText("{\"type\":\"ack\",\"turnId\":\"" + TurnId + "\"}");
                    session.SendText("{\"type\":\"thinking\",\"turnId\":\"" + TurnId + "\"}");
                    session.SendText("{\"type\":\"reply\",\"turnId\":\"" + TurnId + "\",\"text\":\"" + replyText + "\"}");
                });

                client.Configure(new BridgeSettings { Url = bridge.Url });
                client.Start();

                harness = DialoguePanelTests.DialoguePanelHarness.Create();
                var controller = harness.AddController(client);

                yield return WaitUntil(() => client.State == BridgeState.Connected, client);
                Assert.IsTrue(controller.Submit("今天怎么样"));
                yield return WaitUntil(() => controller.Phase == TurnPhase.Answered, client);

                Assert.AreEqual(TurnId, controller.PendingTurnId, "ack 里的 turnId 要记下来");
                StringAssert.Contains(replyText, harness.Panel.LogText, "reply 的话要落进消息流");
                StringAssert.Contains("今天怎么样", harness.Panel.LogText, "玩家那一行也要在消息流里");
                Assert.IsTrue(harness.Panel.Interactable, "这一轮结束之后输入框要放开");
                Assert.AreEqual(BridgeState.Connected, client.State, "收齐 reply 之后通道回到 Connected");
            }
            finally
            {
                client.Dispose();
                if (bridge != null) bridge.Dispose();
                if (harness != null) harness.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator AnErrorFrameEndsTheTurnWithAText()
        {
            ScriptedBridge bridge = null;
            var client = new BridgeClient();
            DialoguePanelTests.DialoguePanelHarness harness = null;
            try
            {
                bridge = new ScriptedBridge(session =>
                {
                    var say = session.ReadText(5000);
                    if (say == null) return;
                    session.SendText("{\"type\":\"ack\",\"turnId\":\"" + TurnId + "\"}");
                    session.SendText("{\"type\":\"error\",\"code\":\"timeout\",\"message\":\"模型 20 秒没有回应\",\"turnId\":\"" + TurnId + "\"}");
                });

                client.Configure(new BridgeSettings { Url = bridge.Url });
                client.Start();

                harness = DialoguePanelTests.DialoguePanelHarness.Create();
                var controller = harness.AddController(client);

                yield return WaitUntil(() => client.State == BridgeState.Connected, client);
                Assert.IsTrue(controller.Submit("晚上好"));

                // 等回复期间玩家又打了半句：错误回来时这半句必须还在（Review Focus 第 2 条）。
                harness.Panel.InputText = "下一句草稿";
                yield return WaitUntil(() => controller.Phase == TurnPhase.Failed, client);

                Assert.AreEqual(TurnPhase.Failed, controller.Phase, "error 帧结束这一轮");
                StringAssert.Contains("她这次没有回应。", harness.Panel.LogText, "超时文案照抄");
                Assert.AreEqual("下一句草稿", harness.Panel.InputText, "错误回来不许清掉玩家正在打的草稿");
                Assert.IsTrue(harness.Panel.Interactable, "失败之后要能再试");
            }
            finally
            {
                client.Dispose();
                if (bridge != null) bridge.Dispose();
                if (harness != null) harness.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator ARejectedSayEndsTheTurnWithTheOfflineCopy()
        {
            ScriptedBridge bridge = null;
            var client = new BridgeClient();
            DialoguePanelTests.DialoguePanelHarness harness = null;
            try
            {
                // 剧本：先好好答完一轮，再按 1008（令牌错）终止连接 —— 之后 SendSay 一定返回 false。
                // 这条用例盯的就是“发不出去”那条分支：状态词、兜底文案、原文写回、解锁、相位、turnId。
                bridge = new ScriptedBridge(session =>
                {
                    var say = session.ReadText(5000);
                    if (say == null) return;
                    session.SendText("{\"type\":\"ack\",\"turnId\":\"" + TurnId + "\"}");
                    session.SendText("{\"type\":\"reply\",\"turnId\":\"" + TurnId + "\",\"text\":\"嗯。\"}");
                    Thread.Sleep(300);   // 让 reply 先落地，再关
                    session.Close(1008, "令牌不正确");
                });

                client.Configure(new BridgeSettings { Url = bridge.Url });
                client.Start();

                harness = DialoguePanelTests.DialoguePanelHarness.Create();
                var controller = harness.AddController(client);

                yield return WaitUntil(() => client.State == BridgeState.Connected, client);
                Assert.IsTrue(controller.Submit("晚上好"));
                yield return WaitUntil(() => controller.Phase == TurnPhase.Answered, client);
                yield return WaitUntil(() => client.State == BridgeState.Closed, client);

                // 通道已经终止（1008 是终结性的，不重连）：这一句发不出去。
                Assert.IsFalse(controller.Submit("再来一句"), "通道关了，发送必须失败");

                Assert.AreEqual(TurnPhase.Failed, controller.Phase, "发不出去就当场判失败，不许停在 Sending");
                Assert.AreEqual("未回应", harness.Document.rootVisualElement.Q<Label>("status").text, "状态词要改成未回应");
                StringAssert.Contains(DialoguePanel.OfflineCopy, harness.Panel.LogText, "桥接没响应要给出兜底说明");
                Assert.AreEqual("再来一句", harness.Panel.InputText, "没发出去：把玩家的话还回输入框");
                Assert.IsTrue(harness.Panel.Interactable, "失败之后输入框要放开，能再试");
                Assert.AreEqual(string.Empty, controller.PendingTurnId ?? string.Empty,
                    "这一轮根本没发出去，不该还挂着上一轮的 turnId");
            }
            finally
            {
                client.Dispose();
                if (bridge != null) bridge.Dispose();
                if (harness != null) harness.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator ARejectedSubmitKeepsTheTypedLine()
        {
            DialoguePanelTests.DialoguePanelHarness harness = null;
            try
            {
                harness = DialoguePanelTests.DialoguePanelHarness.Create();
                // 通道还没接上（P3 半装配场景里 Client 为空）：控制器会拒掉这一句。
                var controller = harness.AddController(null);
                yield return null;

                var input = harness.Document.rootVisualElement.Q<TextField>("input");
                harness.Panel.InputText = "这一句不能白丢";
                DialoguePanelTests.DialoguePanelHarness.PressEnter(input);

                Assert.AreEqual(TurnPhase.Failed, controller.Phase, "没有通道可发：当场判失败");
                Assert.AreEqual("这一句不能白丢", harness.Panel.InputText, "被拒绝的这一句必须留在输入框里，不许被静默吞掉");
                Assert.IsTrue(harness.Panel.Interactable, "拒绝之后输入框要能用");
                StringAssert.Contains(DialoguePanel.OfflineCopy, harness.Panel.LogText, "要说明桥接没响应");
                Assert.AreEqual(string.Empty, controller.PendingTurnId ?? string.Empty, "没有在飞的回合就没有 turnId");
            }
            finally
            {
                if (harness != null) harness.Dispose();
            }
        }

        static IEnumerator WaitUntil(Func<bool> done, BridgeClient client)
        {
            var watch = Stopwatch.StartNew();
            while (!done())
            {
                if (watch.Elapsed.TotalSeconds > Deadline)
                {
                    Assert.Fail("等了 " + Deadline + " 秒仍不满足条件（通道状态 " + client.State + "）");
                }
                client.Pump();
                yield return null;
            }
        }
    }
}
