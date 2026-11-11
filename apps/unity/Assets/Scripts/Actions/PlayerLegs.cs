using System.Collections.Generic;
using Mas3D.Avatar;
using UnityEngine;

namespace Mas3D.Actions
{
    /// <summary>
    /// Task 3 of the action-phases plan: the player's own legs. The tables in
    /// <see cref="ActionPoses"/> fold the legs; this class owns the snapshot they fold from and the
    /// one number the body needs to keep the soles on the floor - how far the fold raised them.
    ///
    /// It is the same contract as <see cref="ActionPose"/> (snapshot once at entry, rebuild from the
    /// base every frame so nothing compounds, hand the base back verbatim) with one difference that
    /// is not cosmetic: the snapshot is keyed by rig. <see cref="ActionPose"/> keeps one static bone
    /// map because only her body is ever posed by it; a player body can be destroyed and spawned
    /// again between actions, and two bodies can be alive at once (the player and a baked clone), so
    /// a single shared map would hand one rig's base to the other.
    /// </summary>
    public static class PlayerLegs
    {
        /// <summary>Every bone a leg table may write, both legs, hips down to the sole.</summary>
        public static readonly HumanBodyBones[] Bones =
        {
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
            HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
        };

        private sealed class Snapshot
        {
            public readonly Dictionary<HumanBodyBones, Quaternion> Base =
                new Dictionary<HumanBodyBones, Quaternion>(Bones.Length);

            /// <summary>Foot height above the avatar root when the snapshot was taken, in metres.</summary>
            public readonly float[] FootHeight = new float[2];

            /// <summary>Whether each foot existed at entry, so a missing foot never reads as height zero.</summary>
            public readonly bool[] FootSeen = new bool[2];

            /// <summary>Metres the soles rose while the table was applied, 0 when nothing moved.</summary>
            public float Lift;
        }

        private static readonly Dictionary<AvatarRig, Snapshot> Rigs = new Dictionary<AvatarRig, Snapshot>(2);

        /// <summary>True while at least one rig has a snapshot.</summary>
        public static bool Active => Rigs.Count > 0;

        /// <summary>
        /// Records the leg bones of <paramref name="rig"/> as they are now. Called once at entry,
        /// after the player is in place and before the first pose frame. A second Begin while the
        /// first snapshot is live is ignored: re-snapshotting a folded leg would make it the base and
        /// the fold would compound frame after frame.
        /// </summary>
        public static void Begin(AvatarRig rig)
        {
            if (rig == null || Rigs.ContainsKey(rig)) return;

            var snapshot = new Snapshot();
            for (var i = 0; i < Bones.Length; i++)
            {
                var bone = rig.Bone(Bones[i]);
                if (bone == null) continue;
                snapshot.Base[Bones[i]] = bone.localRotation;
            }

            CaptureFoot(rig, snapshot, HumanBodyBones.LeftFoot, 0);
            CaptureFoot(rig, snapshot, HumanBodyBones.RightFoot, 1);
            Rigs[rig] = snapshot;
        }

        /// <summary>
        /// Applies the table for <paramref name="posture"/> at <paramref name="weight"/>, always from
        /// the entry snapshot: 0 is the leg as it was, 1 is the full fold, and a leave eases between
        /// them without ever compounding. A posture with no leg table (standing) gets the snapshot
        /// back, so the legs are straight whenever the plan does not bend them.
        /// </summary>
        public static void Apply(AvatarRig rig, PlayerPosture posture, float weight)
        {
            if (rig == null) return;
            if (!Rigs.TryGetValue(rig, out var snapshot)) return;

            var table = Table(posture);
            var w = Mathf.Clamp01(weight);
            Write(rig, snapshot, table, w);
            snapshot.Lift = MeasureLift(rig, snapshot);
        }

