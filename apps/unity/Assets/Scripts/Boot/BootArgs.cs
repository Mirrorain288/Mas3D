using System.Globalization;
using Mas3D.Net;
using UnityEngine;

namespace Mas3D.Boot
{
    /// <summary>
    /// 启动参数（P6 Task 2）。player 和编辑器都能带：
    ///   -bridge host:port / -bridge port   桥接地址；只给端口时用本机回环
    ///   -token &lt;str&gt;                    临时令牌，优先于磁盘上那一份
    ///   -selftest &lt;path&gt;                 自检模式的落盘路径（Task 3 用）
    /// 其余参数一律忽略：-batchmode / -logFile / -projectPath 之类是 Unity 自己的，认不出来不算错。
    ///
    /// 非法端口不猜：HasBridge=false + 一条警告。把"地址写错了"伪装成"连上了但没反应"，
    /// 比直接报错难查得多。
    /// </summary>
    public readonly struct BootArgs
    {
        /// <summary>只给端口时用的 host（桥接默认就在本机）。</summary>
        public const string DefaultHost = "127.0.0.1";

        /// <summary>与 BridgeSettings.DefaultUrl 里的端口一致。</summary>
        public const int DefaultPort = 8790;

        public readonly string Host;
        public readonly int Port;

        /// <summary>-token 给的令牌；没给是 null（交给磁盘令牌）。</summary>
        public readonly string Token;

        /// <summary>-selftest 给的路径；没给是 null（不进自检）。</summary>
        public readonly string SelfTestPath;

        /// <summary>命令行是否显式指定了桥接地址。</summary>
        public readonly bool HasBridge;

        BootArgs(string host, int port, string token, string selfTestPath, bool hasBridge)
        {
            Host = host;
            Port = port;
            Token = token;
            SelfTestPath = selfTestPath;
            HasBridge = hasBridge;
        }

        /// <summary>host:port，日志行直接拼它。</summary>
        public string Endpoint
        {
            get { return Host + ":" + Port; }
        }

        /// <summary>解析一整条命令行（Environment.GetCommandLineArgs() 原样传进来）。</summary>
        public static BootArgs Parse(string[] args)
        {
            var host = DefaultHost;
            var port = DefaultPort;
            string token = null;
            string selfTest = null;
            var hasBridge = false;

            if (args != null)
            {
                for (var i = 0; i + 1 < args.Length; i++)
                {
                    var key = args[i];
                    if (string.IsNullOrEmpty(key)) continue;

                    if (key == "-bridge")
                    {
                        var value = args[i + 1];
                        i++;   // 值已经吃掉，下一轮从它后面继续
                        if (TryParseEndpoint(value, out host, out port))
                        {
                            hasBridge = true;
                        }
                        else
                        {
                            hasBridge = false;
                            host = DefaultHost;
                            port = DefaultPort;
                            Debug.LogWarning("[boot] 忽略非法 -bridge 参数：" + (value ?? "null") + "（用法 -bridge host:port 或 -bridge port）");
                        }
                    }
                    else if (key == "-token")
                    {
                        token = (args[i + 1] ?? string.Empty).Trim();
                        i++;
                    }
                    else if (key == "-selftest")
                    {
                        selfTest = args[i + 1];
                        i++;
                    }
                }
            }

            return new BootArgs(host, port, token, selfTest, hasBridge);
        }

        /// <summary>
        /// 把解析结果落到一份 <see cref="BridgeSettings"/> 上——这是"应用"的唯一写入点，纯静态、不碰场景，
        /// EditMode 能直接测（RoomBootstrap 只负责找到那份 settings 再调这里）。
        /// <see cref="BridgeSettings"/> 没有 Host/Port 字段，所以地址按它自己的 ws://host:port/mas3d/ws
        /// 约定拼回去；没给 -bridge 就一个字节都不改，没给 -token 就保留原令牌（磁盘那份由 DialogueBridge 读）。
        /// </summary>
        public static void ApplyTo(BridgeSettings settings, BootArgs boot)
        {
            if (settings == null) return;
            if (boot.HasBridge) settings.Url = "ws://" + boot.Host + ":" + boot.Port + "/mas3d/ws";
            if (!string.IsNullOrEmpty(boot.Token)) settings.Token = boot.Token;
        }

        /// <summary>命令行里的 host:port，或只给端口（1..65535）。false 时两个 out 值都别用。</summary>
        public static bool TryParseEndpoint(string value, out string host, out int port)
        {
            host = DefaultHost;
            port = DefaultPort;
            if (string.IsNullOrEmpty(value)) return false;

            var text = value.Trim();
            var parsedHost = DefaultHost;
            string portText;

            var colon = text.LastIndexOf(':');
            if (colon < 0)
            {
                portText = text;
            }
            else
            {
                portText = text.Substring(colon + 1);
                var head = text.Substring(0, colon).Trim();
                if (head.Length > 0) parsedHost = head;
            }

            int parsedPort;
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out parsedPort)) return false;
            if (parsedPort < 1 || parsedPort > 65535) return false;

            host = parsedHost;
            port = parsedPort;
            return true;
        }

        /// <summary>
        /// 从 ws://host:port/path 里取端点（日志与提示条用）。解析不出来就给默认值：
        /// 这里只是显示，连接用的是原样的 Url，不许因为显示失败改动配置。
        /// </summary>
        public static void SplitUrl(string url, out string host, out int port)
        {
            host = DefaultHost;
            port = DefaultPort;
            if (string.IsNullOrEmpty(url)) return;

            var scheme = url.IndexOf("://", System.StringComparison.Ordinal);
            var rest = scheme >= 0 ? url.Substring(scheme + 3) : url;
            var slash = rest.IndexOf('/');
            if (slash >= 0) rest = rest.Substring(0, slash);

            var colon = rest.LastIndexOf(':');
            if (colon < 0)
            {
                if (rest.Length > 0) host = rest;
                return;
            }

            var head = rest.Substring(0, colon).Trim();
            if (head.Length > 0) host = head;

            int parsedPort;
            if (int.TryParse(rest.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out parsedPort)) port = parsedPort;
        }
    }
}
