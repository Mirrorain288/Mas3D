using System.Collections.Generic;
using Mas3D.Avatar;
using UnityEngine;

namespace Mas3D.Actions
{
    /// <summary>
    /// Task 4 of docs/superpowers/plans/2026-11-08-action-phases.md: the hold breathes. A pose that is
    /// solved once and then held still reads as a freeze-frame, so while an action holds, small
    /// offsets ride on her head, her spine and her hands, and on his spine.
    ///
    /// Every offset is a sine of the phase clock with a fixed amplitude and frequency - no random, no
    /// time-integrated state - which is what makes it bounded (the amplitude is the cap, forever),
    /// periodic (each function returns to its start on its own period) and frame-rate independent (the
    /// deflection is a function of elapsed time, not of how many frames elapsed in it).
    ///
    /// The offsets are written as <c>base * Euler(offset)</c> from a per-rig base captured on the first
    /// held frame, never from the previous frame's value. Layering them on the live rotation instead
    /// would compound wherever no pose table rebuilds the bone - her spine and head during hands, her
    /// head during pat-her, everything during sit-together, which applies no table at all - and a
    /// two-minute hold would walk the pose off the character. <see cref="ActionPose"/> and
    /// <see cref="AvatarIdle"/> already paid for the same lesson.
    ///
    /// Call order matters: the offsets go in after the pose table and before the IK, so the solve is
    /// still the frame's truth where a hand is glued to a marker.
    /// </summary>
    public static class PerformanceMotion
    {
        const float Tau = 2f * Mathf.PI;

        /// <summary>
        /// Half-swing of her spine about Z, in degrees. The design (spec §5.5) asks for a lateral
        /// lean, not a nod: a spine that pitches while the head pitches too reads as one stiff hinge.
        /// </summary>
        public const float HerSpineDegrees = 0.8f;

        /// <summary>Cycles per second of her spine sway: a 9.09 s breath, deliberately not a round number.</summary>
        public const float HerSpineHz = 0.11f;

        /// <summary>Half-swing of her head about X, in degrees.</summary>
        public const float HerHeadDegreesX = 1.2f;

        /// <summary>Cycles per second of the head's nod.</summary>
        public const float HerHeadHzX = 0.07f;

        /// <summary>Nod phase, so the head is not at its extreme when the action enters the hold.</summary>
        public const float HerHeadPhaseX = 0.9f;

        /// <summary>Half-swing of her head about Z, in degrees.</summary>
        public const float HerHeadDegreesZ = 0.6f;

        /// <summary>Cycles per second of the head's tilt: a second, different rhythm inside the nod.</summary>
        public const float HerHeadHzZ = 0.05f;

        /// <summary>Tilt phase.</summary>
        public const float HerHeadPhaseZ = 2.1f;

        /// <summary>How far her hands tighten, in degrees, at the peak of the breath.</summary>
        public const float HerHandDegrees = 2f;

        /// <summary>Seconds per tighten-and-relax of her hands.</summary>
        public const float HerHandPeriod = 3.7f;

        /// <summary>Half-swing of his spine about X, in degrees. Spec §5.5 says 0.6.</summary>
        public const float HisSpineDegrees = 0.6f;

        /// <summary>
        /// Cycles per second of his sway. His body has no idle breathing of its own, so the two rigs
        /// must not share a rhythm: at 0.16 Hz against her 0.11 Hz the pair drifts in and out of step
        /// every ~20 s instead of swaying as one block.
        /// </summary>
        public const float HisSpineHz = 0.16f;

        /// <summary>
        /// How much of the amplitude the kiss runs at. It is the one action whose contact is measured
        /// rather than solved - nothing pulls her head back after the offsets move it - and the pose
        /// starts 1.34 cm from his face against a 2 cm tolerance. Measured over the full 100 s period
        /// of the breath, full amplitude takes the gap to 2.14 cm and the hold stops crediting; at
        /// 0.6 the worst gap is 1.83 cm, so the faces still read as touching while every frame of the
        /// hold stays inside tolerance. The kiss test pins both ends of that window (the gap must stay
        /// inside tolerance *and* must keep moving): a scale of 0 would pass a cap-only assertion.
        /// </summary>
        public const float KissScale = 0.6f;

        sealed class Snapshot
        {
            public Transform Spine;
            public Transform Head;
            public Transform LeftHand;
            public Transform RightHand;
            public Quaternion SpineBase;
            public Quaternion HeadBase;
            public Quaternion LeftHandBase;
            public Quaternion RightHandBase;
        }

        static readonly Dictionary<AvatarRig, Snapshot> Her = new Dictionary<AvatarRig, Snapshot>();
        static readonly Dictionary<AvatarRig, Quaternion> His = new Dictionary<AvatarRig, Quaternion>();

        /// <summary>Her head's offset at <paramref name="t"/> seconds of phase time, in degrees.</summary>
        public static Vector3 HerHead(float t)
        {
            var x = HerHeadDegreesX * Mathf.Sin(Tau * HerHeadHzX * t + HerHeadPhaseX);
            var z = HerHeadDegreesZ * Mathf.Sin(Tau * HerHeadHzZ * t + HerHeadPhaseZ);
            return new Vector3(x, 0f, z);
        }

        /// <summary>Her spine's offset: one slow lateral lean, no nod, no twist.</summary>
        public static Vector3 HerSpine(float t)
        {
            return new Vector3(0f, 0f, HerSpineDegrees * Mathf.Sin(Tau * HerSpineHz * t));
        }

