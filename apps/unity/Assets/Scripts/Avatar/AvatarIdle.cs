using UniVRM10;
using UnityEngine;

namespace Mas3D.Avatar
{
    /// <summary>
    /// Task 3 of docs/superpowers/plans/2026-11-03-unity-avatar-in-scene.md: breathing, a slow
    /// weight shift and an occasional blink, layered on top of whatever pose somebody else owns.
    /// Every tick rewrites the driven bones as <c>baseline * offset</c>, so offsets never
    /// accumulate and the seated pose survives underneath.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AvatarIdle : MonoBehaviour
    {
        /// <summary>The rig this idle animates; falls back to the rig on the same GameObject.</summary>
        public AvatarRig Rig;

        /// <summary>
        /// Seconds per breath. 3.0 is 20 breaths a minute. The plan said 3.6 (16.7/min), but a
        /// waveform of period 3.6 s is back at its t=0.9 value at t=4.5 s, so the "moves at 0.9 s,
        /// home at 4.5 s" test could never hold for any periodic breathing.
        /// </summary>
        public float BreathPeriod = 3.0f;

        /// <summary>Half-swing of the chest about X; the peak-to-peak travel is twice this.</summary>
        public float BreathDegrees = 1.1f;

        /// <summary>Seconds per weight shift: deliberately slower than the breath, not a multiple.</summary>
        public float SwayPeriod = 7.2f;

        /// <summary>Half-swing of the spine about Z.</summary>
        public float SwayDegrees = 0.8f;

        /// <summary>Seconds between blinks.</summary>
        public float BlinkPeriod = 4.2f;

        /// <summary>Open-to-shut-to-open time of one blink.</summary>
        public float BlinkSeconds = 0.12f;

        /// <summary>A full blink reads as dead rather than alive; keep the lid slightly open.</summary>
        const float BlinkCap = 0.85f;

        /// <summary>Below this the pose is ours; above it somebody else moved the bone.</summary>
        const float StaleEpsilon = 0.01f;

        /// <summary>
        /// Shortest gap between two accepted re-captures. A re-capture restarts the breath clock,
        /// so a writer that touches a driven bone every frame would otherwise keep the breath at
        /// one tick's worth (~0.04 deg) and freeze the blink at its first frame. 0.25 s is a
        /// twelfth of a breath: long enough for the chest to cover half its swing between two
        /// accepted re-captures.
        /// </summary>
        const float RecaptureDebounce = 0.25f;

        Transform _chest;
        Transform _spine;
        Quaternion _chestBase;
        Quaternion _spineBase;
        Quaternion _chestWritten;
        Quaternion _spineWritten;
        bool _chestTouched;
        bool _spineTouched;
        AvatarRig _boundTo;
        bool _captured;
        bool _hasBlink;
        float _time;
        float _sinceRecapture;
        bool _warnedForeignWriter;

        /// <summary>True while the lid is coming down or going up.</summary>
        public bool Blinking { get; private set; }

        /// <summary>Current blink envelope, 0 (open) to <see cref="BlinkCap"/> (shut).</summary>
        public float BlinkWeight { get; private set; }

        /// <summary>
        /// Held shut while true: every tick writes the blink expression at full strength and skips the
        /// blinking envelope, so the face is actually closed on the VRM rather than merely reported as
        /// such. The envelope's own ceiling (<see cref="BlinkCap"/>, 0.85) is untouched and takes over
        /// the moment this goes back to false — that is the difference the kiss test pins.
        /// </summary>
        public bool EyesClosed { get; set; }

        /// <summary>Whoever the eyes are on, if anybody.</summary>
        public Transform GazeTarget { get; private set; }

        void Awake()
        {
            if (Rig == null) Rig = GetComponent<AvatarRig>();
        }

        void OnEnable()
        {
            if (Rig == null) Rig = GetComponent<AvatarRig>();
            if (Rig != null) CaptureBaseline();
        }

        /// <summary>
        /// Snapshot the driven bones' current local rotations as the base every tick stacks onto.
        /// Deliberately not <see cref="AvatarPose.Capture"/>: that returns the standing rest pose,
        /// and the sit table moves the same Chest/Spine, so a rest baseline would erase the seated
        /// torso lean on the first tick. Call this again after <see cref="AvatarSeat.Sit"/> if the
        /// avatar was already ticking in another pose.
        /// </summary>
        public void CaptureBaseline()
        {
            _captured = false;
            _boundTo = null;
            _chest = null;
            _spine = null;
            _chestTouched = false;
            _spineTouched = false;
            _time = 0f;
            _sinceRecapture = RecaptureDebounce;   // an explicit capture is fresh enough to rebase
            _warnedForeignWriter = false;
            BlinkWeight = 0f;
            Blinking = false;

            if (Rig == null) Rig = GetComponent<AvatarRig>();
            if (Rig == null) return;

            _chest = Rig.Bone(HumanBodyBones.Chest);
            _spine = Rig.Bone(HumanBodyBones.Spine);
            if (_chest == null && _spine == null) return;

            if (_chest != null)
            {
                _chestBase = _chest.localRotation;
                _chestWritten = _chestBase;
            }

            if (_spine != null)
            {
                _spineBase = _spine.localRotation;
                _spineWritten = _spineBase;
            }

            // Once per baseline is enough: the expression set is fixed once the VRM runtime exists,
            // GetWeights() hands back the live input table, and a missing key cannot appear later.
            _hasBlink = Rig.HasExpression(ExpressionKey.Blink);
            _captured = true;
            _boundTo = Rig;

            // SetGaze may have run before the VRM runtime existed and could only remember the target.
            if (GazeTarget != null) ApplyGaze(GazeTarget);
        }

        /// <summary>
        /// Bones another system owns right now. The breath writes Chest and Spine every frame, so an
        /// action that leans her forward would be unwound one frame after it started; whoever poses
        /// those bones claims them here and the breath steps aside until the claim is gone.
        /// </summary>
        public static System.Func<HumanBodyBones, bool> BoneClaim;

        void Update()
        {
            Tick(Time.deltaTime);
        }

        /// <summary>The one time entry point; the tests drive it by hand with the component disabled.</summary>
        public void Tick(float dt)
        {
            if (dt < 0f) dt = 0f;
            if (!EnsureBound()) return;

            // Sit, Stand or anything else wrote these bones since our last tick: our base is stale.
            // A claimed bone is the exception: an action owns it right now, so its lean is not a new
            // resting pose. Without this guard the debounce below would capture the lean as the
            // baseline, and a pat shorter than RecaptureDebounce would weld it in for good (the
            // restore writes back the very rotation we last wrote, so nothing else ever rebases).
            _chestTouched = !Claimed(HumanBodyBones.Chest) && MovedOutside(_chest, _chestWritten);
            _spineTouched = !Claimed(HumanBodyBones.Spine) && MovedOutside(_spine, _spineWritten);
            _sinceRecapture += dt;
            if (_chestTouched || _spineTouched)
            {
                if (_sinceRecapture < RecaptureDebounce)
                {
                    // Too soon: a writer that touches the bones every frame would restart the breath
                    // clock every frame. Hold the baseline and keep breathing.
                    WarnForeignWriter();
                }
                else
                {
                    // Re-read only the bone that moved. Re-reading both would bake our own breath
                    // angle into the other bone's base, ratcheting it up by one breath per event.
                    if (_chestTouched) Rebase(_chest, ref _chestBase, ref _chestWritten);
                    if (_spineTouched) Rebase(_spine, ref _spineBase, ref _spineWritten);
                    _time = 0f;
                    _sinceRecapture = 0f;
                }
            }

            _time += dt;

            var breath = Mathf.Sin(_time / Safe(BreathPeriod) * Mathf.PI * 2f) * BreathDegrees;
            if (_chest != null && !Claimed(HumanBodyBones.Chest))
            {
                _chest.localRotation = _chestBase * Quaternion.Euler(breath, 0f, 0f);
                _chestWritten = _chest.localRotation;
            }

            var sway = Mathf.Sin(_time / Safe(SwayPeriod) * Mathf.PI * 2f) * SwayDegrees;
            if (_spine != null && !Claimed(HumanBodyBones.Spine))
            {
                _spine.localRotation = _spineBase * Quaternion.Euler(0f, 0f, sway);
                _spineWritten = _spine.localRotation;
            }

            TickBlink();
        }

        /// <summary>
        /// Point the eyes at <paramref name="target"/>. UniVRM 0.131.4 has no camera mode — its
        /// <c>LookAtTargetTypes</c> is SpecifiedTransform or YawPitchValue only — and a null target
        /// leaves the last direction in place (the eyes freeze), so pass a real transform; call
        /// again once the rig exists if the runtime is not up yet.
        /// </summary>
        public void SetGaze(Transform target)
        {
            GazeTarget = target;
            ApplyGaze(target);
        }

        void ApplyGaze(Transform target)
        {
            var instance = Rig != null ? Rig.Instance : null;
            if (instance == null) return;

            instance.LookAtTarget = target;
            // The type has to follow the target, or the eyes keep their previous mode.
            instance.LookAtTargetType = VRM10ObjectLookAt.LookAtTargetTypes.SpecifiedTransform;
        }

        void WarnForeignWriter()
        {
            if (_warnedForeignWriter) return;
            _warnedForeignWriter = true;
            Debug.LogWarning(
                "[idle] '" + name + "' keeps having its Chest/Spine overwritten by another writer; " +
                "re-baselining at most every " + RecaptureDebounce.ToString("0.00") +
                " s so the breath keeps moving.", this);
        }

        static void Rebase(Transform bone, ref Quaternion baseRotation, ref Quaternion written)
        {
            if (bone == null) return;
            baseRotation = bone.localRotation;
            written = baseRotation;
        }

        static bool Claimed(HumanBodyBones bone)
        {
            var claim = BoneClaim;
            return claim != null && claim(bone);
        }

        void TickBlink()
        {
            // Forced shut (Task 6): the kiss holds the lids down for as long as the action lasts.
            // The blink envelope is skipped entirely and the expression is written here, every tick:
            // BlinkWeight alone is internal state and would never reach the VRM face.
            if (EyesClosed)
            {
                BlinkWeight = 1f;
                Blinking = true;
                Rig.SetExpression(ExpressionKey.Blink, BlinkWeight);
                return;
            }

            BlinkWeight = 0f;
            if (!_hasBlink || BlinkPeriod <= 0f || BlinkSeconds <= 0f)
            {
                Blinking = false;
                return;
            }

            // Phase off the clock instead of a stateful timer: one blink per period, no drift.
            var phase = Mathf.Repeat(_time, BlinkPeriod);
            if (phase < BlinkSeconds)
            {
                var t = phase / BlinkSeconds;
                BlinkWeight = Mathf.Min(BlinkCap, 1f - Mathf.Abs(2f * t - 1f));
            }

            Blinking = BlinkWeight > 0f;
            Rig.SetExpression(ExpressionKey.Blink, BlinkWeight);
        }

        bool EnsureBound()
        {
            if (Rig == null) Rig = GetComponent<AvatarRig>();
            if (Rig == null) return false;
            if (_captured && _boundTo == Rig) return true;

            CaptureBaseline();
            return _captured;
        }

        static bool MovedOutside(Transform bone, Quaternion written)
        {
            return bone != null && Quaternion.Angle(written, bone.localRotation) > StaleEpsilon;
        }

        static float Safe(float period)
        {
            return period > 0.01f ? period : 0.01f;
        }
    }
}
