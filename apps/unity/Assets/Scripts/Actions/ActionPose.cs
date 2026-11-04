using System.Collections.Generic;
using Mas3D.Avatar;
using UnityEngine;

namespace Mas3D.Actions
{
    /// <summary>
    /// Task 4 of docs/superpowers/plans/2026-11-04-unity-actions-p4.md: the snapshot an action pose is
    /// layered on. <see cref="AvatarIdle"/> owns Chest/Spine and rewrites them every frame, so the base
    /// is whatever her bones look like when the action starts — not AvatarPose.Capture, which is the
    /// standing rest pose — and every frame is rebuilt from that base instead of compounding the
    /// previous frame's offsets (the mistake AvatarPose already paid for).
    /// </summary>
    public static class ActionPose
    {
        /// <summary>Every bone an action is allowed to write; only these are snapshotted.</summary>
        static readonly HumanBodyBones[] Writable =
        {
            HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
            HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
            HumanBodyBones.Neck, HumanBodyBones.Head,
            HumanBodyBones.Hips, HumanBodyBones.Chest, HumanBodyBones.Spine,
        };

        static readonly Dictionary<HumanBodyBones, Quaternion> Base = new Dictionary<HumanBodyBones, Quaternion>();
        static readonly HashSet<HumanBodyBones> Taken = new HashSet<HumanBodyBones>();

        /// <summary>
        /// Bones the running table writes this frame. <see cref="AvatarIdle"/> asks before it breathes:
        /// it owns Chest/Spine, and an action that leans her forward cannot survive a breath that
        /// rebuilds those two bones from its own baseline every frame. The claim is per applied key,
        /// so the hands table (arms only) leaves the breath completely alone.
        /// </summary>
        static readonly HashSet<HumanBodyBones> Claimed = new HashSet<HumanBodyBones>();

        static ActionPose()
        {
            AvatarIdle.BoneClaim = Claims;
        }

        /// <summary>True while the current table writes <paramref name="bone"/>.</summary>
        public static bool Claims(HumanBodyBones bone) => Claimed.Contains(bone);

        /// <summary>True once an action holds a snapshot and has not handed it back yet.</summary>
        public static bool Active => Taken.Count > 0;

        /// <summary>
        /// Takes the local rotation of every writable bone that exists and has not been recorded yet.
        /// Recording a bone twice would overwrite the pose the action started from, so the first
        /// snapshot of a performance wins until <see cref="Restore"/> clears it.
        /// </summary>
        public static void Begin(AvatarRig rig)
        {
            if (rig == null) return;
            Claimed.Clear();
            foreach (var bone in Writable)
            {
                if (Taken.Contains(bone)) continue;
                var transform = rig.Bone(bone);
                if (transform == null) continue;
                Base[bone] = transform.localRotation;
                Taken.Add(bone);
            }
        }

        /// <summary>
        /// Writes the table on top of the entry snapshot: every angle is an offset from the base, never
        /// from the previous frame. Bones the table does not mention are left to their own owner.
        /// </summary>
        public static void Apply(AvatarRig rig, IReadOnlyDictionary<HumanBodyBones, Vector3> angles)
        {
            if (rig == null || angles == null) return;
            foreach (var pair in angles)
            {
                if (!Base.TryGetValue(pair.Key, out var basis)) continue;
                var transform = rig.Bone(pair.Key);
                if (transform == null) continue;
                transform.localRotation = basis * Quaternion.Euler(pair.Value);
                Claimed.Add(pair.Key);
            }
        }

        /// <summary>
        /// Writes the snapshot back verbatim and forgets it. Idempotent: with no snapshot this does
        /// nothing, so an idle Cancel can never push a stale pose onto her.
        /// </summary>
        public static void Restore(AvatarRig rig)
        {
            if (rig != null)
            {
                foreach (var pair in Base)
                {
                    var transform = rig.Bone(pair.Key);
                    if (transform != null) transform.localRotation = pair.Value;
                }
            }

            Base.Clear();
            Taken.Clear();
            Claimed.Clear();
        }

        /// <summary>
        /// Forgets the snapshot without touching a single bone. The only caller is the teardown path:
        /// when the actor is already destroyed there is nothing left to restore, but a snapshot kept
        /// here would leak across performers — the next <see cref="Begin"/> would see every bone as
        /// already taken, skip it, and pose the new avatar against a corpse's rotations.
        /// </summary>
        public static void Forget()
        {
            Base.Clear();
            Taken.Clear();
            Claimed.Clear();
        }
    }

