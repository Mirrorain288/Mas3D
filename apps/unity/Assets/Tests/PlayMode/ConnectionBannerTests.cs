using System.Collections;
using System.Text.RegularExpressions;
using Mas3D.Net;
using Mas3D.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Mas3D.Tests
{
    /// <summary>
    /// P6 Task 2 的提示条：文案是纯函数（直接断言字），组件只负责把 BridgeClient 的
    /// StateChanged / Failed 翻成那三句话。面板用真实的 Dialogue.uxml + DialoguePanelSettings 装配
    /// （复用 DialoguePanelTests 的 harness），最后一条才加载 Main 验布线——手搓元素的话
    /// UXML 写坏了测试照样绿。
    /// </summary>
    public class ConnectionBannerTests
    {
        [Test]
        public void FailureCopyNamesTheEndpointTheAttemptAndAstrBot()
        {
            var text = ConnectionBanner.TextFor(BridgeState.Failed, 3, 2.0f);

            StringAssert.Contains("8790", text, "失败文案要写出是哪个端口连不上");
            StringAssert.Contains("第 3 次", text, "失败文案要写出这是第几次重试");
            StringAssert.Contains("AstrBot", text, "失败文案要告诉玩家先去看 AstrBot 起没起");
            StringAssert.Contains("2s 后重试", text, "倒计时按秒取整");
            Assert.AreEqual("已连接", ConnectionBanner.TextFor(BridgeState.Connected, 0, 0f));
            StringAssert.Contains("正在连接 127.0.0.1:8790", ConnectionBanner.TextFor(BridgeState.Connecting, 0, 0f));
        }

        [UnityTest]
        public IEnumerator TheBannerShowsTheRetryThenHidesOnceConnected()
        {
            var harness = DialoguePanelTests.DialoguePanelHarness.Create();
            try
            {
                yield return null;
                var root = harness.Document.rootVisualElement;
                var element = root.Q<VisualElement>("connection-banner");
                Assert.IsNotNull(element, "Dialogue.uxml 必须有一节 connection-banner");
                Assert.IsNotNull(root.Q<Label>("connection-text"), "connection-banner 里必须有 Label connection-text");

                var banner = harness.Panel.gameObject.AddComponent<ConnectionBanner>();
                yield return null;
                Assert.IsFalse(banner.IsVisible, "还没绑客户端、什么都没发生时提示条不占地方");
                Assert.AreEqual(string.Empty, banner.Text);

                banner.ShowFailed(3, 2.0f);
                StringAssert.Contains("第 3 次", banner.Text, "失败要落到 connection-text 上");
                StringAssert.Contains("8790", banner.Text);
                StringAssert.Contains("AstrBot", banner.Text);
                Assert.IsTrue(banner.IsVisible, "失败时必须真的显示出来");
                Assert.AreEqual(DisplayStyle.Flex, element.style.display.value);

                banner.ShowConnected();
                Assert.AreEqual("已连接", banner.Text, "连上之后先给一句已连接");
                banner.Tick(Time.unscaledTime + ConnectionBanner.ConnectedHideSeconds + 0.1f);
                Assert.IsFalse(banner.IsVisible, "已连接停留 2 s 之后自己收起来");
                Assert.AreEqual(DisplayStyle.None, element.style.display.value);
                Assert.AreEqual(string.Empty, root.Q<Label>("connection-text").text, "收起来时字也清掉，下次不残留");
            }
            finally
            {
                harness.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator ARealFailedConnectionReachesTheBanner()
        {
            // 127.0.0.1:1 上没人听：走的是 BridgeClient 真实的 Failed 事件，不是手搓一个假客户端。
            var harness = DialoguePanelTests.DialoguePanelHarness.Create();
            BridgeClient client = null;
            var settings = new BridgeSettings
            {
                Url = "ws://127.0.0.1:1/mas3d/ws",
                Token = "test-token",
                BackoffSeconds = new[] { 0.2f, 0.2f },
            };
            try
            {
                yield return null;
                var banner = harness.Panel.gameObject.AddComponent<ConnectionBanner>();

                client = new BridgeClient();
                client.Configure(settings);
                client.Start();
                banner.Bind(client, settings);

                var deadline = Time.realtimeSinceStartup + 5f;
                while (Time.realtimeSinceStartup < deadline && banner.Text.IndexOf("连不上桥接", System.StringComparison.Ordinal) < 0)
                {
                    client.Pump();
                    yield return null;
                }

                StringAssert.Contains("连不上桥接 127.0.0.1:1", banner.Text, "绑定之后要能收到真实的失败");
                StringAssert.Contains("第 1 次", banner.Text, "第一次故障记第 1 次");
                Assert.IsTrue(banner.IsVisible);
            }
            finally
            {
                if (client != null) client.Dispose();
                harness.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator TheShippedSceneCarriesTheBannerAndALogLine()
        {
            SceneFlow.Require();
            // Boot 打的那行日志是 Task 2 的契约之一：一眼看出这局用的是哪个桥接、令牌从哪来。
            LogAssert.Expect(LogType.Log, new Regex(@"^\[boot\] bridge=127\.0\.0\.1:8790 token=(disk|set) selftest=none$"));

            yield return SceneFlow.LoadMain();
            try
            {
                var boot = UnityEngine.Object.FindAnyObjectByType<RoomBootstrap>();
                yield return SceneFlow.WaitForBoot(boot);

                var panel = UnityEngine.Object.FindAnyObjectByType<DialoguePanel>();
                Assert.IsNotNull(panel, "Main.unity 必须带 DialoguePanel");
                var banner = panel.GetComponent<ConnectionBanner>();
                Assert.IsNotNull(banner, "DialogueBridge.Connect 要把提示条挂到面板同一条 GameObject 上");

                yield return null;
                Assert.IsNotNull(panel.Document.rootVisualElement.Q<Label>("connection-text"),
                    "提示条要能找到 UXML 里的 connection-text");
            }
            finally
            {
                SceneFlow.UnloadMainIfLoaded();
            }
        }

        [UnityTest]
        public IEnumerator ConnectWiresTheBannerToTheClientItJustStarted()
        {
            // 这条盯的是 DialogueBridge.Connect 里那句 banner.Bind(client, settings)：
            // 删掉它，面板上只会剩一个没人通知的空组件——连接中不显示、失败也不显示。
            // 令牌走现成接缝 TokenFileReader（静态），不碰磁盘；连的是 127.0.0.1:1，必然连不上，
            // 但断言只要求"正在连接"这一帧，不需要等失败。
            var harness = DialoguePanelTests.DialoguePanelHarness.Create();
            var previousReader = DialogueBridge.TokenFileReader;
            DialogueBridge.TokenFileReader = () => "test-token";
            GameObject host = null;
            DialogueBridge bridge = null;
            try
            {
                yield return null;
                host = new GameObject("DialogueBridgeUnderTest");
                bridge = host.AddComponent<DialogueBridge>();
                bridge.Panel = harness.Panel;
                bridge.Settings = new BridgeSettings
                {
                    Url = "ws://127.0.0.1:1/mas3d/ws",
                    Token = string.Empty,
                    BackoffSeconds = new[] { 0.2f, 0.2f },
                };

                Assert.IsTrue(bridge.Connect(), "接缝给了令牌就该连上（不是「未配置令牌」那条分支）");

                var banner = harness.Panel.GetComponent<ConnectionBanner>();
                Assert.IsNotNull(banner, "Connect 要把提示条挂到面板同一条 GameObject 上");
                Assert.IsTrue(banner.IsVisible, "Bind 之后要立刻摆出「正在连接」；没绑就一片空白");
                StringAssert.StartsWith("正在连接 127.0.0.1:1", banner.Text, "文案里的端点要跟着 settings.Url 走");
            }
            finally
            {
                DialogueBridge.TokenFileReader = previousReader;
                if (bridge != null) bridge.Shutdown();
                if (host != null) UnityEngine.Object.Destroy(host);
                harness.Dispose();
            }
        }
    }
}
