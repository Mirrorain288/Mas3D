using Unity.Cinemachine;
using UnityEngine;

namespace Mas3D
{
    /// <summary>
    /// Orbit camera built on Cinemachine: an orbit pivot above the player's head and a
    /// CinemachineCamera using ThirdPersonFollow (with obstacle avoidance) that frames it.
    /// The orbit is set through <see cref="SetOrbit"/> so a test can aim deterministically
    /// instead of moving a virtual mouse.
    /// <para>
    /// 为什么是两层节点（CameraRigRoot → CameraPivot → CinemachineCamera）：
    /// 旧结构把 Pivot 直接挂在 head 下，而 head 会随玩家转身（TurnRate 270–720 °/s）一起转。
    /// Apply 写的是「世界」旋转：只要玩家在本帧写完 Pivot 之后又转了一点，Pivot 就会被父节点
    /// 带着偏掉，而 Cinemachine 是在 LateUpdate 取景的，看到的正是这个偏掉的朝向 —— 画面左右摆。
    /// 现在 CameraRigRoot 只跟 head 的「位置」、每帧把世界旋转写回 identity，Pivot 挂在它下面，
    /// 玩家的转身到这一层就断了。
    /// </para>
    /// <para>
    /// 为什么 Apply 留在 Update、不搬进 LateUpdate：CinemachineBrain 只在 LateUpdate 取景
    /// （CinemachineBrain.LateUpdate → DoNonFixedUpdate），两条 LateUpdate 之间没有可配的顺序，
    /// 晚于它写 Pivot 只会让相机读上一帧的朝向（270 °/s 下每帧差 4.5°，比现状更抖）。
    /// 同一帧里最后一次转身由 PlayerMotor.Tick 在转完身之后补一次 <see cref="Apply"/>。
    /// </para>
    /// </summary>
    public class PlayerCameraRig : MonoBehaviour
    {
        /// <summary>最近能推到的距离：真第一人称，相机贴在后脑勺内侧。</summary>
        public const float FirstPersonDistance = 0.6f;

        /// <summary>
        /// 跨过这条线（含）就是第一人称。PlayerBody 藏头的阈值 1.25 比它松一点，
        /// 保证极端帧率下也不会出现「已经是第一人称、后脑勺还在画面里」。
        /// </summary>
        public const float FirstPersonAtOrBelow = 1.2f;

        /// <summary>第一人称时相机到 pivot 的距离：5 cm，落在眼睛后面，不穿出脸颊。</summary>
        const float FirstPersonEyeGap = 0.05f;

        /// <summary>第一/第三人称之间挪 boom 的时长。更快像瞬移，更慢会在切换时拖出残影。</summary>
        const float TransitionSeconds = 0.15f;

        /// <summary>第三人称的阻尼；第一人称必须清零 —— 贴着后脑勺还带 0.08 s 滞后，走两步就晕。</summary>
        static readonly Vector3 ThirdPersonDamping = new Vector3(0.08f, 0.12f, 0.06f);

        public float Distance = 2.8f;

        /// <summary>
        /// Radius of the sphere the boom keeps clear of geometry. Rooms need 0.20 m so the near plane
        /// never pokes through a wall; a scripted close-up between two bodies has no room for it and
        /// squeezes the boom to a hand's width, so the director may shrink it for that action alone.
        /// </summary>
        public float CameraRadius = 0.20f;
        public float ShoulderOffset = 0.28f;
        public float VerticalArm = 0.18f;
        public float MinPitch = -55f;
        public float MaxPitch = 65f;
        public float Sensitivity = 2.2f;
        public float FieldOfView = 55f;
        public bool MouseInput = true;

        public Camera Output;
        public float Yaw { get; private set; }
        public float Pitch { get; private set; } = 14f;
        public CinemachineCamera VCam { get; private set; }
        public Transform Pivot { get; private set; }

        /// <summary>不旋转的中介节点：只跟 head 的位置，世界旋转恒为 identity。</summary>
        public Transform CameraRigRoot { get; private set; }

        /// <summary>
        /// 本帧镜头朝向（度）。移动方向用它而不是渲染相机的欧拉角：Cinemachine 的朝向带阻尼，
        /// 把滞后一帧的 yaw 再喂回移动方向，机器就在自己追自己，画面持续左右摆。
        /// </summary>
        public float IntentYaw => Yaw;

        /// <summary>距离压到 1.2 m 以内就是第一人称（相机贴头、不再有肩部偏移）。</summary>
        public bool IsFirstPerson => Distance <= FirstPersonAtOrBelow;