    /// <summary>
    /// Task 4: pose tables, each value an Euler offset from the entry snapshot (see
    /// <see cref="ActionPose"/>). They only aim the limbs at the contact; the exact contact point is
    /// solved by <see cref="TwoBoneIk"/>.
    /// </summary>
    public static class ActionPoses
    {
        /// <summary>
        /// Hands: one elbow seed and nothing else. <see cref="TwoBoneIk"/> rewrites both bones' aim
        /// after this table is applied, so an upper-arm row here would be dead weight that reads like a
        /// pose - the 70-degree lift it used to carry never survived the solve. What does survive is the
        /// lower-arm twist, which picks the elbow plane the solve rotates about.
        /// </summary>
        public static readonly IReadOnlyDictionary<HumanBodyBones, Vector3> Hands =
            new Dictionary<HumanBodyBones, Vector3>
            {
                { HumanBodyBones.LeftLowerArm, new Vector3(0f, 65f, 0f) },
                { HumanBodyBones.RightLowerArm, new Vector3(0f, -65f, 0f) },
            };

        /// <summary>
        /// Pat-her: she bows her head into the palm the player raises over her. The plan's first
        /// table (Spine 3 / Neck 10) leaves her head top 0.2923 m from that palm, and the one knob it
        /// authorised, Neck 8-20, only reaches 0.2491 m: at the 0.62 m marker the raised hand also
        /// sits 0.122 m to her left, and no amount of pitch removes a sideways offset. Measured on
        /// the real rig, the head joint bottoms out 0.2157 m from the palm at Spine 30, so the
        /// torso has to carry the bow; with these angles it sits 0.2163 m out, and the head aim in
        /// DrivePatHer turns the remaining |d - 0.20| = 1.6 cm into contact. Head stays in the table
        /// at 6 degrees: it is the pose if the aim cannot run, and the test pins the head bone.
        /// The arms are deliberately absent: an earlier table lifted the right upper arm 46 degrees and
        /// put her forearm next to her own ear, which in the capture read as her covering her head
        /// instead of being patted. She keeps the seated baseline's hands-on-knees arms.
        /// </summary>
        public static readonly IReadOnlyDictionary<HumanBodyBones, Vector3> PatHer =
            new Dictionary<HumanBodyBones, Vector3>
            {
                { HumanBodyBones.Spine, new Vector3(24f, 0f, 0f) },
                { HumanBodyBones.Chest, new Vector3(12f, 0f, 0f) },
                { HumanBodyBones.Neck, new Vector3(16f, 0f, 0f) },
                { HumanBodyBones.Head, new Vector3(6f, 0f, 0f) },
            };

        /// <summary>
        /// Kiss: she bows her whole torso onto the kneeling player's face. The four spine keys add up
        /// to 54 degrees of forward pitch, inside the plan's 30-60 range; the bow is deliberately
        /// weighted toward Neck 20 / Head 14 rather than a deep waist fold, because a person lowers
        /// her head to be kissed. Nothing here chases the contact: the marker distance
        /// (<see cref="ActionDirector.KissOffset"/>) places the player, and the gap and the face
        /// alignment are then measured, not posed away.
        /// </summary>
        public static readonly IReadOnlyDictionary<HumanBodyBones, Vector3> Kiss =
            new Dictionary<HumanBodyBones, Vector3>
            {
                { HumanBodyBones.Spine, new Vector3(13f, 0f, 0f) },
                { HumanBodyBones.Chest, new Vector3(7f, 0f, 0f) },
                { HumanBodyBones.Neck, new Vector3(20f, 0f, 0f) },
                { HumanBodyBones.Head, new Vector3(14f, 0f, 0f) },
                { HumanBodyBones.LeftUpperArm, new Vector3(0f, 0f, 34f) },
                { HumanBodyBones.RightUpperArm, new Vector3(0f, 0f, -34f) },
            };

        /// <summary>
        /// Pat-me: her right arm comes up in front of her to find the crouched player's crown. The
        /// contact itself is left to <see cref="TwoBoneIk"/> — these angles only put the chain in the
        /// right quadrant before the solve, and the left arm is deliberately absent so it keeps its
        /// baseline (the free arm must not join in). Spine and Chest lean her whole torso in: at the
        /// 0.62 m marker an upright shoulder leaves that crown 0.5678 m out, past her 0.4708 m arm,
        /// and the lean brings it to 0.4383 m. Drop the two torso keys and pat-me stops reaching.
        /// </summary>
        public static readonly IReadOnlyDictionary<HumanBodyBones, Vector3> PatMe =
            new Dictionary<HumanBodyBones, Vector3>
            {
                { HumanBodyBones.Spine, new Vector3(20f, 0f, 0f) },
                { HumanBodyBones.Chest, new Vector3(10f, 0f, 0f) },
                { HumanBodyBones.RightUpperArm, new Vector3(0f, 0f, -64f) },
                { HumanBodyBones.RightLowerArm, new Vector3(0f, -24f, 0f) },
            };
    }
}
