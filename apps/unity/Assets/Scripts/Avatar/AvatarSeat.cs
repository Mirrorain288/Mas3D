using System.Text;
using UnityEngine;

namespace Mas3D.Avatar
{
    /// <summary>
    /// Places one avatar on one seat from anchors.json. The pose itself is <see cref="AvatarPose"/>;
    /// this component only decides where the root goes and how high the hips end up, so the seat
    /// geometry can change without touching a single bone angle.
    /// </summary>
    [DisallowMultipleComponent]
    public class AvatarSeat : MonoBehaviour
    {
        /// <summary>
        /// Clearance between the seat height and the hips joint. Reverse-computed from the hips skin,
        /// not from the skirt: 0.14 puts the lowest hips vertex 9 mm above the seat top at S1
        /// (0.613 vs 0.604). The skirt is spring-bone cloth and still hangs through the bench; that is
        /// a known cosmetic issue and raising this gap would only float her above the seat.
        /// </summary>
        public const float HipsGap = 0.14f;

        /// <summary>The rig this seat drives; falls back to the rig on the same GameObject.</summary>
        public AvatarRig Rig;

        /// <summary>True between <see cref="Sit"/> and <see cref="Stand"/>.</summary>
        public bool Seated { get; private set; }

        /// <summary>
        /// The hips joint in world space, read live: a caller that measures a frame later sees where
        /// the hips actually are, not where they were when Sit returned.
        /// </summary>
        public Vector3 HipsWorld
        {
            get
            {
                var hips = Rig != null ? Rig.Bone(HumanBodyBones.Hips) : null;
                return hips != null ? hips.position : transform.position;
            }
        }

        void Awake()
        {
            if (Rig == null) Rig = GetComponent<AvatarRig>();
        }

        /// <summary>
        /// Sit on <paramref name="seat"/>: root onto the seat's xz and facing, pose the bones, then
        /// lift the root alone until the hips centre sits <see cref="HipsGap"/> above the seat height.
        /// </summary>
        public void Sit(SeatEntry seat)
        {
            if (seat == null)
            {
                Debug.LogError("[seat] null seat entry", this);
                return;
            }

            if (!EnsureRig()) return;

            // Drop the flag before the pose work: a Sit that returns early (a rig without Hips)
            // must not leave the previous true behind, or the body capsule stays fitted to a pose
            // that no longer exists.
            Seated = false;

            var root = Root();
            root.SetPositionAndRotation(
                new Vector3(seat.x, 0f, seat.z),
                Quaternion.Euler(0f, seat.facingY, 0f));
            AvatarPose.Sitting(Rig);

            var hips = Rig.Bone(HumanBodyBones.Hips);
            if (hips == null)
            {
                Debug.LogError("[seat] rig has no Hips bone", this);
                return;
            }

            // Only the root moves, never a bone: bones keep the pose, the whole body rises.
            root.position += Vector3.up * ((seat.y + HipsGap) - hips.position.y);
            Seated = true;
        }

        /// <summary>
        /// Sit on the bed: the same route as <see cref="Sit"/>, with the bed pose and the hips lifted
        /// to <c>seat.topY + HipsGap</c> instead of <c>seat.y + HipsGap</c>. On a bed the contact
        /// surface is the mattress top, not the hips anchor: S2 carries y 0.916 and topY 0.906, and
        /// sitting to y would bury her a centimetre into the duvet she is supposed to rest on.
        /// </summary>
        /// <returns>True when she is actually on the mattress; false when the entry or her rig could
        /// not carry the pose, in which case nothing moved past the failed step and
        /// <see cref="Seated"/> is false.</returns>
        public bool SitOnBed(SeatEntry seat)
        {
            if (seat == null)
            {
                Debug.LogError("[seat] null bed entry", this);
                return false;
            }

            if (!EnsureRig()) return false;

            // Same guard as Sit: a bed sit that returns early must not leave Seated true behind, or
            // the body capsule stays fitted to a pose that no longer exists.
            Seated = false;

            var root = Root();
            root.SetPositionAndRotation(
                new Vector3(seat.x, 0f, seat.z),
                Quaternion.Euler(0f, seat.facingY, 0f));
            AvatarPose.BedSitting(Rig);

            var hips = Rig.Bone(HumanBodyBones.Hips);
            if (hips == null)
            {
                Debug.LogError("[seat] rig has no Hips bone", this);
                return false;
            }

            // Only the root moves, never a bone: the bed table keeps the pose, the whole body rises.
            root.position += Vector3.up * ((seat.topY + HipsGap) - hips.position.y);
            Seated = true;
            LogBed(seat);
            return true;
        }

        /// <summary>
        /// One line per bed sit with the numbers the bed table was tuned against: where the hips landed
        /// against the mattress top and how high each leg bone sits above it. A table regression then
        /// shows up as a number in the log, not only as a failed assertion.
        /// </summary>
        void LogBed(SeatEntry seat)
        {
            var sb = new StringBuilder("[bed] hips=");
            sb.Append(HipsWorld.y.ToString("F3")).Append(" top=").Append(seat.topY.ToString("F3"));
            var legs = new[]
            {
                HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
                HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
            };
            foreach (var bone in legs)
            {
                var t = Rig != null ? Rig.Bone(bone) : null;
                if (t == null) continue;
                sb.Append(' ').Append(bone).Append('=').Append(t.position.y.ToString("F3"));
            }
            Debug.Log(sb.ToString(), this);
        }

        /// <summary>Back to standing at <paramref name="feet"/>, facing <paramref name="facingY"/>.</summary>
        public void Stand(Vector3 feet, float facingY)
        {
            if (Rig == null) Rig = GetComponent<AvatarRig>();
            if (Rig != null) AvatarPose.Standing(Rig);
            Root().SetPositionAndRotation(feet, Quaternion.Euler(0f, facingY, 0f));
            Seated = false;
        }

        Transform Root()
        {
            if (Rig == null) return transform;
            return Rig.Root != null ? Rig.Root : Rig.transform;
        }

        bool EnsureRig()
        {
            if (Rig == null) Rig = GetComponent<AvatarRig>();
            if (Rig == null) Debug.LogError("[seat] no AvatarRig on " + name, this);
            return Rig != null;
        }
    }
}