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
        public float TurnRate = 720f;
        public float EyeHeight = 1.62f;
        public float CapsuleRadius = 0.30f;
        public float CapsuleHeight = 1.70f;
        public bool KeyboardInput = true;
        public string SprintButton = "Left Shift";

        CharacterController _controller;
        Vector3 _planar;
        float _vertical;

        public Transform Head { get; private set; }
        public PlayerPosture Posture { get; private set; } = PlayerPosture.Standing;
        public Vector3 PlanarVelocity => _planar;
        public bool Grounded => _controller != null && _controller.isGrounded;

        void Awake() => EnsureReady();

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
            if (!KeyboardInput) return;
            Tick(Time.deltaTime, ReadWish());
        }

        /// <summary>Keyboard wish direction, expressed in the ground plane relative to the view.</summary>
        public Vector2 ReadWish()
        {
            var raw = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
            if (raw.sqrMagnitude < 0.0001f) return Vector2.zero;
            var view = Camera.main != null ? Camera.main.transform : null;
            var yaw = view != null ? view.eulerAngles.y : 0f;
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