        /// <summary>相机此刻的实际位置（渲染那一台）。批处理里由 CinemachineBrain 在 LateUpdate 写。</summary>
        public Vector3 CameraPosition =>
            Output != null ? Output.transform.position : (VCam != null ? VCam.transform.position : Vector3.zero);

        CinemachineThirdPersonFollow _follow;
        CinemachineBrain _brain;
        Transform _head;

        public void Build(Transform head, Camera output)
        {
            _head = head != null ? head : transform;
            Output = output != null ? output : UnityEngine.Camera.main;

            // CameraRigRoot 挂在玩家的父级上（玩家没有父级就挂玩家自己）。它不吃转身靠的是
            // SyncRoot 每帧把世界旋转写回 identity，而不是父级的选择 —— 父级只是给它一个能随场景销毁的容身处。
            var rootParent = transform.parent != null ? transform.parent : transform;
            if (CameraRigRoot == null)
            {
                var existing = rootParent.Find("CameraRigRoot");
                CameraRigRoot = existing != null ? existing : new GameObject("CameraRigRoot").transform;
            }
            CameraRigRoot.SetParent(rootParent, false);

            Pivot = FindPivot();
            Pivot.SetParent(CameraRigRoot, false);
            Pivot.localPosition = Vector3.zero;
            SyncRoot();

            if (VCam == null)
            {
                var go = new GameObject("CinemachineCamera");
                go.transform.SetParent(Pivot, false);
                VCam = go.AddComponent<CinemachineCamera>();
                VCam.Follow = Pivot;
                _follow = go.AddComponent<CinemachineThirdPersonFollow>();
            }
            if (_follow == null) _follow = VCam.GetComponent<CinemachineThirdPersonFollow>();

            var lens = VCam.Lens;
            lens.FieldOfView = FieldOfView;
            lens.NearClipPlane = 0.05f;
            VCam.Lens = lens;
            var priority = VCam.Priority;
            priority.Value = 10;
            VCam.Priority = priority;

            SnapFollow();
            var avoid = _follow.AvoidObstacles;
            avoid.Enabled = true;
            avoid.CameraRadius = CameraRadius;
            avoid.IgnoreTag = "Player";
            _follow.AvoidObstacles = avoid;

            if (Output != null && !Output.TryGetComponent(out _brain)) _brain = Output.gameObject.AddComponent<CinemachineBrain>();
            Apply();
        }

        Transform FindPivot()
        {
            if (Pivot != null) return Pivot;
            var found = CameraRigRoot != null ? CameraRigRoot.Find("CameraPivot") : null;
            if (found == null && _head != null) found = _head.Find("CameraPivot");
            if (found == null) found = transform.Find("CameraPivot");
            return found != null ? found : new GameObject("CameraPivot").transform;
        }

