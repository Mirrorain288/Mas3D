using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Mas3D.Conversation;
using Mas3D.Net;
using Mas3D.UI;
using Mas3D.Voice;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;

namespace Mas3D.Tests
{
    /// <summary>
    /// P7 Task 1 的接线层：真的 WebSocket（ScriptedBridge）把 reply 送进控制器，播报组件拿同一段文本，
    /// 用假 transport 逐句"播"——判据落在面板上（字幕、状态词、输入可用），不是落在组件字段里。
    /// 假 transport 不联网、不解码：Task 1 要验的是"回复之后字幕怎么出现"，音频本身交给 P7 的后续任务。
    /// </summary>
    public class ReplySpeakerTests
    {
        const string TurnId = "turn-voice";
        const float Deadline = 10f;

        /// <summary>三句话的回复：句数正好能分辨"一句一句出现"和"整段铺开"。</summary>
        const string ReplyText = "你好。今天过得怎么样？我在看书。";

        [UnityTest]
        public IEnumerator AReplyIsSpokenOneSentenceAtATimeAndTheTurnStillEnds()
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
                    session.SendText("{\"type\":\"reply\",\"turnId\":\"" + TurnId + "\",\"text\":\"" + ReplyText + "\"}");
                });

                client.Configure(new BridgeSettings { Url = bridge.Url });
                client.Start();

                harness = DialoguePanelTests.DialoguePanelHarness.Create();
                var controller = harness.AddController(client);
                var fake = new FakeTtsTransport();
                var speaker = Wire(harness, controller, fake, bridge.Url);

                yield return WaitUntil(() => client.State == BridgeState.Connected, client);
                Assert.IsTrue(controller.Submit("今天怎么样"), "这一句必须被接受");
                yield return WaitUntil(() => controller.Phase == TurnPhase.Answered, client);

                Assert.AreEqual(ReplyText, speaker.LastText, "播报拿到的必须是 reply 的原文（不是自己再拼一遍）");
                Assert.AreEqual(TurnId, speaker.LastTurnId, "要记住这是哪一轮：换轮时要能停掉上一段");
                CollectionAssert.AreEqual(new[] { "你好。", "今天过得怎么样？", "我在看书。" }, speaker.Sentences);

                // 一句一句出现：第一句已经在字幕上，后两句还没到。
                yield return WaitUntil(() => speaker.Revealed >= 1, client);
                Assert.AreEqual(1, speaker.Revealed, "第 0 句一播就该出现，且只出现它");
                Assert.AreEqual("你好。", speaker.SubtitleText);

                var label = harness.Document.rootVisualElement.Q<Label>("subtitle");
                Assert.IsNotNull(label, "Dialogue.uxml 必须有一节 subtitle");
                Assert.AreEqual("你好。", label.text, "字幕要落在面板上，不能只在组件的字段里");
                Assert.AreEqual(DisplayStyle.Flex, label.style.display.value, "有字幕时必须真的显示出来");
                Assert.AreNotEqual(string.Join("\n", speaker.Sentences), label.text,
                    "整段一起铺开就违背了'按句出现'");
                Assert.IsTrue(harness.Panel.Interactable, "播报期间输入必须能用：语音只是附加，不是门");

                yield return WaitUntil(() => speaker.Revealed == 3 && !speaker.Speaking, client);

                Assert.AreEqual(string.Join("\n", speaker.Sentences), speaker.SubtitleText,
                    "播完时字幕必须是完整回复，不能只剩最后一句");
                CollectionAssert.AreEqual(speaker.Sentences, fake.Texts,
                    "每次 synth 只送当前这一句：送整段 + 下标时，中文句数与英文句数不等就会少播或 400");
                Assert.AreEqual(TurnPhase.Answered, controller.Phase, "播报不改这一轮的结论");
                Assert.AreEqual("已回复", harness.Document.rootVisualElement.Q<Label>("status").text,
                    "播报成功时状态词仍是'已回复'（降级才换成那句提示）");
                Assert.IsTrue(harness.Panel.Interactable);

                // 同一轮只播一次：控制器重复交同一段文本时不许从头再来一遍。
                var calls = fake.SynthCalls;
                speaker.Speak(TurnId, ReplyText);
                Assert.AreEqual(calls, fake.SynthCalls, "同一轮重复交文本不重播");
            }
            finally
            {
                Tear(harness, client, bridge);
            }
        }

        [UnityTest]
        public IEnumerator AnUnavailableVoiceFallsBackToSubtitlesWithoutLockingThePanel()
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
                    session.SendText("{\"type\":\"reply\",\"turnId\":\"" + TurnId + "\",\"text\":\"" + ReplyText + "\"}");
                });

                client.Configure(new BridgeSettings { Url = bridge.Url });
                client.Start();

                harness = DialoguePanelTests.DialoguePanelHarness.Create();
                var controller = harness.AddController(client);
                // 模拟桥接 503（TTS 没起）：第一句就降级，后面不再打桥接。
                var fake = new FakeTtsTransport { Unavailable = true };
                var speaker = Wire(harness, controller, fake, bridge.Url);

                // 剧本的 reply 是跟着 say 来的：不发这一句，通道再通也不会有 frame 落下来。
                yield return WaitUntil(() => client.State == BridgeState.Connected, client);
                Assert.IsTrue(controller.Submit("今天怎么样"), "这一句必须被接受");
                yield return WaitUntil(() => controller.Phase == TurnPhase.Answered, client);
                yield return WaitUntil(() => speaker.Revealed == 3 && !speaker.Speaking, client);

                Assert.AreEqual(ReplySpeaker.FallbackCopy, speaker.StatusMessage);
                Assert.AreEqual(ReplySpeaker.FallbackCopy,
                    harness.Document.rootVisualElement.Q<Label>("status").text,
                    "状态栏要一句话说清语音暂时不可用");
                Assert.AreEqual(string.Join("\n", speaker.Sentences), speaker.SubtitleText,
                    "没有声音时字幕必须是完整回复，不能停在第一句");
                StringAssert.Contains(ReplyText, harness.Panel.LogText,
                    "消息流里那一行回复不能被降级删掉（既有断言也在看它）");
                Assert.AreEqual(1, fake.SynthCalls, "判定不可用之后不许继续一句一句地打桥接");
                Assert.IsTrue(harness.Panel.Interactable, "降级之后输入必须能用");
                Assert.AreEqual(TurnPhase.Answered, controller.Phase, "降级不影响这一轮的结论");

                var label = harness.Document.rootVisualElement.Q<Label>("subtitle");
                Assert.AreEqual(DisplayStyle.Flex, label.style.display.value, "只显示字幕：字幕还得在");
            }
            finally
            {
                Tear(harness, client, bridge);
            }
        }

        [UnityTest]
        public IEnumerator TheShippedSceneWiresASpeakerOntoTheConversation()
        {
            SceneFlow.Require();
            RoomBootstrap boot = null;
            try
            {
                yield return SceneFlow.LoadMain();
                boot = UnityEngine.Object.FindAnyObjectByType<RoomBootstrap>();
                Assert.IsNotNull(boot, "Main.unity 必须带 RoomBootstrap");
                yield return SceneFlow.WaitForBoot(boot);

                var controller = UnityEngine.Object.FindAnyObjectByType<ConversationController>();
                Assert.IsNotNull(controller, "Main 里应该有会话控制器");

                var speaker = controller.GetComponent<ReplySpeaker>();
                Assert.IsNotNull(speaker, "Boot() 必须把 ReplySpeaker 挂在会话控制器上——场景里不手摆");
                Assert.AreEqual(controller, speaker.GetComponent<ConversationController>(),
                    "播报与控制器同一块 GameObject：reply 一落就能把文本交过去");
                Assert.IsNotNull(speaker.Panel, "播报必须有面板，否则字幕没地方落");
                Assert.IsNotNull(speaker.Settings, "地址与令牌跟桥接共用同一块 BridgeSettings");
                Assert.IsNull(speaker.Transport,
                    "场景里不给 Transport：真实现由 ResolveTransport 在第一次开播时才建（否则 EditMode 之外的构造会带上真网络）");
            }
            finally
            {
                SceneFlow.UnloadMainIfLoaded();
            }
        }

        /// <summary>停播是最容易被做漏的一步：字幕留着，玩家会以为她还在说上一轮。</summary>
        [UnityTest]
        public IEnumerator StopClearsTheSubtitleAndASecondSpeakStartsFromTheTop()
        {
            DialoguePanelTests.DialoguePanelHarness harness = null;
            ReplySpeaker speaker = null;
            try
            {
                harness = DialoguePanelTests.DialoguePanelHarness.Create();
                var fake = new FakeTtsTransport { SentenceMs = 400 };
                speaker = AttachSpeaker(harness, fake);

                speaker.Speak("turn-1", "甲。乙。丙。");
                yield return WaitFor(() => speaker.Revealed >= 1 && speaker.Speaking);

                var label = harness.Document.rootVisualElement.Q<Label>("subtitle");
                Assert.AreEqual("甲。", label.text);

                speaker.Stop();
                Assert.IsFalse(speaker.Speaking);
                Assert.AreEqual(string.Empty, speaker.SubtitleText,
                    "停止播放必须把字幕也收掉：否则等待新回复时旧字幕一直挂着");
                Assert.AreEqual(string.Empty, label.text, "面板上的字幕也得清");
                Assert.AreEqual(DisplayStyle.None, label.style.display.value, "空字幕要真的收起来");

                speaker.Speak("turn-2", "新的。另一句。");
                yield return WaitFor(() => speaker.Revealed >= 1);
                Assert.AreEqual("新的。", speaker.SubtitleText, "第二轮从第一句重新开始，不接着上一轮");
                yield return WaitFor(() => speaker.Revealed == 2 && !speaker.Speaking);
                Assert.AreEqual("新的。\n另一句。", speaker.SubtitleText);
                CollectionAssert.DoesNotContain(fake.Texts, "丙。", "被停掉的那一轮不许再往下合成");
            }
            finally
            {
                if (speaker != null) speaker.Stop();
                if (harness != null) harness.Dispose();
            }
        }

        /// <summary>新一轮回复直接开说：不需要调用方先 Stop，上一轮必须当场让位（控制器在 error 时才显式 Stop）。</summary>
        [UnityTest]
        public IEnumerator ASecondSpeakInterruptsTheFirstWithoutAnExplicitStop()
        {
            DialoguePanelTests.DialoguePanelHarness harness = null;
            ReplySpeaker speaker = null;
            try
            {
                harness = DialoguePanelTests.DialoguePanelHarness.Create();
                var fake = new FakeTtsTransport { SentenceMs = 400 };
                speaker = AttachSpeaker(harness, fake);

                speaker.Speak("turn-1", "甲。乙。丙。");
                yield return WaitFor(() => speaker.Revealed >= 1 && speaker.Speaking);

                // 不 Stop，直接交下一轮：Speak 自己负责先收上一轮。
                speaker.Speak("turn-2", "丁。戊。");

                Assert.IsTrue(speaker.Speaking, "第二轮立刻开始播");
                CollectionAssert.AreEqual(new[] { "丁。", "戊。" }, speaker.Sentences, "播的是新一轮的句子");
                Assert.AreEqual(string.Empty, speaker.SubtitleText, "上一轮的字幕当场收掉，等新一轮第一句再露");

                yield return WaitFor(() => speaker.Revealed >= 1);
                Assert.AreEqual("丁。", speaker.SubtitleText, "字幕从新一轮第一句重新开始");
                Assert.IsFalse(speaker.SubtitleText.Contains("甲。"), "上一轮的句子不许混进新一轮字幕");

                yield return WaitFor(() => speaker.Revealed == 2 && !speaker.Speaking);
                Assert.AreEqual("丁。\n戊。", speaker.SubtitleText);
                CollectionAssert.DoesNotContain(fake.Texts, "乙。", "被顶掉的那一轮不许继续合成");
                CollectionAssert.DoesNotContain(fake.Texts, "丙。", "被顶掉的那一轮不许继续合成");
            }
            finally
            {
                if (speaker != null) speaker.Stop();
                if (harness != null) harness.Dispose();
            }
        }

        /// <summary>error 帧到达时这一轮已经失败：声音与字幕都不该继续演下去。</summary>
        [UnityTest]
        public IEnumerator AnErrorFrameStopsThePlayingTurnAndClearsTheSubtitle()
        {
            ScriptedBridge bridge = null;
            var client = new BridgeClient();
            DialoguePanelTests.DialoguePanelHarness harness = null;
            ReplySpeaker speaker = null;
            try
            {
                bridge = new ScriptedBridge(session =>
                {
                    var say = session.ReadText(5000);
                    if (say == null) return;
                    session.SendText("{\"type\":\"ack\",\"turnId\":\"" + TurnId + "\"}");
                    session.SendText("{\"type\":\"reply\",\"turnId\":\"" + TurnId + "\",\"text\":\"" + ReplyText + "\"}");
                    System.Threading.Thread.Sleep(300);   // 让她先真的"说"起来，再让这一轮失败
                    session.SendText("{\"type\":\"error\",\"turnId\":\"" + TurnId + "\",\"code\":\"timeout\",\"message\":\"慢\"}");
                });

                client.Configure(new BridgeSettings { Url = bridge.Url });
                client.Start();

                harness = DialoguePanelTests.DialoguePanelHarness.Create();
                var controller = harness.AddController(client);
                var fake = new FakeTtsTransport { SentenceMs = 700 };
                speaker = Wire(harness, controller, fake, bridge.Url);

                yield return WaitUntil(() => client.State == BridgeState.Connected, client);
                Assert.IsTrue(controller.Submit("今天怎么样"));
                yield return WaitUntil(() => controller.Phase == TurnPhase.Answered, client);
                yield return WaitUntil(() => speaker.Revealed >= 1, client);
                Assert.IsTrue(speaker.Speaking, "前提：error 到达时这一轮还在播");

                yield return WaitUntil(() => controller.Phase == TurnPhase.Failed, client);
                yield return null;   // 让 error 分支里的停播执行完

                Assert.IsFalse(speaker.Speaking, "这一轮已经失败，声音与协程都得停");
                Assert.AreEqual(string.Empty, speaker.SubtitleText, "失败之后旧字幕不许留在面板上");
                var label = harness.Document.rootVisualElement.Q<Label>("subtitle");
                Assert.AreEqual(string.Empty, label.text);
                Assert.AreEqual(DisplayStyle.None, label.style.display.value);
                Assert.AreEqual("未回应", harness.Document.rootVisualElement.Q<Label>("status").text);
            }
            finally
            {
                Tear(harness, client, bridge);
            }
        }

        /// <summary>
        /// 断线也是一种失败：reply 已经落地、声音正在放的时候通道断了，这一轮不能继续"演"——
        /// 声音停、字幕收、状态词落到"未回应"。否则玩家会听见一个已经死掉的会话还在说话，
        /// 而界面写着"已回复"。
        /// </summary>
        [UnityTest]
        public IEnumerator ADisconnectWhileSpeakingStopsTheVoiceAndFailsTheTurn()
        {
            ScriptedBridge bridge = null;
            var client = new BridgeClient();
            DialoguePanelTests.DialoguePanelHarness harness = null;
            ReplySpeaker speaker = null;
            var abortNow = new System.Threading.ManualResetEventSlim(false);
            try
            {
                bridge = new ScriptedBridge(session =>
                {
                    if (session.Index != 1) return;   // 重连进来的连接不再演一遍剧本
                    var say = session.ReadText(5000);
                    if (say == null) return;
                    session.SendText("{\"type\":\"ack\",\"turnId\":\"" + TurnId + "\"}");
                    session.SendText("{\"type\":\"reply\",\"turnId\":\"" + TurnId + "\",\"text\":\"" + ReplyText + "\"}");
                    // 等用例确认"她真的在说"再掐线：靠 Sleep 猜时机，机器一慢就变成断线先到、用例永远等不到 Speaking。
                    abortNow.Wait(5000);
                    session.AbortSocket();   // 桥接进程被杀：客户端看到 1006
                });

                client.Configure(new BridgeSettings { Url = bridge.Url });
                client.Start();

                harness = DialoguePanelTests.DialoguePanelHarness.Create();
                var controller = harness.AddController(client);
                var fake = new FakeTtsTransport { SentenceMs = 700 };
                speaker = Wire(harness, controller, fake, bridge.Url);

                yield return WaitUntil(() => client.State == BridgeState.Connected, client);
                Assert.IsTrue(controller.Submit("今天怎么样"), "这一句必须被接受");
                yield return WaitUntil(() => controller.Phase == TurnPhase.Answered, client);
                yield return WaitUntil(() => speaker.Revealed >= 1 && speaker.Speaking, client);
                Assert.IsTrue(speaker.Speaking, "前提：断线到达时这一轮还在播");

                abortNow.Set();
                // 不用 WaitUntil：它内部的 Assert.Fail 会把外层协程的 finally 跳过，面板漏在场景里会连累后面的用例。
                var watch = Stopwatch.StartNew();
                while (controller.Phase != TurnPhase.Failed && watch.Elapsed.TotalSeconds < Deadline)
                {
                    client.Pump();
                    yield return null;
                }
                yield return null;   // 让断线分支里的停播执行完

                Assert.AreEqual(TurnPhase.Failed, controller.Phase,
                    "断线之后这一轮必须收成失败，不能停在 Answered");
                Assert.IsFalse(speaker.Speaking, "通道已经断了，声音与协程都必须停");
                Assert.AreEqual(string.Empty, speaker.SubtitleText, "断线之后旧字幕不许留在面板上");
                var label = harness.Document.rootVisualElement.Q<Label>("subtitle");
                Assert.AreEqual(string.Empty, label.text);
                Assert.AreEqual(DisplayStyle.None, label.style.display.value);
                Assert.AreEqual("未回应", harness.Document.rootVisualElement.Q<Label>("status").text,
                    "播到一半断线：这一轮要按失败收尾，不能停在\"已回复\"");
            }
            finally
            {
                abortNow.Set();
                Tear(harness, client, bridge);
                abortNow.Dispose();
            }
        }

        /// <summary>
        /// 700 字的回复：每次只请求当前这一句（≤200 字），字幕只保留最近 6 句（前面用省略号提示）。
        /// 这条同时钉住 I1 的两种走法（整段送 + 中文下标）与"40 句把输入区挤没"的观感缺陷。
        /// </summary>
        [UnityTest]
        public IEnumerator ALongReplyIsAskedOneShortSentenceAtATimeAndTheSubtitleStaysBounded()
        {
            DialoguePanelTests.DialoguePanelHarness harness = null;
            ReplySpeaker speaker = null;
            try
            {
                var reply = new System.Text.StringBuilder();
                for (var i = 0; i < 10; i++) reply.Append('第').Append(i).Append('句').Append(new string('啊', 65)).Append('。');
                Assert.Greater(reply.Length, 600, "前提：这条回复超过桥接的 600 字整段上限");

                harness = DialoguePanelTests.DialoguePanelHarness.Create();
                var fake = new FakeTtsTransport { SentenceMs = 10 };
                speaker = AttachSpeaker(harness, fake);

                speaker.Speak("turn-long", reply.ToString());
                yield return WaitFor(() => speaker.Revealed == speaker.Sentences.Length && !speaker.Speaking);

                Assert.AreEqual(10, speaker.Sentences.Length);
                Assert.AreEqual(speaker.Sentences.Length, fake.Texts.Count, "一句一次请求");
                CollectionAssert.AreEqual(speaker.Sentences, fake.Texts, "每次送的就是当前这一句");
                foreach (var text in fake.Texts)
                {
                    Assert.LessOrEqual(text.Length, 200, "单次请求必须远低于桥接的 600 字上限");
                }

                var lines = speaker.SubtitleText.Split('\n');
                Assert.LessOrEqual(lines.Length, 7, "字幕只保留最近 6 句，外加一行省略号：不许把 40 句全铺在输入区上面");
                Assert.AreEqual("…", lines[0], "前面还有内容时用省略号提示");
                Assert.AreEqual(speaker.Sentences[speaker.Sentences.Length - 1], lines[lines.Length - 1],
                    "收尾停在最后一句：玩家看到的必须是刚说完的那句");
            }
            finally
            {
                if (speaker != null) speaker.Stop();
                if (harness != null) harness.Dispose();
            }
        }

        /// <summary>清空历史之后，旧字幕不该还挂在已经空掉的消息流下面。</summary>
        [UnityTest]
        public IEnumerator ClearingTheLogAlsoClearsTheStaleSubtitle()
        {
            DialoguePanelTests.DialoguePanelHarness harness = null;
            ReplySpeaker speaker = null;
            try
            {
                harness = DialoguePanelTests.DialoguePanelHarness.Create();
                var fake = new FakeTtsTransport { SentenceMs = 600 };
                speaker = AttachSpeaker(harness, fake);

                speaker.Speak("turn-1", ReplyText);
                yield return WaitFor(() => speaker.Revealed >= 1);

                var root = harness.Document.rootVisualElement;
                var label = root.Q<Label>("subtitle");
                Assert.AreEqual("你好。", label.text, "前提：这时字幕正在显示");

                DialoguePanelTests.DialoguePanelHarness.Click(root.Q<Button>("clear"));

                Assert.AreEqual(string.Empty, label.text, "清空对话也必须收掉字幕：旧字幕不该留在已清空的历史下面");
                Assert.AreEqual(DisplayStyle.None, label.style.display.value);
            }
            finally
            {
                if (speaker != null) speaker.Stop();
                if (harness != null) harness.Dispose();
            }
        }

        /// <summary>每句一个 clip：换句、停播、销毁时都要把手上那个还回去，否则 40 句就是 40 个常驻 clip。</summary>
        [UnityTest]
        public IEnumerator EveryClipItOwnsIsReleased()
        {
            DialoguePanelTests.DialoguePanelHarness harness = null;
            ReplySpeaker speaker = null;
            try
            {
                harness = DialoguePanelTests.DialoguePanelHarness.Create();
                var fake = new FakeTtsTransport { SentenceMs = 30 };
                speaker = AttachSpeaker(harness, fake);

                speaker.Speak("turn-1", ReplyText);
                yield return WaitFor(() => speaker.Revealed == 3 && !speaker.Speaking);
                speaker.Stop();

                Assert.AreEqual(3, speaker.ClipsDestroyed,
                    "三句三个 clip：被下一句替换掉的两个 + Stop 时手上那个，一个都不许漏");
                speaker.Stop();
                Assert.AreEqual(3, speaker.ClipsDestroyed, "重复 Stop 不许把同一个 clip 记两次");
            }
            finally
            {
                if (speaker != null) speaker.Stop();
                if (harness != null) harness.Dispose();
            }
        }

        /// <summary>
        /// 停播可能正好发生在 LoadAudio 回调与协程恢复之间：这一份 clip 已经交给我们，却还没进 Audio.clip，
        /// Stop 只收得回手里那一份 —— 漏了它，每停一次就漏一个 clip（40 句就是 40 个常驻对象）。
        /// </summary>
        [UnityTest]
        public IEnumerator AClipHandedOverAfterStopIsDestroyed()
        {
            DialoguePanelTests.DialoguePanelHarness harness = null;
            ReplySpeaker speaker = null;
            try
            {
                harness = DialoguePanelTests.DialoguePanelHarness.Create();
                var fake = new FakeTtsTransport { SentenceMs = 500 };
                speaker = AttachSpeaker(harness, fake);

                // 音频回调里当场停播：协程再往下走时看到的已经是新一代。
                fake.InsideLoad = clip => speaker.Stop();

                speaker.Speak("turn-1", "甲。乙。");
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed.TotalSeconds < 2 && speaker.ClipsDestroyed == 0) yield return null;

                Assert.IsFalse(speaker.Speaking, "Stop 之后不许还挂着\"正在播\"");
                Assert.AreEqual(1, speaker.ClipsDestroyed,
                    "回调里被 Stop 掉的那一份 clip 也必须销毁：这里为 0 就是漏了（它既不在 Audio.clip，也不会再被 Stop 看见）");
                Assert.IsTrue(speaker.Audio == null || speaker.Audio.clip == null,
                    "手上不许还挂着那个已经停掉的 clip");
                Assert.AreEqual(string.Empty, speaker.SubtitleText, "Stop 顺带把字幕收干净");
            }
            finally
            {
                if (speaker != null) speaker.Stop();
                if (harness != null) harness.Dispose();
            }
        }

        /// <summary>
        /// 收尾：先收面板，再收通道。面板是"场景里那块"的替身，漏一个就会让后面
        /// 按 FindAnyObjectByType&lt;DialoguePanel&gt; 找面板的用例（VoiceSceneTests）撞到替身而不是真面板；
        /// 通道那头有后台线程在阻塞读，收尾抛异常也不能连累面板。
        /// </summary>
        static void Tear(DialoguePanelTests.DialoguePanelHarness harness, BridgeClient client, ScriptedBridge bridge)
        {
            if (harness != null) harness.Dispose();
            try
            {
                client.Dispose();
                if (bridge != null) bridge.Dispose();
            }
            catch (Exception e)
            {
                Debug.Log("[test] 收尾关闭通道时异常（不影响结果）：" + e.Message);
            }
        }

        static ReplySpeaker AttachSpeaker(
            DialoguePanelTests.DialoguePanelHarness harness,
            FakeTtsTransport fake)
        {
            var speaker = harness.Panel.gameObject.AddComponent<ReplySpeaker>();
            speaker.Panel = harness.Panel;
            speaker.Transport = fake;
            speaker.Settings = new BridgeSettings { Url = "ws://127.0.0.1:9/mas3d/ws", Token = "test-token" };
            return speaker;
        }

        static ReplySpeaker Wire(
            DialoguePanelTests.DialoguePanelHarness harness,
            ConversationController controller,
            FakeTtsTransport fake,
            string bridgeUrl)
        {
            var speaker = AttachSpeaker(harness, fake);
            speaker.Settings.Url = bridgeUrl;
            controller.Speaker = speaker;
            return speaker;
        }

        /// <summary>
        /// 停播是"这一段不说了"，不是"这一轮说过了"。停播之后同一轮里再喊同一句，必须重新出声——
        /// 否则玩家按了打断、又让她从头说，她会静悄悄地什么都不做。
        /// </summary>
        [UnityTest]
        public IEnumerator AStoppedLineCanBeSaidAgainInTheSameTurn()
        {
            DialoguePanelTests.DialoguePanelHarness harness = null;
            try
            {
                harness = DialoguePanelTests.DialoguePanelHarness.Create();
                var fake = new FakeTtsTransport();
                var speaker = AttachSpeaker(harness, fake);

                speaker.Speak(TurnId, ReplyText);
                yield return WaitFor(() => fake.SynthCalls >= 1);
                yield return WaitFor(() => speaker.Speaking);

                speaker.Stop();
                yield return null;
                Assert.IsFalse(speaker.Speaking, "前提：Stop 之后必须真的停了");
                var callsAfterStop = fake.SynthCalls;

                speaker.Speak(TurnId, ReplyText);
                yield return WaitFor(() => fake.SynthCalls > callsAfterStop);

                Assert.Greater(fake.SynthCalls, callsAfterStop,
                    "同一轮同一句，停播之后再说一遍要重新合成，不能被去重吞掉");
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

        static IEnumerator WaitFor(Func<bool> done)
        {
            var watch = Stopwatch.StartNew();
            while (!done())
            {
                if (watch.Elapsed.TotalSeconds > Deadline)
                {
                    Assert.Fail("等了 " + Deadline + " 秒仍不满足条件");
                }
                yield return null;
            }
        }

        /// <summary>
        /// 假 transport：不联网、不解码，只按句"播"。音频用 0.05 s 的静音 clip —— 无头编辑器里没有声卡
        /// 也能 Create/Play，免得测试因为"这台机器没音频设备"而红；真正的时长由 DurationMs 驱动。
        /// </summary>
        sealed class FakeTtsTransport : ITtsTransport
        {
            public readonly List<string> Texts = new List<string>();
            public bool Unavailable;
            public double SentenceMs = 120;

            public int SynthCalls
            {
                get { return Texts.Count; }
            }

            public IEnumerator Synth(string text, Action<TtsReply> done)
            {
                yield return null;   // 真的跨一帧：逐句出现才有可观察的时间点
                Texts.Add(text);
                if (Unavailable)
                {
                    done(TtsReply.Unavailable("503 语音服务没起来"));
                    yield break;
                }
                done(TtsReply.VoiceOf(new VoiceRef
                {
                    AudioUrl = "/mas3d/api/voice/fake-" + Texts.Count + ".wav",
                    DurationMs = SentenceMs,
                }));
            }

            /// <summary>clip 交出去之前的钩子：用例靠它在"已经回调、协程还没恢复"的那一格里停播。</summary>
            public Action<AudioClip> InsideLoad;

            public IEnumerator LoadAudio(string url, Action<AudioClip> onOk, Action<string> onFail)
            {
                yield return null;
                var clip = AudioClip.Create("fake-tts-" + url, 2205, 1, 44100, false);
                if (InsideLoad != null) InsideLoad(clip);
                onOk(clip);
            }
        }
    }
}
