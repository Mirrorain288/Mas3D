using System.IO;
using Mas3D.Conversation;
using Mas3D.Net;
using Mas3D.UI;
using UnityEngine;

namespace Mas3D
{
    /// <summary>
    /// 场景里的桥接接线。BridgeClient 是普通类（不是 UnityEngine.Object），序列化不进场景，
    /// 所以场景里只放这个组件：它在 Start 时按 BridgeSettings 建客户端、交给 ConversationController 去 Pump。
    ///
    /// 令牌按 <see cref="BridgeSettings.RuntimeTokenCandidates"/> 的顺序逐个试（仓库里那份 → exe 旁边
    /// → persistentDataPath），取第一个非空；-token 给的仍然最高优先。读不到就不连——没令牌连上去只会被
    /// 1008 踢掉，还会在面板上留下一条吓人的错误；状态行改成「桥接未配置令牌」，面板其余部分照常可用（离线兜底）。
    /// </summary>
    [DisallowMultipleComponent]
    // 排在 ConversationController.Start（默认 0）之后：两条都在 Start 改状态行，后说的算。
    [DefaultExecutionOrder(100)]
    public sealed class DialogueBridge : MonoBehaviour
    {
        public const string MissingTokenCopy = "桥接未配置令牌";

        /// <summary>找不到令牌（或没指定来源）时 source 的取值。</summary>
        public const string NoTokenSource = "none";

        /// <summary>-token 显式给的那份令牌，来源记成它。</summary>
        public const string ArgTokenSource = "-token";

        public ConversationController Controller;
        public DialoguePanel Panel;
        public BridgeSettings Settings = new BridgeSettings();

        /// <summary>建出来的客户端；Connect 之前是 null。</summary>
        public BridgeClient Client { get; private set; }

        void Start() => Connect();

        void OnDestroy() => Shutdown();

        /// <summary>
        /// 建客户端并开连。幂等：场景里的 Start 和（测试/调试里的）显式调用谁先到都行。
        /// 返回 false 表示没连：没有令牌。
        /// </summary>
        public bool Connect()
        {
            // 提示条先挂上：没有令牌时它不显示（状态行会说"未配置令牌"），有令牌时它从"正在连接…"
            // 一路说到"连不上…先启动 AstrBot"。场景里不手摆这个组件，换面板也不会漏。
            var banner = AttachBanner();
            if (Client != null) return true;

            var settings = Settings ?? (Settings = new BridgeSettings());
            var token = (settings.Token ?? string.Empty).Trim();
            var source = ArgTokenSource;
            if (token.Length == 0)
            {
                token = ReadTokenFromDisk(out source);
            }

            if (token.Length == 0)
            {
                Debug.LogWarning("[dialogue] " + MissingTokenCopy + "：试过 " + string.Join("；", BridgeSettings.RuntimeTokenCandidates()));
                if (Panel != null) Panel.SetStatus(MissingTokenCopy);
                return false;
            }
            Debug.Log("[dialogue] token <- " + source + "（" + token.Length + " 字节）");

            settings.Token = token;
            var client = new BridgeClient();
            client.Configure(settings);
            Client = client;
            if (Controller != null) Controller.Client = client;
            client.Start();
            if (banner != null) banner.Bind(client, settings);
            Debug.Log("[dialogue] bridge -> " + settings.Url + " token=" + token.Length + " 字节");
            return true;
        }

        /// <summary>
        /// 面板同一条 GameObject 上挂 <see cref="ConnectionBanner"/>（P6 Task 2）。没有面板
        /// （无 UI 的测试场景）就返回 null——提示条只是提示，不能因为它缺了连桥接都不建。
        /// </summary>
        ConnectionBanner AttachBanner()
        {
            var panel = Panel != null ? Panel : FindAnyObjectByType<DialoguePanel>();
            if (panel == null) return null;
            var banner = panel.GetComponent<ConnectionBanner>();
            if (banner == null) banner = panel.gameObject.AddComponent<ConnectionBanner>();
            return banner;
        }

        /// <summary>停掉并扔掉客户端。场景卸载、测试拆台都走这里。</summary>
        public void Shutdown()
        {
            if (Client == null) return;
            if (Controller != null && ReferenceEquals(Controller.Client, Client)) Controller.Client = null;
            Client.Dispose();
            Client = null;
        }

        /// <summary>测试接缝：不为 null 时用它代替读令牌文件，模拟“文件不在”。</summary>
        public static System.Func<string> TokenFileReader;

        /// <summary>把令牌读成一行；文件不在或读不动都当没有令牌。</summary>
        public static string ReadTokenFromDisk()
        {
            string _;
            return ReadTokenFromDisk(out _);
        }

        /// <summary>
        /// 同上，另给“从哪读的”：候选路径或 <see cref="NoTokenSource"/>。候选表见
        /// <see cref="BridgeSettings.RuntimeTokenCandidates"/>；一个一个试，取第一个非空。
        /// </summary>
        public static string ReadTokenFromDisk(out string sourcePath)
        {
            if (TokenFileReader != null)
            {
                var text = (TokenFileReader() ?? string.Empty).Trim();
                sourcePath = text.Length > 0 ? "reader" : NoTokenSource;
                return text;
            }
            return ReadFirstToken(BridgeSettings.RuntimeTokenCandidates(), out sourcePath);
        }

        /// <summary>
        /// 按顺序读候选，返回第一个非空内容；纯读取，不打日志（调用方决定怎么说）。空文件跳过而不是当成令牌。
        /// </summary>
        public static string ReadFirstToken(string[] candidates, out string usedPath)
        {
            usedPath = NoTokenSource;
            if (candidates == null) return string.Empty;

            foreach (var path in candidates)
            {
                if (string.IsNullOrEmpty(path)) continue;

                string text;
                try
                {
                    if (!File.Exists(path)) continue;
                    text = (File.ReadAllText(path) ?? string.Empty).Trim();
                }
                catch (IOException e)
                {
                    Debug.LogWarning("[dialogue] 读令牌失败：" + path + "：" + e.Message);
                    continue;
                }
                catch (System.UnauthorizedAccessException e)
                {
                    Debug.LogWarning("[dialogue] 读令牌失败：" + path + "：" + e.Message);
                    continue;
                }

                if (text.Length == 0) continue;
                usedPath = path;
                return text;
            }
            return string.Empty;
        }
    }
}
