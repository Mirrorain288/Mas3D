using System.Collections.Generic;
using System.Globalization;
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

        /// <summary>
        /// Longest remaining gap the entry is willing to walk. A player further than this is first
        /// placed on the line at this distance: the entry introduces the action, it is not a
        /// cross-room commute, and the room's furniture makes a long sweep unreliable.
        /// </summary>
        public const float MaxApproach = 2.5f;

        /// <summary>Remaining gap the entry tolerates when it ends; anything larger is snapped, in metres.</summary>
        public const float ApproachArrive = 0.05f;

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
        ActionPhase _phase;
        float _phaseTime;
        float _phaseDuration;
        float _leaveFrom = 1f;
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

        /// <summary>The walk an approach entry follows; unused by a settle and while idle.</summary>
        PlayerPath _path;

        /// <summary>True while the entry still owns the player's feet (an approach, before the snap).</summary>
        bool _pathActive;

        /// <summary>Where the entry's camera ends: the plan's orbit and the boom it asks for.</summary>
        float _cameraOrbitYaw;
        float _cameraOrbitPitch;
        float _cameraDistance;

        /// <summary>Where the camera stood when the leave began; the leave eases home from here.</summary>
        float _leaveCameraYaw;
        float _leaveCameraPitch;
        float _leaveCameraDistance;

        /// <summary>Arm bones the IK may claim, in the order the blend snapshot stores them.</summary>
        static readonly HumanBodyBones[] ArmBones =
        {
            HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
            HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
        };

        /// <summary>Their local rotations before the first solve this frame; reused, never allocated.</summary>
        readonly Quaternion[] _armSnapshot = new Quaternion[6];
        bool _armSnapshotTaken;

        /// <summary>Id of the action in progress, or null while idle.</summary>
        public string Current => _current;

        /// <summary>Seconds of unbroken contact the current action has held.</summary>
        public float Held => _held;

        /// <summary>Metres between her palm and the player's marker, measured after the last solve.</summary>
        public float ContactError => _contactError;

        /// <summary>Which leg of the performance the clock is on; None while idle.</summary>
        public ActionPhase Phase => _phase;

        /// <summary>
        /// Seconds spent in the current phase. The hold continues the entry's clock from the fraction
        /// it carried over, so it neither freezes nor restarts at zero.
        /// </summary>
        public float PhaseTime => _phaseTime;

        /// <summary>Nominal length of the current phase in seconds; zero for the hold and for idle.</summary>
        public float PhaseDuration => _phaseDuration;

        /// <summary>
        /// How much of her bones the performance may claim, 0 to 1: <see cref="PerformanceTiming.IkWeight"/>
        /// of the phase and its progress. The hold is always 1 and idle always 0. A leave starts from
        /// the weight it interrupted, so pressing Escape mid-entry never snaps her to full strength.
        /// </summary>
        public float PhaseWeight
        {
            get
            {
                float u = _phaseDuration > 0f ? _phaseTime / _phaseDuration : 0f;
                return _phase == ActionPhase.Leave
                    ? _leaveFrom * PerformanceTiming.IkWeight(ActionPhase.Leave, u)
                    : PerformanceTiming.IkWeight(_phase, u);
            }
        }

        /// <summary>Why the last entry or cancel was turned down, or null when it went through.</summary>
        public string LastFaults => _lastFaults;

        /// <summary>How often the pose layer was handed back; one action must restore exactly once.</summary>
        public int ClearPoseCount => _clearPoseCount;

        /// <summary>The mark the last successful entry resolved, in world space; the walk ends here.</summary>
        public Vector3 LastSpot { get; private set; }

        /// <summary>
        /// The player's visible body, when one is installed; null on the bare-capsule rigs the older
        /// tests build. Read through the player rather than cached, so a body spawned after the
        /// director was wired is still found on the next frame.
        /// </summary>
        public PlayerBody Body => Player != null ? Player.GetComponent<PlayerBody>() : null;

        /// <summary>Her avatar, or the player's, whichever this director is folding; null when absent.</summary>
        public AvatarRig BodyRig => Body != null ? Body.Avatar : null;

        void Update()
        {
            if (KeyboardInput && Input.GetKeyDown(KeyCode.Escape)) RequestLeave();
            if (DriveInUpdate) Tick(Mathf.Min(Time.deltaTime, MaxUpdateStep));
        }

        void OnDisable()
        {
            // Closing the component or unloading the scene must never leave the player locked.
            if (_current != null) Cancel();
        }

        /// <summary>
        /// Settle actions place the player on the mark in one frame; approach actions walk to it.
        /// The split is by action, not by distance: even a short kiss is a settle.
        /// </summary>
        public bool IsSettle(string id) => id == ActionId.Kiss || id == ActionId.SitTogether;

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
                LastSpot = plan.Spot;
                Player.SetPosture(plan.Posture, plan.SeatTopY);

                // Settle and approach are different verbs. A settle (the kiss, the two of them on
                // the bed) has to be on its mark on frame one: the shot is framed from there and
                // the pose is measured against it, so there is no walk that could hide a placement.
                // An approach starts from wherever the player stands and walks the rest.
                _pathActive = !IsSettle(id);
                if (_pathActive)
                {
                    var from = Player.transform.position;

                    // More than a staging distance away, the surplus is not walked: the entry
                    // introduces the action, it is not a cross-room commute, and the room's
                    // furniture makes a 5 m sweep unreliable. The cut happens before the walk, on
                    // a spot nobody is looking at yet; every metre after it is swept.
                    if (Vector3.Distance(from, plan.Spot) > MaxApproach)
                    {
                        var back = from - plan.Spot;
                        back.y = 0f;
                        from = plan.Spot + back.normalized * MaxApproach;
                        Player.Teleport(from, Player.transform.eulerAngles.y);
                    }

                    // The yaw comes from the transform after the cut: the cut only moves the player,
                    // it does not turn them, so a long approach still turns on the way in.
                    _path = PlayerPath.Between(from, Player.transform.eulerAngles.y, plan.Spot, plan.Yaw);
                }
                else
                {
                    Player.Teleport(plan.Spot, plan.Yaw);
                }

                _cameraOrbitYaw = plan.OrbitYaw;
                _cameraOrbitPitch = plan.OrbitPitch;
                _cameraDistance = id == ActionId.Kiss ? KissCameraDistance : _wasCameraDistance;
                if (Camera != null)
                {
                    // The kiss stands him 0.31 m from her and her capsule surface is 0.115 m from his
                    // head: a 0.20 m clearance sphere overlaps it before the boom even starts, so
                    // Cinemachine squeezes the arm to 0.085 m and the shot is all hair. Nothing to dodge
                    // at that range anyway - the room's walls are metres away - so the close-up runs a
                    // thin sphere and Cancel puts the room's own radius back.
                    Camera.SetCameraRadius(id == ActionId.Kiss ? KissCameraRadius : RoomCameraRadius);
                    if (id == ActionId.Kiss) Camera.SetDistance(KissCameraDistance);
                    if (!_pathActive) Camera.SetOrbit(plan.OrbitYaw, plan.OrbitPitch);

                    // The mouse owns the orbit and the scroll wheel owns the boom during normal play, so
                    // it also owns this shot the moment the player twitches: the framing is set once
                    // here (or eased in Tick, for a walk) and would be dragged away on the next frame.
                    // Take the input out of its hands for the performance and give the flag back on Cancel.
                    Camera.MouseInput = false;
                }

                // Snapshot only after the player is in place and before the first pose frame, so the base
                // is the pose she actually wore when the action began (not AvatarPose's rest pose).
                ActionPose.Begin(Actor.Rig);

                // The player's legs get their own snapshot, keyed by that body's rig: the crouch, the
                // kneel and the sit all fold them, and the fold has to start from the legs the player
                // arrived with. A rig with no body (the older fixtures) simply has nothing to record.
                PlayerLegs.Begin(BodyRig);

                _current = id;
                _held = 0f;
                _contactError = 0f;
                _phase = ActionPhase.Enter;
                _phaseTime = 0f;
                _phaseDuration = PerformanceTiming.Enter(id);
                _leaveFrom = 1f;
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

            // A throw between the pose snapshot and `_current = id` leaves two captures behind and no
            // Cancel to release them: the pose table's base would make the next Begin short-circuit on
            // a stale base (and hand back this entry's pose instead of its own), and the legs' entry
            // would fold a stance nobody asked for. Release both here.
            ActionPose.Forget();
            RestoreLegs();
            PerformanceMotion.Restore(BodyRig);

            _current = null;
            _held = 0f;
            _contactError = 0f;
            _phase = ActionPhase.None;
            _phaseTime = 0f;
            _phaseDuration = 0f;
            _leaveFrom = 1f;
            _pathActive = false;
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
                // Cancel is the one exit every teardown path takes, so the clock is put back here:
                // a reset that lived in its callers would be forgotten by the next one.
                _current = null;
                _held = 0f;
                _contactError = 0f;
                _phase = ActionPhase.None;
                _phaseTime = 0f;
                _phaseDuration = 0f;
                _leaveFrom = 1f;

                // The walk belongs to the entry that started it; once the performance is gone there
                // is no path left to snap to, and a stale one would teleport the next entry's player.
                _pathActive = false;
            }
        }

        /// <summary>
        /// Asks the current performance to play its exit and then restore. Idle and an action that is
        /// already leaving both answer false, so a held-down Escape asks exactly once; the leave ends
        /// through <see cref="Cancel"/>, which is the path that has always put everything back.
        /// </summary>
        public bool RequestLeave()
        {
            if (_current == null || _phase == ActionPhase.Leave) return false;

            // Read the weight before the phase moves: it is the level the leave has to start from,
            // and the leave curve is 1 at its own zero, so without it the first leave frame would
            // snap her from whatever she had reached straight to full strength.
            _leaveFrom = PhaseWeight;

            // The walk stops where it stands: a leave is performed in place and only the final
            // Cancel puts the body back. The camera, however, has to come home from wherever the
            // entry actually got to - an Escape a third of the way in catches it mid-ease - so its
            // starting point is read here rather than assumed to be the plan.
            _pathActive = false;
            if (Camera != null)
            {
                _leaveCameraYaw = Camera.Yaw;
                _leaveCameraPitch = Camera.Pitch;
                _leaveCameraDistance = Camera.Distance;
            }

            _phase = ActionPhase.Leave;
            _phaseTime = 0f;
            _phaseDuration = PerformanceTiming.Leave(_current);
            return true;
        }

        /// <summary>
        /// Advances the action: solves the current pose, runs the phase clock one step, then credits
        /// the step to the hold only while the contact holds. Does nothing while idle.
        /// </summary>
        public void Tick(float dt)
        {
            // A non-positive or NaN step (a comparison with NaN is false) never reaches the clock.
            if (_current == null || !(dt > 0f)) return;

            // The feet move before the hands are solved: her arm then chases the position this
            // frame actually reached, not the previous frame's, so a walk never drags the contact
            // a frame behind. The clock still advances last, as it always has.
            if (_phase == ActionPhase.Enter && _pathActive) AdvanceApproach(dt);

            DrivePose();

            // The clock runs after the pose, so the frame that finishes an entry still poses her. An
            // entry that runs out turns into the hold; a leave that runs out is over, and the teardown
            // is Cancel's, not the leave's own, so there is only ever one restoring path.
            if (_phase == ActionPhase.Enter)
            {
                _phaseTime += dt;
                if (_phaseTime >= _phaseDuration)
                {
                    // The hold keeps running on the entry's clock, minus the entry's length: the
                    // overshoot of this frame carries over instead of being thrown away, so the
                    // micro-motion in PerformanceMotion.Apply has a clock that never jumps.
                    _phaseTime -= _phaseDuration;
                    _phase = ActionPhase.Hold;
                    _phaseDuration = 0f;

                    // The walk is over; anything the capsule could not sweep is closed in one
                    // step now, while the hold begins and nobody is looking at the seam. The feet
                    // moved after this frame's pose was solved, so solve again from where they
                    // landed: otherwise the seam renders one frame of a reach that is already met.
                    if (_pathActive)
                    {
                        SnapToPathEnd();
                        DrivePose();
                    }

                    // The frame that flipped the phase is neither an entry nor a hold yet, so it
                    // must not credit contact; the next frame starts the hold clean.
                    return;
                }
            }
            else if (_phase == ActionPhase.Leave)
            {
                _phaseTime += dt;
                if (_phaseTime >= _phaseDuration)
                {
                    Cancel();
                    return;
                }

                // The exit's camera move runs on the leave's own clock: the weight curve is flat
                // for its first 30 percent, and an Escape early in an entry would otherwise snap
                // the camera home on the frame it was pressed.
                EaseCameraHome();
            }
            else if (_phase == ActionPhase.Hold)
            {
                _phaseTime += dt;
            }

            // Contact is credited only inside the hold: an entry is still reaching for it and a leave
            // has already let go, so neither may add to Held. A broken contact restarts the hold
            // instead of quietly accumulating through it.
            if (_phase == ActionPhase.Hold && _contactError <= ContactTolerance) _held += dt;
            else if (_phase == ActionPhase.Hold) _held = 0f;
        }

        /// <summary>
        /// One frame of a walked entry: the path point for the phase time already played, moved
        /// through the controller, and the camera eased by the same fraction so it arrives with him
        /// instead of overtaking the walk.
        /// </summary>
        void AdvanceApproach(float dt)
        {
            var u = _phaseDuration > 0f ? Mathf.Clamp01(_phaseTime / _phaseDuration) : 1f;
            _path.Evaluate(u, out var feet, out var yaw);
            if (Player != null) Player.MoveTo(feet, yaw, dt);

            if (Camera == null) return;

            // The camera takes the same smoothstep as the feet. Distance is usually unchanged on an
            // approach (only the kiss moves the boom, and it settles on frame one), so that lerp is
            // a no-op here; the radius was already set at entry, where it has to be exact.
            var p = Mathf.SmoothStep(0f, 1f, u);
            Camera.SetOrbit(
                Mathf.LerpAngle(_wasCameraYaw, _cameraOrbitYaw, p),
                Mathf.Lerp(_wasCameraPitch, _cameraOrbitPitch, p));
            Camera.SetDistance(Mathf.Lerp(_wasCameraDistance, _cameraDistance, p));
        }

        /// <summary>
        /// Closes the walk exactly on its mark. The controller may have been stopped short by a
        /// chair, the bed or a wall; that remainder is a teleport because the capsule cannot be
        /// swept through it, and it is logged so a snap of half a metre shows up in the report
        /// instead of passing as a clean walk.
        /// </summary>
        void SnapToPathEnd()
        {
            _pathActive = false;

            if (Player != null)
            {
                var gap = Vector3.Distance(Player.transform.position, _path.To);
                if (gap > ApproachArrive)
                {
                    Player.Teleport(_path.To, _path.ToYaw);
                    Debug.Log("[stage] approach snapped " + gap.ToString("F3", CultureInfo.InvariantCulture));
                }
            }

            if (Camera != null)
            {
                Camera.SetOrbit(_cameraOrbitYaw, _cameraOrbitPitch);
                Camera.SetDistance(_cameraDistance);
            }
        }

        /// <summary>
        /// The leave's half of the camera move: from wherever the entry had eased it to, back to the
        /// orbit the director borrowed. It runs on the leave's own clock rather than the weight
        /// curve, because the weight is still 1 for the first 30 percent of a leave taken from the
        /// hold - and an Escape pressed mid-entry has a weight near zero with the camera nowhere
        /// near home.
        /// </summary>
        void EaseCameraHome()
        {
            if (Camera == null) return;

            var p = _phaseDuration > 0f ? Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_phaseTime / _phaseDuration)) : 1f;
            Camera.SetOrbit(
                Mathf.LerpAngle(_leaveCameraYaw, _wasCameraYaw, p),
                Mathf.Lerp(_leaveCameraPitch, _wasCameraPitch, p));
            Camera.SetDistance(Mathf.Lerp(_leaveCameraDistance, _wasCameraDistance, p));
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

                // Her bones are gone, but his spine may not be: the breath leaves the last offset it
                // wrote in the player's skeleton, and nothing else ever writes that bone back. Restore
                // when the body survives the teardown; a destroyed rig still answers null here, which
                // is the one case where dropping the snapshot is all that can be done.
                var rig = BodyRig;
                if (rig != null) PerformanceMotion.Restore(rig);
                else PerformanceMotion.Forget();
                RestoreLegs();
                return;
            }

            ActionPose.Restore(Actor.Rig);

            // Hers are handed back by the call above; only his spine is ours to write back.
            PerformanceMotion.Restore(BodyRig);
            RestoreLegs();
        }

        /// <summary>
        /// Solves the running action's pose once. Split out of <see cref="Tick"/> so the frame that
        /// snaps the walk onto its mark can solve a second time, from the feet it actually landed on.
        /// </summary>
        void DrivePose()
        {
            switch (_current)
            {
                case ActionId.Hands: DriveHands(); break;
                case ActionId.PatHer: DrivePatHer(); break;
                case ActionId.PatMe: DrivePatMe(); break;
                case ActionId.Kiss: DriveKiss(); break;
                case ActionId.SitTogether: DriveSitTogether(); break;
            }

            // His body breathes on the same clock, but on a rhythm of its own. Nothing in a performance
            // depends on his skeleton - every marker the IK chases hangs off the player capsule - so
            // this one write cannot move a contact; it is here, once, instead of in each Drive.
            if (_phase == ActionPhase.Hold) PerformanceMotion.ApplyHis(BodyRig, _phaseTime);

            // The legs follow the posture the plan put the player in. Solving them after her own pose
            // means the body is re-seated from the pose this frame actually produced, and doing the
            // re-seat here rather than leaving it to LateUpdate is what makes a test that ticks the
            // director without rendering a frame see the floor contact on the tick it asks for.
            ApplyLegs();
        }

        /// <summary>
        /// Lets the held pose breathe: her micro-motion at the current phase time, stacked on the table
        /// this frame already wrote and written before the IK solve, so the solve stays the frame's
        /// truth wherever a hand or her head is glued to a marker. Anything but a hold has nothing to add.
        /// </summary>
        void Breathe(AvatarRig rig)
        {
            if (_phase != ActionPhase.Hold) return;
            PerformanceMotion.Apply(rig, _current, _phaseTime);
        }

        /// <summary>
        /// Folds the player's legs for the posture the plan chose and lets the body re-seat itself.
        /// A bare player has no rig to fold and no body to move, so this is a no-op there.
        /// </summary>
        void ApplyLegs()
        {
            var rig = BodyRig;
            if (rig == null) return;

            // The fold takes a full weight the moment a posture exists: the stance it has to match is
            // applied by SetPosture in one frame, so easing the fold with PhaseWeight would leave the
            // soles that far under the floor for the whole of Enter and Leave (0.57 m crouched, 0.34 m
            // kneeling). Blending the eye as well does not help - the lift is quadratic in the fold
            // angle and the eye drop is linear, so half the weight still sinks 0.11 m.
            var posture = Player != null ? Player.Posture : PlayerPosture.Standing;
            PlayerLegs.Apply(rig, posture, PlayerLegs.Table(posture) != null ? 1f : PhaseWeight);
            var body = Body;
            if (body != null) body.ApplyNow();
        }

        /// <summary>
        /// Hands the player's legs back to their entry rotations and re-seats the body. Kept beside
        /// <see cref="ApplyLegs"/> so the hand-back happens exactly once, on the same frame the pose
        /// layer hands her bones back.
        /// </summary>
        void RestoreLegs()
        {
            var rig = BodyRig;
            if (rig != null) PlayerLegs.Restore(rig);
            else PlayerLegs.Forget();

            var body = Body;
            if (body != null) body.ApplyNow();
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
            Breathe(rig);

            SnapshotArms(rig);
            var left = DriveHand(rig, true);
            var right = DriveHand(rig, false);
            BlendArms(rig, PhaseWeight);
            _contactError = Mathf.Max(left, right);
        }

        /// <summary>Solves one hand onto the player's palm marker and returns the resulting gap.</summary>
        float DriveHand(AvatarRig rig, bool left)
        {
            // Elbows swing out to the player's own left/right and a little forward, never through her.
            var pole = Player.transform.right * (left ? -1f : 1f) + Player.transform.forward * 0.2f;
            return DriveArm(rig, left, PlayerReach.Palm(Player, left), pole);
        }

        /// <summary>
        /// Stores the six arm rotations the blend has to return to, before the IK writes. A bone the
        /// table wrote takes the pose layer's value; a bone only the IK owns (both upper arms, both
        /// hands) takes the entry base, because its live value on every frame after the first is
        /// already blended - snapshotting that would compound instead of easing back.
        /// </summary>
        void SnapshotArms(AvatarRig rig)
        {
            for (var i = 0; i < ArmBones.Length; i++)
            {
                var bone = rig.Bone(ArmBones[i]);
                if (bone == null) continue;
                _armSnapshot[i] = ActionPose.Claims(ArmBones[i]) || !ActionPose.TryGetBase(ArmBones[i], out var basis)
                    ? bone.localRotation
                    : basis;
            }
            _armSnapshotTaken = true;
        }

        /// <summary>
        /// Gives the IK's claim back to the pose layer in proportion to the weight: a full hold is
        /// the solved chain untouched, an early entry frame is still mostly her own pose, and a
        /// leave hands the chain back as the weight falls. The arrays are fixed, so no frame allocates.
        /// </summary>
        void BlendArms(AvatarRig rig, float weight)
        {
            if (!_armSnapshotTaken) return;
            _armSnapshotTaken = false;

            // At full weight the solve is the frame's truth; writing it back would only round it.
            if (weight >= 1f) return;

            for (var i = 0; i < ArmBones.Length; i++)
            {
                var bone = rig.Bone(ArmBones[i]);
                if (bone == null) continue;
                bone.localRotation = Quaternion.Slerp(_armSnapshot[i], bone.localRotation, weight);
            }
        }

        /// <summary>Pat-her: bows her head into the hand the player holds over her and measures the gap.</summary>
        void DrivePatHer()
        {
            var rig = Actor.Rig;
            ActionPose.Apply(rig, ActionPoses.PatHer);
            Breathe(rig);

            // The head is the only bone BowHeadTo writes, so one snapshot is the whole blend: at a
            // partial weight she aims at the palm only as far as the performance has started.
            var head = rig.Bone(HumanBodyBones.Head);
            var before = head != null ? head.localRotation : Quaternion.identity;
            _contactError = BowHeadTo(rig, PlayerReach.Raised(Player, false));

            var weight = PhaseWeight;
            if (head != null && weight < 1f)
                head.localRotation = Quaternion.Slerp(before, head.localRotation, weight);
        }

        /// <summary>Pat-me: her right hand finds the crouched player's crown; the left arm stays out.</summary>
        void DrivePatMe()
        {
            var rig = Actor.Rig;
            ActionPose.Apply(rig, ActionPoses.PatMe);
            Breathe(rig);

            // The elbow goes forward, like a real hand coming over a head: a sideways pole would
            // stick her elbow out beside the player's face. The gap is kept before the blend so
            // Held measures the intent, not how far the animation has ramped in.
            SnapshotArms(rig);
            var gap = DriveArm(rig, false, PlayerReach.Crown(Player), Player.transform.forward);
            BlendArms(rig, PhaseWeight);
            _contactError = gap;
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
            Breathe(rig);
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
            // No table is applied here: she is posed on the mattress by AvatarActor.SitOnBed, so the
            // breath is the only thing that moves her, and the hips stay where the seat put them.
            Breathe(Actor != null ? Actor.Rig : null);

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
            {
                var rotation = Quaternion.LookRotation(look, Player.transform.up);

                // A held hand has to close its fingers or it reads as a prop. The roll is post-multiplied
                // about the very axis LookRotation just aimed down the approach, so it spins the palm
                // around its own normal: position and forward - the two things the contact is measured
                // from - come out of this bit-identical, and the solve pays nothing for the motion.
                if (_phase == ActionPhase.Hold)
                    rotation *= Quaternion.Euler(0f, 0f, PerformanceMotion.HandRoll(left, _current, _phaseTime));

                hand.rotation = rotation;
            }

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
