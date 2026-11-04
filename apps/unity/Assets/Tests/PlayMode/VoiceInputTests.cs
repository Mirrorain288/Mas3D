using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Mas3D.Net;
using Mas3D.UI;
using Mas3D.Voice;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Mas3D.Tests
{
    /// <summary>
    /// 按住 V 说话的状态机（Task 4）。麦克风与网络都换成假实现：
    /// <see cref="FakeRecorder"/> 给固定样本，<see cref="FakePoster"/> 记录“有没有发、发的什么”，
    /// 于是“松手 → 编码 → 识别 → 写输入框”这条链在无麦克风、无桥接的机器上也能断言。
    ///
    /// 键盘不合成：Update 只负责把 GetKeyDown/GetKeyUp 转给 Press/Release，
    /// 用例直接调这两个方法（真实按键的按键重复在 Update 层天然只有一次 GetKeyDown）。
    /// </summary>
    public class VoiceInputTests
    {
        const int Rate = 16000;

        DialoguePanelTests.DialoguePanelHarness _panel;
        GameObject _voiceGo;
        VoiceInput _voice;
        FakeRecorder _recorder;
        FakePoster _poster;

        [TearDown]
        public void TearDown()
        {
            if (_voiceGo != null) UnityEngine.Object.Destroy(_voiceGo);
            if (_panel != null) _panel.Dispose();
            _voiceGo = null;
            _panel = null;
            _voice = null;
            _recorder = null;
            _poster = null;
        }

        IEnumerator Build(float[] samples = null)
        {
            _panel = DialoguePanelTests.DialoguePanelHarness.Create();
            yield return null;   // 让 UIDocument 把 UXML 挂上并跑一帧布局

            _recorder = new FakeRecorder { Samples = samples ?? Loud(Seconds(0.5f)) };
            _poster = new FakePoster();

            _voiceGo = new GameObject("VoiceInputTests");
            _voice = _voiceGo.AddComponent<VoiceInput>();
            _voice.KeyboardInput = false;    // 键盘分支只在 Update 用例里走
            _voice.Panel = _panel.Panel;
            _voice.Recorder = _recorder;
            _voice.Poster = _poster.Send;
        }

        // ---- 主路径 -----------------------------------------------------------

        [UnityTest]
        public IEnumerator HoldThenReleaseWritesTheTranscriptWithoutSubmitting()
        {
            yield return Build();
            var submitted = new List<string>();
            _panel.Panel.Submitted += submitted.Add;
            _panel.Panel.Interactable = false;   // 语音层不许碰它
            _panel.Panel.SetThinking(true);      // 也不许碰思考指示

            _voice.Press();
            Assert.IsTrue(_voice.Listening, "按下 V 之后必须处于录音状态");
            Assert.AreEqual(VoiceInput.ListeningCopy, _voice.LastStatus);
            Assert.AreEqual(VoiceInput.ListeningCopy, StatusText());

            _voice.Release();
            Assert.IsFalse(_voice.Listening, "松开之后不再录音");
            yield return WaitUntil(() => _panel.Panel.InputText == "晚上好", "识别结果没有写进输入框");

            Assert.AreEqual("晚上好", _panel.Panel.InputText, "识别结果写进输入框");
            Assert.AreEqual(0, submitted.Count, "写进输入框不等于发送：Submitted 不能触发");
            Assert.AreEqual(DialoguePanel.IdleCopy, _panel.Panel.LogText,
                "不能把她的话/玩家的话追加进消息流 —— 那是发送才做的事");
            Assert.IsFalse(_panel.Panel.Interactable, "输入框可用状态归控制器管，语音层不碰");
            Assert.AreEqual(DisplayStyle.Flex, ThinkingDisplay(), "思考指示归控制器管，语音层不碰");
            Assert.AreEqual(1, _poster.Calls, "一次松手只发一次识别");
            AssertWav(_poster.Wav, _recorder.Samples.Length, Rate);
        }

        [UnityTest]
        public IEnumerator AReleaseWithoutAHoldDoesNothing()
        {
            yield return Build();

            _voice.Release();
            yield return null;

            Assert.AreEqual(0, _recorder.Starts, "没按过就不该开始录音");
            Assert.AreEqual(0, _recorder.Stops, "没按过就不该停止录音");
            Assert.AreEqual(0, _poster.Calls, "没录到东西就不该发请求");
            Assert.IsNull(_voice.LastStatus, "没按过就不该改状态词");
            Assert.AreEqual(string.Empty, _panel.Panel.InputText);
        }

        [UnityTest]
        public IEnumerator AKeyRepeatDoesNotRestartTheRecording()
        {
            yield return Build();

            _voice.Press();
            _voice.Press();   // 按住时的按键重复
            yield return null;

            Assert.AreEqual(1, _recorder.Starts, "按键重复不能把录音重启");
            Assert.IsTrue(_voice.Listening);

            _voice.Release();
            yield return WaitUntil(() => _poster.Answered, "第二次按下把录音搞坏了：请求没发出去");

            Assert.AreEqual(1, _recorder.Stops);
            Assert.AreEqual(1, _poster.Calls);
        }

        [UnityTest]
        public IEnumerator ASecondClipReplacesTheTranscript()
        {
            yield return Build();

            _voice.Press();
            _voice.Release();
            yield return WaitUntil(() => _poster.Answered, "第一段没有发出去");
            Assert.AreEqual("晚上好", _panel.Panel.InputText);

            _poster.Text = "第二句";
            _voice.Press();
            _voice.Release();
            yield return WaitUntil(() => _panel.Panel.InputText == "第二句", "第二段没有覆盖输入框");

            Assert.AreEqual(2, _poster.Calls, "两段录音 = 两次识别");
            Assert.AreEqual(2, _recorder.Starts);
            Assert.AreEqual(2, _recorder.Stops);
        }

        [UnityTest]
        public IEnumerator TheWavBuiltFromTheRecorderSampleRateIsSent()
        {
            yield return Build(Loud(Seconds(0.5f)));
            _recorder.SampleRate = 8000;   // 编码器必须用 Stop 给的采样率，而不是写死 16000

            _voice.Press();
            _voice.Release();
            yield return WaitUntil(() => _poster.Answered, "请求没有发出去");

            AssertWav(_poster.Wav, Seconds(0.5f), 8000);
        }

        // ---- 回执 -------------------------------------------------------------

        [UnityTest]
        public IEnumerator ASuccessfulTranscriptWritesTheReadyCopy()
        {
            yield return Build();

            _voice.Press();
            _voice.Release();
            yield return WaitUntil(() => _panel.Panel.InputText == "晚上好", "识别结果没有写进输入框");

            Assert.AreEqual("已填入，按 Enter 发送", _voice.LastStatus,
                "填入之后状态行要告诉玩家按 Enter 发送");
            Assert.AreEqual("已填入，按 Enter 发送", StatusText(), "状态行要显示同一句");
            Assert.AreNotEqual(DialoguePanel.IdleCopy, _voice.LastStatus,
                "待机文案归 ConversationController：语音层不写");
        }

        [UnityTest]
        public IEnumerator ATypedDraftSurvivesAndTheTranscriptIsAppended()
        {
            yield return Build();
            _panel.Panel.InputText = "草稿";

            _voice.Press();
            _voice.Release();
            _panel.Panel.InputText = "我打的字";   // 请求已经发出，玩家又改了几笔
            yield return WaitUntil(() => _poster.Answered, "识别没有走完");

            Assert.AreEqual("我打的字 晚上好", _panel.Panel.InputText,
                "玩家改过的草稿不能被识别结果覆盖：追加，用单个空格隔开");
        }

        /// <summary>
        /// 请求飞行途中玩家自己按 Enter 把这一句发走了（面板的 Submit 路径会清空输入框）。
        /// 迟到的识别结果既不能把已经发出去的那一句写回框里（再按一次 Enter 就是重复发送），
        /// 也不能把状态词改回“已填入”。
        /// </summary>
        [UnityTest]
        public IEnumerator ALateTranscriptIsDroppedAfterThePlayerSends()
        {
            yield return Build();
            var deferred = new DeferredPoster();
            _voice.Poster = deferred.Send;
            _voice.Bind(_panel.Panel, new BridgeSettings());   // 场景里 RoomBootstrap 就是这么接的
            var input = _panel.Document.rootVisualElement.Q<TextField>("input");

            _voice.Press();
            _voice.Release();
            Assert.AreEqual(1, deferred.Calls, "松手之后必须发出一次识别请求");

            // 结果还没回来，玩家先把这一句发了出去：面板清空输入框、Submitted 通知订阅者。
            // 面板只在“有人接走”时才清空（DialoguePanel.cs:376-379）：这里补一个订阅者，
            // 等价于场景里 ConversationController 接手这一句。
            _panel.Panel.Submitted += _ => { };
            _panel.Panel.InputText = "晚上好";
            DialoguePanelTests.DialoguePanelHarness.PressEnter(input);
            Assert.AreEqual(string.Empty, _panel.Panel.InputText, "前提：提交之后输入框由面板清空");

            deferred.Ok[0](new AsrResult("晚上好", 500, "small"));
            yield return null;

            Assert.AreEqual(string.Empty, _panel.Panel.InputText,
                "玩家已经发走了这一句：迟到的识别结果不许写回输入框（写回 = 下一次回车重复发送）");
            Assert.AreNotEqual(VoiceInput.ReadyCopy, _voice.LastStatus,
                "玩家已经走了：迟到的识别结果也不许把状态词改回“已填入”");
            Assert.AreNotEqual(VoiceInput.ReadyCopy, StatusText(), "状态行同样不许被迟到回执改写");
        }

        /// <summary>
        /// 请求飞行途中玩家把手动打的草稿清空了（没走 Submitted）：快照对不上，但框是空的 ——
        /// 这时要覆盖而不是追加，否则识别结果前面会多一个前导空格。
        /// </summary>
        [UnityTest]
        public IEnumerator ABlankBoxIsFilledWithoutALeadingSpace()
        {
            yield return Build();
            var deferred = new DeferredPoster();
            _voice.Poster = deferred.Send;

            _panel.Panel.InputText = "草稿";
            _voice.Press();
            _voice.Release();
            Assert.AreEqual(1, deferred.Calls, "松手之后必须发出一次识别请求");

            _panel.Panel.InputText = string.Empty;   // 玩家自己清空，不是发送
            deferred.Ok[0](new AsrResult("晚上好", 500, "small"));
            yield return null;

            Assert.AreEqual("晚上好", _panel.Panel.InputText,
                "框空着的时候要覆盖：追加会写出“ 晚上好”，前导空格会被下一次回车一起发出去");
        }

        [UnityTest]
        public IEnumerator ALateCallbackFromASupersededCaptureIsDropped()
        {
            yield return Build();
            var deferred = new DeferredPoster();
            _voice.Poster = deferred.Send;

            _voice.Press();
            _voice.Release();
            _voice.Press();
            _voice.Release();
            Assert.AreEqual(2, deferred.Calls, "两段录音 = 两次请求");

            deferred.Ok[1](new AsrResult("第二句", 500, "small"));
            Assert.AreEqual("第二句", _panel.Panel.InputText);

            deferred.Ok[0](new AsrResult("第一句", 500, "small"));   // 旧请求的回执最后才到
            Assert.AreEqual("第二句", _panel.Panel.InputText, "被取代的采集回执必须丢弃");
            Assert.AreEqual("已填入，按 Enter 发送", _voice.LastStatus,
                "迟到的旧回执也不能改状态词");
        }

        [UnityTest]
        public IEnumerator ACallbackDeliveredAfterDisableIsIgnored()
        {
            yield return Build();
            var deferred = new DeferredPoster();
            _voice.Poster = deferred.Send;

            _voice.Press();
            _voice.Release();
            _voiceGo.SetActive(false);   // 相当于场景卸载/组件关掉
            yield return null;

            deferred.Ok[0](new AsrResult("晚上好", 500, "small"));

            Assert.AreEqual(string.Empty, _panel.Panel.InputText, "禁用之后到的回执不能写进输入框");
            Assert.AreEqual(VoiceInput.ListeningCopy, _voice.LastStatus, "禁用之后到的回执也不能改状态词");
        }

        // ---- 没听清 -----------------------------------------------------------

        [UnityTest]
        public IEnumerator AShortClipAsksToRepeatAndSkipsThePoster()
        {
            yield return Build(Loud(Seconds(0.1f)));   // 0.1 s < MinSeconds
            _panel.Panel.InputText = "草稿";

            _voice.Press();
            _voice.Release();
            yield return null;

            Assert.AreEqual(VoiceInput.EmptyCopy, _voice.LastStatus);
            Assert.AreEqual(VoiceInput.EmptyCopy, StatusText(), "状态行要显示同一句");
            Assert.AreEqual(0, _poster.Calls, "短于 0.3 s 不该发请求");
            Assert.AreEqual("草稿", _panel.Panel.InputText, "没听清时不要动玩家已经打好的草稿");
        }

        [UnityTest]
        public IEnumerator ARecorderThatReturnedNoSamplesAsksToRepeat()
        {
            yield return Build(Array.Empty<float>());

            _voice.Press();
            _voice.Release();
            yield return null;

            Assert.AreEqual(VoiceInput.EmptyCopy, _voice.LastStatus);
            Assert.AreEqual(0, _poster.Calls, "没有样本就不该编码/发送");
        }

        [UnityTest]
        public IEnumerator AnEmptyTranscriptAsksToRepeatWithoutClobberingTheDraft()
        {
            yield return Build();
            _panel.Panel.InputText = "草稿";
            _poster.Text = string.Empty;

            _voice.Press();
            _voice.Release();
            yield return WaitUntil(() => _poster.Answered, "空识别也必须走完一次请求");

            Assert.AreEqual(VoiceInput.EmptyCopy, _voice.LastStatus);
            Assert.AreEqual("草稿", _panel.Panel.InputText, "空识别不能用空串盖掉草稿");
            Assert.AreEqual(1, _poster.Calls);
        }

        // ---- 失败 -------------------------------------------------------------

        [UnityTest]
        public IEnumerator ARecorderStartFailureShowsItsError()
        {
            yield return Build();
            _recorder.StartResult = false;
            _recorder.ErrorText = "没有可用的麦克风";

            _voice.Press();
            yield return null;

            Assert.IsFalse(_voice.Listening, "Start 失败就不是在听");
            Assert.AreEqual("没有可用的麦克风", _voice.LastStatus, "recorder 有话说就用它的话");
            Assert.AreEqual("没有可用的麦克风", StatusText());

            _voice.Release();
            Assert.AreEqual(0, _recorder.Stops, "没开始录就不该 Stop");
            Assert.AreEqual(0, _poster.Calls);
        }

        [UnityTest]
        public IEnumerator ARecorderStartFailureWithoutAMessageFallsBackToTheFailCopy()
        {
            yield return Build();
            _recorder.StartResult = false;
            _recorder.ErrorText = string.Empty;

            _voice.Press();

            Assert.AreEqual(VoiceInput.FailCopy, _voice.LastStatus);
        }

        [UnityTest]
        public IEnumerator AFailedRequestShowsTheFailCopyAndKeepsTheDraft()
        {
            yield return Build();
            _panel.Panel.InputText = "草稿";
            _poster.Fail = true;

            _voice.Press();
            _voice.Release();
            yield return WaitUntil(() => _poster.Answered, "失败也必须走完一次请求");

            Assert.AreEqual(VoiceInput.FailCopy, _voice.LastStatus);
            Assert.AreEqual(VoiceInput.FailCopy, StatusText());
            Assert.AreEqual("草稿", _panel.Panel.InputText);
        }

        [UnityTest]
        public IEnumerator AReleaseWithoutAReadyPanelStillStopsTheRecorder()
        {
            yield return Build();
            _voice.Press();
            _voice.Panel = null;
            LogAssert.Expect(LogType.Warning, new Regex("VoiceInput"));

            _voice.Release();
            yield return null;

            Assert.AreEqual(1, _recorder.Stops, "面板掉了也要 Stop，别把麦克风留在打开状态");
            Assert.IsFalse(_recorder.Recording);
            Assert.AreEqual(VoiceInput.ListeningCopy, _voice.LastStatus, "这一条 take 直接丢掉，不改状态词");
        }

        [UnityTest]
        public IEnumerator AMissingPosterAndClientShowTheFailCopy()
        {
            yield return Build();
            _voice.Poster = null;
            _voice.Client = null;

            _voice.Press();
            _voice.Release();
            yield return null;

            Assert.AreEqual(VoiceInput.FailCopy, _voice.LastStatus);
            Assert.AreEqual(VoiceInput.FailCopy, StatusText());
            Assert.AreEqual(string.Empty, _panel.Panel.InputText);
            Assert.AreEqual(0, _poster.Calls, "没有发送层就不该有请求");
        }

        // ---- 接线 -------------------------------------------------------------

        [UnityTest]
        public IEnumerator BindPointsTheClientAtTheAsrRouteFromTheBridgeSettings()
        {
            _panel = DialoguePanelTests.DialoguePanelHarness.Create();
            yield return null;

            _voiceGo = new GameObject("VoiceInputBind");
            _voice = _voiceGo.AddComponent<VoiceInput>();
            _voice.KeyboardInput = false;

            _voice.Bind(_panel.Panel, new BridgeSettings { Url = "ws://127.0.0.1:9911/mas3d/ws", Token = "tok" });

            Assert.AreSame(_panel.Panel, _voice.Panel);
            Assert.IsInstanceOf<MicrophoneRecorder>(_voice.Recorder, "没注入录音器时 Bind 要装一个真麦克风录音器");
            Assert.IsNotNull(_voice.Client, "Bind 要备好 ASR 客户端");
            Assert.AreEqual("http://127.0.0.1:9911/mas3d/api/asr", _voice.Client.Endpoint,
                "ASR 端点跟着桥接的 host/port 走");
            Assert.AreEqual("tok", _voice.Client.Token);

            _voice.Bind(_panel.Panel, new BridgeSettings());
            Assert.AreEqual(AsrClient.DefaultEndpoint, _voice.Client.Endpoint, "默认设置回到默认端点");
            Assert.AreEqual(string.Empty, _voice.Client.Token);
        }

        [UnityTest]
        public IEnumerator UpdateWithoutWiringWarnsOnceAndDoesNotThrow()
        {
            _voiceGo = new GameObject("VoiceInputUnwired");
            _voice = _voiceGo.AddComponent<VoiceInput>();   // Panel/Recorder 都是 null，KeyboardInput 默认为 true

            LogAssert.Expect(LogType.Warning, new Regex("VoiceInput"));
            yield return null;
            yield return null;   // 第二帧不能再警告一次

            Assert.IsFalse(_voice.Listening);
            Assert.DoesNotThrow(() => { _voice.Press(); _voice.Release(); });
        }

        [UnityTest]
        public IEnumerator OnDisableMidHoldStopsTheRecorder()
        {
            yield return Build();

            _voice.Press();
            Assert.IsTrue(_recorder.Recording, "按下之后录音器要在录");

            _voiceGo.SetActive(false);
            yield return null;

            Assert.IsFalse(_voice.Listening, "禁用之后不再算在听");
            Assert.AreEqual(1, _recorder.Stops, "禁用时要 Stop，别把麦克风留在录音状态");
            Assert.IsFalse(_recorder.Recording);
        }

        // ---- 焦点 -------------------------------------------------------------

        [UnityTest]
        public IEnumerator ThePanelKnowsWhenItsInputBoxHasFocus()
        {
            yield return Build();
            var root = _panel.Document.rootVisualElement;

            var input = root.Q<TextField>("input");
            Assert.IsNotNull(input);
            input.Focus();
            yield return null;
            Assert.IsTrue(_panel.Panel.IsInputFocused, "焦点在输入框（或它的 TextElement）上时必须为 true");

            var send = root.Q<Button>("send");
            Assert.IsNotNull(send);
            send.Focus();
            yield return null;
            Assert.IsFalse(_panel.Panel.IsInputFocused, "焦点换了地方就必须为 false");

            var bareGo = new GameObject("DialoguePanelWithoutDocument");
            var bare = bareGo.AddComponent<DialoguePanel>();
            Assert.IsFalse(bare.IsInputFocused, "Document 没挂上时 IsInputFocused 必须是 false");
            UnityEngine.Object.Destroy(bareGo);
        }

        /// <summary>
        /// 焦点在输入框里时，键盘路径只被挡住“起录”这一半：已经在录的那一段仍然由松手/兜底结束。
        /// 早先的实现是整段早退，录音中鼠标点进输入框后 Release 被跳过，麦克风要等焦点挪开才关。
        ///
        /// 批处理里合成不了按键，所以这里用 Press() 接缝起录，再让 Update 跑一帧：
        /// 键盘上并没有真的按住 V，兜底分支据此当作“已经松手”，于是照常收尾、把这一段送出去。
        /// </summary>
        [UnityTest]
        public IEnumerator AFocusedPanelLetsTheKeyPathEndACapture()
        {
            yield return Build();
            _voice.KeyboardInput = true;
            var input = _panel.Document.rootVisualElement.Q<TextField>("input");
            input.Focus();
            yield return null;
            Assert.IsTrue(_panel.Panel.IsInputFocused, "前置条件：输入框拿着焦点");

            _voice.Press();          // Press/Release 接缝照旧可用
            Assert.IsTrue(_voice.Listening, "接缝起录不该被焦点挡住");
            yield return null;       // Update 跑过：兜底分支收掉这一段

            Assert.AreEqual(1, _recorder.Starts, "键盘路径不许另起一段录音");
            Assert.IsFalse(_voice.Listening, "焦点不该让录音拖着不停：这一段的键已经不在按");
            Assert.AreEqual(1, _recorder.Stops, "兜底分支要真的把设备关掉");
            yield return WaitUntil(() => _poster.Answered, "兜底收尾的那一段照常送出去识别");
        }

        // ---- 面板消失 / 按住期间提交 -------------------------------------------

        /// <summary>
        /// 录音中面板被销毁（场景换掉、UI 重建）：Ready() 从此为 false，键盘路径再也收不了尾。
        /// 早先的实现让 Update 在 !Ready() 时整段早退，麦克风一路录到 30 秒片段上限；
        /// 而且就算录满也不会有人收结果 —— 面板都没了。
        /// </summary>
        [UnityTest]
        public IEnumerator ADestroyedPanelEndsTheTakeInsteadOfRecordingForever()
        {
            yield return Build();
            _voice.KeyboardInput = true;   // 这一条走的是 Update 的兜底分支
            _voice.Press();
            Assert.IsTrue(_voice.Listening, "前置条件：正在录");

            _voice.Panel = null;           // 面板没了：Ready() 变 false
            LogAssert.Expect(LogType.Warning, new Regex("VoiceInput"));
            yield return null;             // Update 跑过

            Assert.IsFalse(_voice.Listening, "面板都没了就不能还算在听");
            Assert.AreEqual(1, _recorder.Stops, "必须把设备关掉，别录到 30 秒片段上限");
            Assert.IsFalse(_recorder.Recording);
            Assert.AreEqual(0, _poster.Calls, "没有面板就没人收结果，不要白发一次请求");
        }

        /// <summary>
        /// 按住期间玩家按 Enter 把上一句发走了（控制器会清空输入框），然后才松手。
        /// 松手时不能把“玩家已经翻篇”这件事忘掉：否则迟到的转写落进刚清空的框，
        /// 玩家再按一次回车就把同一句又发了一遍。早先的实现正是在 Release 里无条件清零。
        /// </summary>
        [UnityTest]
        public IEnumerator ASubmitMadeDuringTheHoldKeepsTheLateTranscriptOut()
        {
            yield return Build();
            var deferred = new DeferredPoster();
            _voice.Poster = deferred.Send;
            _voice.Bind(_panel.Panel, new BridgeSettings());
            var input = _panel.Document.rootVisualElement.Q<TextField>("input");

            _voice.Press();
            Assert.IsTrue(_voice.Listening, "前置条件：正在录");

            _panel.Panel.Submitted += _ => { };   // 场景里接手这一句的是 ConversationController
            _panel.Panel.InputText = "上一句";
            DialoguePanelTests.DialoguePanelHarness.PressEnter(input);
            Assert.AreEqual(string.Empty, _panel.Panel.InputText, "前提：提交之后输入框被清空");

            _voice.Release();
            Assert.AreEqual(1, deferred.Calls, "松手照常把这一段送出去识别");

            deferred.Ok[0](new AsrResult("晚到的转写", 500, "small"));
            yield return null;

            Assert.AreEqual(string.Empty, _panel.Panel.InputText,
                "按住期间已经发过一句：迟到的转写不许落进控制器刚清空的框（落进去 = 再回车重发）");
            Assert.AreNotEqual(VoiceInput.ReadyCopy, _voice.LastStatus, "状态词也不许改回“已填入”");
        }

        // ---- 小工具 -----------------------------------------------------------

        string StatusText()
        {
            var label = _panel.Document.rootVisualElement.Q<Label>("status");
            Assert.IsNotNull(label, "面板里必须有 status 标签");
            return label.text;
        }

        DisplayStyle ThinkingDisplay()
        {
            var label = _panel.Document.rootVisualElement.Q<Label>("thinking");
            Assert.IsNotNull(label, "面板里必须有 thinking 标签");
            return label.style.display.value;
        }

        static int Seconds(float seconds)
        {
            return Mathf.RoundToInt(seconds * Rate);
        }

        static float[] Loud(int count)
        {
            var samples = new float[count];
            for (var i = 0; i < samples.Length; i++) samples[i] = 0.5f;
            return samples;
        }

        static IEnumerator WaitUntil(Func<bool> done, string what)
        {
            for (var frame = 0; frame < 10 && !done(); frame++) yield return null;
            Assert.IsTrue(done(), "等待超时：" + what);
        }

        static void AssertWav(byte[] wav, int samples, int sampleRate)
        {
            Assert.IsNotNull(wav, "松手后必须把编码好的 WAV 交给发送层");
            Assert.AreEqual(WavEncoder.HeaderBytes + samples * 2, wav.Length, "WAV = 44 字节头 + 16-bit 单声道样本");
            Assert.AreEqual((byte)'R', wav[0]);
            Assert.AreEqual((byte)'I', wav[1]);
            Assert.AreEqual((byte)'F', wav[2]);
            Assert.AreEqual((byte)'F', wav[3]);
            Assert.AreEqual(sampleRate, ReadInt32(wav, 24), "头里的采样率必须来自 recorder.Stop");
            Assert.AreEqual(samples * 2, ReadInt32(wav, 40), "data 块长度 = 样本数 × 2");
            Assert.AreEqual(0x00, wav[44], "0.5f 编码成 16384（0x4000）的小端低字节");
            Assert.AreEqual(0x40, wav[45], "高位是 0x40");
        }

        static int ReadInt32(byte[] buffer, int offset)
        {
            return buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24);
        }

        sealed class FakeRecorder : IRecorder
        {
            public float[] Samples;
            public int SampleRate = Rate;
            public bool StartResult = true;
            public string ErrorText = string.Empty;
            public int Starts;
            public int Stops;

            public bool Recording { get; private set; }
            public string Error { get { return ErrorText; } }

            public bool Start()
            {
                Starts++;
                if (StartResult) Recording = true;
                return StartResult;
            }

            public float[] Stop(out int sampleRate)
            {
                Stops++;
                Recording = false;
                sampleRate = SampleRate;
                return Samples;
            }
        }

        /// <summary>把回执存下来、由用例按自己的顺序送达，用来制造“旧请求后到”。</summary>
        sealed class DeferredPoster
        {
            public readonly List<Action<AsrResult>> Ok = new List<Action<AsrResult>>();
            public int Calls;

            public IEnumerator Send(byte[] wav, Action<AsrResult> onOk, Action<string> onError)
            {
                Calls++;
                Ok.Add(onOk);
                yield break;   // 回执不进协程，用例手动送
            }
        }

        sealed class FakePoster
        {
            public int Calls;
            public byte[] Wav;
            public string Text = "晚上好";
            public bool Fail;
            public bool Answered;

            public IEnumerator Send(byte[] wav, Action<AsrResult> onOk, Action<string> onError)
            {
                Calls++;
                Wav = wav;
                yield return null;   // 先让用例看到“请求已发出、结果还没回来”
                Answered = true;
                if (Fail)
                {
                    if (onError != null) onError("桥接没有响应");
                }
                else if (onOk != null)
                {
                    onOk(new AsrResult(Text, 500, "small"));
                }
            }
        }
    }
}
