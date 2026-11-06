using System;
using UnityEngine;

namespace Mas3D
{
    /// <summary>
    /// Ground movement for the player, driven either by the keyboard or, in tests, directly:
    /// <see cref="Tick"/> takes the delta time and the wish direction, so a PlayMode test can walk a
    /// fixed path at a fixed step instead of depending on frame timing.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerMotor : MonoBehaviour
    {
        public float WalkSpeed = 2.0f;
        public float RunSpeed = 3.4f;
        public float Acceleration = 18f;
        public float Gravity = -20f;
        /// <summary>旧默认值 720 °/s。Main.unity 里序列化的还是它，Awake 时迁移成 <see cref="DefaultTurnRate"/>。</summary>
        const float LegacyTurnRate = 720f;

        /// <summary>
        /// 每秒钟最多的转身角度。720 时一个 60 fps 的帧能甩 12°，镜头跟着根节点一起晃；
        /// 270 是「跟手」与「稳」之间的折中，也是本计划定的值。
        /// </summary>
        public const float DefaultTurnRate = 270f;

        public float TurnRate = DefaultTurnRate;
        public float EyeHeight = 1.62f;
        public float CapsuleRadius = 0.30f;
        public float CapsuleHeight = 1.70f;
        public bool KeyboardInput = true;

        /// <summary>
        /// 原始输入轴的注入口（测试用）：为空时读 Unity 的 Horizontal/Vertical。
        /// 批处理没有真实输入设备，「方向来自意图 yaw」这条判据只能靠注入的原始轴来钉。
        /// </summary>
        public Func<Vector2> AxisInput;

        PlayerCameraRig _rig;
        public string SprintButton = "Left Shift";

        /// <summary>
        /// 输入框所在的面板。不为空时，焦点落在输入框里就停掉键盘走路：打字时 WASD 是文字，不是移动。
        /// RoomBootstrap 在 Boot 里接上；测试里单独赋值即可。
        /// </summary>
        public Mas3D.UI.DialoguePanel Panel;

        /// <summary>
        /// 键盘门的另一半（Esc 锁死）。可为空：没接 Modes 时只看 Panel，
        /// 所以旧测试只给 Panel 也能跑。
        /// </summary>
        public InputModes Modes;

        CharacterController _controller;
        Vector3 _planar;
        float _vertical;

        public Transform Head { get; private set; }
        public PlayerPosture Posture { get; private set; } = PlayerPosture.Standing;
        public Vector3 PlanarVelocity => _planar;
        public bool Grounded => _controller != null && _controller.isGrounded;

        /// <summary>
        /// 键盘这一帧到底算不算可用：KeyboardInput 是总闸（演出、接触用例靠它整台冻结），
        /// 焦点在输入框里时停。没有第三种「锁」—— 上一版 Esc 之后会锁住 WASD，而界面上没有任何
        /// 提示，实机表现就是「走一会儿突然不受控」（m17495 第 4 条）。
        /// 它只决定喂给 Tick 的行走意图 —— 门关着就把 wish 换成 0，Tick 照跑（重力与贴地不能一起停），
        /// 所以它不是 Update 的唯一判据：整台早退在 KeyboardInput 上。
        /// </summary>
        public bool WantsKeyboard =>
            KeyboardInput &&
            (Panel == null || !Panel.IsInputFocused);

        void Awake()
        {
            // Main.unity 里存的还是旧默认 720：只改 C# 默认值对已经在场景里的玩家无效，而那个场景文件
            // 正被渲染侧大改，不去碰它。恰好等于旧默认的值按旧默认迁移，手工调过的其它值原样保留。
            if (Mathf.Approximately(TurnRate, LegacyTurnRate)) TurnRate = DefaultTurnRate;
            EnsureReady();
        }

        /// <summary>
        /// Configures the controller and creates the head; safe to call again, and required by
        /// editor tooling that wires the rig without entering play mode (Awake does not run there).
        /// </summary>
        public void EnsureReady()
        {
            if (_controller == null) _controller = GetComponent<CharacterController>();
            if (_controller == null) return;
            _controller.height = CapsuleHeight;
            _controller.radius = CapsuleRadius;
            _controller.center = new Vector3(0f, CapsuleHeight * 0.5f, 0f);
            _controller.slopeLimit = 46f;
            _controller.stepOffset = 0.25f;
            _controller.skinWidth = 0.02f;
            _controller.minMoveDistance = 0f;

            var head = transform.Find("Head");
            if (head == null)
            {
                var go = new GameObject("Head");
                go.transform.SetParent(transform, false);
                head = go.transform;
            }
            head.localPosition = new Vector3(0f, EyeHeight, 0f);
            Head = head;
        }

        /// <summary>
        /// The eye height, capsule height and capsule centre a stance implies. Standing is the
        /// original rig (1.62 / 1.70 / 0.85); a sitting player sits on a 1.15 m capsule so the
        /// eye lands 0.50 m above the seat. Kneeling (1.28) is the only stance that lands a face
        /// level with a seated, forward-leaning her: standing 1.62 and crouching 1.05 both miss.
        /// </summary>
        public static (float eye, float capsuleHeight, float capsuleCentre) Metrics(PlayerPosture posture)
        {
            switch (posture)
            {
                case PlayerPosture.Crouching: return (1.05f, 1.10f, 0.55f);
                case PlayerPosture.Sitting: return (1.10f, 1.15f, 0.575f);
                case PlayerPosture.Kneeling: return (1.28f, 1.32f, 0.66f);
                default: return (1.62f, 1.70f, 0.85f);
            }
        }

        /// <summary>
        /// Puts the player in <paramref name="posture"/>. The two public metrics are rewritten
        /// first so a later <see cref="EnsureReady"/> reproduces exactly the same capsule and eye
        /// instead of standing the player back up.
        /// <para>
        /// <paramref name="seatTopY"/> is the surface the player sits on, and only Sitting reads it:
        /// the bench (topY 0.604) leaves the plan's fixed 1.10 eye, while the bed (topY 0.906) has to
        /// put the eye 0.50 m above the mattress at 1.406 or the player sits with their knees in it.
        /// Zero means "no surface named" and keeps the fixed 1.10.
        /// </para>
        /// </summary>
        public void SetPosture(PlayerPosture posture, float seatTopY = 0f)
        {
            Posture = posture;
            var (eye, capsuleHeight, capsuleCentre) = Metrics(posture);
            if (posture == PlayerPosture.Sitting && seatTopY > 0f) eye = seatTopY + 0.50f;
            EyeHeight = eye;
            CapsuleHeight = capsuleHeight;

            if (_controller == null) _controller = GetComponent<CharacterController>();
            if (_controller != null)
            {
                _controller.height = capsuleHeight;
                _controller.center = new Vector3(0f, capsuleCentre, 0f);
            }

            if (Head == null) EnsureReady();
            if (Head != null) Head.localPosition = new Vector3(0f, eye, 0f);
        }

        void Update()
        {
            // 总闸关掉（测试、演出）时整台 motor 停：那是刻意的冻结，不是“打字门”，
            // ActionKiss/ActionHands 这些接触用例靠它把玩家钉在原地。
            if (!KeyboardInput) return;

            // 打字门只关行走意图，不关物理：焦点在输入框里、或 Esc 锁死时把 wish 换成 0，
            // 但 Tick 照跑 —— 否则重力和贴地也会一起停（空中按 E 会悬停）。
            // Tick 里是 MoveTowards(_planar, 0, Acceleration * dt)：门清的是意图，速度按加速度收敛到 0，不是瞬停。
            Tick(Time.deltaTime, WantsKeyboard ? ReadWish() : Vector2.zero);
        }

        /// <summary>Keyboard wish direction, expressed in the ground plane relative to the intent yaw.</summary>
        public Vector2 ReadWish()
        {
            var raw = AxisInput != null
                ? AxisInput()
                : new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
            if (raw.sqrMagnitude < 0.0001f) return Vector2.zero;
            // 用意图 yaw，不用渲染相机：Cinemachine 的朝向带阻尼，滞后一帧的 yaw 被喂回移动方向后，
            // 机器就在自己追自己，画面会持续左右摆。没有 rig（老测试）时退回玩家自己的朝向。
            if (_rig == null) _rig = GetComponent<PlayerCameraRig>();
            var yaw = _rig != null ? _rig.IntentYaw : transform.eulerAngles.y;
            var rotated = Quaternion.Euler(0f, yaw, 0f) * new Vector3(raw.x, 0f, raw.y);
            return new Vector2(rotated.x, rotated.z);
        }

        public bool Sprinting => Input.GetButton(SprintButton);

        /// <summary>
        /// One step of movement. <paramref name="wishWorld"/> is a ground-plane direction in world
        /// space, magnitude 0..1; speed is scaled by the walk/run setting.
        /// </summary>
        public void Tick(float dt, Vector2 wishWorld, bool running = false)
        {
            if (_controller == null) _controller = GetComponent<CharacterController>();
            if (dt <= 0f) return;

            var wish = Vector3.ClampMagnitude(new Vector3(wishWorld.x, 0f, wishWorld.y), 1f);
            var target = wish * (running ? RunSpeed : WalkSpeed);
            _planar = Vector3.MoveTowards(_planar, target, Acceleration * dt);

            if (_controller.isGrounded && _vertical < 0f) _vertical = -2f;
            else _vertical += Gravity * dt;

            var motion = _planar * dt + Vector3.up * (_vertical * dt);
            _controller.Move(motion);

            if (wish.sqrMagnitude > 0.0001f)
            {
                var look = Quaternion.LookRotation(wish, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, look, TurnRate * dt);

                // 转完身之后补一次相机姿态：PlayerCameraRig.Update 很可能比这里先跑（同物体上组件顺序），
                // 它写完 Pivot 再被这一转带着偏 TurnRate*dt，而 Cinemachine 是在 LateUpdate 取景的。
                // Apply 幂等，没有 rig 时什么都不做。
                if (_rig == null) _rig = GetComponent<PlayerCameraRig>();
                if (_rig != null) _rig.Apply();
            }
        }

        /// <summary>Move the capsule without letting the CharacterController sweep it there.</summary>
        public void Teleport(Vector3 feet, float yawDegrees)
        {
            if (_controller == null) _controller = GetComponent<CharacterController>();
            var wasEnabled = _controller.enabled;
            _controller.enabled = false;
            transform.SetPositionAndRotation(feet, Quaternion.Euler(0f, yawDegrees, 0f));
            _controller.enabled = wasEnabled;
            _planar = Vector3.zero;
            _vertical = 0f;
        }
    }
}