using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Mas3D.Conversation;
using Mas3D.Net;
using Mas3D.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Mas3D.Tests.PlayMode
{
    /// <summary>
    /// 无头冒烟：真起一个 mock-bridge（happy 剧本），从面板输入走到她的回复，一条链路到底。
    ///
    /// 它不只是测 BridgeClient：装配方式跟场景里一致——PanelSettings/UXML 取自 Assets/UI，
    /// 桥接由 DialogueBridge 按 BridgeSettings 建客户端再接给 ConversationController，
    /// 所以场景接线漏了一环，这里也会红。
    ///
    /// Debug.Log 在 PlayMode 里不进 NUnit XML，Unity 的 CLI 也把测试进程的日志收在别处；
    /// 所以成功标记额外落一行到 apps/unity/Logs/smoke-marker.txt，tools/unity.ps1 smoke 读它。
    /// </summary>
    public sealed class BridgeSmokeTests
    {
        const string SayLine = "晚上好";
        const float Deadline = 15f;
        const int GoneTimeoutMs = 5000;
        const string MarkerRelative = "Logs/smoke-marker.txt";

        [UnityTest]
        public IEnumerator OneTurnAgainstTheRealMockBridge()
        {
            var port = MockBridgeProcess.FindFreePort();
            var token = "smoke-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            MockBridgeProcess mock = null;
            DialoguePanelTests.DialoguePanelHarness harness = null;
            GameObject bridgeGo = null;
            GameObject offlineGo = null;
            BridgeClient client = null;
            string replyText = null;
            string replyTurnId = null;

            Action<ServerFrame> onFrame = frame =>
            {
                if (frame == null || frame.Type != "reply") return;
                replyText = frame.Text;
                replyTurnId = frame.TurnId;
            };

            try
            {
                mock = MockBridgeProcess.Start("happy", port, token);
                harness = DialoguePanelTests.DialoguePanelHarness.Create();
                var controller = harness.AddController(null);

                // 离线路径：没有令牌就不连，状态行说清楚，面板照常可用（不弹错误、不抛异常）。
                // 机器上可能真的有令牌文件，所以这里把“读文件”换成空，模拟文件不在。
                DialogueBridge.TokenFileReader = () => string.Empty;
                offlineGo = new GameObject("OfflineBridge");
                var offline = offlineGo.AddComponent<DialogueBridge>();
                offline.Panel = harness.Panel;
                offline.Settings = new BridgeSettings { Url = "ws://127.0.0.1:1", Token = "   " };
                Assert.IsFalse(offline.Connect(), "没有令牌时不该开连接");
                Assert.IsNull(offline.Client, "没有令牌时不该留下客户端");
                var statusLabel = harness.Document.rootVisualElement.Q<Label>("status");
                Assert.IsNotNull(statusLabel, "面板必须有 status 行");
                Assert.AreEqual(DialogueBridge.MissingTokenCopy, statusLabel.text, "状态行必须说明缺少令牌");
                Assert.AreEqual(DialoguePanel.IdleCopy, harness.Panel.LogText, "离线时日志区显示待机文案");
                WriteMarker("[ui] idle copy on screen: " + DialoguePanel.IdleCopy);
                DialogueBridge.TokenFileReader = null;
                offlineGo.SetActive(false);   // 别让它的 Start 再去连一次

                bridgeGo = new GameObject("DialogueBridge");
                var bridge = bridgeGo.AddComponent<DialogueBridge>();
                bridge.Controller = controller;
                bridge.Panel = harness.Panel;
                bridge.Settings = new BridgeSettings { Url = mock.Url, Token = token };
                Assert.IsTrue(bridge.Connect(), "DialogueBridge 必须把客户端建出来（令牌已在 Settings 里）");
                client = bridge.Client;
                Assert.IsNotNull(client, "DialogueBridge.Client 不能是空的");
                Assert.AreSame(client, controller.Client, "客户端必须接给 ConversationController");
                client.FrameReceived += onFrame;

                // 等连接：每帧 pump，和场景里的 Update 一个节奏。
                yield return PumpUntil(() => client.State == BridgeState.Connected, client);
                Assert.AreEqual(BridgeState.Connected, client.State, "15 秒内没连上 mock bridge，进程输出：\n" + mock.Tail);

                // 再放一帧：控制器在 Update 里订阅 FrameReceived，别让 ack 在没有订阅者的时候被 Pump 掉。
                yield return null;

                Assert.IsTrue(controller.Submit(SayLine), "这一句必须被接受");
                yield return PumpUntil(
                    () => controller.Phase == TurnPhase.Answered || controller.Phase == TurnPhase.Failed,
                    client);

                Assert.AreEqual(TurnPhase.Answered, controller.Phase,
                    "这一轮必须以回复收场，面板上现在写着：\n" + harness.Panel.LogText);
                Assert.IsNotNull(replyText, "没收到 reply 帧");
                Assert.IsNotEmpty(replyText, "reply.text 不能是空的");
                StringAssert.Contains(SayLine, replyText, "mock 的回复里会复述这一句");
                Assert.IsNotEmpty(controller.PendingTurnId, "ack 之后控制器要记住 turnId");
                Assert.AreEqual(controller.PendingTurnId, replyTurnId, "reply 的 turnId 必须跟 ack 的一致");
                StringAssert.Contains(replyText, harness.Panel.LogText, "回复要真的画进面板");

                var marker = "[smoke] turn ok turnId=" + controller.PendingTurnId;
                WriteMarker(marker);
            }
            finally
            {
                if (client != null) client.FrameReceived -= onFrame;
                if (bridgeGo != null) UnityEngine.Object.Destroy(bridgeGo);
                if (offlineGo != null) UnityEngine.Object.Destroy(offlineGo);
                DialogueBridge.TokenFileReader = null;
                if (harness != null) harness.Dispose();
                if (mock != null) mock.Dispose();
            }

            Assert.IsTrue(PortClosed(port, GoneTimeoutMs),
                "Dispose 之后 mock 进程必须已经退出：127.0.0.1:" + port + " 还能连上");
        }

        /// <summary>
        /// 一张能看见面板的静帧。编辑器批处理里的 CliPlayer.Capture 走 camera.Render()，
        /// UI Toolkit 的覆盖层不在相机输出里；真场景 + 真面板的截图只能在 PlayMode 里拍。
        /// [Explicit]：不进默认套件（26 + 1 还是 27 条），由 tools\unity.ps1 capture 单独点跑。
        /// </summary>
        [UnityTest]
        [Explicit]
        public IEnumerator UiSceneShot()
        {
            Assert.IsTrue(Application.CanStreamedLevelBeLoaded("Main"),
                "主场景不在 Build Settings 里，先跑 tools\\unity.ps1 player");
            SceneManager.LoadScene("Main", LoadSceneMode.Single);
            yield return null;
            yield return null;

            DialoguePanel panel = null;
            var limit = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < limit)
            {
                panel = UnityEngine.Object.FindAnyObjectByType<DialoguePanel>();
                if (panel != null && panel.LogText == DialoguePanel.IdleCopy) break;
                yield return null;
            }
            Assert.IsNotNull(panel, "主场景里必须有 DialoguePanel");
            Assert.AreEqual(DialoguePanel.IdleCopy, panel.LogText, "待机文案要真的画在面板上");

            var shots = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Shots"));
            Directory.CreateDirectory(shots);
            var file = Path.Combine(shots, "ui-scene.png");
            if (File.Exists(file)) File.Delete(file);

            // 批处理模式里 ScreenCapture 不可靠（WaitForEndOfFrame 都不触发），所以走渲染纹理：
            // 相机和面板画进同一张 RT，再 ReadPixels —— 跟 CliPlayer.Capture 同一条路，多一个面板。
            var camera = UnityEngine.Object.FindAnyObjectByType<Camera>();
            Assert.IsNotNull(camera, "主场景里必须有相机");
            var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var original = panel.Document.panelSettings;
            var settings = UnityEngine.Object.Instantiate(original);
            settings.name = original.name + "（截图副本）";
            settings.targetTexture = rt;
            panel.Document.panelSettings = settings;
            camera.targetTexture = rt;

            for (var i = 0; i < 8; i++) yield return null;

            RenderTexture.active = rt;
            var texture = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            texture.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(file, texture.EncodeToPNG());

            panel.Document.panelSettings = original;
            camera.targetTexture = null;
            UnityEngine.Object.Destroy(texture);
            UnityEngine.Object.Destroy(settings);
            rt.Release();
            UnityEngine.Object.Destroy(rt);

            Assert.IsTrue(File.Exists(file) && new FileInfo(file).Length > 5000, "截图没写出来：" + file);
            WriteMarker("[shot] ui-scene " + file + " bytes=" + new FileInfo(file).Length);
        }

        /// <summary>每帧 client.Pump() 一次，直到 done 成立或者超时（超时交给后面的断言报）。</summary>
        static IEnumerator PumpUntil(Func<bool> done, BridgeClient client)
        {
            var limit = Time.realtimeSinceStartup + Deadline;
            while (Time.realtimeSinceStartup < limit)
            {
                if (client != null) client.Pump();
                if (done()) yield break;
                yield return null;
            }
            if (client != null) client.Pump();
        }

        /// <summary>连一下试试：连不上（连接被拒）就说明监听者没了。</summary>
        static bool PortClosed(int port, int timeoutMs)
        {
            var limit = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < limit)
            {
                try
                {
                    using (var probe = new TcpClient())
                    {
                        probe.Connect(IPAddress.Loopback, port);
                    }
                }
                catch (SocketException)
                {
                    return true;
                }
                System.Threading.Thread.Sleep(100);
            }
            return false;
        }

        static void WriteMarker(string line)
        {
            var path = MarkerPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.AppendAllText(path, line + Environment.NewLine);
            Debug.Log(line);
        }

        static string MarkerPath()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", MarkerRelative));
        }
    }
}
