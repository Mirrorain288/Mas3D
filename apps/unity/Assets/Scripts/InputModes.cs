using Mas3D.UI;
using UnityEngine;

namespace Mas3D
{
    /// <summary>
    /// 键盘的两条边：E 进/出输入框，Esc 退出输入框，点面板外面也退出。
    ///
    /// 曾经还有第三态：Esc 退出打字时把走路键盘「锁」上，等焦点回来才解开。实机结果是
    /// 「方向键控制一会儿之后又没反应」——焦点离开以后 WASD 却不工作，界面上又没有任何
    /// 提示说键盘被锁了（m17495 第 4 条）。键盘门只认一件事：焦点在不在输入框。
    /// 退出打字就立刻能走；至于「Esc 那一帧会迈一步」，最多 1.6 cm，比看不见的死键好。
    ///
    /// 已知的一帧窗口（评审 Minor 4，故意不修）：<see cref="DialoguePanel.FocusInput"/> 同步抛事件，
    /// 但 UI Toolkit 的 focusController 要等下一帧才让 <see cref="DialoguePanel.IsInputFocused"/> 为真。
    /// 于是按 E 的那一帧 WantsKeyboard 仍可能为真，最多多走 WalkSpeed × 一帧 ≈ 2.0 m/s × 8 ms ≈ 1.6 cm。
    /// 要消掉它就得让门不再只信面板（例如按键当帧立刻锁），那会把“面板是唯一真相”拆成两份状态，
    /// 收益不抵复杂度，记在这里备查。
    ///
    /// 用例只调接缝 PressFocus/PressEscape/PressOutsideClick：批处理里合成不了真实按键，
    /// 下面这个 Update 只是把按键翻译成同一套调用，真正的行为由接缝保证。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InputModes : MonoBehaviour
    {
        public PlayerMotor Motor;
        public DialoguePanel Panel;

        /// <summary>总闸：测试里关掉，免得真实按键在两帧断言之间改变状态。</summary>
        public bool KeyboardInput = true;

        public KeyCode FocusKey = KeyCode.E;
        public KeyCode EscapeKey = KeyCode.Escape;

        /// <summary>
        /// 这一帧按键到底该不该被处理：总闸串上演出的闸。
        /// 演出期间 ActionDirector 会把 <see cref="PlayerMotor.KeyboardInput"/> 置 false，那时 E/Esc
        /// 不许抢焦点（否则玩家一边看动作一边把输入框点亮）。
        /// </summary>
        public bool CanTakeKeys => KeyboardInput && (Motor == null || Motor.KeyboardInput);

        /// <summary>
        /// 把玩家和面板接上。Boot 里调一次，测试里调一次；重复调用只换引用。
        ///
        /// 这里不再订阅 <see cref="DialoguePanel.FocusChanged"/>：锁没了以后，焦点事件没有任何
        /// 需要被“唤醒”的状态，门每帧直接问面板即可（单一真相，少一条会漏订的路径）。
        /// </summary>
        public void Bind(PlayerMotor motor, DialoguePanel panel)
        {
            if (Motor != motor)
            {
                // 换 motor 之前先断旧 motor 的反向引用：否则旧 motor 会一直指着这个还活着的组件，
                // 而新 motor 又在同一字段上被写了一遍，两边各记一份迟早对不上。
                if (Motor != null)
                {
                    Motor.Modes = null;
                    Motor.Panel = null;
                }
                Motor = motor;
                if (Motor != null) Motor.Modes = this;
            }

            // 键盘门的两半在同一处交接：Modes 认这个面板，Motor 也认同一个 ——
            // 调用方（Boot、用例）只负责把两边交给 Bind，别各自再记一份，否则迟早一边记得一边忘了。
            if (Motor != null) Motor.Panel = panel;
            Panel = panel;
        }

        void Update()
        {
            if (!CanTakeKeys) return;
            if (Panel == null) return;

            // E 只在“没在打字”时是开关：一旦焦点在框里，E 就是一个字母（拼音里满地都是），
            // 再拿它退出就会把正在组的词打断。退出统一交给 Esc。
            if (Input.GetKeyDown(FocusKey) && !Panel.IsInputFocused)
            {
                PressFocus();
                return;
            }

            if (Input.GetKeyDown(EscapeKey))
            {
                PressEscape();
                return;
            }

            if (Input.GetMouseButtonDown(0)) PressOutsideClick(Input.mousePosition);
        }

        /// <summary>
        /// E：进输入框。只进不出 —— 退出归 Esc。已经聚焦时再按 E 什么也不做
        /// （生产路径里这一下根本到不了这里，E 那时是一个字母；见 <see cref="Update"/>）。
        /// </summary>
        public void PressFocus()
        {
            if (Panel == null) return;
            if (!Panel.IsInputFocused) Panel.FocusInput();
        }

        /// <summary>Esc：退出输入框，走路立刻恢复。</summary>
        public void PressEscape()
        {
            if (Panel == null) return;
            Panel.BlurInput();
        }

        /// <summary>左键点在面板外面 → 失焦；点在面板里面什么都不做。</summary>
        public void PressOutsideClick(Vector2 screenPoint)
        {
            if (Panel == null || Panel.ContainsScreenPoint(screenPoint)) return;
            Panel.BlurInput();
        }
    }
}
