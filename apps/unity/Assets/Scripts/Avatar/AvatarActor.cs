using Mas3D.Conversation;
using UnityEngine;

namespace Mas3D.Avatar
{
    /// <summary>
    /// Task 5 of docs/superpowers/plans/2026-11-03-unity-avatar-in-scene.md: the one handle the room
    /// holds for her. It owns nothing and animates nothing — it puts the rig, the seat, the idle and
    /// the mood on one object, gives her a body the player cannot walk through, and is the single
    /// place the room wires gaze and conversation into.
    ///
    /// Why a capsule and not a mesh collider: the player's CharacterController sweeps against
    /// colliders, and a skinned mesh collider would be rebuilt every frame by the idle breathing.
    /// The capsule is a cheap stand-in for "there is a person sitting here".
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AvatarActor : MonoBehaviour
    {
        /// <summary>Radius of the body capsule. Narrower than the player (0.30) on purpose: the
        /// bench S1 sits 0.25 m from the bed, so a wider body would push into it.</summary>
        public const float BodyRadius = 0.20f;

        /// <summary>Total capsule height, hemispheres included.</summary>
        public const float BodyHeight = 1.05f;

        /// <summary>The capsule centre sits this far above the hips joint, so it covers thighs to
        /// shoulders in the seated pose. Read off the seated pose, never off the root: sitting
        /// drops the root about 0.11 m below the floor plane.</summary>
        public const float BodyLift = 0.20f;

        /// <summary>The seat id this actor uses when the scene does not name another one.</summary>
        public const string DefaultSeatId = "S1";

        /// <summary>The rig this actor drives; falls back to the rig on the same GameObject.</summary>
        public AvatarRig Rig;

        /// <summary>Where she sits; falls back to the seat on the same GameObject.</summary>
        public AvatarSeat Seat;

        /// <summary>Breathing, sway and blink; falls back to the idle on the same GameObject.</summary>
        public AvatarIdle Idle;

        /// <summary>The conversation-driven face; falls back to the mood on the same GameObject.</summary>
        public AvatarMood Mood;

        /// <summary>The body capsule, once <see cref="FitBody"/> has run.</summary>
        public CapsuleCollider Body { get; private set; }

        /// <summary>Whether the last <see cref="FitBody"/> left a capsule on the object.</summary>
        bool _bodyFitted;

        /// <summary>Collider a previous <see cref="FitBody"/> queued for destruction. GetComponent keeps
        /// answering with it until the end of that frame, so it must not count as reuse.</summary>
        CapsuleCollider _removed;

        [SerializeField] string _seatId = DefaultSeatId;

        /// <summary>The seat she takes when the scene loads.</summary>
        public string SeatId => _seatId;

        /// <summary>
        /// Seats her when the scene starts. This is not redundant with the build step: AvatarSeat.Seated
        /// is a runtime-only flag, so a scene saved with her sitting loads into a pose nothing believes
        /// in, and the scene builder therefore saves the rest pose and leaves the sitting to this call.
        /// It has to be the first application of the sitting table on this rig, too — AvatarPose caches
        /// the first pose it sees as the rest pose, so re-sitting a saved sitting pose would fold her
        /// legs twice.
        /// </summary>
        void Awake()
        {
            Wire();
        }

        /// <summary>
        /// Seating waits for Start, not Awake. AvatarRig binds to the Vrm10Instance in its own Awake,
        /// and Unity does not promise that Awake order between components on the same object; seating
        /// in Awake can therefore ask a rig that is not attached yet for its Hips and get null.
        /// Start runs after every Awake, and nothing has ticked an Update before it, so the saved
        /// rest pose is still the pose AvatarPose captures as its baseline.
        /// </summary>
        void Start()
        {
            if (Seat != null && !Seat.Seated && Rig != null) SitAt(_seatId);
        }

        /// <summary>
        /// Poll the one bit that says the body is stale. AvatarSeat owns <c>Seated</c> and raises no
        /// event, and Stand is called from outside this component, so the actor cannot be told: a
        /// capsule fitted while seated would otherwise stay frozen where the seated body was and the
        /// player would walk into air in front of the bench.
        /// </summary>
        void LateUpdate()
        {
            if (_bodyFitted != (Seat != null && Seat.Seated)) FitBody();
        }

        /// <summary>
        /// Put the eyes on a head. Null is not "keep whatever the rig does by default": UniVRM 0.131.4
        /// has no camera look-at mode, so this pins <c>LookAtTargetType</c> to
        /// <c>SpecifiedTransform</c> with a null target and the eyes freeze wherever they last pointed
        /// (AvatarIdle.ApplyGaze). Pass a real transform once the rig exists.
        /// </summary>
        public void BindGaze(Transform head)
        {
            Wire();
            if (Idle == null) return;
            Idle.SetGaze(head);
        }

        /// <summary>
        /// Drive the face from a conversation. Null is a no-op, not an error: the mood already
        /// treats a missing controller as "never changes".
        /// </summary>
        public void BindConversation(ConversationController controller)
        {
            Wire();
            if (Mood == null) return;
            Mood.Controller = controller;
        }

        /// <summary>
        /// Sit on a seat from anchors.json, re-base the idle on the seated pose, then fit the body
        /// capsule to where the hips ended up. Order matters: the idle captures the seated bones,
        /// and the capsule is placed from the live hips joint.
        /// </summary>
        public void SitAt(string seatId)
        {
            Wire();
            // Edit-mode callers have no Awake, so a rig restored from the scene is still unbound.
            if (Rig != null) Rig.EnsureBound();
            if (Seat == null)
            {
                Debug.LogError("[actor] no AvatarSeat on " + name, this);
                return;
            }

            var table = AnchorTable.Load();
            var seat = table != null ? table.Seat(seatId) : null;
            if (seat == null)
            {
                Debug.LogError("[actor] seat '" + seatId + "' not found in anchors.json", this);
                return;
            }

            Seat.Sit(seat);
            if (Idle != null) Idle.CaptureBaseline();
            FitBody();
        }

        /// <summary>
        /// Place the capsule from the live hips joint. Safe to call repeatedly; the collider is
        /// reused. When she is not seated there is no height to measure against, so the capsule is
        /// removed instead of left floating where the seated body used to be.
        /// </summary>
        public void FitBody()
        {
            Wire();
            var existing = GetComponent<CapsuleCollider>();
            if (existing == _removed) existing = null;

            if (Seat == null || !Seat.Seated)
            {
                if (existing != null)
                {
                    Kill(existing);
                    _removed = existing;
                }
                Body = null;
                _bodyFitted = false;
                return;
            }

            var body = existing != null ? existing : gameObject.AddComponent<CapsuleCollider>();
            body.direction = 1;
            body.radius = BodyRadius;
            body.height = BodyHeight;
            body.isTrigger = false;
            body.center = transform.InverseTransformPoint(Seat.HipsWorld + Vector3.up * BodyLift);
            Body = body;
            _bodyFitted = true;
        }

        /// <summary>
        /// Instantiate the imported VRM prefab, attach rig/seat/idle/mood, sit her down and give her
        /// a body. The scene builder calls this once at edit time; the scene then carries the result,
        /// so nothing is built at runtime.
        /// </summary>
        public static AvatarActor Spawn(string prefabPath = AvatarRig.DefaultPrefabPath, string seatId = DefaultSeatId)
        {
            var prefab = LoadPrefab(prefabPath);
            if (prefab == null) return null;

            var rig = AvatarRig.Spawn(prefab);
            var actor = rig.gameObject.AddComponent<AvatarActor>();
            actor.Rig = rig;
            actor.Seat = actor.gameObject.AddComponent<AvatarSeat>();
            actor.Idle = actor.gameObject.AddComponent<AvatarIdle>();
            actor.Mood = actor.gameObject.AddComponent<AvatarMood>();

            actor.Seat.Rig = rig;
            actor.Idle.Rig = rig;
            actor.Mood.Rig = rig;
            actor._seatId = seatId;

            actor.SitAt(seatId);
            return actor;
        }

        static GameObject LoadPrefab(string path)
        {
#if UNITY_EDITOR
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) Debug.LogError("[actor] avatar prefab not imported: " + path);
            return prefab;
#else
            Debug.LogError("[actor] a built player cannot load " + path + " by path; bake her into the scene with tools/unity.ps1 player");
            return null;
#endif
        }

        static void Kill(Component component)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) { Object.DestroyImmediate(component); return; }
#endif
            Object.Destroy(component);
        }

        /// <summary>Bind the siblings on this object the way every other avatar component does.</summary>
        void Wire()
        {
            if (Rig == null) Rig = GetComponent<AvatarRig>();
            if (Seat == null) Seat = GetComponent<AvatarSeat>();
            if (Idle == null) Idle = GetComponent<AvatarIdle>();
            if (Mood == null) Mood = GetComponent<AvatarMood>();
        }
    }
}
