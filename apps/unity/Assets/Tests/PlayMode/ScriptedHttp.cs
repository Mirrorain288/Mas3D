using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Mas3D.Tests
{
    /// <summary>
    /// 一条被 <see cref="ScriptedHttp"/> 记下来的 HTTP 请求。
    /// 只保留断言需要的形状：方法、请求目标（path + query）、头、body 字节与长度。
    /// </summary>
    public sealed class ScriptedRequest
    {
        readonly Dictionary<string, string> _headers;

        internal ScriptedRequest(string method, string target, Dictionary<string, string> headers, byte[] body)
        {
            Method = method;
            Target = target;
            _headers = headers;
            Body = body;
        }

        /// <summary>请求方法，例如 POST。</summary>
        public string Method { get; private set; }

        /// <summary>请求行里的目标原文，例如 /mas3d/api/asr?x=1。</summary>
        public string Target { get; private set; }

        /// <summary>去掉 query 之后的路径。</summary>
        public string Path
        {
            get
            {
                var query = Target.IndexOf('?');
                return query < 0 ? Target : Target.Substring(0, query);
            }
        }

        /// <summary>请求 body 的原始字节。</summary>
        public byte[] Body { get; private set; }

        /// <summary>Content-Length 索要的字节数；与 <see cref="Body"/>.Length 一致。</summary>
        public int BodyLength
        {
            get { return Body.Length; }
        }

        /// <summary>请求头（名字大小写不敏感）。</summary>
        public IReadOnlyDictionary<string, string> Headers
        {
            get { return _headers; }
        }

        /// <summary>取一个请求头；没有就返回 null。名字大小写不敏感，与 <see cref="ScriptedBridge"/> 的 Header 同义。</summary>
        public string Header(string name)
        {
            string value;
            return name != null && _headers.TryGetValue(name, out value) ? value : null;
        }

        public override string ToString()
        {
            return Method + " " + Target + " (" + Body.Length.ToString(CultureInfo.InvariantCulture) + " bytes)";
        }
    }

    /// <summary>
    /// 裸 <see cref="TcpListener"/> 版的 HTTP 1.1 服务端：每条连接只答一个请求，答完就关。
    ///
    /// 为什么要真 socket：Task 3 的 AsrClient 走 UnityWebRequest，要验的是“线缆上真的发了什么”
    /// —— POST 到哪个 path、Content-Type 是不是 audio/wav、Authorization 带没带、body 是不是
    /// 原始 WAV 字节（而不是 multipart 包了一层）。这些只有看真请求才能断言。
    ///
    /// 线程模型与 <see cref="ScriptedBridge"/> 一致：accept 一条连接起一个后台线程，读请求头到
    /// 空行、再读 Content-Length 指定的 body，记录之后回固定响应并关闭 socket。后台线程的异常
    /// 一律吞掉：判据在测试的断言里，线程里冒出来只会污染 Editor.log。
    /// </summary>
    public sealed class ScriptedHttp : IDisposable
    {
        /// <summary>请求头上限：正常请求不到 1 KiB，超过这个数就是对端没在说 HTTP。</summary>
        const int MaxRequestBytes = 64 * 1024;

        readonly TcpListener _listener;
        readonly Thread _acceptLoop;
        readonly int _status;
        readonly string _reason;
        readonly byte[] _body;
        readonly List<ScriptedRequest> _requests = new List<ScriptedRequest>();
        readonly List<TcpClient> _clients = new List<TcpClient>();
        readonly object _gate = new object();

        int _accepted;
        volatile bool _stopped;
        bool _disposed;

        /// <summary>起一个只监听 127.0.0.1 的脚本化 HTTP 服务端；每条连接都回同一个状态码与 body。</summary>
        public ScriptedHttp(int status, string body)
            : this(status, ReasonPhrase(status), body)
        {
        }

        /// <summary>同上，状态原因短语自己给。</summary>
        public ScriptedHttp(int status, string reason, string body)
        {
            _status = status;
            _reason = string.IsNullOrEmpty(reason) ? "Status" : reason;
            _body = Encoding.UTF8.GetBytes(body ?? string.Empty);

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

            _acceptLoop = new Thread(AcceptLoop) { IsBackground = true, Name = "ScriptedHttp-accept" };
            _acceptLoop.Start();
        }

        /// <summary>系统分配到的端口。</summary>
        public int Port { get; private set; }

        /// <summary>
        /// 记录到请求之后、回响应之前先睡这么久（毫秒）。用来制造"请求已经在线缆上、
        /// 但结果还没回来"的窗口：<c>AsrClient.Abort()</c> 的用例靠它把连接掐在中途。
        /// </summary>
        public int DelayMs;

        /// <summary>服务端基地址（不带结尾斜杠）：被测客户端自己拼 path，例如 Url + "/mas3d/api/asr"。</summary>
        public string Url
        {
            get
            {
                return "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>累计 accept 过多少条 TCP 连接。</summary>
        public int Accepted
        {
            get { return Volatile.Read(ref _accepted); }
        }

        /// <summary>目前记录到的请求快照（按 accept 顺序）。请求在回响应之前就已记录，客户端一拿到响应这里就齐了。</summary>
        public ScriptedRequest[] Requests
        {
            get
            {
                lock (_gate) return _requests.ToArray();
            }
        }

        /// <summary>最后一条记录；还没收到请求时为 null。</summary>
        public ScriptedRequest Last
        {
            get
            {
                lock (_gate)
                {
                    return _requests.Count == 0 ? null : _requests[_requests.Count - 1];
                }
            }
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

            TcpClient[] clients;
            lock (_gate)
            {
                clients = _clients.ToArray();
                _clients.Clear();
            }
            foreach (var client in clients)
            {
                try
                {
                    client.Close();
                }
                catch (Exception)
                {
                    // 已经断了。
                }
            }
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
                lock (_gate) _clients.Add(client);

                var thread = new Thread(() => Serve(client, index))
                {
                    IsBackground = true,
                    Name = "ScriptedHttp-conn-" + index.ToString(CultureInfo.InvariantCulture),
                };
                thread.Start();
            }
        }

        void Serve(TcpClient client, int index)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                using (var stream = client.GetStream())
                {
                    var request = ReadRequest(stream);
                    if (request == null) return;

                    lock (_gate) _requests.Add(request);
                    if (DelayMs > 0) Thread.Sleep(DelayMs);
                    WriteResponse(stream);

                    // Connection: close —— 发完 FIN，让客户端读到干净的 EOF。
                    try
                    {
                        client.Client.Shutdown(SocketShutdown.Send);
                    }
                    catch (Exception)
                    {
                        // 对面已经先关了。
                    }
                }
            }
            catch (Exception)
            {
                // 见类注释：连接线程的异常不外抛。
            }
            finally
            {
                lock (_gate) _clients.Remove(client);
                try
                {
                    client.Close();
                }
                catch (Exception)
                {
                    // 已经断了。
                }
            }
        }

        static ScriptedRequest ReadRequest(NetworkStream stream)
        {
            var buffer = new List<byte>(1024);
            var chunk = new byte[4096];
            var headerEnd = -1;

            while (headerEnd < 0)
            {
                var read = stream.Read(chunk, 0, chunk.Length);
                if (read <= 0) return null;
                for (var i = 0; i < read; i++) buffer.Add(chunk[i]);
                if (buffer.Count > MaxRequestBytes) return null;
                headerEnd = IndexOfHeaderEnd(buffer);
            }

            var headerText = Encoding.ASCII.GetString(buffer.ToArray(), 0, headerEnd);
            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return null;

            var requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2) return null;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }

            var length = 0;
            string lengthText;
            if (headers.TryGetValue("Content-Length", out lengthText))
            {
                int.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out length);
            }
            if (length < 0 || length > 64 * 1024 * 1024) return null;

            var bodyStart = headerEnd + 4;
            var body = new byte[length];
            var copied = Math.Min(length, buffer.Count - bodyStart);
            if (copied > 0) buffer.CopyTo(bodyStart, body, 0, copied);

            var offset = copied;
            while (offset < length)
            {
                var read = stream.Read(body, offset, length - offset);
                if (read <= 0) return null;
                offset += read;
            }

            return new ScriptedRequest(requestLine[0], requestLine[1], headers, body);
        }

        static int IndexOfHeaderEnd(List<byte> buffer)
        {
            for (var i = 0; i + 3 < buffer.Count; i++)
            {
                if (buffer[i] == (byte)'\r' && buffer[i + 1] == (byte)'\n' &&
                    buffer[i + 2] == (byte)'\r' && buffer[i + 3] == (byte)'\n')
                {
                    return i;
                }
            }
            return -1;
        }

        void WriteResponse(NetworkStream stream)
        {
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(_status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(_reason).Append("\r\n");
            head.Append("Content-Type: application/json; charset=utf-8\r\n");
            head.Append("Content-Length: ").Append(_body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            head.Append("Connection: close\r\n\r\n");

            var headBytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            if (_body.Length > 0) stream.Write(_body, 0, _body.Length);
            stream.Flush();
        }

        static string ReasonPhrase(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 413: return "Payload Too Large";
                case 500: return "Internal Server Error";
                case 503: return "Service Unavailable";
                case 504: return "Gateway Timeout";
                default: return "Status";
            }
        }
    }
}
