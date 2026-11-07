using System;
using Mas3D.Actions;
using Mas3D.UI;
using UnityEngine;

namespace Mas3D
{
    /// <summary>
    /// 数字键 1–5 找动作、C / Z / X 摆姿态。这一层只做翻译：按一下翻成 ActionDirector /
    /// PlayerMotor 的调用；能不能进动作、坐哪张座面、姿态门槛是什么，全部由那两个类决定。
    ///
    /// Update 只把真实按键收成一个 <see cref="KeyCode"/> 再交给 <see cref="HandleKey"/>；批处理里
    /// 合成不了按键，测试直接调同一条接缝（与 InputModes 同一套口径）。四条门（总闸 / 打字 /
    /// 演出）只有 <see cref="Ready"/> 一处，Update 与四个公开接缝都从它过 —— 不许出现
    /// 「接缝能按、Update 不能按」两套口径。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ActionHotkeys : MonoBehaviour
    {
        public ActionDirector Director;
        public PlayerMotor Player;
        public DialoguePanel Panel;
        public AnchorTable Anchors;

        public KeyCode[] ActionKeys = { KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5 };
        public KeyCode CrouchKey = KeyCode.C;
        public KeyCode KneelKey = KeyCode.Z;
        public KeyCode SitKey = KeyCode.X;

        /// <summary>
        /// 按 X 时座面要在这个半径（米）以内。两张座面 S1/S2 相距 1.636 m，站在中点到两边各
        /// 0.818 m，凳面半宽只有 0.294 m：半径 1.0 会让人坐在离凳子 0.8 m 的空气里。
        /// 0.6 只允许「人已经在座面跟前」的坐下；按下之后也不吸附 x/z/yaw（见报告「已知限制」）。
        /// </summary>
        public float SeatReach = 0.6f;

        /// <summary>总闸。测试里关掉它，接缝也会跟着拒绝 —— 门只有一处。</summary>
        public bool KeyboardInput = true;

        /// <summary>状态行当前那句是不是我们写的：成功时只清自己的，别把「思考中」抹掉。</summary>
        bool _statusFromUs;

        public bool CanTakeKeys => KeyboardInput && (Player == null || Player.KeyboardInput);

        /// <summary>人正在输入框里打字：数字和字母都归输入框。</summary>
        public bool Typing => Panel != null && Panel.IsInputFocused;

        /// <summary>导演正在演出：按键整块静默，不打断，也不往面板里写原因。</summary>
        bool Busy => Director != null && Director.Current != null;

        /// <summary>四条门合成一条：总闸、打字、演出。Update 和四个公开接缝共用它。</summary>
        public bool Ready() => CanTakeKeys && !Typing && !Busy;

        public void Bind(ActionDirector director, PlayerMotor player, DialoguePanel panel, AnchorTable anchors)
        {
            Director = director;
            Player = player;
            Panel = panel;
            Anchors = anchors;
            Debug.Log("[hotkeys] bind director=" + (director != null ? "ok" : "none")
                + " player=" + (player != null ? "ok" : "none")
                + " panel=" + (panel != null ? "ok" : "none")
                + " seats=" + (anchors != null && anchors.seats != null ? anchors.seats.Length : 0));
        }

        void Update()
        {
            // 只做收集：哪一下按了什么键，判断全在 HandleKey / Ready 里。
            var key = PollKey();
            if (key != KeyCode.None) HandleKey(key);
        }

        KeyCode PollKey()
        {
            if (ActionKeys != null)
            {
                for (var i = 0; i < ActionKeys.Length; i++)
                {
                    if (ActionKeys[i] == KeyCode.None) continue;
                    if (Input.GetKeyDown(ActionKeys[i])) return ActionKeys[i];
                }
            }
            if (Input.GetKeyDown(CrouchKey)) return CrouchKey;
            if (Input.GetKeyDown(KneelKey)) return KneelKey;
            if (Input.GetKeyDown(SitKey)) return SitKey;
            return KeyCode.None;
        }

        /// <summary>
        /// 一次按键。返回 false 表示这一下什么也没发生：正在打字、正在演出，或者动作被拒绝了。
        /// </summary>
        public bool HandleKey(KeyCode key)
        {
            if (!Ready()) return false;

            if (ActionKeys != null)
            {
                for (var i = 0; i < ActionKeys.Length; i++)
                    if (ActionKeys[i] != KeyCode.None && ActionKeys[i] == key) return PressAction(i + 1);
            }
            if (key == CrouchKey) return PressCrouch();
            if (key == KneelKey) return PressKneel();
            if (key == SitKey) return PressSit();
            return false;
        }

        /// <summary>槽位从 1 数到 5，顺序就是 <see cref="ActionId.All"/>。</summary>
        public bool PressAction(int slot)
        {
            if (!Ready() || Director == null) return false;
            if (slot < 1 || slot > ActionId.All.Length) return false;
            if (Director.TryEnterAction(ActionId.All[slot - 1]))
            {
                ClearStatus();
                return true;
            }
            Say(Director.LastFaults);
            return false;
        }

        public bool PressCrouch() => TogglePosture(PlayerPosture.Crouching);

        public bool PressKneel() => TogglePosture(PlayerPosture.Kneeling);

        public bool PressSit()
        {
            if (!Ready() || Player == null) return false;
            if (Player.Posture == PlayerPosture.Sitting)
            {
                Player.SetPosture(PlayerPosture.Standing);
                ClearStatus();
                return true;
            }

            if (Anchors == null || Anchors.seats == null)
            {
                Say("座位数据缺失", "座位数据缺失：先确认 anchors.json 读到了");
                return false;
            }

            var seat = NearestSeat();
            if (seat == null)
            {
                Say("坐不下", "附近没有能坐的地方（" + SeatReach.ToString("F1") + " 米以内）");
                return false;
            }

            Player.SetPosture(PlayerPosture.Sitting, seat.topY);
            ClearStatus();
            return true;
        }

        bool TogglePosture(PlayerPosture target)
        {
            if (!Ready() || Player == null) return false;
            Player.SetPosture(Player.Posture == target ? PlayerPosture.Standing : target);
            ClearStatus();
            return true;
        }

        /// <summary>最近的一张座面，XZ 距离不超过 SeatReach；一张都没有就返回 null（不许往空气里坐）。</summary>
        SeatEntry NearestSeat()
        {
            if (Player == null || Anchors == null || Anchors.seats == null) return null;
            var feet = Player.transform.position;
            SeatEntry best = null;
            var bestDistance = SeatReach;
            foreach (var seat in Anchors.seats)
            {
                if (seat == null) continue;
                var dx = seat.x - feet.x;
                var dz = seat.z - feet.z;
                var distance = Mathf.Sqrt(dx * dx + dz * dz);
                if (distance > bestDistance) continue;
                bestDistance = distance;
                best = seat;
            }
            return best;
        }

        /// <summary>
        /// 只清自己写过的状态行。状态行同时被会话阶段（思考中 / 发送中）使用，
        /// 成功按一下就把它抹掉，玩家会以为对话卡住了（评审指出）。
        /// </summary>
        void ClearStatus()
        {
            if (Panel == null || !_statusFromUs) return;
            Panel.SetStatus("");
            _statusFromUs = false;
        }

        /// <summary>
        /// 把导演的拒绝原因翻成给玩家看的中文。导演的 fault 是诊断用的（含动作 id 与英文），
        /// 只进日志；面板上只放中文短句，否则玩家读到的是「hands: out of reach」。
        /// </summary>
        static void FaultCopy(string fault, out string status, out string body)
        {
            var text = fault ?? string.Empty;
            if (Has(text, "out of reach")) { status = "够不着"; body = "够不着：她离得太远了"; return; }
            if (Has(text, "busy")) { status = "她正忙着"; body = "她正忙着，等这一下做完"; return; }
            if (Has(text, "kneel") || text.Contains("跪下")) { status = "得先跪下"; body = "得先跪下才做得了这个"; return; }
            if (Has(text, "crouch") || text.Contains("蹲下")) { status = "得先蹲下"; body = "得先蹲下才做得了这个"; return; }
            status = "现在做不了";
            body = "现在做不了这个动作";
        }

        static bool Has(string text, string token) => text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>导演的拒绝：先翻译，再上屏。</summary>
        void Say(string fault)
        {
            FaultCopy(fault, out var status, out var body);
            Show(status, body, fault);
        }

        /// <summary>自己人写的文案（无座面 / 数据缺失）：已经是中文，不用翻译。</summary>
        void Say(string status, string body) => Show(status, body, null);

        void Show(string status, string body, string fault)
        {
            if (Panel != null)
            {
                Panel.SetStatus(status);
                Panel.SetError(body);
                _statusFromUs = true;
            }
            Debug.Log("[hotkeys] " + status + " / " + body + (string.IsNullOrEmpty(fault) ? string.Empty : " · fault=" + fault));
        }
    }
}
