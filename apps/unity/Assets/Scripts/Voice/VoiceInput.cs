using System;
using System.Collections;
using Mas3D.Net;
using Mas3D.UI;
using UnityEngine;

namespace Mas3D.Voice
{
    /// <summary>
    /// 按住 V 说话：按下开始录，松开编码成 WAV 送去识别，识别结果只写进输入框，不发送。
    ///
    /// 三段职责分得开：
    /// - 键盘：Update 只把 GetKeyDown/GetKeyUp 转给 <see cref="Press"/>/<see cref="Release"/>；
    /// - 录音：只认 <see cref="IRecorder"/>，测试用假实现，真麦克风只在 <see cref="MicrophoneRecorder"/>；
    /// - 网络：<see cref="Poster"/> 留了一条缝，测试注入假发送器，运行时为空就走 <see cref="Client"/>。
    ///
    /// 状态词只覆盖“语音这一层知道的事”（在听 / 没听清 / 识别没响应）；
    /// 待机、发送中、已回复那些归 ConversationController，这里不碰。
    ///
    /// 快照只认玩家的动作：松开时记下输入框原文，玩家自己按 Enter 发走这一句
    /// （<see cref="DialoguePanel.Submitted"/>）或另起一段录音，快照立刻作废。
    /// 控制器在请求飞行途中会清空/写回同一个框，拿文字比对分不清“玩家改的”和“控制器改的”。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VoiceInput : MonoBehaviour
    {
        /// <summary>短于这个长度就当没说话：不编码、不发请求。</summary>
        public const float MinSeconds = 0.3f;

        /// <summary>按下之后的提示。</summary>
        public const string ListeningCopy = "正在听…（松开 V 提交）";

        /// <summary>识别结果已经填进输入框、等玩家自己按 Enter 时的提示。</summary>
        public const string ReadyCopy = "已填入，按 Enter 发送";

        /// <summary>太短 / 没有样本 / 识别结果为空时的提示。</summary>
        public const string EmptyCopy = "没听清，再说一次？";

        /// <summary>传输或识别失败时的提示。</summary>
        public const string FailCopy = "语音识别没有响应，先打字吧。";

        /// <summary>面板：状态词与输入框都通过它落下去。</summary>
        public DialoguePanel Panel;

        /// <summary>桥接设置：只取 Token 与 host/port 拼 ASR 端点。</summary>
        public BridgeSettings Settings = new BridgeSettings();

        /// <summary>按住说话用的键。</summary>
        public KeyCode HoldKey = KeyCode.V;

        /// <summary>录音器；运行时由 <see cref="Bind"/> 装上 <see cref="MicrophoneRecorder"/>。</summary>
        public IRecorder Recorder;

        /// <summary>ASR 客户端；运行时由 <see cref="Bind"/> 按 Settings 配好。</summary>
        public AsrClient Client;

        /// <summary>
        /// 把 WAV 送出去的那一层。为 null 时走 <see cref="Client"/>；
        /// 测试注入假实现，这样松手不会真的打网络。
        /// </summary>
        public Func<byte[], Action<AsrResult>, Action<string>, IEnumerator> Poster;

        /// <summary>Update 里读不读键盘。测试关掉它，直接调 Press/Release。</summary>
        public bool KeyboardInput = true;

        bool _listening;
        bool _warnedMissing;

        /// <summary>采集代号：每次按下与 OnDisable 都 +1，旧代号回来的回执一律丢掉。</summary>
        int _generation;

        /// <summary>当前这次识别请求的协程；换代/禁用时停掉，别让旧请求还挂在组件上。</summary>
        Coroutine _request;

        /// <summary>
        /// 这一段快照已经被玩家取代（他自己按 Enter 把这一句发走了）、或另起了一段录音。
        /// 只认玩家的动作，不猜文字 —— 控制器在请求飞行途中会清空（提交成功）或写回（提交被拒）
        /// 同一个框，拿文字比对永远分不清那一下是谁改的。
        /// </summary>
        bool _superseded;

        /// <summary>当前订阅着 <see cref="DialoguePanel.Submitted"/> 的面板；重绑/禁用时照它退订。</summary>
        DialoguePanel _subscribedPanel;

        /// <summary>这一次按住期间玩家自己发过言（按过 Enter）。松手时用它决定这一段快照还算不算数。</summary>
        bool _submittedThisTake;

        /// <summary>正在录。</summary>
        public bool Listening { get { return _listening; } }

        /// <summary>语音层最后一次写下去的状态词；没写过是 null。</summary>
        public string LastStatus { get; private set; }

        void Update()
        {
            if (!KeyboardInput) return;

            if (!Ready())
            {
                // 面板或录音器没了（面板被销毁、组件没接线）：键盘路径再也收不了尾，
                // 但麦克风可能还开着 —— 在这里把这一段丢掉，别让它一路录到 30 秒的片段上限。
                if (_listening) AbandonTake();
                return;
            }

            // 焦点在输入框里时 V 是普通字符：只挡“起录”，别把玩家正在打的字打断。
            // 松手的两条路径始终放行 —— 否则录音中鼠标点进输入框，Release 会被整段跳过，
            // 麦克风要等焦点挪开或 OnDisable 才关。Press()/Release() 接缝不受影响。
            var typing = Panel != null && Panel.IsInputFocused;

            // 只认 GetKeyDown/GetKeyUp：按住时的重复帧是 GetKey，不会把录音重启。
            if (Input.GetKeyDown(HoldKey))
            {
                if (!typing) Press();
            }
            else if (_listening && !Input.GetKey(HoldKey))
            {
                // 切到别的窗口（alt-tab）时系统会把 GetKeyUp 吞掉，录音就一直开着：
                // 只要“按住”不再成立就当作松手。
                Release();
            }
            if (Input.GetKeyUp(HoldKey)) Release();
        }

        void OnEnable()
        {
            // 面板也可能是序列化字段直接挂上的（没走 Bind）：启用时先接一次玩家的提交。
            SubscribePanel();
        }

        void OnDisable()
        {
            // 卸载/关掉之后不许再被面板回调：先退订，再看麦克风。
            UnsubscribePanel();
            _superseded = false;

            // 场景卸载/组件关掉时别把麦克风留在录音状态。
            _generation++;    // 之后到的回执都属于上一代，丢掉
            CancelRequest();  // 先停请求协程，再停麦克风

            if (!_listening) return;
            _listening = false;
            if (Recorder == null || !Recorder.Recording) return;
            int sampleRate;
            Recorder.Stop(out sampleRate);
        }

        /// <summary>按下（Update 的 GetKeyDown 分支）。已经在录时忽略按键重复。</summary>
        public void Press()
        {
            if (!Ready()) return;
            if (_listening) return;

            _generation++;    // 新的采集开始：上一段的回执从这一刻起作废
            _superseded = true;   // 新的一段会接管输入框：上一条快照同样作废
            _submittedThisTake = false;
            SubscribePanel(); // 面板是字段直接挂上的那种接法：按下时补上订阅（重复调用不会挂两次）
            CancelRequest();  // 上一段还挂着的请求协程也停掉

            if (!Recorder.Start())
            {
                SetStatus(string.IsNullOrEmpty(Recorder.Error) ? FailCopy : Recorder.Error);
                return;
            }

            _listening = true;
            SetStatus(ListeningCopy);
        }

        /// <summary>松开（Update 的 GetKeyUp 分支）。没在录时什么都不做。</summary>
        public void Release()
        {
            if (!_listening) return;
            _listening = false;
            // 下面这份快照从这一刻起生效 —— 除非玩家在按住期间已经把上一句发走了：
            // 那一次提交必须一直算数，否则迟到的转写会落进控制器刚清空的框里（再回车就是重发）。
            _superseded = _submittedThisTake;
            _submittedThisTake = false;

            if (!Ready())
            {
                // 面板掉了也要把设备关上：Stop 一次，这一条 take 直接丢弃。
                if (Recorder != null) Recorder.Stop(out _);
                return;
            }

            // 先停录音再编码：Stop 之后麦克风缓冲就固定了。
            int sampleRate;
            var samples = Recorder.Stop(out sampleRate);

            if (samples == null || samples.Length == 0 || sampleRate <= 0)
            {
                SetStatus(EmptyCopy);
                return;
            }
            if (samples.Length < MinSeconds * sampleRate)
            {
                SetStatus(EmptyCopy);
                return;
            }
            if (Poster == null && Client == null)
            {
                SetStatus(FailCopy);
                return;
            }

            var wav = WavEncoder.Encode(samples, sampleRate);

            // 请求开始时的输入框快照 + 采集代号：回执回来时才知道玩家改没改、这一代还作不作数。
            var generation = _generation;
            var draft = Panel != null ? Panel.InputText : string.Empty;

            // 发送这一层也留缝：Poster 优先，测试因此碰不到 Client/网络。
            IEnumerator Send(Action<AsrResult> onOk, Action<string> onError)
            {
                return Poster != null ? Poster(wav, onOk, onError) : Client.Transcribe(wav, onOk, onError);
            }

            var request = Send(
                result => OnTranscribed(result, generation, draft),
                message => OnFailed(message, generation));
            if (request == null) return;

            CancelRequest();   // 上一段的协程即便还在跑也停掉：它的回执已经过期
            _request = StartCoroutine(request);
        }

        /// <summary>停掉当前识别请求的协程并清掉句柄；没有请求时什么都不做。</summary>
        void CancelRequest()
        {
            // 先掐连接再停协程：只 StopCoroutine 的话请求还在飞，桥接那边的 worker 会继续
            // 推理并占着 GPU 锁，下一次采集可能等满 15 秒拿到 503。
            if (Client != null) Client.Abort();

            if (_request == null) return;
            StopCoroutine(_request);
            _request = null;
        }

        /// <summary>把当前这一段 take 丢掉：关掉录音器、作废回执；面板还在就落一句空文案。</summary>
        void AbandonTake()
        {
            _listening = false;
            _superseded = false;
            _submittedThisTake = false;
            _generation++;   // 这一段如果已经发出去过，回执同样作废
            if (Recorder != null && Recorder.Recording) Recorder.Stop(out _);
            if (Panel != null) SetStatus(EmptyCopy);
        }

        /// <summary>接上场景：面板、桥接设置，并补齐录音器/客户端。</summary>
        public void Bind(DialoguePanel panel, BridgeSettings settings)
        {
            Panel = panel;
            SubscribePanel();   // 换面板时先从旧面板退订，再接上玩家的提交
            Settings = settings ?? new BridgeSettings();

            if (Recorder == null) Recorder = new MicrophoneRecorder();
            if (Client == null) Client = new AsrClient();

            Client.Endpoint = AsrEndpoint(Settings);
            Client.Token = Settings.Token ?? string.Empty;
        }

        // ---- 回调 -------------------------------------------------------------

        void OnTranscribed(AsrResult result, int generation, string draft)
        {
            if (generation != _generation) return;   // 被取代的采集：回执丢掉

            // 玩家已经自己动过面板（按 Enter 发走了这一句）：他已经在下一件事上。
            // 这时框可能被控制器清空或写回，回执再碰一下都会写错（重复发送、状态词串台）。
            if (_superseded)
            {
                _superseded = false;
                return;
            }

            if (!string.IsNullOrEmpty(result.Text))
            {
                if (Panel != null)
                {
                    var current = Panel.InputText;
                    // 玩家没动过、或自己把框清空了（空框追加只会多一个前导空格）：覆盖；
                    // 框里还有别的字（真在打字）：追加，绝不吞掉刚打的字。
                    var untouched = current == draft || string.IsNullOrWhiteSpace(current);
                    // 覆盖也走唯一写入口（值 + 光标一起搬）；追加交给 AppendToInput，别在这里自己拼字符串写 value。
                    if (untouched) Panel.InputText = result.Text;
                    else Panel.AppendToInput(" " + result.Text);
                    SetStatus(ReadyCopy);
                }
                return;
            }
            SetStatus(EmptyCopy);
        }

        void OnFailed(string message, int generation)
        {
            if (generation != _generation) return;

            // 玩家已经走了：状态词归控制器（发送中/思考中），失败文案不许盖上去。
            if (_superseded)
            {
                _superseded = false;
                return;
            }

            SetStatus(FailCopy);
            if (!string.IsNullOrEmpty(message)) Debug.Log("[VoiceInput] 识别失败：" + message);
        }

        // ---- 玩家的提交 -------------------------------------------------------

        /// <summary>接上面板的 Submitted：重复调用不会挂两次，换面板时先从旧面板退掉。</summary>
        void SubscribePanel()
        {
            if (ReferenceEquals(_subscribedPanel, Panel)) return;
            UnsubscribePanel();
            _subscribedPanel = Panel;
            if (_subscribedPanel != null) _subscribedPanel.Submitted += OnPlayerSubmitted;
        }

        void UnsubscribePanel()
        {
            var panel = _subscribedPanel;
            _subscribedPanel = null;
            if (panel == null) return;   // Unity 的假 null：面板已销毁，事件源也没了
            panel.Submitted -= OnPlayerSubmitted;
        }

        /// <summary>玩家按了 Enter/发送：输入框马上要由控制器清空或写回，这一段的快照不再作数。</summary>
        void OnPlayerSubmitted(string text)
        {
            _superseded = true;
            _submittedThisTake = true;   // 按住期间发走的这一句，松手后也不能被迟到回执盖回来
        }

        // ---- 小工具 -----------------------------------------------------------

        void SetStatus(string text)
        {
            LastStatus = text;
            if (Panel != null) Panel.SetStatus(text);
        }

        /// <summary>面板/录音器缺一个就只警告一次、之后什么都不做（Update 每帧都会问）。</summary>
        bool Ready()
        {
            if (Panel != null && Recorder != null) return true;
            if (!_warnedMissing)
            {
                _warnedMissing = true;
                Debug.LogWarning("[VoiceInput] Panel 或 Recorder 没接上，按住 " + HoldKey + " 不会工作。");
            }
            return false;
        }

        /// <summary>
        /// 桥接的 ws 端点 → ASR 的 http 端点：沿用同一个 host/port，路径固定成 /mas3d/api/asr。
        /// 解析不了就退回 <see cref="AsrClient.DefaultEndpoint"/>。
        /// </summary>
        static string AsrEndpoint(BridgeSettings settings)
        {
            Uri uri;
            if (settings == null || string.IsNullOrEmpty(settings.Url) ||
                !Uri.TryCreate(settings.Url, UriKind.Absolute, out uri))
            {
                return AsrClient.DefaultEndpoint;
            }

            var scheme = uri.Scheme == "wss" ? "https" : "http";
            var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port;
            return scheme + "://" + uri.Host + port + "/mas3d/api/asr";
        }
    }
}
