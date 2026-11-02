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
    /// 令牌从 <see cref="BridgeSettings.TokenFilePath"/>（apps/bridge/data/mas3d/token）读。
    /// 读不到就不连——没令牌连上去只会被 1008 踢掉，还会在面板上留下一条吓人的错误；
    /// 状态行改成“桥接未配置令牌”，面板其余部分照常可用（离线兜底）。
    /// </summary>
    [DisallowMultipleComponent]
    // 排在 ConversationController.Start（默认 0）之后：两条都在 Start 改状态行，后说的算。
    [DefaultExecutionOrder(100)]
    public sealed class DialogueBridge : MonoBehaviour
    {
        public const string MissingTokenCopy = "桥接未配置令牌";

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
            if (Client != null) return true;

            var settings = Settings ?? (Settings = new BridgeSettings());
            var token = (settings.Token ?? string.Empty).Trim();
            if (token.Length == 0) token = ReadTokenFromDisk();
            if (token.Length == 0)
            {
                Debug.LogWarning("[dialogue] " + MissingTokenCopy + "：" + BridgeSettings.TokenFilePath);
                if (Panel != null) Panel.SetStatus(MissingTokenCopy);
                return false;
            }

            settings.Token = token;
            var client = new BridgeClient();
            client.Configure(settings);
            Client = client;
            if (Controller != null) Controller.Client = client;
            client.Start();
            Debug.Log("[dialogue] bridge -> " + settings.Url + " token=" + token.Length + " 字节");
            return true;
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

        /// <summary>把令牌文件读成一行；文件不在或读不动都当没有令牌。</summary>
        public static string ReadTokenFromDisk()
        {
            if (TokenFileReader != null) return (TokenFileReader() ?? string.Empty).Trim();
            var path = BridgeSettings.TokenFilePath;
            try
            {
                return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
            }
            catch (IOException e)
            {
                Debug.LogWarning("[dialogue] 读令牌失败：" + path + "：" + e.Message);
                return string.Empty;
            }
        }
    }
}
