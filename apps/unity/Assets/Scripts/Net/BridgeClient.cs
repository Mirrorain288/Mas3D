using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Mas3D.Net
{
    /// <summary>
    /// 通道状态。<see cref="BridgeState.Waiting"/> 表示一句话已经发出、正在等这一轮的 reply/error。
    ///
    /// <see cref="BridgeState.Failed"/> 是给 UI 选文案的展示态，不是状态机的一步：退避重连期间状态仍然停在
    /// <see cref="BridgeState.Connecting"/>（RunAsync 的循环只在连上/断开时改状态），客户端从不 SetState 到它。
    /// </summary>
    public enum BridgeState
    {
        Idle,
        Connecting,
        Connected,
        Waiting,
        Closed,

        /// <summary>失败/退避重试中，只由 UI 传进 ConnectionBanner.TextFor（见上）。</summary>
        Failed,
    }

    /// <summary>
    /// 到 Python 桥接的 WebSocket 通道（设计文档 §3，Review Focus 1）。
    ///
    /// 线程模型：后台线程只做“连 + 收字节 + 入队”，从不碰 Unity API，也不改状态；
    /// 所有回调都由主线程调用的 <see cref="Pump"/> 触发。好处是收到帧的业务代码不用自己加锁，
    /// <see cref="FrameParser.Parse"/> 抛的 <see cref="ProtocolException"/> 也不会从后台线程冒出来。
    ///
    /// 重连：网络层异常按 <see cref="BridgeSettings.BackoffSeconds"/> 退避重试；退避期间状态停在
    /// <see cref="BridgeState.Connecting"/>，一次故障只报一条 <see cref="Failed"/>，不会刷屏。
    /// 关闭码 1008 是终止性的（令牌错），只报一条错误并停在 <see cref="BridgeState.Closed"/>，不重连。
    /// </summary>
    public sealed class BridgeClient : IDisposable
    {
        /// <summary>单帧上限；桥接的帧都是小 JSON，超了说明对面不对，整帧丢弃。</summary>
        const int MaxFrameBytes = 64 * 1024;

        /// <summary>
        /// 连接要“证明自己”的存活窗口：连上之后既没收到过整帧、也没撑过这么久就断了，
        /// 不算一次成功的连接，不清零退避（否则 accept 完立刻关的对端会打出重连风暴）。
        /// </summary>
        const double StableSeconds = 1.0;

        /// <summary>RFC 6455 的 1006：没有收到关闭帧就断了。</summary>
        const int AbnormalClosure = 1006;

        /// <summary>RFC 6455 的 1008：策略违规，桥接用它表示令牌不对。</summary>
        const int PolicyViolation = 1008;

        static readonly float[] FallbackBackoff = { 0.5f, 1f, 2f, 4f, 8f };

        readonly ConcurrentQueue<BridgeEvent> _inbox = new ConcurrentQueue<BridgeEvent>();
        readonly object _gate = new object();

        BridgeSettings _settings = new BridgeSettings();
        ClientWebSocket _socket;
        CancellationTokenSource _lifetime;
        int _generation;
        bool _disposed;

        /// <summary>状态真的变了才触发；参数是新状态。</summary>
        public event Action<BridgeState> StateChanged;

        /// <summary>收到一个可解析的帧；在主线程触发。</summary>
        public event Action<ServerFrame> FrameReceived;

        /// <summary>人话错误，可以直接显示到 UI。</summary>
        public event Action<string> Failed;

        /// <summary>当前状态，只在主线程读写。</summary>
        public BridgeState State { get; private set; }

        /// <summary>改连接参数；只影响下一次 <see cref="Start"/>。后台循环拿的是 Start 那一刻的副本。</summary>
        public void Configure(BridgeSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            _settings = settings.Clone();
        }

        /// <summary>起后台连接循环。已经在跑时重复调用什么都不做。</summary>
        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BridgeClient));
            if (State == BridgeState.Connecting || State == BridgeState.Connected || State == BridgeState.Waiting) return;

            var lifetime = new CancellationTokenSource();
            BridgeSettings settings;
            int generation;
            lock (_gate)
            {
                // 上一轮的取消源不再需要：旧循环看到取消就退，自己负责收尾。
                if (_lifetime != null)
                {
                    try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
                }
                _generation++;
                generation = _generation;
                _lifetime = lifetime;
                settings = _settings.Clone();
            }

            SetState(BridgeState.Connecting);
            Task.Run(async () =>
            {
                try
                {
                    await RunAsync(settings, generation, lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 收尾阶段（Stop/Dispose 正在关 socket）的异常没有信息量，别冒到 Task 外面。
                }
                finally
                {
                    lifetime.Dispose();
                }
            });
        }

        /// <summary>停掉通道。幂等；停完状态是 <see cref="BridgeState.Closed"/>，队列里旧连接留下的事件全部作废。</summary>
        public void Stop()
        {
            ClientWebSocket socket;
            CancellationTokenSource lifetime;
            lock (_gate)
            {
                _generation++;
                socket = _socket;
                _socket = null;
                lifetime = _lifetime;
                _lifetime = null;
            }

            if (lifetime != null)
            {
                try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
            }
            Abort(socket);
            SetState(BridgeState.Closed);
        }

        /// <summary>
        /// 发一句话。只有 <see cref="BridgeState.Connected"/> 时才发，未连接、已释放、
        /// 或者上一句还在等 reply（<see cref="BridgeState.Waiting"/>）时返回 false，不抛。
        /// Waiting 里必须挡住：<see cref="ClientWebSocket"/> 不允许并发 SendAsync，第二个 send 会抛
        /// <see cref="InvalidOperationException"/>，把状态推到 Connecting 而接收循环还活着 —— 那之后
        /// 再也不会重连。输入框本来就按回合禁掉了，上一句没回来就不该能发。
        /// </summary>
        public bool SendSay(string text)
        {
            if (_disposed || string.IsNullOrEmpty(text)) return false;
            if (State != BridgeState.Connected) return false;

            ClientWebSocket socket;
            int generation;
            lock (_gate)
            {
                socket = _socket;
                generation = _generation;
            }
            if (socket == null || socket.State != WebSocketState.Open) return false;

            var payload = Encoding.UTF8.GetBytes(ClientFrame.Say(text));
            SetState(BridgeState.Waiting);
            try
            {
                var send = socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, CancellationToken.None);
                if (send.IsFaulted)
                {
                    ReportSendFailure(generation, send.Exception);
                    return false;
                }
                // 异步发送失败也要报出来，否则状态会永远停在 Waiting。
                send.ContinueWith(task => ReportSendFailure(generation, task.Exception), TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception error)
            {
                ReportSendFailure(generation, error);
                return false;
            }
            return true;
        }

        /// <summary>
        /// 主线程泵：把后台收到的状态/帧/错误派发出去。PlayMode 测试每帧调一次，
        /// 游戏里挂在 Update 上。只有这里会触发事件。
        /// </summary>
        public void Pump()
        {
            BridgeEvent item;
            while (_inbox.TryDequeue(out item))
            {
                int current;
                lock (_gate) current = _generation;
                if (item.Generation != current) continue; // 上一轮连接留下的，作废

                switch (item.Kind)
                {
                    case BridgeEventKind.State:
                        SetState(item.State);
                        break;
                    case BridgeEventKind.Frame:
                        DeliverFrame(item.Text);
                        break;
                    default:
                        var failed = Failed;
                        if (failed != null) failed(item.Text);
                        break;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();

            BridgeEvent ignored;
            while (_inbox.TryDequeue(out ignored))
            {
                // 关掉之后没人会再 Pump，清空防止残留事件被下一条用例看到。
            }
        }

        async Task RunAsync(BridgeSettings settings, int generation, CancellationToken token)
        {
            var backoff = settings.BackoffSeconds != null && settings.BackoffSeconds.Length > 0
                ? settings.BackoffSeconds
                : FallbackBackoff;
            var attempt = 0;
            var reported = false; // 一次故障只报一条 Failed

            while (!token.IsCancellationRequested)
            {
                var socket = new ClientWebSocket();
                var connected = false;
                var closure = new Closure();
                Exception failure = null;
                var live = new System.Diagnostics.Stopwatch();
                try
                {
                    await socket.ConnectAsync(BuildUri(settings), token).ConfigureAwait(false);
                    connected = true;
                    live.Start();
                    Publish(socket, generation);
                    Enqueue(BridgeEvent.StateChange(generation, BridgeState.Connected));
                    await ReceiveLoopAsync(socket, generation, token, closure).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception error)
                {
                    failure = error;
                }
                finally
                {
                    Forget(socket);
                    socket.Dispose();
                }

                if (token.IsCancellationRequested) return;

                if (closure.Code == PolicyViolation)
                {
                    // 令牌错是终止性的：重连只会再被拒一次，纯属噪音。
                    Enqueue(BridgeEvent.Failure(generation,
                        "令牌不正确：桥接以 1008 关闭了连接。核对 BridgeSettings.Token 与 apps/bridge/data/mas3d/token 是否一致。"));
                    Enqueue(BridgeEvent.StateChange(generation, BridgeState.Closed));
                    return;
                }

                // 连上之后立刻被关（对端 accept 完就 close）不算“连上过”：既没收到过整帧，
                // 也没活过 StableSeconds 的话，退避继续保持增长，故障也只报一条。
                // 反过来，真活过一次的连接掉线要立刻从最短间隔重连，不让一次掉线拖成越来越长的退避。
                if (connected && (closure.SawFrame || live.Elapsed.TotalSeconds >= StableSeconds))
                {
                    attempt = 0;
                    reported = false;
                }

                if (connected)
                {
                    Enqueue(BridgeEvent.StateChange(generation, BridgeState.Connecting));
                    if (!reported)
                    {
                        reported = true;
                        Enqueue(BridgeEvent.Failure(generation, "与桥接的连接断开了" + DescribeClosure(closure.Code, failure) + "，正在重连。"));
                    }
                }
                else if (!reported)
                {
                    reported = true;
                    Enqueue(BridgeEvent.Failure(generation,
                        "连接失败" + DescribeClosure(closure.Code, failure) + "。桥接可能还没起来，后台按退避重试。"));
                }

                var delay = backoff[Math.Min(attempt, backoff.Length - 1)];
                attempt++;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// 一次连接的收尾观察：关闭码 + 有没有收到过整帧。
        /// 放在对象里而不是返回值，是因为接收循环正常结束和抛异常两种情况都要读得到。
        /// </summary>
        sealed class Closure
        {
            public int Code = AbnormalClosure;
            public bool SawFrame;
        }

        /// <summary>收循环：攒整帧文本入队；关闭码和“收到过整帧”记进 <paramref name="closure"/>（连接异常时异常抛给调用方）。</summary>
        async Task ReceiveLoopAsync(ClientWebSocket socket, int generation, CancellationToken token, Closure closure)
        {
            var chunk = new byte[4096];
            var message = new MemoryStream(4096);
            var skipped = false;
            try
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(chunk), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        closure.Code = result.CloseStatus.HasValue ? (int)result.CloseStatus.Value : AbnormalClosure;
                        return;
                    }

                    if (!skipped)
                    {
                        if (message.Length + result.Count <= MaxFrameBytes)
                        {
                            message.Write(chunk, 0, result.Count);
                        }
                        else
                        {
                            skipped = true;
                            message.SetLength(0);
                            Enqueue(BridgeEvent.Failure(generation, "桥接发来的帧超过 " + (MaxFrameBytes / 1024) + " KiB，已整帧丢弃。"));
                        }
                    }

                    if (!result.EndOfMessage) continue;
                    if (skipped)
                    {
                        skipped = false;
                        continue;
                    }

                    var json = Encoding.UTF8.GetString(message.ToArray());
                    message.SetLength(0);
                    if (!string.IsNullOrEmpty(json))
                    {
                        closure.SawFrame = true;
                        Enqueue(BridgeEvent.Incoming(generation, json));
                    }
                }
            }
            finally
            {
                message.Dispose();
            }
        }

        void DeliverFrame(string json)
        {
            ServerFrame frame;
            try
            {
                frame = FrameParser.Parse(json);
            }
            catch (ProtocolException error)
            {
                // 单帧坏掉只丢这一帧，不掐连接（Review Focus 第 5 条）。
                Debug.LogWarning("[BridgeClient] 丢弃无法解析的帧：" + error.Message + " 原文：" + Truncate(json));
                return;
            }

            if (frame.Type == "reply" || frame.Type == "error") SetState(BridgeState.Connected);

            var handler = FrameReceived;
            if (handler != null) handler(frame);
        }

        void ReportSendFailure(int generation, Exception error)
        {
            Enqueue(BridgeEvent.StateChange(generation, BridgeState.Connecting));
            Enqueue(BridgeEvent.Failure(generation, "发送失败" + DescribeClosure(0, error) + "，等待重连。"));
        }

        void SetState(BridgeState next)
        {
            if (State == next) return;
            State = next;
            var handler = StateChanged;
            if (handler != null) handler(next);
        }

        void Publish(ClientWebSocket socket, int generation)
        {
            lock (_gate)
            {
                if (generation == _generation) _socket = socket;
            }
        }

        void Forget(ClientWebSocket socket)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_socket, socket)) _socket = null;
            }
        }

        void Enqueue(BridgeEvent item)
        {
            _inbox.Enqueue(item);
        }

        static void Abort(ClientWebSocket socket)
        {
            if (socket == null) return;
            try
            {
                socket.Abort();
            }
            catch (Exception)
            {
                // 已经关掉/正在关：Abort 再抛也没有可做的事。
            }
        }

        static Uri BuildUri(BridgeSettings settings)
        {
            var url = string.IsNullOrEmpty(settings.Url) ? BridgeSettings.DefaultUrl : settings.Url;
            if (string.IsNullOrEmpty(settings.Token)) return new Uri(url, UriKind.Absolute);
            var separator = url.IndexOf('?') >= 0 ? "&" : "?";
            return new Uri(url + separator + "token=" + Uri.EscapeDataString(settings.Token), UriKind.Absolute);
        }

        static string DescribeClosure(int code, Exception failure)
        {
            if (failure != null) return "（" + Explain(failure) + "）";
            if (code == 0 || code == AbnormalClosure) return string.Empty;
            return "（关闭码 " + code + "）";
        }

        static string Explain(Exception error)
        {
            var text = new StringBuilder();
            for (var current = error; current != null && text.Length < 240; current = current.InnerException)
            {
                if (text.Length > 0) text.Append(" <- ");
                text.Append(current.GetType().Name).Append(": ").Append(OneLine(current.Message));
            }
            return text.ToString();
        }

        /// <summary>
        /// 异常消息里常带控制字符（Mono 的 SocketException 消息就带着换行，偶尔还有 NUL），
        /// 直接交给 UI 会断行甚至把整行日志截断，这里统一压成一行可打印文本。
        /// </summary>
        static string OneLine(string message)
        {
            if (string.IsNullOrEmpty(message)) return string.Empty;

            var text = new StringBuilder(message.Length);
            var spaced = false;
            foreach (var character in message)
            {
                if (character < ' ' || character == '\u007f')
                {
                    if (!spaced && text.Length > 0) text.Append(' ');
                    spaced = true;
                    continue;
                }
                text.Append(character);
                spaced = false;
            }
            return text.ToString().Trim();
        }

        static string Truncate(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= 200) return text;
            return text.Substring(0, 200) + "…";
        }

        enum BridgeEventKind
        {
            State,
            Frame,
            Failed,
        }

        /// <summary>后台线程只往队列里塞这个；Generation 用来丢弃旧连接留下的事件。</summary>
        struct BridgeEvent
        {
            public int Generation;
            public BridgeEventKind Kind;
            public BridgeState State;
            public string Text;

            public static BridgeEvent StateChange(int generation, BridgeState state)
            {
                return new BridgeEvent { Generation = generation, Kind = BridgeEventKind.State, State = state };
            }

            public static BridgeEvent Incoming(int generation, string json)
            {
                return new BridgeEvent { Generation = generation, Kind = BridgeEventKind.Frame, Text = json };
            }

            public static BridgeEvent Failure(int generation, string message)
            {
                return new BridgeEvent { Generation = generation, Kind = BridgeEventKind.Failed, Text = message };
            }
        }
    }
}
