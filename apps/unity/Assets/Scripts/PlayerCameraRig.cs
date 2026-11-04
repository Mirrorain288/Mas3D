using Unity.Cinemachine;
using UnityEngine;

namespace Mas3D
{
    /// <summary>
    /// Third-person camera built on Cinemachine: a pivot at the player's head that carries the orbit
    /// angles, and a CinemachineCamera using ThirdPersonFollow (with obstacle avoidance) that frames
    /// the pivot. The orbit is set through <see cref="SetOrbit"/> so a test can aim deterministically
    /// instead of moving a virtual mouse.
    /// </summary>
    public class PlayerCameraRig : MonoBehaviour
    {
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

        CinemachineThirdPersonFollow _follow;
        CinemachineBrain _brain;

        public void Build(Transform head, Camera output)
        {
            Output = output != null ? output : UnityEngine.Camera.main;
            Pivot = transform.Find("CameraPivot");
            if (Pivot == null)
            {
                var go = new GameObject("CameraPivot");
                go.transform.SetParent(transform, false);
                Pivot = go.transform;
            }
            Pivot.SetParent(head != null ? head : transform, false);
            Pivot.localPosition = Vector3.zero;

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

            _follow.ShoulderOffset = new Vector3(ShoulderOffset, 0f, 0f);
            _follow.VerticalArmLength = VerticalArm;
            _follow.CameraSide = 1f;
            _follow.CameraDistance = Distance;
            _follow.Damping = new Vector3(0.08f, 0.12f, 0.06f);
            var avoid = _follow.AvoidObstacles;
            avoid.Enabled = true;
            avoid.CameraRadius = CameraRadius;
            avoid.IgnoreTag = "Player";
            _follow.AvoidObstacles = avoid;

            if (Output != null && !Output.TryGetComponent(out _brain)) _brain = Output.gameObject.AddComponent<CinemachineBrain>();
            Apply();
        }

        void Update()
        {
            if (!MouseInput || Output == null) return;
            AddOrbit(Input.GetAxis("Mouse X") * Sensitivity, Input.GetAxis("Mouse Y") * Sensitivity);
            var scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 0.01f) SetDistance(Distance - scroll * 0.25f);
            Apply();
        }

        public void SetOrbit(float yawDegrees, float pitchDegrees)
        {
            Yaw = Mathf.Repeat(yawDegrees, 360f);
            Pitch = Mathf.Clamp(pitchDegrees, MinPitch, MaxPitch);
            Apply();
        }

        public void AddOrbit(float deltaYaw, float deltaPitch)
        {
            SetOrbit(Yaw + deltaYaw, Pitch - deltaPitch);
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

        public void SetDistance(float distance)
        {
            Distance = Mathf.Clamp(distance, 0.9f, 5f);
            if (_follow != null) _follow.CameraDistance = Distance;
        }

        /// <summary>Push the orbit angles onto the pivot; Cinemachine resolves the camera pose from it.</summary>
        public void Apply()
        {
            if (Pivot != null) Pivot.rotation = Quaternion.Euler(Pitch, Yaw, 0f);
        }
    }
}