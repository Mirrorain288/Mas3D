using Mas3D.Avatar;
using UnityEngine;

namespace Mas3D.Actions
{
    /// <summary>
    /// Task 4 of docs/superpowers/plans/2026-11-04-unity-actions-p4.md: the contact points the
    /// actions solve against. Every value is read off her live skeleton through
    /// <see cref="AvatarRig.Bone(HumanBodyBones)"/>, so the offsets live in exactly one place and the
    /// tests and the solver measure the same point.
    /// </summary>
    public static class ActionGeometry
    {
        /// <summary>How far in front of the hand bone the palm sits (hand-calibrated, see the plan).</summary>
        public const float PalmForward = 0.08f;

        /// <summary>How far above the head bone the centre of her head sits.</summary>
        public const float HeadCenterLift = 0.10f;

        /// <summary>How far above the head bone the top of her head sits (hand-calibrated).</summary>
        public const float HeadTopLift = 0.20f;

        /// <summary>Her palm in world space; <see cref="Vector3.zero"/> when the hand bone is missing.</summary>
        public static Vector3 HerPalm(AvatarRig rig, bool left)
        {
            var hand = Bone(rig, left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            return hand == null ? Vector3.zero : hand.position + hand.forward * PalmForward;
        }

        /// <summary>The centre of her head in world space; zero when the head bone is missing.</summary>
        public static Vector3 HerHeadCenter(AvatarRig rig)
        {
            var head = Bone(rig, HumanBodyBones.Head);
            return head == null ? Vector3.zero : head.position + head.up * HeadCenterLift;
        }

        /// <summary>The top of her head in world space; zero when the head bone is missing.</summary>
        public static Vector3 HerHeadTop(AvatarRig rig)
        {
            var head = Bone(rig, HumanBodyBones.Head);
            return head == null ? Vector3.zero : head.position + head.up * HeadTopLift;
        }

        /// <summary>Where her face points; zero when the head bone is missing.</summary>
        public static Vector3 HerFacing(AvatarRig rig)
        {
            var head = Bone(rig, HumanBodyBones.Head);
            return head == null ? Vector3.zero : head.forward;
        }

        /// <summary>
        /// Degrees between where she faces and where <paramref name="toward"/> is, measured from her
        /// head centre. A missing head bone is 180 (she cannot face anything) and a point inside her
        /// head is 0 (there is no direction to turn to).
        /// </summary>
        public static float FaceAlign(AvatarRig rig, Vector3 toward)
        {
            var head = Bone(rig, HumanBodyBones.Head);
            if (head == null) return 180f;

            var to = toward - HerHeadCenter(rig);
            if (to.sqrMagnitude <= 1e-6f) return 0f;
            return Vector3.Angle(HerFacing(rig), to);
        }

        static Transform Bone(AvatarRig rig, HumanBodyBones bone)
        {
            return rig != null ? rig.Bone(bone) : null;
        }
    }
}