        void Update()
        {
            // 姿态每帧都要写回，与鼠标锁不锁无关：MouseInput = false 只表示「不读鼠标」，
            // 不是「把相机钉在上一帧」。旧实现在这里整段早退，于是对话中/Esc 后玩家一转，镜头就被拖走。
            SyncRoot();
            if (MouseInput && Output != null)
            {
                // 传原始鼠标位移：Sensitivity 只在 AddOrbit 里乘一次，否则滚轮/测试两条路会各乘一遍。
                AddOrbit(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y"));
                var scroll = Input.mouseScrollDelta.y;
                if (Mathf.Abs(scroll) > 0.01f) SetDistance(Distance - scroll * 0.25f);
            }
            EaseFollow(Time.deltaTime);
            Apply();
        }

        public void SetOrbit(float yawDegrees, float pitchDegrees)
        {
            Yaw = Mathf.Repeat(yawDegrees, 360f);
            Pitch = Mathf.Clamp(pitchDegrees, MinPitch, MaxPitch);
            Apply();
        }

        /// <summary>直接写仰角（AddOrbit 之外的另一条写入口），同样夹在 Min/MaxPitch 之间。</summary>
        public void SetPitch(float pitchDegrees)
        {
            Pitch = Mathf.Clamp(pitchDegrees, MinPitch, MaxPitch);
            Apply();
        }

        /// <summary>
        /// 按「鼠标原始位移」转轨道：灵敏度只在这里乘一次（<see cref="Sensitivity"/>，2.2 度/单位），
        /// 所以 Update 传进来的是 Input.GetAxis 的原始值，测试也能直接给 10 个单位。
        /// </summary>
        public void AddOrbit(float deltaYaw, float deltaPitch)
        {
            // 鼠标往上推（deltaPitch > 0）必须把视线抬起来。Pitch 走的是 Quaternion.Euler 的 X 轴，
            // 在 Unity 里 X 为正 = 低头，所以抬视线要**减**。上一版这里写了加号，用例又只断言
            // "Pitch 变大"，两者一起把方向锁反了（2026-11-06 实机反馈：鼠标上移镜头向下转）。
            // 现在的用例断言 Pivot 的 forward.y 必须为正，数值和手感绑在同一件事上。
            SetOrbit(Yaw + deltaYaw * Sensitivity, Pitch - deltaPitch * Sensitivity);
        }

        /// <summary>Shrink or restore the boom's clearance sphere. Applied immediately, not next Build.</summary>
        public void SetCameraRadius(float radius)
        {
            CameraRadius = Mathf.Max(0.01f, radius);
            if (_follow == null) return;
            var avoid = _follow.AvoidObstacles;
            avoid.CameraRadius = CameraRadius;
            _follow.AvoidObstacles = avoid;
        }

        /// <summary>
        /// 最近 0.6 m（真第一人称），最远 5 m。这里只改目标值：boom 由 <see cref="EaseFollow"/> 在
        /// 0.15 s 内挪过去，一次滚轮跳到底会像瞬移。编辑器里的工具（capture）没有 Update 循环，
        /// 所以非播放模式下当场落位。
        /// </summary>
        public void SetDistance(float distance)
        {
            Distance = Mathf.Clamp(distance, FirstPersonDistance, 5f);
            if (_follow == null) return;
            if (Application.isPlaying) return;
            SnapFollow();
        }

        /// <summary>
        /// Push the orbit angles onto the pivot; Cinemachine resolves the camera pose from it.
        /// 写世界旋转：父节点（CameraRigRoot）每帧被按住 identity，所以这里的值就是最终朝向。
        /// </summary>
        public void Apply()
        {
            if (Pivot != null) Pivot.rotation = Quaternion.Euler(Pitch, Yaw, 0f);
        }

        /// <summary>把不旋转的那一层贴到 head 上：只跟位置，旋转每帧按回 identity。</summary>
        void SyncRoot()
        {
            if (CameraRigRoot == null) return;
            CameraRigRoot.position = _head != null ? _head.position : transform.position;
            CameraRigRoot.rotation = Quaternion.identity;
        }

        /// <summary>
        /// 第一/第三人称之间的 0.15 s 过渡。目标值都在 <see cref="SnapFollow"/> 里，
        /// 这里按秒速用 MoveTowards 追过去：距离 18.5 m/s（2.75 m / 0.15 s），
        /// 竖臂与肩位按各自行程换算，三条一起到点。
        /// </summary>
        void EaseFollow(float dt)
        {
            if (_follow == null) return;
            var firstPerson = IsFirstPerson;
            _follow.CameraDistance = Mathf.MoveTowards(_follow.CameraDistance,
                firstPerson ? FirstPersonEyeGap : Distance, 18.5f * dt);
            _follow.VerticalArmLength = Mathf.MoveTowards(_follow.VerticalArmLength,
                firstPerson ? 0f : VerticalArm, VerticalArm / TransitionSeconds * dt);
            _follow.CameraSide = Mathf.MoveTowards(_follow.CameraSide,
                firstPerson ? 0f : 1f, 1f / TransitionSeconds * dt);
            // 肩位也要收：第一人称下留着 0.28 m 的肩偏移，相机就偏到脸颊外侧去了（实测残差 0.284 m）。
            var shoulder = _follow.ShoulderOffset;
            shoulder.x = Mathf.MoveTowards(shoulder.x, firstPerson ? 0f : ShoulderOffset,
                ShoulderOffset / TransitionSeconds * dt);
            _follow.ShoulderOffset = shoulder;
            _follow.Damping = firstPerson ? Vector3.zero : ThirdPersonDamping;
        }

        /// <summary>把 boom 直接摆到当前 Distance 对应的位置（Build、以及非播放模式下的 SetDistance）。</summary>
        void SnapFollow()
        {
            if (_follow == null) return;
            var firstPerson = IsFirstPerson;
            _follow.ShoulderOffset = new Vector3(firstPerson ? 0f : ShoulderOffset, 0f, 0f);
            _follow.CameraDistance = firstPerson ? FirstPersonEyeGap : Distance;
            _follow.VerticalArmLength = firstPerson ? 0f : VerticalArm;
            _follow.CameraSide = firstPerson ? 0f : 1f;
            _follow.Damping = firstPerson ? Vector3.zero : ThirdPersonDamping;
        }
    }
}
