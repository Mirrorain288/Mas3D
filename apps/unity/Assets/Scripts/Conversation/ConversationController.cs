using Mas3D.Net;
using Mas3D.UI;
using UnityEngine;

namespace Mas3D.Conversation
{
    /// <summary>一回合走到哪一步了。Idle 之外的四步与面板上的状态词一一对应。</summary>
    public enum TurnPhase
    {
        Idle,
        Sending,
        Thinking,
        Answered,
        Failed
    }

    /// <summary>
    /// 会话状态机（Spec ui/dialogue-console）：Submit 记下这一轮，帧回来了再决定收尾。
    ///
    /// 帧只有 <see cref="BridgeClient"/> 往下送（Update 里 Pump），所以这一层不用自己的计时器：
    /// 超时是后端主动发 error{code:"timeout"}，断了是 <see cref="BridgeClient"/> 自己重连。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ConversationController : MonoBehaviour
    {
        const string StatusIdle = "待机";
        const string StatusSending = "发送中";
        const string StatusThinking = "思考中";
        const string StatusAnswered = "已回复";
        const string StatusFailed = "未回应";

        /// <summary>通道。场景里由 P4 挂到 Session 上，这里只认实例。</summary>
        public BridgeClient Client;

        /// <summary>面板。消息流、状态词、输入锁都通过它落下去。</summary>
        public DialoguePanel Panel;

        DialoguePanel _wiredPanel;
        BridgeClient _wiredClient;

        /// <summary>ack 带回来的 turnId；留到 reply/error 用来认领这一轮。</summary>
        public string PendingTurnId { get; private set; }

        /// <summary>当前回合的阶段。</summary>
        public TurnPhase Phase { get; private set; }

        void OnEnable()
        {
            EnsureWired();
        }

        void Start()
        {
            EnsureWired();
            if (Panel != null && Phase == TurnPhase.Idle) Panel.SetStatus(StatusIdle);
        }

        void OnDisable()
        {
            Unwire();
        }

        void OnDestroy()
        {
            Unwire();
        }

        void Update()
        {
            EnsureWired();
            if (Client != null) Client.Pump();
        }

        /// <summary>
        /// 发一句。空白、或上一轮还没收尾（Sending/Thinking）时返回 false，且什么都不改。
        /// 真的发出去了才进入 Thinking —— 发不出去就当场失败，不让界面停在“等一个根本没发出去的请求”。
        /// </summary>
        public bool Submit(string text)
        {
            EnsureWired();
            if (Panel == null) return false;
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (Phase == TurnPhase.Sending || Phase == TurnPhase.Thinking) return false;

            var say = text.Trim();
            Phase = TurnPhase.Sending;
            Panel.SetStatus(StatusSending);
            Panel.Interactable = false;

            if (Client == null || !Client.SendSay(say))
            {
                RejectSend(text);
                return false;
            }

            PendingTurnId = null;
            Phase = TurnPhase.Thinking;
            Panel.SetStatus(StatusThinking);
            Panel.AppendPlayer(say);
            Panel.SetThinking(true);
            Panel.InputText = string.Empty;
            return true;
        }

        /// <summary>
        /// 这一句没送出去：当场判失败 —— 状态词、兜底说明、原文写回、解锁，一个都不能少；
        /// 上一轮的 turnId 也要摘掉，否则界面会拿它当“还有一回合在飞”。
        /// </summary>
        void RejectSend(string text)
        {
            PendingTurnId = null;
            Phase = TurnPhase.Failed;
            Panel.SetStatus(StatusFailed);
            Panel.SetThinking(false);
            Panel.SetError(DialoguePanel.OfflineCopy);
            Panel.InputText = text;          // 没发出去：把玩家的话还回输入框
            Panel.Interactable = true;
        }

        // ---- 帧 ---------------------------------------------------------------

        void OnFrameReceived(ServerFrame frame)
        {
            if (frame == null) return;
            if (Panel == null) return;

            switch (frame.Type)
            {
                case "ack":
                    PendingTurnId = frame.TurnId;
                    Phase = TurnPhase.Thinking;
                    Panel.SetStatus(StatusThinking);
                    Panel.SetThinking(true);
                    break;

                case "thinking":
                    if (string.IsNullOrEmpty(PendingTurnId)) PendingTurnId = frame.TurnId;
                    Phase = TurnPhase.Thinking;
                    Panel.SetStatus(StatusThinking);
                    Panel.SetThinking(true);
                    break;

                case "reply":
                    if (!IsCurrentTurn(frame.TurnId)) return;
                    if (string.IsNullOrEmpty(PendingTurnId)) PendingTurnId = frame.TurnId;
                    Phase = TurnPhase.Answered;
                    Panel.SetThinking(false);
                    Panel.AppendReply(frame.Text);
                    Panel.SetStatus(StatusAnswered);
                    Panel.Interactable = true;
                    break;

                case "error":
                    if (!IsCurrentTurn(frame.TurnId)) return;
                    Phase = TurnPhase.Failed;
                    Panel.SetThinking(false);
                    Panel.SetError(frame.Code == "timeout" ? DialoguePanel.TimeoutCopy : DialoguePanel.OfflineCopy);
                    Panel.SetStatus(StatusFailed);
                    Panel.Interactable = true;     // 失败之后要能再试；输入框里的草稿不动
                    break;

                case "intent":
                    // 动作计划 P4 才消费，这一阶段只留一条日志。
                    Debug.Log("[Conversation] intent turn=" + frame.TurnId + " plan=" + (frame.Plan == null ? "null" : "received"));
                    break;

                default:
                    Debug.LogWarning("[Conversation] 不认识的帧类型：" + frame.Type);
                    break;
            }
        }

        /// <summary>没带 turnId 的帧认作这一轮（后端兜底时可能不回填）；带了就必须对得上。</summary>
        bool IsCurrentTurn(string turnId)
        {
            if (string.IsNullOrEmpty(turnId)) return true;
            if (string.IsNullOrEmpty(PendingTurnId)) return true;
            return string.Equals(turnId, PendingTurnId);
        }

        // ---- 接线 -------------------------------------------------------------

        void EnsureWired()
        {
            if (!ReferenceEquals(_wiredPanel, Panel))
            {
                if (_wiredPanel != null) _wiredPanel.Submitted -= OnSubmitted;
                _wiredPanel = Panel;
                if (_wiredPanel != null) _wiredPanel.Submitted += OnSubmitted;
            }

            if (!ReferenceEquals(_wiredClient, Client))
            {
                if (_wiredClient != null)
                {
                    _wiredClient.FrameReceived -= OnFrameReceived;
                    _wiredClient.Failed -= OnClientFailed;
                }
                _wiredClient = Client;
                if (_wiredClient != null)
                {
                    _wiredClient.FrameReceived += OnFrameReceived;
                    _wiredClient.Failed += OnClientFailed;
                }
            }
        }

        void Unwire()
        {
            if (_wiredPanel != null) _wiredPanel.Submitted -= OnSubmitted;
            if (_wiredClient != null)
            {
                _wiredClient.FrameReceived -= OnFrameReceived;
                _wiredClient.Failed -= OnClientFailed;
            }
            _wiredPanel = null;
            _wiredClient = null;
        }

        void OnSubmitted(string text)
        {
            Submit(text);
        }

        /// <summary>
        /// 通道断了（Spec：MUST NOT 无限等待）。只在真的有回合在飞的时候收尾：启动阶段的重连失败
        /// 不该把面板直接判成“未回应”，那时玩家还没发东西。
        /// </summary>
        void OnClientFailed(string message)
        {
            if (Panel == null) return;
            if (Phase != TurnPhase.Sending && Phase != TurnPhase.Thinking) return;

            Phase = TurnPhase.Failed;
            Panel.SetThinking(false);
            Panel.SetError(DialoguePanel.OfflineCopy);
            Panel.SetStatus(StatusFailed);
            Panel.Interactable = true;
            Debug.LogWarning("[Conversation] 通道故障，这一轮按失败收尾：" + message);
        }
    }
}
