using System.Collections.Generic;
using Mas3D.Avatar;
using UnityEngine;

namespace Mas3D.Actions
{
    /// <summary>
    /// Task 3 of docs/superpowers/plans/2026-11-04-unity-actions-p4.md: the performance state
    /// machine. It owns one action at a time, remembers what the player looked like before the
    /// action (pose, spot, facing, keyboard, camera orbit), and puts every one of those things back
    /// when the action ends.
    ///
    /// Three plans resolve so far — hands, pat-her and pat-me — and every one of them solves a real
    /// contact, so <see cref="ContactError"/> is a measured gap and <see cref="Held"/> only counts
    /// while that gap stays inside <see cref="ContactTolerance"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ActionDirector : MonoBehaviour
    {
        /// <summary>Who performs the action; the reach check runs against her arm bones.</summary>
        public AvatarActor Actor;

        /// <summary>The player body that gets moved and locked.</summary>
        public PlayerMotor Player;

        /// <summary>The orbit that gets moved and restored; optional so a bare rig can still refuse.</summary>
        public PlayerCameraRig Camera;

        /// <summary>When true, Escape ends the action.</summary>
        public bool KeyboardInput = true;

        /// <summary>When true, Update drives <see cref="Tick(float)"/>; tests turn this off.</summary>
        public bool DriveInUpdate = true;

        /// <summary>Distance in front of her seat where the player stands for "hands".</summary>
        public const float HandsOffset = 0.62f;

        /// <summary>
        /// Distance in front of her seat where the player crouches for "pat-me": the same marker the
        /// hands action uses, so one spot serves both. Straight-backed she cannot span it — her arm is
        /// 0.4708 m and an upright seated shoulder leaves the crouched crown 0.5678 m off — so the
        /// <see cref="ActionPoses.PatMe"/> table leans her torso in and the pre-check measures the
        /// shoulder that lean produces, not the upright one: 0.4383 m shoulder-to-crown, and the palm
        /// lands on the crown with 0.0000 m to spare.
        /// </summary>
        public const float PatMeOffset = 0.62f;

        /// <summary>
        /// Distance in front of her seat where the player kneels for the kiss. 0.31 m is what the scan
        /// answered: with the 54-degree <see cref="ActionPoses.Kiss"/> bow her head centre sits
        /// 0.0134 m from his face and 3.6 degrees off his gaze, so the contact the hold needs is
        /// there; at 0.34 the gap is 1.8 cm and the hold never credits, and at 0.30 the faces have
        /// already crossed (1.2 cm apart, but her gaze runs 50 degrees past his face).
        /// <para>
        /// The kneeling capsule (radius 0.30) therefore reaches x = 0.095 and overlaps the bench mesh
        /// Mesh_0001out (AABB from x = -0.189) by 0.284 m. That overlap is approved for the kiss:
        /// two faces have to meet at one point and no stance reaches it from further out. None of it
        /// is on screen in first person — the camera sits at the kneeling eye 1.28, well above the
        /// 0.604 m seat top, and the player has no body mesh. The one line kept is that the kneeling
        /// eye must stay above seat top + 0.40 m (1.28 > 1.004), or the near plane would look into
        /// the cushion; <c>ActionKissTests</c> asserts it.
        /// </para>
        /// </summary>
        public const float KissOffset = 0.31f;

        /// <summary>
        /// The bed slot in anchors.json. S2 is the mattress centre line (x 1.741, z -0.025, topY 0.906):
        /// the only bed anchor the generator produces, so "sit together" sits them on the bed surface
        /// rather than on an edge that has no measured slot. See the Task 7 note in the plan.
        /// </summary>
        public const string BedSeatId = "S2";

        /// <summary>
        /// Camera yaw for "sit together", relative to her facing: 200 degrees puts the camera in front
        /// of and a little to her right, so the shot holds both seated bodies instead of the back of
        /// her head.
        /// </summary>
        public const float SitOrbitYaw = 200f;

        /// <summary>Camera pitch the "hands" shot is framed with.</summary>
        public const float HandsOrbitPitch = 10f;

        /// <summary>
        /// Camera yaw for the kiss, relative to his facing. The other actions put the arm on his own
        /// gaze line and 0.62 m of room absorbs the 0.20 m camera sphere; the kiss stands him 0.31 m
        /// from her, and Cinemachine's shoulder offset then lands the camera hand inside her body, so
        /// the boom collapses to 0.085 m - a screenful of hair. A quarter turn lays the arm across
        /// the room instead: the shoulder goes away from her and the shot becomes the profile view
        /// both faces read best in.
        /// </summary>
        public const float KissOrbitYaw = 90f;

        /// <summary>Boom clearance the room is tuned for, restored whenever an action ends.</summary>
        public const float RoomCameraRadius = 0.20f;

        /// <summary>
        /// Boom clearance while the kiss plays. See <see cref="KissOrbitYaw"/>: two heads 1.4 cm
        /// apart leave no room for the room's sphere.
        /// </summary>
        public const float KissCameraRadius = 0.06f;

        /// <summary>Boom length for the kiss: 2.8 m leaves the pair a small island in a big room.</summary>
        public const float KissCameraDistance = 1.9f;

        /// <summary>How far apart her palm and the player's may be and still count as contact, in metres.</summary>
        public const float ContactTolerance = 0.02f;

        /// <summary>
        /// Longest wall-clock step Update credits. A hitch, a paused editor or a resumed window must
        /// not jump the action forward, so the clamp lives where real time enters and
        /// <see cref="Tick(float)"/> stays linear for callers that drive exact time.
        /// </summary>
        public const float MaxUpdateStep = 0.1f;

        /// <summary>Everything one action needs decided up front, before any state is touched.</summary>
        struct Plan
        {
            public PlayerPosture Posture;
            public Vector3 Spot;
            public float Yaw;
            public float OrbitYaw;
            public float OrbitPitch;

            /// <summary>The surface the plan sits the player on, or 0 when the player stays upright.</summary>
            public float SeatTopY;
        }

        string _current;
        float _held;
        float _contactError;
        string _lastFaults;

        Vector3 _wasSpot;
        float _wasYaw;
        PlayerPosture _wasPosture;
        bool _wasKeyboardInput;
        float _wasCameraYaw;
        float _wasCameraPitch;
        float _wasCameraDistance;
        float _wasCameraRadius;
        bool _wasMouseInput;
        int _clearPoseCount;

        /// <summary>Where the bed plan puts her hips, so the sit's contact can be measured live.</summary>
        Vector3 _bedHips;

        /// <summary>Id of the action in progress, or null while idle.</summary>
        public string Current => _current;

        /// <summary>Seconds of unbroken contact the current action has held.</summary>
        public float Held => _held;

        /// <summary>Metres between her palm and the player's marker, measured after the last solve.</summary>
        public float ContactError => _contactError;

        /// <summary>Why the last entry or cancel was turned down, or null when it went through.</summary>
        public string LastFaults => _lastFaults;

        /// <summary>How often the pose layer was handed back; one action must restore exactly once.</summary>
        public int ClearPoseCount => _clearPoseCount;

        void Update()
        {
            if (KeyboardInput && Input.GetKeyDown(KeyCode.Escape)) Cancel();
            if (DriveInUpdate) Tick(Mathf.Min(Time.deltaTime, MaxUpdateStep));
        }

        void OnDisable()
        {
            // Closing the component or unloading the scene must never leave the player locked.
            if (_current != null) Cancel();
        }

        /// <summary>
        /// Enters an action. This is a question, not a command: an unknown id, an id this task has
        /// no plan for, a busy director, or a marker the actor cannot reach all return false and
        /// leave the player, the camera and the posture exactly as they were.
        /// </summary>
        public bool TryEnterAction(string id)
        {
            if (_current != null) return Refuse("busy: " + _current);
            if (string.IsNullOrEmpty(id) || System.Array.IndexOf(ActionId.All, id) < 0)
                return Refuse("unknown action: " + id);
            if (Actor == null || Actor.Rig == null) return Refuse("missing actor: " + id);
            if (Player == null || Player.Head == null) return Refuse("missing player: " + id);

            if (!ResolvePlan(id, out var plan)) return Refuse(_lastFaults ?? (id + ": not implemented"));
            if (!Reachable(id, plan)) return Refuse(id + ": out of reach");

            _wasSpot = Player.transform.position;
            _wasYaw = Player.transform.eulerAngles.y;
            _wasPosture = Player.Posture;
            _wasKeyboardInput = Player.KeyboardInput;
            if (Camera != null)
            {
                _wasCameraYaw = Camera.Yaw;
                _wasCameraPitch = Camera.Pitch;
                _wasCameraDistance = Camera.Distance;
                _wasCameraRadius = Camera.CameraRadius;
                _wasMouseInput = Camera.MouseInput;
            }

            // She sits down first. Sit-together is the one action that moves her: the player's slot is
            // her twin on the mattress, and her capsule is fitted from the hips the bed pose leaves.
            // Her seat is the one step that can fail without throwing, and a silent failure would leave
            // the player standing at a mattress she is not on: DriveSitTogether then measures a 1.5 m
            // gap forever while the director reports a performance. Ask first, and refuse before a
            // single byte of the player's state moves.
            if (id == ActionId.SitTogether && !Actor.SitOnBed(BedSeatId))
                return Refuse("sit-together: she could not sit on the bed");

            Player.KeyboardInput = false;
            try
            {
                Player.SetPosture(plan.Posture, plan.SeatTopY);
                Player.Teleport(plan.Spot, plan.Yaw);
                if (Camera != null)
                {
                    // The kiss stands him 0.31 m from her and her capsule surface is 0.115 m from his
                    // head: a 0.20 m clearance sphere overlaps it before the boom even starts, so
                    // Cinemachine squeezes the arm to 0.085 m and the shot is all hair. Nothing to dodge
                    // at that range anyway - the room's walls are metres away - so the close-up runs a
                    // thin sphere and Cancel puts the room's own radius back.
                    Camera.SetCameraRadius(id == ActionId.Kiss ? KissCameraRadius : RoomCameraRadius);
                    if (id == ActionId.Kiss) Camera.SetDistance(KissCameraDistance);
                    Camera.SetOrbit(plan.OrbitYaw, plan.OrbitPitch);

                    // The mouse owns the orbit and the scroll wheel owns the boom during normal play, so
                    // it also owns this shot the moment the player twitches: the kiss framing is set once
                    // here and would be dragged away on the next frame. Take the input out of its hands
                    // for the performance and give the flag back on Cancel.
                    Camera.MouseInput = false;
                }

                // Snapshot only after the player is in place and before the first pose frame, so the base
                // is the pose she actually wore when the action began (not AvatarPose's rest pose).
                ActionPose.Begin(Actor.Rig);

                _current = id;
                _held = 0f;
                _contactError = 0f;
                _lastFaults = null;
            }
            catch (System.Exception boom)
            {
                // Entering is meant to be atomic (see the summary): if any step throws between locking
                // the keyboard and setting Current, Cancel's idle guard would never run again and the
                // player would stay locked for the rest of the session. Put back what was touched.
                RestoreEntering(id);
                return Refuse(id + ": " + boom.Message);
            }

            if (id == ActionId.Kiss) SetEyesClosed(true);
            return true;
        }

        /// <summary>
        /// Puts back everything <see cref="TryEnterAction"/> touched when it throws halfway through.
        /// Not a Cancel: the action never started, so there is no pose to hand back and
        /// <c>ClearPose</c> would count a performance that never happened.
        /// </summary>
        void RestoreEntering(string id)
        {
            if (Player != null)
            {
                Player.KeyboardInput = _wasKeyboardInput;
                Player.SetPosture(_wasPosture);
                Player.Teleport(_wasSpot, _wasYaw);
            }
            if (Camera != null)
            {
                Camera.MouseInput = _wasMouseInput;
                Camera.SetCameraRadius(_wasCameraRadius);
                Camera.SetDistance(_wasCameraDistance);
                Camera.SetOrbit(_wasCameraYaw, _wasCameraPitch);
            }
            if (id == ActionId.SitTogether && Actor != null)
                Actor.SitAt(string.IsNullOrEmpty(Actor.SeatId) ? AvatarActor.DefaultSeatId : Actor.SeatId);

            _current = null;
            _held = 0f;
            _contactError = 0f;
        }

        /// <summary>
        /// Ends the current action and puts the player, the posture and the camera orbit back where
        /// they were. Calling it while idle does nothing at all — in particular it does not hand the
        /// pose back to a stale capture — and calling it twice is safe.
        /// </summary>
        public void Cancel()
        {
            if (_current == null) return;

            // The keyboard goes back first: it is the one piece of state the player cannot recover
            // themselves, so if anything below throws they can still walk away.
            if (Player != null) Player.KeyboardInput = _wasKeyboardInput;

            // Her eyes are the one piece of *her* state an action borrows; give them back first.
            SetEyesClosed(false);
            try
            {
                ClearPose();

                // The pose came back first; now the whole body goes home. Sit-together is the one
                // action that moved her, and leaving her on the mattress would strand the capsule
                // there for the next action to walk into.
                if (_current == ActionId.SitTogether && Actor != null)
                    Actor.SitAt(string.IsNullOrEmpty(Actor.SeatId) ? AvatarActor.DefaultSeatId : Actor.SeatId);

                if (Player != null)
                {
                    Player.SetPosture(_wasPosture);
                    Player.Teleport(_wasSpot, _wasYaw);
                }
                if (Camera != null)
                {
                    // The radius is restored from the snapshot, not from RoomCameraRadius: the rig owns
                    // its own default and the director only borrows it for the kiss.
                    Camera.SetCameraRadius(_wasCameraRadius);
                    Camera.SetDistance(_wasCameraDistance);
                    Camera.SetOrbit(_wasCameraYaw, _wasCameraPitch);
                    Camera.MouseInput = _wasMouseInput;
                }
            }
            finally
            {
                _current = null;
                _held = 0f;
                _contactError = 0f;
            }
        }

        /// <summary>
        /// Advances the action: solves the current pose, then credits the step to the clock only while
        /// the contact holds. Does nothing while idle.
        /// </summary>
        public void Tick(float dt)
        {
            // A non-positive or NaN step (a comparison with NaN is false) never reaches the clock.
            if (_current == null || !(dt > 0f)) return;

            switch (_current)
            {
                case ActionId.Hands: DriveHands(); break;
                case ActionId.PatHer: DrivePatHer(); break;
                case ActionId.PatMe: DrivePatMe(); break;
                case ActionId.Kiss: DriveKiss(); break;
                case ActionId.SitTogether: DriveSitTogether(); break;
            }

            // A broken contact restarts the hold instead of quietly accumulating through it.
            if (_contactError <= ContactTolerance) _held += dt;
            else _held = 0f;
        }

        /// <summary>
        /// Hands the actor's bones back to her own pose layer: every bone the action snapshotted at
        /// entry gets its local rotation back, verbatim. Safe to call when nothing was captured.
        /// </summary>
        public void ClearPose()
        {
            // The count stays ahead of the guard: it records that the action was asked to hand the
            // bones back, including the teardown call OnDisable makes while she is being destroyed.
            _clearPoseCount++;

            // OnDisable runs mid-Destroy, when the actor or her rig may already be gone; touching a
            // destroyed transform there would trip the test framework, so there is nothing to restore
            // — but the snapshot itself must still go, or it leaks onto the next performer.
            if (Actor == null || Actor.Rig == null)
            {
                ActionPose.Forget();
                return;
            }

            ActionPose.Restore(Actor.Rig);
        }

        /// <summary>
        /// Poses both arms, solves each hand onto the player's palm marker and records the worse of the
        /// two gaps. The arms are rebuilt from the entry snapshot by <see cref="ActionPose.Apply"/> and
        /// then re-solved every frame, so her breathing (AvatarIdle owns Chest/Spine) moves the chain
        /// but never drags the contact out of place.
        /// </summary>
        void DriveHands()
        {
            var rig = Actor.Rig;
            ActionPose.Apply(rig, ActionPoses.Hands);

            var left = DriveHand(rig, true);
            var right = DriveHand(rig, false);
            _contactError = Mathf.Max(left, right);
        }

        /// <summary>Solves one hand onto the player's palm marker and returns the resulting gap.</summary>
        float DriveHand(AvatarRig rig, bool left)
        {
            // Elbows swing out to the player's own left/right and a little forward, never through her.
            var pole = Player.transform.right * (left ? -1f : 1f) + Player.transform.forward * 0.2f;
            return DriveArm(rig, left, PlayerReach.Palm(Player, left), pole);
        }

        /// <summary>Pat-her: bows her head into the hand the player holds over her and measures the gap.</summary>
        void DrivePatHer()
        {
            var rig = Actor.Rig;
            ActionPose.Apply(rig, ActionPoses.PatHer);
            _contactError = BowHeadTo(rig, PlayerReach.Raised(Player, false));
        }

        /// <summary>Pat-me: her right hand finds the crouched player's crown; the left arm stays out.</summary>
        void DrivePatMe()
        {
            var rig = Actor.Rig;
            ActionPose.Apply(rig, ActionPoses.PatMe);

            // The elbow goes forward, like a real hand coming over a head: a sideways pole would
            // stick her elbow out beside the player's face.
            _contactError = DriveArm(rig, false, PlayerReach.Crown(Player), Player.transform.forward);
        }

        /// <summary>
        /// Points the top of her head at <paramref name="hand"/> and returns how far it still is.
        /// The pose table brings the head joint inside 0.22 m of the palm; the head bone is 0.20 m
        /// long, so aiming its up axis at the palm is the closest the top can ever get and the
        /// residual is exactly |distance - 0.20|. Left to the table alone the top lands wherever the
        /// neck happens to point, which is what left the plan's version 29 cm short.
        /// </summary>
        float BowHeadTo(AvatarRig rig, Vector3 hand)
        {
            var head = rig.Bone(HumanBodyBones.Head);
            if (head == null) return float.PositiveInfinity;

            var to = hand - head.position;
            var distance = to.magnitude;
            if (distance > 1e-5f)
            {
                var up = to / distance;

                // LookRotation needs a forward that is not parallel to up; the head's own forward
                // projected into the new plane keeps her facing the same way while she bows.
                var forward = Vector3.ProjectOnPlane(head.forward, up);
                if (forward.sqrMagnitude <= 1e-6f) forward = Vector3.ProjectOnPlane(Vector3.forward, up);
                if (forward.sqrMagnitude > 1e-6f)
                    head.rotation = Quaternion.LookRotation(forward.normalized, up);
            }

            return Vector3.Distance(ActionGeometry.HerHeadTop(rig), hand);
        }

        /// <summary>
        /// Kiss: leans her in and measures the gap between her head centre and the player's face. The
        /// contact is measured rather than solved — she does not reach for him; <see cref="KissOffset"/>
        /// puts him where the pose lands her face, and <see cref="ContactError"/> reports the truth of
        /// that placement, including the direction, which is what <see cref="ActionGeometry.FaceAlign"/>
        /// turns into degrees.
        /// </summary>
        void DriveKiss()
        {
            var rig = Actor.Rig;
            ActionPose.Apply(rig, ActionPoses.Kiss);
            _contactError = Vector3.Distance(ActionGeometry.HerHeadCenter(rig), PlayerReach.Face(Player));
        }

        /// <summary>
        /// Sit-together: she is already posed on the mattress by <see cref="AvatarActor.SitOnBed"/>, so
        /// nothing is solved here — the contact is whether her hips are still where the plan put them,
        /// measured flat and vertical against <see cref="AvatarSeat.HipsGap"/>'s target and reported as
        /// the worse of the two.
        /// </summary>
        void DriveSitTogether()
        {
            var hips = Actor != null && Actor.Seat != null ? Actor.Seat.HipsWorld : _bedHips;
            var flat = new Vector2(hips.x - _bedHips.x, hips.z - _bedHips.z).magnitude;
            var drop = Mathf.Abs(hips.y - _bedHips.y);
            _contactError = Mathf.Max(flat, drop);
        }

        /// <summary>Opens or shuts her eyes through the idle's own switch; a bare rig has no eyes.</summary>
        void SetEyesClosed(bool closed)
        {
            var idle = Actor != null ? Actor.Idle : null;
            if (idle != null) idle.EyesClosed = closed;
        }

        /// <summary>
        /// Solves one arm onto <paramref name="target"/> and returns the gap from her palm to it.
        /// Every plan shares this: only the marker, the pole and the table in front of it differ.
        /// </summary>
        float DriveArm(AvatarRig rig, bool left, Vector3 target, Vector3 pole)
        {
            var upper = rig.Bone(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
            var lower = rig.Bone(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
            var hand = rig.Bone(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            if (upper == null || lower == null || hand == null) return float.PositiveInfinity;

            // TwoBoneIk puts the hand bone itself on the target, but the contact point is
            // ActionGeometry.PalmForward in front of that bone. Aim the wrist short of the target by
            // exactly that offset, then turn the hand at it; HerPalm is then the target, whereas
            // solving straight at it would leave the proxy that far past.
            var approach = target - upper.position;
            approach = approach.sqrMagnitude > 1e-8f ? approach.normalized : Player.transform.forward;
            var wrist = target - approach * ActionGeometry.PalmForward;

            TwoBoneIk.Solve(upper, lower, wrist, pole);

            var look = target - hand.position;
            if (look.sqrMagnitude > 1e-8f)
                hand.rotation = Quaternion.LookRotation(look, Player.transform.up);

            return Vector3.Distance(ActionGeometry.HerPalm(rig, left), target);
        }

        bool Refuse(string fault)
        {
            // Diagnostics only: a refusal changes no player, camera or action state.
            _lastFaults = fault;
            return false;
        }

        bool ResolvePlan(string id, out Plan plan)
        {
            plan = new Plan();
            var known = id == ActionId.Hands || id == ActionId.PatHer || id == ActionId.PatMe
                || id == ActionId.Kiss || id == ActionId.SitTogether;
            if (!known)
            {
                // Anything else is a refusal, not a stretched pose.
                _lastFaults = id + ": not implemented";
                return false;
            }

            // pat-me and the kiss are about the player's own stance, and the stance is the player's to
            // choose: refuse instead of quietly pressing them into one. Each action names its own
            // stance, because they are not the same stance: pat-me is solved by leaning her torso over
            // a crouched player, while the kiss puts two faces on one point and no crouch can close
            // that 0.33 m gap — the player has to kneel.
            var patMe = id == ActionId.PatMe;
            var kiss = id == ActionId.Kiss;
            if (patMe && Player.Posture != PlayerPosture.Crouching)
            {
                _lastFaults = "需要蹲下";
                return false;
            }

            if (kiss && Player.Posture != PlayerPosture.Kneeling)
            {
                _lastFaults = "需要跪下";
                return false;
            }

            var table = AnchorTable.Load();

            // Sit-together is the one plan that moves her: both bodies sit on the bed, so it is the bed
            // slot plus the synthetic twin 0.70 m along her right hand, not a marker in front of her.
            if (id == ActionId.SitTogether)
            {
                var bed = table != null ? table.Seat(BedSeatId) : null;
                if (bed == null)
                {
                    _lastFaults = id + ": bed " + BedSeatId + " missing";
                    return false;
                }

                var pair = ActionSeats.BedPair(bed);
                plan.Posture = PlayerPosture.Sitting;
                plan.SeatTopY = bed.topY;
                plan.Spot = new Vector3(pair.x, 0f, pair.z);
                plan.Yaw = bed.facingY;
                plan.OrbitYaw = Mathf.Repeat(bed.facingY + SitOrbitYaw, 360f);
                plan.OrbitPitch = HandsOrbitPitch;
                _bedHips = new Vector3(bed.x, bed.topY + AvatarSeat.HipsGap, bed.z);
                return true;
            }

            var seatId = string.IsNullOrEmpty(Actor.SeatId) ? AvatarActor.DefaultSeatId : Actor.SeatId;
            var seat = table != null ? table.Seat(seatId) : null;
            if (seat == null)
            {
                _lastFaults = id + ": seat " + seatId + " missing";
                return false;
            }

            var facing = Quaternion.Euler(0f, seat.facingY, 0f);
            plan.Posture = kiss ? PlayerPosture.Kneeling
                : patMe ? PlayerPosture.Crouching
                : PlayerPosture.Standing;
            var offset = kiss ? KissOffset : patMe ? PatMeOffset : HandsOffset;
            plan.Spot = new Vector3(seat.x, 0f, seat.z) + facing * Vector3.forward * offset;
            plan.Yaw = Mathf.Repeat(seat.facingY + 180f, 360f);
            plan.OrbitYaw = kiss ? Mathf.Repeat(plan.Yaw + KissOrbitYaw, 360f) : plan.Yaw;
            plan.OrbitPitch = HandsOrbitPitch;
            return true;
        }

        /// <summary>
        /// Whether the plan's contact is inside her reach before anything moves. Which arm has to
        /// reach is the action's own business: hands uses both palms, pat-me her right hand against
        /// the crouched crown, and pat-her neither — there the player's hand travels to her head, so
        /// her own arm can never make the entry impossible.
        /// </summary>
        bool Reachable(string id, in Plan plan)
        {
            switch (id)
            {
                case ActionId.PatHer:
                case ActionId.Kiss:
                case ActionId.SitTogether:
                    return true;
                case ActionId.PatMe:
                    return ReachesCrown(plan);
                default:
                    return Reaches(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, plan, true)
                        && Reaches(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, plan, false);
            }
        }

        bool Reaches(HumanBodyBones upperBone, HumanBodyBones lowerBone, in Plan plan, bool left)
        {
            var upper = Actor.Rig.Bone(upperBone);
            var lower = Actor.Rig.Bone(lowerBone);
            return TwoBoneIk.CanReach(upper, lower, PlannedPalm(plan, left));
        }

        /// <summary>The same question for pat-me, whose marker is a crown rather than a palm.</summary>
        bool ReachesCrown(in Plan plan)
        {
            var upper = Actor.Rig.Bone(HumanBodyBones.RightUpperArm);
            var lower = Actor.Rig.Bone(HumanBodyBones.RightLowerArm);
            if (upper == null) return false;

            var crown = PlannedPoint(plan, PlayerReach.Crown(Player));
            var approach = crown - upper.position;
            approach = approach.sqrMagnitude > 1e-8f ? approach.normalized : Player.transform.forward;

            // DriveArm solves the wrist, one palm-length short of the marker; ask about that point,
            // and ask it from the shoulder the leaning table produces rather than the upright one.
            var wrist = crown - approach * ActionGeometry.PalmForward;
            var shoulder = ShoulderUnder(ActionPoses.PatMe);
            return TwoBoneIk.CanReachFrom(upper, lower, shoulder, wrist);
        }

        /// <summary>
        /// Where her right shoulder ends up once <paramref name="table"/> has leaned her torso. The two
        /// torso bones are posed for the length of this measurement and put back verbatim: no snapshot,
        /// no claim, nothing that outlives the question.
        /// </summary>
        Vector3 ShoulderUnder(IReadOnlyDictionary<HumanBodyBones, Vector3> table)
        {
            var rig = Actor.Rig;
            var upper = rig.Bone(HumanBodyBones.RightUpperArm);
            if (upper == null) return Vector3.zero;

            var bones = new[] { HumanBodyBones.Spine, HumanBodyBones.Chest };
            var saved = new Quaternion[bones.Length];
            var posed = new bool[bones.Length];
            for (var i = 0; i < bones.Length; i++)
            {
                var bone = rig.Bone(bones[i]);
                if (bone == null || !table.TryGetValue(bones[i], out var euler)) continue;
                saved[i] = bone.localRotation;
                posed[i] = true;
                bone.localRotation = bone.localRotation * Quaternion.Euler(euler);
            }

            var shoulder = upper.position;
            for (var i = 0; i < bones.Length; i++)
            {
                if (!posed[i]) continue;
                rig.Bone(bones[i]).localRotation = saved[i];
            }

            return shoulder;
        }

        /// <summary>
        /// Where the player's palm will be once the plan is committed. The offset comes from the
        /// live reach proxy rather than a second copy of its constants: take the palm relative to
        /// the player now, swap the eye height the posture change implies, then place it at the
        /// planned spot and yaw.
        /// </summary>
        Vector3 PlannedPalm(in Plan plan, bool left) => PlannedPoint(plan, PlayerReach.Palm(Player, left));

        /// <summary>Moves one of the player's live proxy points to where the plan will put them.</summary>
        Vector3 PlannedPoint(in Plan plan, Vector3 world)
        {
            var local = Player.transform.InverseTransformPoint(world);
            // The live eye height, not the table: SetPosture already wrote it (a seat adds seatTopY to
            // it), and reading the table here would put a sitting action's hand target 0.306 m low.
            local.y += PlayerMotor.Metrics(plan.Posture).eye - Player.EyeHeight;
            return plan.Spot + Quaternion.Euler(0f, plan.Yaw, 0f) * local;
        }
    }
}
