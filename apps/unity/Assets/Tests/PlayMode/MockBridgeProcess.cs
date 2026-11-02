using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Mas3D.Tests
{
    /// <summary>
    /// 用真 node 进程跑 <c>tools/mock-bridge.mjs</c> 的测试替身：Task 2 的 PlayMode 用例连的是
    /// 真正的 WebSocket 服务端，不是内存假对象，所以 1008 关闭、端口没人听这类线缆行为都是真的。
    ///
    /// 启动参数（与 mock 脚本自己的 --help 对齐）：
    /// <c>node tools/mock-bridge.mjs --port &lt;port&gt; --scenario happy|timeout|error|command [--token TOKEN]</c>。
    /// 就绪判据是脚本向 stdout 打出的 "listening on ws://…" 那行，而不是盲等固定时间。
    /// Dispose 用 Kill(entireProcessTree: true) 收掉整棵进程树；重复 Dispose 不抛。
    /// </summary>
    public sealed class MockBridgeProcess : IDisposable
    {
        const string ReadyMarker = "listening on ws://";
        static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);
        static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(5);

        readonly Process _process;
        readonly StringBuilder _log = new StringBuilder();
        readonly object _gate = new object();
        bool _disposed;

        MockBridgeProcess(Process process, string scenario, int port, string commandLine)
        {
            _process = process;
            Scenario = scenario;
            Port = port;
            CommandLine = commandLine;
            Url = "ws://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/mas3d/ws";
        }

        /// <summary>测试实际连的端点（不含令牌，令牌由调用方塞进 <c>BridgeSettings.Token</c>）。</summary>
        public string Url { get; private set; }

        /// <summary>本次用的端口。</summary>
        public int Port { get; private set; }

        /// <summary>mock 的剧本名。</summary>
        public string Scenario { get; private set; }

        /// <summary>实际执行的命令行，写进测试日志便于报告复现。</summary>
        public string CommandLine { get; private set; }

        /// <summary>仓库根（Application.dataPath 是 &lt;repo&gt;/apps/unity/Assets，往上三层）。</summary>
        public static string RepoRoot
        {
            get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..")); }
        }

        /// <summary>让系统临时分配一个空闲 TCP 端口再还给 mock，避免并行/残留进程抢固定端口。</summary>
        public static int FindFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>起一个 mock 桥接并等它开始监听。失败时抛 <see cref="InvalidOperationException"/>，消息里带进程输出。</summary>
        public static MockBridgeProcess Start(string scenario, int port, string token = null)
        {
            if (string.IsNullOrEmpty(scenario)) throw new ArgumentException("scenario 不能为空", nameof(scenario));
            if (port <= 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port), port, "端口不合法");

            var repoRoot = RepoRoot;
            var script = Path.Combine(repoRoot, "tools", "mock-bridge.mjs");
            if (!File.Exists(script))
            {
                throw new FileNotFoundException("找不到 mock 桥接脚本：" + script, script);
            }

            var arguments = new StringBuilder();
            arguments.Append(Quote(script));
            arguments.Append(" --port ").Append(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            arguments.Append(" --scenario ").Append(scenario);
            if (!string.IsNullOrEmpty(token)) arguments.Append(" --token ").Append(Quote(token));

            var startInfo = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = arguments.ToString(),
                WorkingDirectory = repoRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            var process = new Process { StartInfo = startInfo };
            var mock = new MockBridgeProcess(process, scenario, port, "node " + arguments);
            try
            {
                process.Start();
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("起不了 mock 桥接：需要 node 在 PATH 上（" + error.Message + "）", error);
            }

            mock.DrainStreams();
            mock.WaitUntilReady();
            return mock;
        }

        /// <summary>进程输出（stdout + stderr），断言失败时贴进消息里方便定位。</summary>
        public string Output
        {
            get
            {
                lock (_gate) return _log.ToString();
            }
        }

        /// <summary>输出末尾若干字符，用于异常消息。</summary>
        public string Tail
        {
            get
            {
                var text = Output;
                const int limit = 800;
                return text.Length <= limit ? text : text.Substring(text.Length - limit);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (!_process.HasExited)
                {
                    // Unity 的 .NET Standard 2.1 档没有 Kill(entireProcessTree: true)（CS1501）；
                    // mock 就是直接的 node 进程、不派生子进程，Kill() + WaitForExit 就收干净了。
                    _process.Kill();
                    _process.WaitForExit((int)ExitTimeout.TotalMilliseconds);
                }
            }
            catch (Exception)
            {
                // 已经退出 / 已经被收掉：Dispose 必须幂等，这里不抛。
            }
            finally
            {
                _process.Dispose();
            }
        }

        void DrainStreams()
        {
            Task.Run(() => Drain(_process.StandardOutput, "out"));
            Task.Run(() => Drain(_process.StandardError, "err"));
        }

        void Drain(StreamReader reader, string stream)
        {
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lock (_gate) _log.Append('[').Append(stream).Append("] ").Append(line).Append('\n');
                }
            }
            catch (Exception)
            {
                // 进程被杀时管道会断开，安静收尾。
            }
        }

        void WaitUntilReady()
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < StartTimeout)
            {
                if (_process.HasExited)
                {
                    throw new InvalidOperationException(
                        "mock 桥接启动即退出（exit " + _process.ExitCode + "）：" + Tail);
                }
                if (Output.IndexOf(ReadyMarker, StringComparison.Ordinal) >= 0) return;
                Thread.Sleep(25);
            }

            throw new TimeoutException(
                "mock 桥接 " + StartTimeout.TotalSeconds + " 秒内没有开始监听：" + Tail);
        }

        /// <summary>把参数包成命令行的一段；mock 的参数都是简单取值（端口、场景名、令牌、路径）。</summary>
        static string Quote(string value)
        {
            if (value.IndexOf('"') < 0 && value.IndexOf(' ') < 0) return value;
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
