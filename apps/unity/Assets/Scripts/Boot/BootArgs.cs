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
    ///   -nobody [0|1]                    不给玩家挂身体（P7 Task 3）；不写值等于 1
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

        /// <summary>
        /// -nobody 开着就不给玩家挂 VRM。留这个开关不是为了好看：截图机位、离线对比、以及
        /// "身体是不是把某条回归弄红了"这类二分，都需要一个没有身体的玩家。
        /// </summary>
        public readonly bool NoBody;

        BootArgs(string host, int port, string token, string selfTestPath, bool hasBridge, bool noBody)
        {
            Host = host;
            Port = port;
            Token = token;
            SelfTestPath = selfTestPath;
            HasBridge = hasBridge;
            NoBody = noBody;
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
            var noBody = false;

            // 单独先扫一遍 -nobody：它允许不写值（-nobody 就是开），而下面那条循环按"键值成对"走，
            // 结尾孤零零的开关会被 i + 1 的边界直接跳过。
            if (args != null)
            {
                for (var i = 0; i < args.Length; i++)
                {
                    if (!string.Equals(args[i], "-nobody", System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (i + 1 < args.Length && IsFlagValue(args[i + 1]))
                    {
                        noBody = ParseSwitch(args[i + 1]);
                        i++;
                    }
                    else
                    {
                        noBody = true;
                    }
                }
            }

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

            // -token -nobody 这种写法：值会被预扫当成开关，令牌就变成了字符串 "-nobody"。
            // 与其带着一个假令牌去撞 401，不如当作没给（桥接回 401，播报照常降级）。
            if (token != null && token.StartsWith("-", System.StringComparison.Ordinal))
            {
                Debug.LogWarning("[boot] -token 的值看起来是另一个开关（" + token + "），按没给令牌处理");
                token = null;
            }

            return new BootArgs(host, port, token, selfTest, hasBridge, noBody);
        }

        /// <summary>下一个参数是"值"还是"另一个开关"：以 - 开头就当成开关，别把 -selftest 吃掉。</summary>
        static bool IsFlagValue(string value)
        {
            return !string.IsNullOrEmpty(value) && !value.StartsWith("-", System.StringComparison.Ordinal);
        }

        /// <summary>0/false/off/no 是关，1/true/on/yes 是开；认不出来的值按开处理，并留一条警告。</summary>
        static bool ParseSwitch(string value)
        {
            var text = (value ?? string.Empty).Trim();
            if (text == "0"
                || text.Equals("false", System.StringComparison.OrdinalIgnoreCase)
                || text.Equals("off", System.StringComparison.OrdinalIgnoreCase)
                || text.Equals("no", System.StringComparison.OrdinalIgnoreCase)) return false;

            if (text == "1"
                || text.Equals("true", System.StringComparison.OrdinalIgnoreCase)
                || text.Equals("on", System.StringComparison.OrdinalIgnoreCase)
                || text.Equals("yes", System.StringComparison.OrdinalIgnoreCase)) return true;

            Debug.LogWarning("[boot] 看不懂的 -nobody 取值：" + text + "（按开处理）");
            return true;
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
