using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using Mas3D.Net;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mas3D.Tests
{
    /// <summary>
    /// Spec 5.3 / Review Focus 1：Unity 客户端接真桥接的线缆用例，全部走真 WebSocket，
    /// 不 mock 掉 System.Net.WebSockets：1008 关闭码、连接被拒这类行为只有在真线缆上才有意义。
    /// 前半截（连上收一整轮、错令牌、连不上、断线不发）对着 tools/mock-bridge.mjs 跑；
    /// 后半截（accept 完就关、会话中途掉线、坏帧、超长帧、Waiting 里再发）用 ScriptedBridge
    /// 起一个按剧本走的服务端 —— 这些剧本 mock-bridge 不出，也不该为一个用例去改它。
    ///
    /// 用例之间的清理顺序是“先客户端、后 mock”（finally 里就是这顺序），否则收掉 mock 会让客户端
    /// 看见一次断线并触发出现在下一条用例里的重连。
    /// </summary>
    public class BridgeClientTests
    {
        /// <summary>令牌文件里的真实格式是 64 位 hex，测试照这个形状造。</summary>
        const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const float Deadline = 10f;

        [UnityTest]
        public IEnumerator ConnectsAndReceivesAWholeTurn()
        {
            var mock = MockBridgeProcess.Start("happy", MockBridgeProcess.FindFreePort(), Token);
            var client = new BridgeClient();
            var frames = new List<ServerFrame>();
            var failures = new List<string>();
            var watch = Stopwatch.StartNew();
            try
            {
                UnityEngine.Debug.Log("[BridgeClientTests] " + mock.CommandLine + " -> " + mock.Url);
                client.Configure(new BridgeSettings { Url = mock.Url, Token = Token });
                client.FrameReceived += frames.Add;
                client.Failed += failures.Add;
                client.Start();

                yield return WaitUntil(() => client.State == BridgeState.Connected, client, mock, failures);
                var connectedAfter = watch.Elapsed;
                Assert.AreEqual(BridgeState.Connected, client.State, "没连上：" + string.Join(" | ", failures));

                Assert.IsTrue(client.SendSay("晚上好"), "连上之后 SendSay 必须成功");
                Assert.AreEqual(BridgeState.Waiting, client.State, "发出 say 之后进入 Waiting，等这一轮的 reply");

                yield return WaitUntil(() => frames.Exists(f => f.Type == "reply"), client, mock, failures);

                var ack = frames.Find(f => f.Type == "ack");
                var thinking = frames.Find(f => f.Type == "thinking");
                var reply = frames.Find(f => f.Type == "reply");
                Assert.IsNotNull(ack, "happy 场景必须先给 ack：" + Describe(frames));
                Assert.IsNotNull(thinking, "happy 场景要有 thinking：" + Describe(frames));
                Assert.IsNotNull(reply, "happy 场景要有 reply：" + Describe(frames));
                Assert.Less(frames.IndexOf(ack), frames.IndexOf(reply), "ack 必须排在 reply 之前：" + Describe(frames));
                Assert.IsNotEmpty(reply.Text, "reply.Text 不能为空");
                Assert.AreEqual(ack.TurnId, reply.TurnId, "同一轮的 ack / reply 必须共用 turnId");
                Assert.AreEqual(ack.TurnId, thinking.TurnId, "ack / thinking 必须共用 turnId");
                Assert.AreEqual(BridgeState.Connected, client.State, "收齐 reply 之后回到 Connected，才能再发下一句");

                UnityEngine.Debug.Log("[BridgeClientTests] happy: 连接 " + connectedAfter.TotalSeconds.ToString("F2") +
                    " s，整轮 " + watch.Elapsed.TotalSeconds.ToString("F2") + " s，帧顺序 " + Describe(frames));
            }
            finally
            {
                client.Dispose();
                mock.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator RejectsABadToken()
        {
            var mock = MockBridgeProcess.Start("happy", MockBridgeProcess.FindFreePort(), "secret");
            var client = new BridgeClient();
            var states = new List<BridgeState>();
            var failures = new List<string>();
            var watch = Stopwatch.StartNew();
            try
            {
                UnityEngine.Debug.Log("[BridgeClientTests] " + mock.CommandLine + " -> " + mock.Url);
                client.Configure(new BridgeSettings { Url = mock.Url, Token = "wrong" });
                client.StateChanged += states.Add;
                client.Failed += failures.Add;
                client.Start();

                yield return WaitUntil(() => failures.Exists(e => e.Contains("令牌")), client, mock, failures);
                var rejectedAfter = watch.Elapsed;
                var afterFailure = states.Count;

                // 1008 是终止性的：不许退避重连。多看半秒，确认状态不会再回 Connected。
                yield return Quiet(client, 0.5f);

                Assert.AreEqual(BridgeState.Closed, client.State, "坏令牌之后应当停在 Closed：" + Describe(states));
                for (var i = afterFailure; i < states.Count; i++)
                {
                    Assert.AreNotEqual(BridgeState.Connected, states[i], "1008 之后不该重连回 Connected：" + Describe(states));
                }
                Assert.IsFalse(states.Contains(BridgeState.Waiting), "坏令牌不该进入 Waiting：" + Describe(states));
                Assert.IsFalse(client.SendSay("晚上好"), "令牌被拒绝之后不该再发出帧");
                UnityEngine.Debug.Log("[BridgeClientTests] bad token: 拒绝耗时 " + rejectedAfter.TotalSeconds.ToString("F2") +
                    " s，状态序列 " + Describe(states) + "，错误 " + string.Join(" | ", failures));
            }
            finally
            {
                client.Dispose();
                mock.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator ReportsAnUnreachableBridge()
        {
            var client = new BridgeClient();
            var failures = new List<string>();
            var watch = Stopwatch.StartNew();
            try
            {
                UnityEngine.Debug.Log("[BridgeClientTests] unreachable: ws://127.0.0.1:6553/mas3d/ws");
                client.Configure(new BridgeSettings { Url = "ws://127.0.0.1:6553/mas3d/ws" });
                client.Failed += failures.Add;
                client.Start();

                yield return WaitUntil(() => failures.Exists(e => e.Contains("连接")), client, null, failures);
                var reportedAfter = watch.Elapsed;

                Assert.AreEqual(BridgeState.Connecting, client.State, "端口没人听时应当留在 Connecting（退避重试中）");
                Assert.IsFalse(client.SendSay("晚上好"), "没连上时 SendSay 必须返回 false");
                UnityEngine.Debug.Log("[BridgeClientTests] unreachable: 报错耗时 " + reportedAfter.TotalSeconds.ToString("F2") +
                    " s，错误 " + string.Join(" | ", failures));
            }
            finally
            {
                client.Dispose();
            }
        }

        [Test]
        public void DoesNotSendWhenDisconnected()
        {
            var client = new BridgeClient();
            try
            {
                Assert.IsFalse(client.SendSay("晚上好"), "没有 Start 时 SendSay 必须返回 false");
                client.Configure(new BridgeSettings { Url = "ws://127.0.0.1:6553/mas3d/ws" });
                Assert.IsFalse(client.SendSay("晚上好"), "Configure 之后、Start 之前仍然不该发");
                Assert.DoesNotThrow(() => client.Pump(), "空泵不该抛");
                Assert.DoesNotThrow(() => client.Stop(), "Stop 不该抛");
                Assert.DoesNotThrow(() => client.Stop(), "Stop 必须幂等");
                Assert.IsFalse(client.SendSay("晚上好"), "Stop 之后 SendSay 必须返回 false");
            }
            finally
            {
                client.Dispose();
                Assert.DoesNotThrow(() => client.Dispose(), "Dispose 必须幂等");
            }
        }

        /// <summary>
        /// review FIX 1：对面 accept 完立刻用非 1008 的关闭码（1000 / 1011）关掉。
        /// 这种对端永远连得上，如果每次连上都把退避清零，客户端就变成
        /// Connected → Connecting → Connected 每 0.5 s 一轮的重连风暴，而且每轮都报一条错误。
        /// 判据是错误条数（一次故障只报一条），外加“坏连接之后仍然能重连回一条好连接”。
        /// </summary>
        [UnityTest]
        public IEnumerator BacksOffWhenThePeerAcceptsThenCloses()
        {
            ScriptedBridge bridge = null;
            var client = new BridgeClient();
            var frames = new List<ServerFrame>();
            var failures = new List<string>();
            try
            {
                bridge = new ScriptedBridge(session =>
                {
                    if (session.Index == 1)
                    {
                        session.Close(1000, "说完就关");
                        return;
                    }
                    if (session.Index == 2)
                    {
                        session.Close(1011, "服务端错误");
                        return;
                    }

                    var say = session.ReadText(5000);
                    if (say == null) return;
                    session.SendText("{\"type\":\"ack\",\"turnId\":\"turn-recovered\"}");
                    session.SendText("{\"type\":\"reply\",\"turnId\":\"turn-recovered\",\"text\":\"重连回来了。\"}");
                });

                UnityEngine.Debug.Log("[BridgeClientTests] accept-then-close: " + bridge.Url);
                client.Configure(new BridgeSettings { Url = bridge.Url });
                client.FrameReceived += frames.Add;
                client.Failed += failures.Add;
                client.Start();

                yield return WaitUntil(() => client.State == BridgeState.Connected && bridge.Accepted >= 3, client, null, failures);

                Assert.GreaterOrEqual(bridge.Accepted, 3, "两条坏连接之后必须重连到第三条好连接");
                Assert.AreEqual(1, failures.Count,
                    "accept 完立刻关只该报一次错误，不是每次重连都报一遍（每轮都报就是重连风暴）：" + Describe(failures));

                Assert.IsTrue(client.SendSay("在吗"), "重连回来的连接必须还能发");
                yield return WaitUntil(() => frames.Exists(f => f.Type == "reply"), client, null, failures);
                Assert.AreEqual(BridgeState.Connected, client.State, "收齐 reply 之后回到 Connected");
                UnityEngine.Debug.Log("[BridgeClientTests] accept-then-close: 连接 " + bridge.Accepted + " 次，错误 " + Describe(failures));
            }
            finally
            {
                client.Dispose();
                if (bridge != null) bridge.Dispose();
            }
        }

        /// <summary>
        /// review FIX 1 的正面用例：连接活得够久（超过稳定窗口）之后掉线，退避必须清零、
        /// 自己重连回 Connected，并且这一整段只报一条“断开”。
        /// </summary>
        [UnityTest]
        public IEnumerator ReconnectsAfterAMidSessionDisconnect()
        {
            ScriptedBridge bridge = null;
            var client = new BridgeClient();
            var frames = new List<ServerFrame>();
            var failures = new List<string>();
            var states = new List<BridgeState>();
            try
            {
                bridge = new ScriptedBridge(session =>
                {
                    if (session.Index == 1)
                    {
                        session.ReadText(5000);   // 第一句收到了，但这一轮故意不回
                        Thread.Sleep(1200);       // 活过稳定窗口，算一次“好连接”
                        session.AbortSocket();    // 直接掐 TCP：模拟桥接进程被杀
                        return;
                    }
                    if (session.Index == 2)
                    {
                        // 紧接着这次重连没活过稳定窗口就又断了：同一次故障，不该再报一条。
                        Thread.Sleep(700);
                        session.AbortSocket();
                        return;
                    }

                    var say = session.ReadText(5000);
                    if (say == null) return;
                    session.SendText("{\"type\":\"ack\",\"turnId\":\"turn-2\"}");
                    session.SendText("{\"type\":\"reply\",\"turnId\":\"turn-2\",\"text\":\"我还在这儿。\"}");
                });

                UnityEngine.Debug.Log("[BridgeClientTests] mid-session disconnect: " + bridge.Url);
                client.Configure(new BridgeSettings { Url = bridge.Url });
                client.FrameReceived += frames.Add;
                client.Failed += failures.Add;
                client.StateChanged += states.Add;
                client.Start();

                yield return WaitUntil(() => client.State == BridgeState.Connected, client, null, failures);
                Assert.IsTrue(client.SendSay("第一句"), "连上之后 SendSay 必须成功");
                Assert.AreEqual(BridgeState.Waiting, client.State, "发出 say 之后进入 Waiting");

                yield return WaitUntil(
                    () => states.FindAll(s => s == BridgeState.Connected).Count >= 3 && bridge.Accepted >= 3,
                    client, null, failures);

                Assert.AreEqual(BridgeState.Connected, client.State, "掉线之后必须自己重连回 Connected：" + Describe(states));
                Assert.AreEqual(1, failures.FindAll(f => f.Contains("断开")).Count,
                    "从断到重新稳住算一次故障，只该报一条（每次连上都把计数清零就会每轮报一条）：" + Describe(failures));

                Assert.IsTrue(client.SendSay("第二句"), "重连之后必须还能发");
                yield return WaitUntil(() => frames.Exists(f => f.Type == "reply"), client, null, failures);
                Assert.AreEqual("我还在这儿。", frames.Find(f => f.Type == "reply").Text);
                Assert.AreEqual(BridgeState.Connected, client.State);
                UnityEngine.Debug.Log("[BridgeClientTests] mid-session disconnect: 状态序列 " + Describe(states) + "，错误 " + Describe(failures));
            }
            finally
            {
                client.Dispose();
                if (bridge != null) bridge.Dispose();
            }
        }

        /// <summary>
        /// review FIX 3 / Review Focus 第 5 条：一帧坏 JSON 只留一条 warning，不掐连接；
        /// 紧接着的好帧照常派发，之后这条连接还能发。
        /// </summary>
        [UnityTest]
        public IEnumerator DropsAMalformedFrameAndKeepsTheConnection()
        {
            ScriptedBridge bridge = null;
            var client = new BridgeClient();
            var frames = new List<ServerFrame>();
            var failures = new List<string>();
            try
            {
                bridge = new ScriptedBridge(session =>
                {
                    session.SendText("{\"type\":\"ack\",\"turnId\":");                  // 语法就坏的 JSON
                    session.SendText("{\"type\":\"thinking\",\"turnId\":\"turn-1\"}");  // 紧接着一帧好的
                    var say = session.ReadText(5000);
                    if (say == null) return;
                    session.SendText("{\"type\":\"ack\",\"turnId\":\"turn-2\"}");
                    session.SendText("{\"type\":\"reply\",\"turnId\":\"turn-2\",\"text\":\"我还在。\"}");
                });

                // 坏帧只该在 Editor.log 留一条 warning。
                LogAssert.Expect(LogType.Warning, new Regex("丢弃无法解析的帧"));

                UnityEngine.Debug.Log("[BridgeClientTests] malformed frame: " + bridge.Url);
                client.Configure(new BridgeSettings { Url = bridge.Url });
                client.FrameReceived += frames.Add;
                client.Failed += failures.Add;
                client.Start();

                yield return WaitUntil(() => frames.Exists(f => f.Type == "thinking"), client, null, failures);
                Assert.AreEqual(1, frames.Count, "坏帧不该被派发，好帧要照常到：" + Describe(frames));
                Assert.IsFalse(failures.Exists(f => f.Contains("断开")), "坏帧不该把连接弄断：" + Describe(failures));

                Assert.IsTrue(client.SendSay("还在吗"), "坏帧之后连接必须还能发");
                yield return WaitUntil(() => frames.Exists(f => f.Type == "reply"), client, null, failures);
                Assert.AreEqual("我还在。", frames.Find(f => f.Type == "reply").Text);
                Assert.AreEqual(BridgeState.Connected, client.State, "这一轮收完仍然回到 Connected");
                UnityEngine.Debug.Log("[BridgeClientTests] malformed frame: 丢弃一帧后仍收到 " + Describe(frames));
            }
            finally
            {
                client.Dispose();
                if (bridge != null) bridge.Dispose();
            }
        }

        /// <summary>
        /// review FIX 3：超过 64 KiB 的帧整帧丢弃（不派发、不截半帧），连接照常可用。
        /// </summary>
        [UnityTest]
        public IEnumerator DropsAnOversizedFrameAndKeepsTheConnection()
        {
            ScriptedBridge bridge = null;
            var client = new BridgeClient();
            var frames = new List<ServerFrame>();
            var failures = new List<string>();
            try
            {
                var oversized = new string('x', 70 * 1024);
                bridge = new ScriptedBridge(session =>
                {
                    session.SendText(oversized);
                    session.SendText("{\"type\":\"reply\",\"turnId\":\"turn-1\",\"text\":\"小帧照常到。\"}");
                });

                UnityEngine.Debug.Log("[BridgeClientTests] oversized frame: " + bridge.Url);
                client.Configure(new BridgeSettings { Url = bridge.Url });
                client.FrameReceived += frames.Add;
                client.Failed += failures.Add;
                client.Start();

                yield return WaitUntil(() => frames.Exists(f => f.Type == "reply"), client, null, failures);

                Assert.IsFalse(frames.Exists(f => f.Text != null && f.Text.Length > 1024), "超长帧不该被派发（整帧丢，不留半截）：" + Describe(frames));
                Assert.AreEqual(1, frames.Count, "超长帧之后只该有那一帧小 reply：" + Describe(frames));
                Assert.AreEqual(1, failures.FindAll(f => f.Contains("64 KiB")).Count, "超长帧应当报一次丢弃：" + Describe(failures));

                Assert.AreEqual(BridgeState.Connected, client.State);
                Assert.IsTrue(client.SendSay("还在吗"), "超长帧之后连接必须还能发");
                UnityEngine.Debug.Log("[BridgeClientTests] oversized frame: " + Describe(frames) + "，错误 " + Describe(failures));
            }
            finally
            {
                client.Dispose();
                if (bridge != null) bridge.Dispose();
            }
        }

        /// <summary>
        /// review FIX 2：上一句还在等 reply（Waiting）时不允许再发。
        /// ClientWebSocket 不允许并发 SendAsync，放第二个 send 过去会把状态推到 Connecting，
        /// 而接收循环还活着、永远不会重连 —— 所以第二个 send 必须在入口就被挡掉。
        /// </summary>
        [UnityTest]
        public IEnumerator RefusesToSendWhileATurnIsInFlight()
        {
            var mock = MockBridgeProcess.Start("happy", MockBridgeProcess.FindFreePort(), Token);
            var client = new BridgeClient();
            var frames = new List<ServerFrame>();
            var failures = new List<string>();
            try
            {
                UnityEngine.Debug.Log("[BridgeClientTests] " + mock.CommandLine + " -> " + mock.Url);
                client.Configure(new BridgeSettings { Url = mock.Url, Token = Token });
                client.FrameReceived += frames.Add;
                client.Failed += failures.Add;
                client.Start();

                yield return WaitUntil(() => client.State == BridgeState.Connected, client, mock, failures);

                Assert.IsTrue(client.SendSay("第一句"), "连上之后 SendSay 必须成功");
                Assert.AreEqual(BridgeState.Waiting, client.State, "发出 say 之后进入 Waiting，等这一轮的 reply");

                Assert.IsFalse(client.SendSay("第二句"), "上一句还在等 reply 时 SendSay 必须返回 false");
                Assert.AreEqual(BridgeState.Waiting, client.State, "被拒绝的 send 不该改动状态");

                // 给后台一点时间：如果第二个 send 真的写到 socket 上了，
                // 这里会看到 “发送失败（…），等待重连” 并把状态推到 Connecting。
                yield return Quiet(client, 0.2f);
                Assert.IsFalse(failures.Exists(f => f.Contains("发送失败")), "Waiting 里不该碰到 socket（并发 SendAsync 会抛 InvalidOperationException）：" + Describe(failures));
                Assert.AreEqual(BridgeState.Waiting, client.State, "第二个 send 被挡下来之后仍在等这一轮：" + Describe(failures));

                yield return WaitUntil(() => frames.Exists(f => f.Type == "reply"), client, mock, failures);
                Assert.AreEqual(BridgeState.Connected, client.State, "这一轮收完回到 Connected");

                Assert.IsTrue(client.SendSay("第三句"), "上一轮结束之后必须还能发下一句");
                Assert.AreEqual(BridgeState.Waiting, client.State);
                UnityEngine.Debug.Log("[BridgeClientTests] waiting send: 第二句被挡下，帧 " + Describe(frames));
            }
            finally
            {
                client.Dispose();
                mock.Dispose();
            }
        }

        static IEnumerator WaitUntil(Func<bool> done, BridgeClient client, MockBridgeProcess mock, List<string> failures)
        {
            var watch = Stopwatch.StartNew();
            while (!done())
            {
                if (watch.Elapsed.TotalSeconds > Deadline)
                {
                    Assert.Fail("等了 " + Deadline + " 秒仍不满足条件。错误：" + (failures.Count == 0 ? "（无）" : string.Join(" | ", failures)) +
                        (mock == null ? string.Empty : "。mock 输出：" + mock.Tail));
                }
                client.Pump();
                yield return null;
            }
        }

        static IEnumerator Quiet(BridgeClient client, float seconds)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < seconds)
            {
                client.Pump();
                yield return null;
            }
        }

        static string Describe(List<ServerFrame> frames)
        {
            var parts = new List<string>();
            foreach (var frame in frames) parts.Add(frame.Type + (string.IsNullOrEmpty(frame.TurnId) ? string.Empty : "@" + frame.TurnId));
            return parts.Count == 0 ? "（无帧）" : string.Join(" -> ", parts);
        }

        static string Describe(List<BridgeState> states)
        {
            var parts = new List<string>();
            foreach (var state in states) parts.Add(state.ToString());
            return parts.Count == 0 ? "（无状态）" : string.Join(" -> ", parts);
        }

        static string Describe(List<string> messages)
        {
            return messages.Count == 0 ? "（无错误）" : string.Join(" | ", messages);
        }
    }
}
