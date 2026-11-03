using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace Mas3D.Avatar
{
    /// <summary>
    /// The sitting pose as data: one table of local-euler offsets away from the rest pose.
    /// Constants instead of an AnimationClip because the avatar stays code-driven (no controller,
    /// no Playable graph) and the numbers are tuned against the avatarpose probe, not authored
    /// in a timeline. Every call starts from the captured rest pose, so calling Sitting twice
    /// never stacks, and <see cref="Standing"/> is the exact inverse of <see cref="Sitting"/>.
    /// </summary>
    public static class AvatarPose
    {
        /// <summary>
        /// Offset from the rest pose in each bone's own local space, for the bones that differ
        /// from standing. Bones absent from the table keep their rest rotation.
        /// </summary>
        static readonly (HumanBodyBones bone, Vector3 euler)[] Table =
        {
            (HumanBodyBones.Hips, new Vector3(-6f, 0f, 0f)),
            (HumanBodyBones.Spine, new Vector3(4f, 0f, 0f)),
            (HumanBodyBones.Chest, new Vector3(2f, 0f, 0f)),
            (HumanBodyBones.LeftUpperLeg, new Vector3(-78f, 0f, 4f)),
            (HumanBodyBones.RightUpperLeg, new Vector3(-78f, 0f, -4f)),
            (HumanBodyBones.LeftLowerLeg, new Vector3(76f, 0f, 0f)),
            (HumanBodyBones.RightLowerLeg, new Vector3(76f, 0f, 0f)),
            (HumanBodyBones.LeftFoot, new Vector3(6f, 0f, 0f)),
            (HumanBodyBones.RightFoot, new Vector3(6f, 0f, 0f)),
            (HumanBodyBones.LeftUpperArm, new Vector3(0f, 0f, 70f)),
            (HumanBodyBones.RightUpperArm, new Vector3(0f, 0f, -70f)),
            (HumanBodyBones.LeftLowerArm, new Vector3(0f, 65f, 0f)),
            (HumanBodyBones.RightLowerArm, new Vector3(0f, -65f, 0f)),
        };

        // Keyed by the rig object itself, not by an instance id: ids get reused after Destroy, and a
        // stale table would make Standing restore another avatar's pose. The weak key lets a
        // destroyed rig's table go away with it.
        static readonly ConditionalWeakTable<AvatarRig, Dictionary<HumanBodyBones, Quaternion>> Rest =
            new ConditionalWeakTable<AvatarRig, Dictionary<HumanBodyBones, Quaternion>>();

        /// <summary>
        /// The rig's rest pose: every humanoid bone that exists, in local space.
        /// Captured once per rig and returned from then on, so a caller that captures after
        /// <see cref="Sitting"/> (AvatarIdle's Awake, for one) still gets the standing baseline
        /// and never the pose somebody else applied.
        /// </summary>
        public static IReadOnlyDictionary<HumanBodyBones, Quaternion> Capture(AvatarRig rig)
        {
            if (rig == null) throw new System.ArgumentNullException(nameof(rig));
            return Rest.GetValue(rig, CaptureRest);
        }

        static Dictionary<HumanBodyBones, Quaternion> CaptureRest(AvatarRig rig)
        {
            var rest = new Dictionary<HumanBodyBones, Quaternion>();
            for (var bone = HumanBodyBones.Hips; bone < HumanBodyBones.LastBone; bone++)
            {
                var t = rig.Bone(bone);
                if (t != null) rest[bone] = t.localRotation;
            }
            return rest;
        }

        /// <summary>Apply the sitting table on top of the rest pose.</summary>
        public static void Sitting(AvatarRig rig)
        {
            var rest = Capture(rig);
            foreach (var (bone, euler) in Table)
            {
                var t = rig.Bone(bone);
                if (t == null) continue;
                if (!rest.TryGetValue(bone, out var baseline)) continue;
                t.localRotation = baseline * Quaternion.Euler(euler);
            }
        }

        /// <summary>Put every captured bone back on its rest rotation.</summary>
        public static void Standing(AvatarRig rig)
        {
            var rest = Capture(rig);
            foreach (var pair in rest)
            {
                var t = rig.Bone(pair.Key);
                if (t != null) t.localRotation = pair.Value;
            }
        }

        /// <summary>The table as one log line, for the probe and for reports.</summary>
        public static string Describe()
        {
            var sb = new StringBuilder();
            foreach (var (bone, euler) in Table)
            {
                if (sb.Length > 0) sb.Append("  ");
                sb.Append(bone).Append('(').Append(euler.x.ToString("0.#")).Append(',')
                  .Append(euler.y.ToString("0.#")).Append(',').Append(euler.z.ToString("0.#")).Append(')');
            }
            return sb.ToString();
        }
    }
}
