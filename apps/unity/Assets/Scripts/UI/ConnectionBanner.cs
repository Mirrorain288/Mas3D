using Mas3D.Boot;
using Mas3D.Net;
using UnityEngine;
using UnityEngine.UIElements;

namespace Mas3D.UI
{
    /// <summary>
    /// 面板顶上那条「桥接没起来」的提示（P6 Task 2）。
    ///
    /// 只做展示：不建连接、不改重连节奏、不碰协议。重连仍在 <see cref="BridgeClient"/> 里，
    /// 这里订阅它已经有的 <see cref="BridgeClient.StateChanged"/> / <see cref="BridgeClient.Failed"/>，
    /// 把状态翻译成人话。文案全在纯函数 <see cref="TextFor"/> 里，PlayMode 用例直接断言字符串。
    ///
    /// 组件挂在 <see cref="DialoguePanel"/> 同一条 GameObject 上（由 DialogueBridge.Connect 补上，
    /// 场景不用手摆）；没有这一节 UXML 时它什么都不做，不会把启动卡住。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ConnectionBanner : MonoBehaviour
    {
        /// <summary>连上了：先给人看一眼，再收起来。</summary>
        public const string ConnectedCopy = "已连接";

        /// <summary>连接中；参数是 host、port。</summary>
        public const string ConnectingFormat = "正在连接 {0}:{1}…";

        /// <summary>连不上/退避重试中；参数是 host、port、第几次、多少秒后重试。</summary>
        public const string FailedFormat = "连不上桥接 {0}:{1}（第 {2} 次，{3:F0}s 后重试）· 先启动 AstrBot，确认 bridge 正在监听";

        /// <summary>「已连接」停留的秒数（用 unscaled 时间，暂停游戏也照样收）。</summary>
        public const float ConnectedHideSeconds = 2f;

        /// <summary>倒计时刷新间隔：够看出在走，又不至于每帧改文本。</summary>
        const float RefreshSeconds = 0.5f;

        static readonly string DefaultHost;
        static readonly int DefaultPort;

        static ConnectionBanner()
        {
            BootArgs.SplitUrl(BridgeSettings.DefaultUrl, out DefaultHost, out DefaultPort);
        }

        /// <summary>场景里的 UIDocument；不填就用同一条 GameObject 上的。</summary>
        public UIDocument Document;

        /// <summary>提示条上写的端点；Bind 时按实际 settings.Url 覆盖。</summary>
        public string Host = "127.0.0.1";

        public int Port = 8790;

        VisualElement _banner;
        Label _text;
        BridgeClient _client;
        BridgeSettings _settings;

        int _attempt;
        float _retrySeconds;
        float _failedAt;
        float _nextRefresh;
        float _hideAt = -1f;
        bool _isFailure;

        /// <summary>提示条现在显示着没有（真可见性也由它决定；UXML 少一节时永远是 false）。</summary>
        public bool IsVisible { get; private set; }

        /// <summary>提示条上的字；没绑上 UXML 时是空串。</summary>
        public string Text
        {
            get { return _text != null ? _text.text : string.Empty; }
        }

        // ---------------------------------------------------------------- 纯函数

        /// <summary>默认端点（127.0.0.1:8790）下的三态文案；测试走这一条。</summary>
        public static string TextFor(BridgeState state, int attempt, float seconds)
        {
            return TextFor(state, attempt, seconds, DefaultHost, DefaultPort);
        }

        /// <summary>
        /// 三态文案：已连接 / 连接中 / 失败（退避重试）。失败态才带次数与倒计时。
        /// 纯函数——不读时间、不读组件状态，字符串可以被直接断言。
        /// </summary>
        public static string TextFor(BridgeState state, int attempt, float seconds, string host, int port)
        {
            if (state == BridgeState.Connected) return ConnectedCopy;
            if (state == BridgeState.Failed)
            {
                var nth = Mathf.Max(attempt, 1);
                return string.Format(FailedFormat, host, port, nth, Mathf.Max(seconds, 0f));
            }
            return string.Format(ConnectingFormat, host, port);
        }

        // ---------------------------------------------------------------- 绑定

        void Awake()
        {
            EnsureBound();
        }

        void OnEnable()
        {
            EnsureBound();
        }

        void OnDestroy()
        {
            Unbind();
        }

        /// <summary>把 UXML 里的 connection-banner / connection-text 找出来；树重建后会重新找。</summary>
        public void EnsureBound()
        {
            if (Document == null) Document = GetComponent<UIDocument>();
            if (Document == null) return;
            var root = Document.rootVisualElement;
            if (root == null) return;
            if (_banner != null && _banner.panel != null) return;

            _banner = root.Q<VisualElement>("connection-banner");
            _text = root.Q<Label>("connection-text");
            if (_banner != null)
            {
                _banner.style.display = IsVisible ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        /// <summary>订阅一个客户端。同一个 banner 重复 Bind 会先摘掉上一个。</summary>
        public void Bind(BridgeClient client)
        {
            Bind(client, null);
        }

        /// <summary>订阅客户端，并按 settings.Url 更正提示条上的端点。</summary>
        public void Bind(BridgeClient client, BridgeSettings settings)
        {
            EnsureBound();
            Unbind();

            _settings = settings;
            if (settings != null) BootArgs.SplitUrl(settings.Url, out Host, out Port);

            _attempt = 0;
            _client = client;
            if (_client == null) return;

            _client.StateChanged += OnStateChanged;
            _client.Failed += OnFailed;

            // Start() 之后通常已经在 Connecting：先把"正在连接"摆出来，别让玩家对着空白等。
            if (_client.State == BridgeState.Connecting || _client.State == BridgeState.Idle) ShowConnecting();
        }

        /// <summary>摘掉订阅（换客户端、场景卸载、测试拆台）。</summary>
        public void Unbind()
        {
            if (_client == null) return;
            _client.StateChanged -= OnStateChanged;
            _client.Failed -= OnFailed;
            _client = null;
        }

        // ---------------------------------------------------------------- 展示

        /// <summary>显示「正在连接 host:port…」。</summary>
        public void ShowConnecting()
        {
            _isFailure = false;
            _hideAt = -1f;
            Show(TextFor(BridgeState.Connecting, 0, 0f, Host, Port));
        }

        /// <summary>显示「连不上…第 n 次…」。attempt 从 1 起，seconds 是这次退避的秒数。</summary>
        public void ShowFailed(int attempt, float seconds)
        {
            _isFailure = true;
            _hideAt = -1f;
            _attempt = Mathf.Max(attempt, 1);
            _retrySeconds = Mathf.Max(seconds, 0f);
            _failedAt = Time.unscaledTime;
            _nextRefresh = _failedAt + RefreshSeconds;
            Show(TextFor(BridgeState.Failed, _attempt, _retrySeconds, Host, Port));
        }

        /// <summary>显示「已连接」，<see cref="ConnectedHideSeconds"/> 之后自己收起来。</summary>
        public void ShowConnected()
        {
            _isFailure = false;
            Show(ConnectedCopy);
            _hideAt = Time.unscaledTime + ConnectedHideSeconds;
        }

        /// <summary>收起来，字也清掉：下次显示不该看到上一次的旧文案。</summary>
        public void Hide()
        {
            _isFailure = false;
            _hideAt = -1f;
            if (_banner != null) _banner.style.display = DisplayStyle.None;
            if (_text != null) _text.text = string.Empty;
            IsVisible = false;
        }

        void Show(string text)
        {
            EnsureBound();
            if (_text != null) _text.text = text;
            if (_banner != null) _banner.style.display = DisplayStyle.Flex;
            IsVisible = _banner != null;
        }

        void Update()
        {
            Tick(Time.unscaledTime);
        }

        /// <summary>时间推进（测试直接调，不必真等 2 s）。</summary>
        public void Tick(float now)
        {
            if (_hideAt >= 0f && now >= _hideAt) Hide();
            if (!_isFailure || !IsVisible || _text == null) return;
            if (now < _nextRefresh) return;

            _nextRefresh = now + RefreshSeconds;
            var remaining = Mathf.Max(0f, _retrySeconds - (now - _failedAt));
            _text.text = TextFor(BridgeState.Failed, _attempt, remaining, Host, Port);
        }

        // ---------------------------------------------------------------- 事件

        void OnStateChanged(BridgeState state)
        {
            if (state == BridgeState.Connected) ShowConnected();
            else if (state == BridgeState.Connecting) ShowConnecting();
        }

        void OnFailed(string message)
        {
            _attempt++;
            ShowFailed(_attempt, DelayFor(_attempt));
        }

        /// <summary>退避节奏跟 BridgeClient 一致：settings.BackoffSeconds，取不到就 0.5 s。</summary>
        float DelayFor(int attempt)
        {
            var backoff = _settings != null ? _settings.BackoffSeconds : null;
            if (backoff == null || backoff.Length == 0) return 0.5f;
            return backoff[Mathf.Clamp(attempt - 1, 0, backoff.Length - 1)];
        }
    }
}
