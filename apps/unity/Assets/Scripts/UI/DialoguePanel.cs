using System;
using System.Collections.Generic;
using System.Text;
using Mas3D.Net;
using UnityEngine;
using UnityEngine.UIElements;

namespace Mas3D.UI
{
    /// <summary>
    /// 打字面板（Spec ui/dialogue-console 的“显示 + 收集输入”那一半）。
    ///
    /// 这一层不懂通道状态：回车/发送只是把文字变成 <see cref="Submitted"/>。
    /// 什么时候算发出去了、回复到了没、超时怎么收尾，全在 ConversationController。
    ///
    /// 文案常量（待机/思考中/超时/桥接断开）照抄 specs，测试和控制器共用这一份，别在各处再写一遍。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DialoguePanel : MonoBehaviour
    {
        /// <summary>待机：面板空着的时候显示这一句。</summary>
        public const string IdleCopy = "她坐在书桌前等你先开口。";

        /// <summary>思考中：等回复时的状态词。</summary>
        public const string ThinkingCopy = "她正在想…";

        /// <summary>超时：error 帧 code=timeout 的文案。</summary>
        public const string TimeoutCopy = "她这次没有回应。";

        /// <summary>桥接断开：后端不可达时代的本地兜底说明。</summary>
        public const string OfflineCopy = "桥接服务没有响应，先由本地兜底回话。";

        /// <summary>输入框占位。TextField 没有 placeholderText（见 6000.6.7f1 的探针），所以是叠一层的 Label。</summary>
        public const string PlaceholderCopy = "和她说点什么（Enter 发送，Shift+Enter 换行）";

        /// <summary>场景里的 UIDocument（Dialogue.uxml + DialoguePanelSettings.asset）。</summary>
        public UIDocument Document;

        /// <summary>玩家按了回车/发送，且文字不是空白。参数是输入框里的原文。</summary>
        public event Action<string> Submitted;

        struct Entry
        {
            public string Text;
            public string Class;
        }

        readonly List<Entry> _entries = new List<Entry>();

        UIDocument _boundDocument;
        TextField _input;
        Button _send;
        Button _clear;
        Label _counter;
        Label _placeholder;
        Label _status;
        Label _thinking;
        ScrollView _log;
        Label _hint;
        Label _subtitle;
        string _subtitleText = string.Empty;
        bool _thinkingShown;
        bool _interactable = true;
        static Font _cjkFont;
        static bool _fontResolved;

        /// <summary>输入框里的文字。setter 按 ClientFrame.MaxInputChars（300）截断，并同步计数。</summary>
        public string InputText
        {
            get
            {
                EnsureBound();
                return _input != null && _input.value != null ? _input.value : string.Empty;
            }
            set
            {
                EnsureBound();
                if (_input == null) return;
                var text = value ?? string.Empty;
                if (text.Length > ClientFrame.MaxInputChars) text = text.Substring(0, ClientFrame.MaxInputChars);
                if (_input.value == text) return;
                _input.SetValueWithoutNotify(text);   // 不触发 ChangeEvent：计数在下面显式刷
                RefreshCounter();
                RefreshPlaceholder();
            }
        }

        /// <summary>
        /// 焦点是不是在输入框里（含 TextField 内部的 TextElement，玩家真实输入时焦点在那一层）。
        /// 语音键靠它让路：在面板里打字时按 V 是输入字符，不该开关录音。树没挂上时一律 false。
        /// </summary>
        public bool IsInputFocused
        {
            get
            {
                EnsureBound();
                if (_input == null || _input.panel == null) return false;
                var controller = _input.panel.focusController;
                if (controller == null) return false;
                var focused = controller.focusedElement as VisualElement;
                if (focused == null) return false;
                return focused == _input || _input.Contains(focused);
            }
        }

        /// <summary>输入框和发送按钮能不能用。等回复时为 false（重复提交防护的一半，另一半在控制器）。</summary>
        public bool Interactable
        {
            get { return _interactable; }
            set
            {
                _interactable = value;
                EnsureBound();
                ApplyInteractable();
            }
        }

        /// <summary>消息流里所有已显示行的纯文本（换行拼接），测试与调试用。</summary>
        public string LogText
        {
            get
            {
                EnsureBound();
                // 空着的时候只显示待机那一句：它是“空着”的替身，不是一行历史，所以不占 _entries。
                if (_entries.Count == 0) return IdleCopy;
                var builder = new StringBuilder();
                for (var i = 0; i < _entries.Count; i++)
                {
                    if (i > 0) builder.Append('\n');
                    builder.Append(_entries[i].Text);
                }
                return builder.ToString();
            }
        }

        void Awake()
        {
            EnsureBound();
        }

        void OnEnable()
        {
            EnsureBound();
        }

        void Start()
        {
            EnsureBound();
        }

        /// <summary>状态行的状态词：待机 / 发送中 / 思考中 / 已回复 / 未回应。</summary>
        public void SetStatus(string text)
        {
            EnsureBound();
            if (_status != null) _status.text = text ?? string.Empty;
        }

        /// <summary>
        /// 播报字幕（P7）：整块显示当前该露出的句子，空文本就收起。
        ///
        /// 为什么不塞进消息流：_entries 是"历史"，会被清空按钮、重连重画整段重排；
        /// 字幕跟着音频走、随时换、播完停在原地，混进去只会把历史搅乱（既有的 LogText 断言也在看历史）。
        /// </summary>
        public void ShowSubtitle(string text)
        {
            _subtitleText = text ?? string.Empty;
            EnsureBound();       // 树可能还没挂上：先记下来，绑定那一刻再补画（见 ApplySubtitle 调用点）
            ApplySubtitle();
        }

        void ApplySubtitle()
        {
            if (_subtitle == null) return;
            var hasText = !string.IsNullOrEmpty(_subtitleText);
            _subtitle.text = hasText ? _subtitleText : string.Empty;
            _subtitle.style.display = hasText ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>把玩家的这一句追加进消息流（右对齐）。</summary>
        public void AppendPlayer(string text)
        {
            AppendLine(text, "player");
        }

        /// <summary>把她的话追加进消息流（左对齐）。</summary>
        public void AppendReply(string text)
        {
            AppendLine(text, "reply");
        }

        /// <summary>等待指示：开/关。它永远排在消息流最后一行。</summary>
        public void SetThinking(bool on)
        {
            _thinkingShown = on;
            EnsureBound();
            if (_thinking == null) return;
            _thinking.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
            if (!on) return;
            _thinking.BringToFront();
            ScrollToLatest();
        }

        /// <summary>错误/兜底说明：作为一条 notice 行进消息流（这样 LogText 里也看得到）。</summary>
        public void SetError(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            AppendLine(text, "notice");
        }

        // ---- 绑定 -------------------------------------------------------------

        /// <summary>
        /// UIDocument 的树不一定在 Awake 那一刻就挂好了（同一个 GameObject 上组件的 OnEnable 顺序），
        /// 所以这里每次都试一遍：没绑上就下次再来，绑上了就只做一次。
        /// </summary>
        void EnsureBound()
        {
            if (Document == null) Document = GetComponent<UIDocument>();
            if (Document == null) return;
            // 树会被重建（换 visualTreeAsset、面板关掉再开）：缓存里的元素引用还在、字段非空，
            // 但它们已经脱离 panel，写进去的东西没人看得见 —— 所以“绑好了”必须带上 panel 这一条。
            if (ReferenceEquals(_boundDocument, Document) && _input != null && _input.panel != null) return;

            var root = Document.rootVisualElement;
            if (root == null) return;
            var input = root.Q<TextField>("input");
            if (input == null) return;   // 树还没挂上：下一次调用再试

            Unbind();                    // 旧树上的回调先摘掉，免得重建后一次 Enter 触发两遍
            _boundDocument = Document;
            _input = input;
            _send = root.Q<Button>("send");
            _clear = root.Q<Button>("clear");
            _counter = root.Q<Label>("counter");
            _placeholder = root.Q<Label>("placeholder");
            _status = root.Q<Label>("status");
            _thinking = root.Q<Label>("thinking");
            _log = root.Q<ScrollView>("log");
            _subtitle = root.Q<Label>("subtitle");

            ApplyCjkFallback(root);
            if (_placeholder != null) _placeholder.text = PlaceholderCopy;
            // 只挂 trickle 一次：真实输入时目标是 TextField 内部的 TextElement，trickle 会先经过这里把 Enter
            // 拦下来；测试里事件直接打到 TextField 本身时，trickle 阶段同样包含目标元素，一次就够。
            _input.RegisterCallback<KeyDownEvent>(OnInputKeyDown, TrickleDown.TrickleDown);
            _input.RegisterValueChangedCallback(OnInputValueChanged);
            if (_send != null) _send.clicked += SendFromInput;
            if (_clear != null) _clear.clicked += ClearLog;

            // 设置 / 导出 这两颗只占位：P4 才接，先灰掉，免得点下去没反应。
            DisableChip(root, "settings");
            DisableChip(root, "export");

            SyncLog();
            RefreshCounter();
            RefreshPlaceholder();
            ApplyInteractable();
            ApplySubtitle();     // 树重建后字幕不能丢：这里补画一次
        }

        /// <summary>
        /// 摘掉挂在缓存元素上的回调。旧元素本身还活着（字段握着引用），不摘就会在重建后重复触发。
        /// </summary>
        void Unbind()
        {
            if (_input != null)
            {
                _input.UnregisterCallback<KeyDownEvent>(OnInputKeyDown, TrickleDown.TrickleDown);
                _input.UnregisterValueChangedCallback(OnInputValueChanged);
            }
            if (_send != null) _send.clicked -= SendFromInput;
            if (_clear != null) _clear.clicked -= ClearLog;
            _boundDocument = null;
            _input = null;
            _subtitle = null;    // 旧树上的元素：重新绑定时会换新的
        }

        static void DisableChip(VisualElement root, string name)
        {
            var chip = root.Q<Button>(name);
            if (chip == null) return;
            chip.SetEnabled(false);
            chip.tooltip = "还没接上（P4 再做）";
        }

        /// <summary>
        /// UI Toolkit 的内置主题字体只有拉丁字形，中文会渲染成方块。这里退到系统字体（不引字体资源，
        /// 也不进 Assets）：找得到就整棵树继承，找不到（非 Windows）就保持默认。
        /// </summary>
        void ApplyCjkFallback(VisualElement root)
        {
            if (!_fontResolved)
            {
                _fontResolved = true;
                _cjkFont = Font.CreateDynamicFontFromOSFont(
                    new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Noto Sans CJK SC", "PingFang SC" }, 14);
            }
            if (_cjkFont != null) root.style.unityFont = _cjkFont;
        }

        /// <summary>把 _entries 重新画一遍。清空/重新绑定都走这里，消息流的真相只有一份。</summary>
        void SyncLog()
        {
            if (_log == null) return;
            var content = _log.contentContainer;
            content.Clear();
            _hint = null;

            if (_entries.Count == 0)
            {
                // 待机那句只是“空着”的替身，不进 _entries：这样重建（换 visualTreeAsset、面板关了再开）
                // 之后它也不会变成一行历史，下一条消息一来就摘掉，LogText 里一点痕迹都不留。
                _hint = AddLineLabel(IdleCopy, "hint");
            }
            else
            {
                for (var i = 0; i < _entries.Count; i++) AddLineLabel(_entries[i].Text, _entries[i].Class);
            }

            // thinking 行是 UXML 里的元素，Clear() 会把它摘下来：原样挂回去，永远排最后。
            if (_thinking != null)
            {
                content.Add(_thinking);
                _thinking.style.display = _thinkingShown ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        Label AddLineLabel(string text, string cls)
        {
            if (_log == null) return null;
            var label = new Label(text);
            label.AddToClassList("line");
            if (!string.IsNullOrEmpty(cls)) label.AddToClassList(cls);
            _log.contentContainer.Add(label);
            if (_thinking != null && _thinking.hierarchy.parent != null) _thinking.BringToFront();
            return label;
        }

        // ---- 消息流 -----------------------------------------------------------

        void AppendLine(string text, string cls)
        {
            if (string.IsNullOrEmpty(text)) return;
            EnsureBound();
            RemoveHint();
            _entries.Add(new Entry { Text = text, Class = cls });
            AddLineLabel(text, cls);
            ScrollToLatest();
        }

        void RemoveHint()
        {
            if (_hint == null) return;
            _hint.RemoveFromHierarchy();
            _hint = null;
        }

        void ClearLog()
        {
            EnsureBound();
            _entries.Clear();
            SyncLog();
            // 字幕不属于历史（见 ShowSubtitle 的注释），但历史清空后它继续挂着就成了孤零零的旧话。
            ShowSubtitle(string.Empty);
        }

        void ScrollToLatest()
        {
            if (_log == null || _log.panel == null) return;
            // 排到本帧布局之后：刚加进去的 Label 还没有 layout.height。
            _log.schedule.Execute(() =>
            {
                if (_log == null || _log.panel == null) return;
                var height = _log.contentContainer.layout.height;
                if (height <= 0f) return;
                _log.scrollOffset = new Vector2(0f, height);
            }).ExecuteLater(1);
        }

        // ---- 输入 -------------------------------------------------------------

        /// <summary>
        /// Enter 发送、Shift+Enter 换行（IME 确认候选词也走这里）。
        /// 用 trickle 阶段先接住：挡住 TextField 内部把 Enter 当换行插进文本。
        /// 不用 TextField.isDelayed —— 那样要等失焦才回调，与“Enter 即发”冲突。
        /// </summary>
        void OnInputKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) return;
            if (evt.shiftKey) return;
            evt.StopPropagation();
            SendFromInput();
        }

        void SendFromInput()
        {
            EnsureBound();
            if (_input == null || !_interactable) return;
            // 真实输入时 Enter 可能已经被 TextField 内部当成换行插进值里了：尾部换行不算玩家打的字。
            var text = (_input.value ?? string.Empty).TrimEnd('\r', '\n');
            if (string.IsNullOrWhiteSpace(text)) return;   // 只有空白：不提交，原文留在框里
            // 没人接手就什么都不动：这一句还留在框里，别静默吞掉玩家打的字。
            // 有人接手才清空进“发送中”—— 控制器拒收时会把原文写回来。
            var handler = Submitted;
            if (handler == null) return;
            InputText = string.Empty;
            handler(text);
        }

        void OnInputValueChanged(ChangeEvent<string> evt)
        {
            var text = evt.newValue ?? string.Empty;
            if (_input != null && text.Length > ClientFrame.MaxInputChars)
            {
                _input.SetValueWithoutNotify(text.Substring(0, ClientFrame.MaxInputChars));
            }
            RefreshCounter();
            RefreshPlaceholder();
        }

        void RefreshCounter()
        {
            if (_counter == null) return;
            var length = _input != null && _input.value != null ? _input.value.Length : 0;
            _counter.text = length + "/" + ClientFrame.MaxInputChars;
        }

        void RefreshPlaceholder()
        {
            if (_placeholder == null) return;
            var empty = _input == null || string.IsNullOrEmpty(_input.value);
            _placeholder.style.display = empty ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void ApplyInteractable()
        {
            if (_input != null && _input.enabledSelf != _interactable) _input.SetEnabled(_interactable);
            if (_send != null && _send.enabledSelf != _interactable) _send.SetEnabled(_interactable);
        }
    }
}