        /// <summary>
        /// Hands every bone back to its entry rotation, verbatim, and drops the snapshot. Safe to call
        /// for a rig that was never begun or that has already been restored.
        /// </summary>
        public static void Restore(AvatarRig rig)
        {
            if (rig == null) return;
            if (!Rigs.TryGetValue(rig, out var snapshot)) return;

            foreach (var pair in snapshot.Base)
            {
                var bone = rig.Bone(pair.Key);
                if (bone == null) continue;
                bone.localRotation = pair.Value;
            }

            Rigs.Remove(rig);
        }

        /// <summary>Drops every snapshot without touching a bone; for a rig that is already gone.</summary>
        public static void Forget()
        {
            Rigs.Clear();
        }

        /// <summary>
        /// Metres the soles rose above where the snapshot found them, 0 when no legs are applied.
        /// Nothing consumes it today: the body is placed from the eye, and the leg tables are tuned so
        /// a full fold lifts the ankles by exactly the stance's eye drop, which is what puts the soles
        /// on the floor. It stays as the measurement that tuning is checked against - it is the wrong
        /// term to subtract (it would double-count the fold), the right term to compare with.
        /// </summary>
        public static float FootLift(AvatarRig rig)
        {
            if (rig == null) return 0f;
            return Rigs.TryGetValue(rig, out var snapshot) ? snapshot.Lift : 0f;
        }

        /// <summary>
        /// Degrees between the thigh and the shin, 0 for a straight leg. Measured from the bones so a
        /// test reads the pose that is actually on screen, not the table it came from.
        /// </summary>
        public static float KneeAngle(AvatarRig rig, bool left)
        {
            if (rig == null) return 0f;

            var hip = rig.Bone(left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg);
            var knee = rig.Bone(left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg);
            var ankle = rig.Bone(left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot);
            if (hip == null || knee == null || ankle == null) return 0f;

            var thigh = knee.position - hip.position;
            var shin = ankle.position - knee.position;
            if (thigh.sqrMagnitude < 1e-8f || shin.sqrMagnitude < 1e-8f) return 0f;
            return Vector3.Angle(thigh, shin);
        }

        /// <summary>The table the posture folds, or null when it stands on straight legs.</summary>
        public static IReadOnlyDictionary<HumanBodyBones, Vector3> Table(PlayerPosture posture)
        {
            switch (posture)
            {
                case PlayerPosture.Crouching: return ActionPoses.CrouchLegs;
                case PlayerPosture.Kneeling: return ActionPoses.KneelLegs;
                case PlayerPosture.Sitting: return ActionPoses.SitLegs;
                default: return null;
            }
        }

        private static void Write(
            AvatarRig rig, Snapshot snapshot, IReadOnlyDictionary<HumanBodyBones, Vector3> table, float weight)
        {
            foreach (var pair in snapshot.Base)
            {
                var bone = rig.Bone(pair.Key);
                if (bone == null) continue;

                Quaternion target = pair.Value;
                if (table != null && table.TryGetValue(pair.Key, out var angles))
                    target = pair.Value * Quaternion.Euler(angles);

                bone.localRotation = weight >= 1f ? target : Quaternion.Slerp(pair.Value, target, weight);
            }
        }

        private static void CaptureFoot(AvatarRig rig, Snapshot snapshot, HumanBodyBones bone, int index)
        {
            var foot = rig.Bone(bone);
            if (foot == null) return;
            snapshot.FootSeen[index] = true;
            snapshot.FootHeight[index] = foot.position.y - rig.transform.position.y;
        }

        private static float MeasureLift(AvatarRig rig, Snapshot snapshot)
        {
            var root = rig.transform.position.y;
            var lift = 0f;

            var left = rig.Bone(HumanBodyBones.LeftFoot);
            if (left != null && snapshot.FootSeen[0])
                lift = Mathf.Max(lift, left.position.y - root - snapshot.FootHeight[0]);

            var right = rig.Bone(HumanBodyBones.RightFoot);
            if (right != null && snapshot.FootSeen[1])
                lift = Mathf.Max(lift, right.position.y - root - snapshot.FootHeight[1]);

            return Mathf.Max(0f, lift);
        }
    }
}
