using System;
using System.Collections.Generic;
using System.Reflection;
using Mas3D.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.TestTools;

namespace Mas3D.Tests
{
    /// <summary>
    /// Spec ui/dialogue-console 的打字面板三条契约：结构（UXML 真的能装配）、300 字上限、Enter 提交。
    ///
    /// 面板一律从项目里真实的 Dialogue.uxml + DialoguePanelSettings.asset 装配，不手搓元素：
    /// 否则 UXML 写坏、USS 没挂上，测试照样绿。
    /// </summary>
    public class DialoguePanelTests
    {
        [UnityTest]
        public System.Collections.IEnumerator PanelBuildsFromUxml()
        {
            var harness = DialoguePanelHarness.Create();
            try
            {
                yield return null;   // 让 UIDocument 把树挂上、跑一帧布局

                var root = harness.Document.rootVisualElement;
                Assert.IsNotNull(root, "UIDocument 必须把 UXML 挂成 rootVisualElement");
                Assert.IsNotNull(root.Q<TextField>("input"), "按 name 必须找得到输入框 input");
                Assert.IsNotNull(root.Q<Button>("send"), "按 name 必须找得到发送按钮 send");
                Assert.IsNotNull(root.Q<Label>("counter"), "按 name 必须找得到计数标签 counter");
                Assert.IsNotNull(root.Q<ScrollView>("log"), "按 name 必须找得到滚动区 log");
                Assert.IsNotNull(root.Q<Label>("placeholder"), "输入框占位提示（TextField 没有 placeholderText）");

                // PanelSettings 的契约：ScaleWithScreenSize + 1280x720 参考分辨率 + 有主题。
                Assert.AreEqual(PanelScaleMode.ScaleWithScreenSize, harness.Settings.scaleMode,
                    "DialoguePanelSettings.asset 必须是 ScaleWithScreenSize");
                Assert.AreEqual(new Vector2Int(1280, 720), harness.Settings.referenceResolution,
                    "DialoguePanelSettings.asset 的参考分辨率必须是 1280x720");
                Assert.IsNotNull(harness.Settings.themeStyleSheet, "PanelSettings 必须挂主题，否则控件没有默认样式");

                // 样式真的生效了：面板底色来自 Dialogue.uss（与分辨率缩放无关，所以按颜色断言）。
                var panel = root.Q("panel");
                Assert.IsNotNull(panel, "面板根元素 name=panel");
                var expected = new Color(18f / 255f, 16f / 255f, 22f / 255f, 0.82f);
                var painted = panel.resolvedStyle.backgroundColor;
                Assert.AreEqual(expected.r, painted.r, 0.01f, "面板底色 R 应来自 USS");
                Assert.AreEqual(expected.g, painted.g, 0.01f, "面板底色 G 应来自 USS");
                Assert.AreEqual(expected.b, painted.b, 0.01f, "面板底色 B 应来自 USS");
                Assert.AreEqual(expected.a, painted.a, 0.01f, "面板底色 A 应来自 USS");

                // 待机文案（照抄，别改写）在空消息流里就要看得到。
                StringAssert.Contains("她坐在书桌前等你先开口。", harness.Panel.LogText, "空消息流必须显示待机文案");
                StringAssert.Contains("和她说点什么（Enter 发送，Shift+Enter 换行）", root.Q<Label>("placeholder").text,
                    "占位文案照抄");
            }
            finally
            {
                harness.Dispose();
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator CounterFollowsTheThreeHundredLimit()
        {
            var harness = DialoguePanelHarness.Create();
            try
            {
                yield return null;

                harness.Panel.InputText = new string('字', 301);

                Assert.AreEqual(300, harness.Panel.InputText.Length, "超过 300 字必须截断到 ClientFrame.MaxInputChars");
                Assert.AreEqual(300, harness.Document.rootVisualElement.Q<TextField>("input").value.Length,
                    "TextField 里的值也要是截断后的");
                Assert.AreEqual("300/300", harness.Document.rootVisualElement.Q<Label>("counter").text,
                    "计数标签显示 n/300");
                Assert.AreEqual(300, Mas3D.Net.ClientFrame.MaxInputChars,
                    "上限必须与 frames.ts 的 MAX_INPUT_CHARS 对齐");

                harness.Panel.InputText = "晚上好";
                Assert.AreEqual("3/300", harness.Document.rootVisualElement.Q<Label>("counter").text);
            }
            finally
            {
                harness.Dispose();
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator EnterSubmitsAndBlankDoesNot()
        {
            var harness = DialoguePanelHarness.Create();
            try
            {
                yield return null;

                var payloads = new List<string>();
                harness.Panel.Submitted += payloads.Add;
                var input = harness.Document.rootVisualElement.Q<TextField>("input");

                // 只有空白：不提交、不产生消息记录，原文留在框里。
                harness.Panel.InputText = "  ";
                DialoguePanelHarness.PressEnter(input);
                Assert.AreEqual(0, payloads.Count, "空白输入不该触发提交");
                Assert.AreEqual("  ", harness.Panel.InputText, "空白输入被忽略时原文要留着");
                Assert.AreEqual(0, harness.Panel.LogText.Contains("  ") ? 1 : 0, "空白输入不该进消息流");

                // Enter 提交：载荷就是框里的字，提交后清空（Shift+Enter 才是换行）。
                harness.Panel.InputText = "晚上好";
                DialoguePanelHarness.PressEnter(input);
                Assert.AreEqual(1, payloads.Count, "Enter 必须提交一次");
                Assert.AreEqual("晚上好", payloads[0], "载荷必须原样带上");
                Assert.AreEqual(string.Empty, harness.Panel.InputText, "提交之后输入框清空");
                Assert.AreEqual("0/300", harness.Document.rootVisualElement.Q<Label>("counter").text);

                // Shift+Enter 是换行，不是发送。
                harness.Panel.InputText = "还在打字";
                DialoguePanelHarness.PressEnter(input, shift: true);
                Assert.AreEqual(1, payloads.Count, "Shift+Enter 不该提交");
                Assert.AreEqual("还在打字", harness.Panel.InputText, "Shift+Enter 不该清空草稿");

                // 发送失败（面板层面）也要能再按一次：这里换行后的空输入不拦着下一次提交。
                harness.Panel.InputText = "再来一句";
                DialoguePanelHarness.PressEnter(input);
                Assert.AreEqual(2, payloads.Count);
                Assert.AreEqual("再来一句", payloads[1]);
            }
            finally
            {
                harness.Dispose();
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator ASubmittedLineLeavesTheBoxOnlyWhenSomeoneTakesIt()
        {
            var harness = DialoguePanelHarness.Create();
            try
            {
                yield return null;

                var input = harness.Document.rootVisualElement.Q<TextField>("input");

                // 没人接这一句（Submitted 没有订阅者）：原文必须留在框里，谁也不许静默吞掉玩家的字。
                harness.Panel.InputText = "没人接的话";
                DialoguePanelHarness.PressEnter(input);
                Assert.AreEqual("没人接的话", harness.Panel.InputText, "没有订阅者时提交，原文要留在框里");
                Assert.AreEqual("5/300", harness.Document.rootVisualElement.Q<Label>("counter").text,
                    "原文留在框里，计数也要跟着");

                // 有人接了才清空：这一句确实被接走了。
                var payloads = new List<string>();
                harness.Panel.Submitted += payloads.Add;
                DialoguePanelHarness.PressEnter(input);
                Assert.AreEqual(1, payloads.Count, "有订阅者时 Enter 必须提交一次");
                Assert.AreEqual("没人接的话", payloads[0], "载荷必须原样带上");
                Assert.AreEqual(string.Empty, harness.Panel.InputText, "有人接走之后输入框才清空");
            }
            finally
            {
                harness.Dispose();
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator ThePanelRebindsWhenTheDocumentRebuildsItsTree()
        {
            var harness = DialoguePanelHarness.Create();
            try
            {
                yield return null;

                var firstRoot = harness.Document.rootVisualElement;
                Assert.IsNotNull(firstRoot, "前提：面板树先得挂上");
                var firstInput = firstRoot.Q<TextField>("input");
                harness.Panel.SetStatus("思考中");
                Assert.AreEqual("思考中", firstRoot.Q<Label>("status").text, "前提：绑定时状态词写得进去");

                // UIDocument 重建视觉树：旧树连同缓存里的那些元素一起作废（换 visualTreeAsset、
                // 面板 GameObject 关了再开都会走到这里）。这里用组件开关逼出同样的一次重建。
                harness.Document.enabled = false;
                yield return null;
                harness.Document.enabled = true;
                yield return null;

                var rebuilt = harness.Document.rootVisualElement;
                Assert.IsNotNull(rebuilt, "重新启用之后 UIDocument 必须重新挂上树");
                Assert.AreNotSame(firstRoot, rebuilt, "前提：这条用例要的是真的重建（新树），否则验不到缓存失效");
                Assert.AreNotSame(firstInput, rebuilt.Q<TextField>("input"), "前提：输入框必须是新元素");

                // 旧缓存里的元素已经脱离面板：面板必须自己发现，并重新绑到新树上。
                harness.Panel.SetStatus("已回复");
                Assert.AreEqual("已回复", rebuilt.Q<Label>("status").text, "重建之后 SetStatus 必须落到新树");

                harness.Panel.AppendReply("我重新接上了");
                StringAssert.Contains("我重新接上了", harness.Panel.LogText, "消息记录本身不该丢");
                Assert.IsTrue(HasLabel(rebuilt, "我重新接上了"), "重建之后 AppendReply 必须画进新树的消息流");

                harness.Panel.InputText = "还在打字";
                Assert.AreEqual("还在打字", rebuilt.Q<TextField>("input").value, "InputText 必须写进新树的输入框");
                Assert.AreEqual("还在打字", harness.Panel.InputText, "InputText 也必须能从新树读回来");
                Assert.AreEqual("4/300", rebuilt.Q<Label>("counter").text, "计数跟着新树的输入框走");

                // Enter 也必须还活着：重新绑定不能把它丢了，也不能让它注册成两次。
                var payloads = new List<string>();
                harness.Panel.Submitted += payloads.Add;
                DialoguePanelHarness.PressEnter(rebuilt.Q<TextField>("input"));
                Assert.AreEqual(1, payloads.Count, "重建之后 Enter 必须提交一次（不是零次，也不是两次）");
                Assert.AreEqual("还在打字", payloads[0], "载荷必须原样带上");
            }
            finally
            {
                harness.Dispose();
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator TheIdleCopyNeverBecomesHistoryAfterARebuild()
        {
            var harness = DialoguePanelHarness.Create();
            try
            {
                yield return null;

                var firstRoot = harness.Document.rootVisualElement;
                Assert.IsNotNull(firstRoot, "前提：面板树先得挂上");
                Assert.AreEqual(1, CountLabels(firstRoot, DialoguePanel.IdleCopy), "空消息流：待机那句只出现一次");

                // 逼出一次真重建（换 visualTreeAsset、面板关了再开都走这条路）。
                harness.Document.enabled = false;
                yield return null;
                harness.Document.enabled = true;
                yield return null;

                var rebuilt = harness.Document.rootVisualElement;
                Assert.IsNotNull(rebuilt, "重新启用之后 UIDocument 必须重新挂上树");
                Assert.AreNotSame(firstRoot, rebuilt, "前提：这条用例要的是真的重建（新树）");

                // 面板是懒绑定的：先碰它一下（重建后控制器本来也会刷状态词），逼它重绑到新树。
                harness.Panel.SetStatus("待机");
                // 重建之后还没说话：待机那句仍然是“空着”时的替身，只有一份，不是一行历史。
                Assert.AreEqual(1, CountLabels(rebuilt, DialoguePanel.IdleCopy), "重建之后待机那句仍然只出现一次");

                // 说了一句：待机那句必须同时从树里和 LogText 里消失，LogText 只剩这一句。
                harness.Panel.AppendReply("晚上好呀。");
                Assert.AreEqual("晚上好呀。", harness.Panel.LogText,
                    "重建之后的第一条消息：LogText 必须只有这一句（待机那句不许残留成历史）");
                Assert.AreEqual(0, CountLabels(rebuilt, DialoguePanel.IdleCopy), "待机那句必须从树里消失");

                // 清空之后回到“空着”：待机那句重新出现，且只有一份。
                DialoguePanelHarness.Click(rebuilt.Q<Button>("clear"));
                Assert.AreEqual(1, CountLabels(rebuilt, DialoguePanel.IdleCopy), "清空之后待机那句重新出现且只有一份");
            }
            finally
            {
                harness.Dispose();
            }
        }

        static int CountLabels(VisualElement root, string text)
        {
            var labels = root.Query<Label>().ToList();
            var count = 0;
            for (var i = 0; i < labels.Count; i++)
            {
                if (labels[i].text == text) count++;
            }
            return count;
        }

        static bool HasLabel(VisualElement root, string text)
        {
            var labels = root.Query<Label>().ToList();
            for (var i = 0; i < labels.Count; i++)
            {
                if (labels[i].text == text) return true;
            }
            return false;
        }

        static T LoadAsset<T>(string path) where T : UnityEngine.Object
        {
#if UNITY_EDITOR
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<T>(path);
            Assert.IsNotNull(asset, "工程里找不到资产：" + path);
            return asset;
#else
            Assert.Fail("面板用例只在 Editor 里跑：加载不到 " + path);
            return null;
#endif
        }

        /// <summary>把面板按“场景里的样子”装起来：UIDocument + PanelSettings + Dialogue.uxml + DialoguePanel。</summary>
        internal sealed class DialoguePanelHarness : IDisposable
        {
            readonly GameObject _root;

            DialoguePanelHarness(GameObject root, UIDocument document, PanelSettings settings, DialoguePanel panel)
            {
                _root = root;
                Document = document;
                Settings = settings;
                Panel = panel;
            }

            public UIDocument Document { get; private set; }
            public PanelSettings Settings { get; private set; }
            public DialoguePanel Panel { get; private set; }

            public static DialoguePanelHarness Create()
            {
                var uxml = LoadAsset<VisualTreeAsset>("Assets/UI/Dialogue.uxml");
                var settingsAsset = LoadAsset<PanelSettings>("Assets/UI/DialoguePanelSettings.asset");
                // 用副本，测试里改 targetTexture 之类不会脏了工程资产。
                var settings = UnityEngine.Object.Instantiate(settingsAsset);
                settings.name = settingsAsset.name + "（测试副本）";

                var root = new GameObject("DialoguePanelHarness");
                root.SetActive(false);   // 先关掉：UIDocument 在没拿到 PanelSettings 之前 OnEnable 会报错
                var document = root.AddComponent<UIDocument>();
                document.panelSettings = settings;
                document.visualTreeAsset = uxml;
                var panel = root.AddComponent<DialoguePanel>();
                panel.Document = document;
                root.SetActive(true);
                return new DialoguePanelHarness(root, document, settings, panel);
            }

            public Mas3D.Conversation.ConversationController AddController(Mas3D.Net.BridgeClient client)
            {
                var controller = _root.AddComponent<Mas3D.Conversation.ConversationController>();
                controller.Client = client;
                controller.Panel = Panel;
                return controller;
            }

            /// <summary>
            /// 点一下按钮。Button 的 clicked 只有 +=（分派藏在 Clickable 的 internal Invoke(EventBase) 里），
            /// PlayMode 里又造不出真的指针序列，所以沿用本类既有做法反射调用它。
            /// </summary>
            public static void Click(Button button)
            {
                Assert.IsNotNull(button, "按钮必须存在");
                var clickable = button.clickable;
                var invoke = clickable.GetType().GetMethod("Invoke",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.IsNotNull(invoke, "Clickable 上找不到 Invoke(EventBase)");
                invoke.Invoke(clickable, new object[] { new ClickEvent() });
            }

            /// <summary>
            /// 造一个真 KeyDownEvent 打到输入框上。keyCode / modifiers 的 setter 在 UIElements 里是 internal
            /// （KeyDownEvent 只有公开的无参构造），这里用反射填值：被测的是面板的回调，不是事件的构造。
            /// </summary>
            public static void PressEnter(TextField input, bool shift = false)
            {
                Assert.IsNotNull(input, "输入框 input 必须存在");
                var key = new KeyDownEvent();
                SetHiddenProperty(key, "keyCode", KeyCode.Return);
                SetHiddenProperty(key, "modifiers", shift ? EventModifiers.Shift : EventModifiers.None);
                // 合成事件没有 target，VisualElement.SendEvent 也不会替你补：不写这一行事件会被直接丢弃
                // （实测 bubble / trickle 回调都收不到）。真实输入由 panel 的 dispatcher 填 target。
                key.target = input;
                input.SendEvent(key);
                key.Dispose();
            }

            static void SetHiddenProperty(object target, string name, object value)
            {
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var property = target.GetType().GetProperty(name, flags);
                Assert.IsNotNull(property, "KeyDownEvent 上找不到 " + name);
                var setter = property.GetSetMethod(true);
                Assert.IsNotNull(setter, name + " 没有 setter");
                setter.Invoke(target, new[] { value });
            }

            public void Dispose()
            {
                if (_root != null) UnityEngine.Object.Destroy(_root);
                if (Settings != null) UnityEngine.Object.Destroy(Settings);
            }
        }
    }
}
