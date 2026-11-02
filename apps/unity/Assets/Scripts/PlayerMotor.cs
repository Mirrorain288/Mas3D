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