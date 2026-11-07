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

        /// <summary>
        /// 焦点进出输入框（true = 刚聚焦）。<see cref="InputModes"/> 靠它解 Esc 锁：
        /// 锁只认这一个信号，别的路径（点外面、面板失活）都不该悄悄把键盘放开。
        /// 注意：同一帧可能被抛两次（InputModes 的 Esc 与面板根上的 Escape 各触发一次 BlurInput），
        /// 订阅者必须幂等 —— 重复收到同一个值不许产生第二次副作用。
        /// </summary>
        public event Action<bool> FocusChanged;

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
        // IME 组合态：真输入法读 UnityEngine.Input.compositionString（旧 Input Manager 的公开 API，
        // 组合期非空、上屏后清空）；测试/外部另有 ApplyComposition 接缝推值。
        // 不走 UI Toolkit 的 IMECompositionEvent（6000.6.7f1 里不是公开类型，引用直接编译不过），
        // 也不走反射读 textEdition.isComposing（实测该属性为 null，整段是死代码）。
        bool _composingManual;
        // 焦点诊断（hotkeys Task 1）：_root 是注册 Escape 回调的那棵树，_escapeHooked 守幂等；
        // _lastFocusLogged 记「上一次写进日志的目标状态」，回流检测（NoteFocusReflow）拿它当基准。
        VisualElement _root;
        bool _escapeHooked;
        string _lastFocusLogged = "none";
        static Font _cjkFont;
        static bool _fontResolved;
        static bool _warnedMissingPanel;

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
                WriteInput(value);
            }
        }

        /// <summary>
        /// 输入框文字的唯一写入口：值、计数、光标一起搬（光标永远落在新文本末尾）。
        /// 为什么非要收成一个口：先前各处各自写 `_input.value`，写完不搬光标，下一次真按键就插在旧光标处 ——
        /// 「你以站起来吗可」那种末几字错位就是这么来的。别在别处直接写 value，一律走这里。
        /// </summary>
        void WriteInput(string value)
        {
            EnsureBound();
            if (_input == null) return;
            var text = value ?? string.Empty;
            if (text.Length > ClientFrame.MaxInputChars) text = text.Substring(0, ClientFrame.MaxInputChars);
            if (_input.value != text) _input.SetValueWithoutNotify(text);   // 不触发 ChangeEvent：计数在下面显式刷
            RefreshCounter();
            RefreshPlaceholder();
            MoveCaretToEnd();
        }

        /// <summary>
        /// 把一段文字接在现有内容末尾（语音回执、未来的快捷输入都走这里）。
        /// 追加语义固定按“末尾”算：哪怕玩家把光标挪到中间，语音回执也不该插进句子中间去。
        /// </summary>
        public void AppendToInput(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            EnsureBound();
            if (_input == null) return;
            WriteInput((_input.value ?? string.Empty) + text);
        }

        /// <summary>光标下标（0..长度）。测试用它钉“写完光标在末尾”，也给将来插入文字用。</summary>
        public int CursorIndex
        {
            get
            {
                EnsureBound();
                if (_input == null) return 0;
                return _input.cursorIndex;
            }
            set
            {
                EnsureBound();
                if (_input == null) return;
                SetCaret(value);
            }
        }

        /// <summary>
        /// 输入框本身是否可编辑（跟 Interactable 不是一回事：发送期只是不许再发，字仍然能打）。
        /// 读 enabledInHierarchy 而不是 enabledSelf —— 父级被灰掉时这里也必须如实说“不能打字”。
        /// </summary>
        public bool InputFieldEnabled
        {
            get
            {
                EnsureBound();
                return _input != null && _input.enabledInHierarchy;
            }
        }

        /// <summary>IME 是否正在组词（候选还没上屏）。接缝与操作系统任一路说是，就是。</summary>
        public bool IsComposing
        {
            get { return _composingManual || !string.IsNullOrEmpty(Input.compositionString); }
        }

        /// <summary>
        /// 组合态接缝：非空 = 正在组词，空串 = 组词结束（候选已上屏）。
        /// 只改这一个布尔，不碰文本 —— 上屏本身由 UI Toolkit / 输入法完成，这里多写一次只会写重。
        /// </summary>
        public void ApplyComposition(string composition)
        {
            _composingManual = !string.IsNullOrEmpty(composition);
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

        /// <summary>输入框元素本身（测试与焦点诊断用，只读；不替代 InputText 这条写入口）。</summary>
        public TextField InputField
        {
            get
            {
                EnsureBound();
                return _input;
            }
        }

        /// <summary>
        /// 把焦点搬进输入框（玩家按 E，或 InputModes 判定该打字了）。
        /// 这里只做“聚焦”这一件事：光标落点归 Task 2 的唯一写入口，别在这里再写一份。
        /// </summary>
        public void FocusInput()
        {
            EnsureBound();
            if (_input == null) return;
            var wasFocused = FocusName(CurrentFocused());
            _input.Focus();
            LogFocus("focus", FocusName(_input), wasFocused);
            var handler = FocusChanged;
            if (handler != null) handler(true);
        }

        /// <summary>
        /// 把焦点从输入框拿走（Esc、点面板外面、面板收起来）。树没挂上时静默返回。
        /// reason 只影响日志（escape / outside-click / escape-root），行为一条道。
        /// 默认值故意取 unknown：调用方漏传时，日志里一眼看得出是谁没交代理由。
        /// </summary>
        public void BlurInput(string reason = "unknown")
        {
            EnsureBound();
            if (_input == null) return;
            var wasFocused = FocusName(CurrentFocused());
            DeepBlur();
            LogFocus(reason, "none", wasFocused);
            var handler = FocusChanged;
            if (handler != null) handler(false);
        }

        /// <summary>
        /// 外部把焦点抢回输入框时的留痕（I2）：由 InputModes 每帧代劳，不在这里新开 Update。
        /// 「过一会它自己回来了」没法靠我们自己的入口记账，只能靠下一次轮询把差异写下来。
        /// </summary>
        public void NoteFocusReflow()
        {
            EnsureBound();
            if (_input == null) return;
            var now = IsInputFocused ? "TextField" : "none";
            if (now == _lastFocusLogged) return;
            LogFocus("reflow", now, _lastFocusLogged);
        }

        /// <summary>
        /// 深扫一遍再把焦点清掉（H1 的防御）：真实输入时焦点可能落在 TextField 的内层元素上，
        /// 只 Blur 外层 TextField 未必清得干净；所以「焦点在哪就 Blur 谁」，最后再复查一遍。
        /// 本轮实测：6000.6.7f1 里 TextField.Blur() 已经能连子元素一起清（TypingGateTests 第 2 条一开始就是绿的），
        /// 这段是给别的版本/别的焦点来源留的兜底，留着比删掉便宜。
        /// </summary>
        void DeepBlur()
        {
            var controller = _input.panel != null ? _input.panel.focusController : null;
            var focused = controller != null ? controller.focusedElement as VisualElement : null;
            if (focused != null && (focused == _input || _input.Contains(focused))) focused.Blur();
            _input.Blur();
            // 复查：还有内层元素握着焦点就再清一次（controller.Blur 不是公开 API，直接对它 Blur）。
            // 本版本这里空转：TextField.Blur() 已经把子树清干净，这段属未验证假设（别的版本/别的焦点来源才可能用到）。
            var left = controller != null ? controller.focusedElement as VisualElement : null;
            if (left != null && _input.Contains(left)) left.Blur();
        }

        /// <summary>
        /// 焦点变化留痕（脱敏：只写类型名，绝不写玩家打的字）。from 由调用方在动手前取，
        /// 所以「TextField -> none reason=escape」读得出是谁被清掉的；from 与 to 相同则算没变化，不写。
        /// </summary>
        void LogFocus(string reason, string to, string from)
        {
            // 不看 _lastFocusLogged 做二次去重：焦点被外部抢回后再失焦，from/to 与第一次相同，
            // 那一行恰恰是「回流发生过」的唯一证据，吞掉它就等于把证人删了（双触发由 from == to 挡住）。
            if (from == to)
            {
                _lastFocusLogged = to;
                return;
            }

            Debug.Log("[focus] " + from + " -> " + to + " reason=" + reason);
            _lastFocusLogged = to;
        }

        VisualElement CurrentFocused()
        {
            if (_input == null || _input.panel == null) return null;
            var controller = _input.panel.focusController;
            return controller != null ? controller.focusedElement as VisualElement : null;
        }

        static string FocusName(VisualElement element)
        {
            return element == null ? "none" : element.GetType().Name;
        }

        /// <summary>
        /// 面板根上的 Escape（设计 §5 防御 2）：不依赖焦点停在哪一层，也不依赖 InputModes 是否在跑。
        /// 与 InputModes 的 Esc 同一条路（都走 BlurInput），重复触发是幂等的。
        /// </summary>
        void OnRootKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Escape) return;
            if (!IsInputFocused) return;
            BlurInput("escape-root");
            evt.StopPropagation();
        }

        /// <summary>把 Escape 挂到当前这棵树的根上（幂等：同一棵树重复调用不会挂两遍）。</summary>
        void HookEscape(VisualElement root)
        {
            if (root == null) return;
            if (_escapeHooked && ReferenceEquals(_root, root)) return;
            UnhookEscape();
            _root = root;
            _root.RegisterCallback<KeyDownEvent>(OnRootKeyDown, TrickleDown.TrickleDown);
            _escapeHooked = true;
        }

        void UnhookEscape()
        {
            if (_escapeHooked && _root != null)
            {
                _root.UnregisterCallback<KeyDownEvent>(OnRootKeyDown, TrickleDown.TrickleDown);
            }
            _root = null;
            _escapeHooked = false;
        }

        /// <summary>
        /// 屏幕坐标是不是落在面板矩形里（“点面板外面就失焦”用）。
        /// 拿不到树或容器时返回 true —— 当成“在里面”，免得一次误判把玩家正在打的字清掉。
        /// </summary>
        public bool ContainsScreenPoint(Vector2 screenPoint)
        {
            EnsureBound();
            var root = Document != null ? Document.rootVisualElement : null;
            var container = root != null ? root.Q("panel") : null;
            if (container == null || container.panel == null)
            {
                // 静默返回 true 会让“点面板外”永远判不出来；提示一次，但不改语义（宁可不清空输入）。
                if (!_warnedMissingPanel)
                {
                    _warnedMissingPanel = true;
                    Debug.LogWarning("[DialoguePanel] 拿不到 \"panel\" 容器，点面板外暂时都按“面板内”处理（只提示一次）。");
                }
                return true;
            }
            var panelPoint = RuntimePanelUtils.ScreenToPanel(container.panel, screenPoint);
            return container.worldBound.Contains(panelPoint);
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

        /// <summary>面板关掉/场景卸载时把回调摘干净：别把野回调留在树上（M6）。</summary>
        void OnDisable()
        {
            Unbind();
        }

        void OnDestroy()
        {
            Unbind();
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
            HookEscape(root);            // 根节点上的 Escape：不依赖焦点停在哪一层，也不依赖 InputModes 是否在跑

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
            UnhookEscape();              // 旧树上的 Escape 也要摘：否则重建后一次按键会走两遍
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
        /// Enter 发送、Shift+Enter 换行。
        /// 用 trickle 阶段先接住：挡住 TextField 内部把 Enter 当换行插进文本。
        /// 不用 TextField.isDelayed —— 那样要等失焦才回调，与“Enter 即发”冲突。
        /// IME 组词中一律放行：那一下 Enter 是“确认候选”，既不是发送也不是换行。
        /// </summary>
        void OnInputKeyDown(KeyDownEvent evt)
        {
            if (IsComposing)
            {
                // 组词中的 Enter 是“确认候选”，必须原样交给输入法/TextField 去上屏。
                // 这里**不要** StopPropagation：在 trickle 阶段掐断事件会把这一次上屏吞掉，
                // 候选字就会跑到句子末尾或被丢掉 —— 实机「你以站起来吗可」那种错位就是这么来的。
                // 多出来的那个换行由发送时的 TrimEnd 收拾，代价远小于丢字。
                return;
            }
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
            ApplyComposition(string.Empty);   // 发出去之后组合态必须清：真机一旦漏了空串，Enter 会被永久堵死
            handler(text);
        }

        void OnInputValueChanged(ChangeEvent<string> evt)
        {
            var text = evt.newValue ?? string.Empty;
            if (_input != null && text.Length > ClientFrame.MaxInputChars)
            {
                // 超长要截断，但不许在这里再写一次 _input.value：那会绕开 WriteInput，
                // 光标留在旧位置，下一键就插到句子中间去（“唯一写入口”是这条面板的硬规矩）。
                WriteInput(text);
                return;
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
            // 输入框永远不禁用：发送期把 TextField SetEnabled(false) 会把焦点、光标和正在打的字一起丢掉，
            // 回执回来再 SetEnabled(true) 也不会把光标还给你 —— 这就是“打字掉字/错位”的另一半病根。
            // 能不能发由 SendFromInput 里的 _interactable 把关；按钮灰掉只是给玩家看的提示。
            // 这里也不去“顺手复活”输入框：Interactable 只管发送按钮，输入框的启用状态归它自己的调用方
            //（将来任何有意禁用输入框的状态不该被这里顶掉）。
            if (_send != null && _send.enabledSelf != _interactable) _send.SetEnabled(_interactable);
        }

        void MoveCaretToEnd()
        {
            if (_input == null) return;
            SetCaret(_input.value != null ? _input.value.Length : 0);
        }

        void SetCaret(int index)
        {
            if (_input == null) return;
            var length = _input.value != null ? _input.value.Length : 0;
            if (index < 0) index = 0;
            if (index > length) index = length;
            // 同时把选区收成一点：只写 cursorIndex 会留下旧选区，下一次键入会把整段选中内容替换掉。
            _input.SelectRange(index, index);
        }
    }
}