        /// <summary>
        /// One hand's offset. Squared and clamped at zero so the hand only ever tightens from the pose:
        /// a symmetric swing would open the fingers past the pose the table set, which reads as a grab.
        /// The two hands mirror each other, so the pair closes together instead of both twisting one way.
        /// </summary>
        public static Vector3 HerHand(bool left, float t)
        {
            var open = Mathf.Sin(Tau * t / HerHandPeriod);
            var tighten = HerHandDegrees * Mathf.Max(0f, open) * Mathf.Max(0f, open);
            return new Vector3(0f, 0f, left ? tighten : -tighten);
        }

        /// <summary>
        /// One hand's roll at <paramref name="t" /> seconds, in degrees, already scaled for the action.
        /// The arm solve post-multiplies this about the wrist's own forward axis, so it turns the palm
        /// without moving it: the contact the IK bought is untouched.
        /// </summary>
        public static float HandRoll(bool left, string actionId, float t)
        {
            return HerHand(left, t).z * Scale(actionId);
        }

        /// <summary>His spine's offset: slower and shallower than hers, on his own rhythm.</summary>
        public static Vector3 HisSpine(float t)
        {
            return new Vector3(HisSpineDegrees * Mathf.Sin(Tau * HisSpineHz * t), 0f, 0f);
        }

        /// <summary>How much of the amplitude the action <paramref name="actionId"/> runs at.</summary>
        static float Scale(string actionId)
        {
            return actionId == ActionId.Kiss ? KissScale : 1f;
        }

        /// <summary>
        /// Writes her breathing offsets for the frame. The base every offset is stacked on is captured
        /// once, on the first held frame - after the pose table and before the IK - and is never read
        /// again while the action holds.
        /// </summary>
        public static void Apply(AvatarRig rig, string actionId, float t)
        {
            if (rig == null) return;

            Snapshot snapshot;
            if (!Her.TryGetValue(rig, out snapshot)) snapshot = Capture(rig);

            var scale = Scale(actionId);

            // No pose table writes her spine during hands or sit-together, so the claim is what stops
            // AvatarIdle from rebuilding it underneath us on its next Update.
            if (snapshot.Spine != null)
            {
                ActionPose.Claim(HumanBodyBones.Spine);
                snapshot.Spine.localRotation = snapshot.SpineBase * Quaternion.Euler(HerSpine(t) * scale);
            }

            if (snapshot.Head != null)
                snapshot.Head.localRotation = snapshot.HeadBase * Quaternion.Euler(HerHead(t) * scale);

            if (snapshot.LeftHand != null)
                snapshot.LeftHand.localRotation = snapshot.LeftHandBase * Quaternion.Euler(HerHand(true, t) * scale);

            if (snapshot.RightHand != null)
                snapshot.RightHand.localRotation = snapshot.RightHandBase * Quaternion.Euler(HerHand(false, t) * scale);
        }

        /// <summary>
        /// Writes his spine's offset for the frame. His body carries no idle of its own and nothing else
        /// in a performance touches that bone, so one snapshot per rig is the whole contract.
        /// </summary>
        public static void ApplyHis(AvatarRig rig, float t)
        {
            if (rig == null) return;

            var spine = rig.Bone(HumanBodyBones.Spine);
            if (spine == null) return;

            Quaternion basis;
            if (!His.TryGetValue(rig, out basis))
            {
                basis = spine.localRotation;
                His[rig] = basis;
            }

            spine.localRotation = basis * Quaternion.Euler(HisSpine(t));
        }

        /// <summary>
        /// Hands his spine back and forgets every snapshot. Her bones are deliberately not written:
        /// <see cref="ActionPose.Restore"/> owns them, and a second writer on the way out would fight it.
        /// </summary>
        public static void Restore(AvatarRig his)
        {
            Quaternion basis;
            if (his != null && His.TryGetValue(his, out basis))
            {
                var spine = his.Bone(HumanBodyBones.Spine);
                if (spine != null) spine.localRotation = basis;
            }

            Forget();
        }

        /// <summary>
        /// Drops every snapshot without touching a bone. The teardown path needs this: once the actor is
        /// being destroyed there is nothing to write back, but a snapshot kept here would leak onto the
        /// next performer and stack its offsets on a corpse's rotations.
        /// </summary>
        public static void Forget()
        {
            Her.Clear();
            His.Clear();
        }

        /// <summary>Snapshots her four bones as they stand after this frame's pose table.</summary>
        static Snapshot Capture(AvatarRig rig)
        {
            var snapshot = new Snapshot
            {
                Spine = rig.Bone(HumanBodyBones.Spine),
                Head = rig.Bone(HumanBodyBones.Head),
                LeftHand = rig.Bone(HumanBodyBones.LeftHand),
                RightHand = rig.Bone(HumanBodyBones.RightHand),
            };

            if (snapshot.Spine != null) snapshot.SpineBase = snapshot.Spine.localRotation;
            if (snapshot.Head != null) snapshot.HeadBase = snapshot.Head.localRotation;
            if (snapshot.LeftHand != null) snapshot.LeftHandBase = snapshot.LeftHand.localRotation;
            if (snapshot.RightHand != null) snapshot.RightHandBase = snapshot.RightHand.localRotation;

            Her[rig] = snapshot;
            return snapshot;
        }
    }
}
