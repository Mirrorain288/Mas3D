using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Mas3D.Tests
{
    /// <summary>
    /// 裸 <see cref="TcpListener"/> 版的 WebSocket 服务端，只实现握手 + 文本帧 + 关闭帧。
    ///
    /// 为什么不复用 tools/mock-bridge.mjs：那个脚本只会按剧本好好说话，而 Task 2 有三条行为
    /// 必须由“不配合的对端”来验 —— accept 完立刻关（review FIX 1 的重连风暴）、发一帧坏 JSON、
    /// 发一帧超过 64 KiB 的帧、连到一半掐线。这些都不能靠改 mock 脚本实现（那个文件另归测试基础设施），
    /// 所以这里用真 socket 自己演。
    ///
    /// 线程模型：Accept 一条连接就起一个后台线程跑 <c>script</c>；每条连接各自一个 <see cref="ScriptedSession"/>。
    /// 脚本返回之后会话保持打开（否则客户端会看见一次断线），直到 <see cref="Dispose"/> 收摊。
    /// 后台线程的异常一律吞掉：判据在测试的断言里，线程里冒出来只会污染 Editor.log。
    /// </summary>
    public sealed class ScriptedBridge : IDisposable
    {
        /// <summary>RFC 6455 规定拼在 Sec-WebSocket-Key 后面的魔数。</summary>
        const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        readonly TcpListener _listener;
        readonly Thread _acceptLoop;
        readonly Action<ScriptedSession> _script;
        readonly List<ScriptedSession> _sessions = new List<ScriptedSession>();
        readonly object _gate = new object();

        int _accepted;
        volatile bool _stopped;
        bool _disposed;

        /// <summary>起一个只监听 loopback 的脚本化桥接；<paramref name="script"/> 每条连接跑一次（参数是该会话）。</summary>
        public ScriptedBridge(Action<ScriptedSession> script)
        {
            if (script == null) throw new ArgumentNullException(nameof(script));
            _script = script;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            try
            {
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            }
            catch (Exception)
            {
                _listener.Stop();
                throw;
            }

            _acceptLoop = new Thread(AcceptLoop) { IsBackground = true, Name = "ScriptedBridge-accept" };
            _acceptLoop.Start();
        }

        /// <summary>系统分配到的端口。</summary>
        public int Port { get; private set; }

        /// <summary>客户端该连的端点，形状与 mock-bridge 一致。</summary>
        public string Url
        {
            get
            {
                return "ws://127.0.0.1:" + Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/mas3d/ws";
            }
        }

        /// <summary>累计 accept 过多少条 TCP 连接；重连风暴的直接判据。</summary>
        public int Accepted
        {
            get { return Volatile.Read(ref _accepted); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stopped = true;
            try
            {
                _listener.Stop();
            }
            catch (Exception)
            {
                // 已经停掉。
            }

            ScriptedSession[] sessions;
            lock (_gate)
            {
                sessions = _sessions.ToArray();
                _sessions.Clear();
            }
            foreach (var session in sessions) session.Dispose();
        }

        void AcceptLoop()
        {
            while (!_stopped)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    return; // Dispose 把 listener 停了，accept 抛异常是正常收尾。
                }

                var index = Interlocked.Increment(ref _accepted);
                var session = new ScriptedSession(client, index);
                lock (_gate) _sessions.Add(session);
                var thread = new Thread(() => RunSession(session))
                {
                    IsBackground = true,
                    Name = "ScriptedBridge-session-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };
                thread.Start();
            }
        }

        void RunSession(ScriptedSession session)
        {
            try
            {
                if (!session.Handshake()) return;
                _script(session);
                session.Hold();
            }
            catch (Exception)
            {
                // 见类注释：会话线程的异常不外抛。
            }
            finally
            {
                session.Release();
            }
        }
    }

    /// <summary>一条被 scripted 的 WebSocket 连接：握手、发文本帧、发关闭帧、读客户端帧、掐线。</summary>
    public sealed class ScriptedSession
    {
        /// <summary>握手请求头的上限：正常请求不到 1 KiB，超过这个数就是对端没在说 WebSocket。</summary>
        const int MaxRequestBytes = 16 * 1024;

        readonly TcpClient _client;
        readonly NetworkStream _stream;
        readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);
        readonly object _writeGate = new object();

        internal ScriptedSession(TcpClient client, int index)
        {
            _client = client;
            _stream = client.GetStream();
            Index = index;
        }

        /// <summary>1 起的连接序号；脚本靠它区分“第一条坏连接/第三条好连接”。</summary>
        public int Index { get; private set; }

        /// <summary>读 HTTP Upgrade 请求并回 101。返回 false 表示对面根本没按 WebSocket 说话。</summary>
        public bool Handshake()
        {
            var request = ReadRequest();
            if (request == null) return false;
            var key = Header(request, "Sec-WebSocket-Key");
            if (string.IsNullOrEmpty(key)) return false;

            string accept;
            using (var sha1 = SHA1.Create())
            {
                accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            }

            var response = "HTTP/1.1 101 Switching Protocols\r\n" +
                           "Upgrade: websocket\r\n" +
                           "Connection: Upgrade\r\n" +
                           "Sec-WebSocket-Accept: " + accept + "\r\n" +
                           "\r\n";
            Write(Encoding.ASCII.GetBytes(response));
            return true;
        }

        /// <summary>发一个完整文本帧（服务端帧不加掩码）。</summary>
        public void SendText(string text)
        {
            SendFrame(1, Encoding.UTF8.GetBytes(text ?? string.Empty));
        }

        /// <summary>发一个关闭帧；TCP 不马上关，留时间让客户端把关闭码读走。</summary>
        public void Close(int code, string reason)
        {
            var text = Encoding.UTF8.GetBytes(reason ?? string.Empty);
            var payload = new byte[2 + text.Length];
            payload[0] = (byte)((code >> 8) & 0xFF);
            payload[1] = (byte)(code & 0xFF);
            Array.Copy(text, 0, payload, 2, text.Length);
            SendFrame(8, payload);
        }

        /// <summary>不发关闭帧，直接把 TCP 掐掉（模拟桥接进程被杀，客户端看到 1006）。</summary>
        public void AbortSocket()
        {
            try
            {
                _client.Client.LingerState = new LingerOption(true, 0);
                _client.Close();
            }
            catch (Exception)
            {
                // 已经断了。
            }
        }

        /// <summary>读一个客户端文本帧；超时/对端关闭返回 null。客户端帧带掩码，这里解掉。</summary>
        public string ReadText(int timeoutMs)
        {
            try
            {
                _client.ReceiveTimeout = timeoutMs;
                while (true)
                {
                    var header = ReadExactly(2);
                    if (header == null) return null;

                    var opcode = header[0] & 0x0F;
                    var masked = (header[1] & 0x80) != 0;
                    long length = header[1] & 0x7F;
                    if (length == 126)
                    {
                        var extended = ReadExactly(2);
                        if (extended == null) return null;
                        length = (extended[0] << 8) | extended[1];
                    }
                    else if (length == 127)
                    {
                        var extended = ReadExactly(8);
                        if (extended == null) return null;
                        length = 0;
                        for (var i = 0; i < 8; i++) length = (length << 8) | extended[i];
                    }

                    var mask = masked ? ReadExactly(4) : null;
                    if (masked && mask == null) return null;

                    var payload = ReadExactly((int)length);
                    if (payload == null) return null;
                    if (masked)
                    {
                        for (var i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];
                    }

                    if (opcode == 8) return null;   // 客户端关了
                    if (opcode == 9) continue;      // ping：忽略
                    if (opcode == 1) return Encoding.UTF8.GetString(payload);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>脚本跑完后把连接挂住，别让客户端以为对端下线了。</summary>
        public void Hold()
        {
            try
            {
                _release.Wait(30000);
            }
            catch (Exception)
            {
                // 收摊时被打断，正常。
            }
        }

        internal void Release()
        {
            try
            {
                _release.Set();
            }
            catch (Exception)
            {
                // 已经释放。
            }
        }

        public void Dispose()
        {
            Release();
            try
            {
                _client.Close();
            }
            catch (Exception)
            {
                // 已经断了。
            }
            try
            {
                _stream.Dispose();
            }
            catch (Exception)
            {
                // 已经断了。
            }
        }

        void SendFrame(byte opcode, byte[] payload)
        {
            // 头长算准再开数组：多出来的字节会被对端当成下一帧，整条连接就废了。
            var header = payload.Length < 126 ? 2 : (payload.Length <= 0xFFFF ? 4 : 10);
            var frame = new byte[header + payload.Length];
            var at = 0;
            frame[at++] = (byte)(0x80 | opcode);
            if (payload.Length < 126)
            {
                frame[at++] = (byte)payload.Length;
            }
            else if (payload.Length <= 0xFFFF)
            {
                frame[at++] = 126;
                frame[at++] = (byte)((payload.Length >> 8) & 0xFF);
                frame[at++] = (byte)(payload.Length & 0xFF);
            }
            else
            {
                frame[at++] = 127;
                var size = (long)payload.Length;
                for (var shift = 56; shift >= 0; shift -= 8) frame[at++] = (byte)((size >> shift) & 0xFF);
            }

            Array.Copy(payload, 0, frame, at, payload.Length);
            Write(frame);
        }

        void Write(byte[] bytes)
        {
            lock (_writeGate)
            {
                try
                {
                    _stream.Write(bytes, 0, bytes.Length);
                    _stream.Flush();
                }
                catch (Exception)
                {
                    // 对面已经关了：写失败没有可做的事，交给脚本读完/超时退出。
                }
            }
        }

        string ReadRequest()
        {
            var buffer = new List<byte>(1024);
            var chunk = new byte[1024];
            try
            {
                _client.ReceiveTimeout = 5000;
                while (buffer.Count < MaxRequestBytes)
                {
                    var read = _stream.Read(chunk, 0, chunk.Length);
                    if (read <= 0) return null;
                    for (var i = 0; i < read; i++) buffer.Add(chunk[i]);

                    var count = buffer.Count;
                    if (count >= 4 &&
                        buffer[count - 4] == (byte)'\r' && buffer[count - 3] == (byte)'\n' &&
                        buffer[count - 2] == (byte)'\r' && buffer[count - 1] == (byte)'\n')
                    {
                        return Encoding.ASCII.GetString(buffer.ToArray());
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }
            return null;
        }

        byte[] ReadExactly(int count)
        {
            var buffer = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var read = _stream.Read(buffer, offset, count - offset);
                if (read <= 0) return null;
                offset += read;
            }
            return buffer;
        }

        static string Header(string request, string name)
        {
            var lines = request.Split(new[] { "\r\n" }, StringSplitOptions.None);
            foreach (var line in lines)
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                if (string.Equals(line.Substring(0, colon).Trim(), name, StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring(colon + 1).Trim();
                }
            }
            return null;
        }
    }
}
