using UnityEngine;

namespace Mas3D
{
    /// <summary>
    /// Task 2 of docs/superpowers/plans/2026-11-04-unity-actions-p4.md: the analytic two-bone IK the
    /// action layer uses to put a hand (or a head) on a contact point. Rotations only — never position
    /// or scale — so a chain keeps its bone lengths to the millimetre and a target that is out of reach
    /// is refused instead of stretching the arm.
    /// </summary>
    public static class TwoBoneIk
    {
        /// <summary>
        /// Fraction of the summed bone lengths that still counts as reachable; the missing 0.1 %
        /// is 1 mm of headroom that keeps floating point from flipping sides at the boundary.
        /// </summary>
        public const float ReachTolerance = 0.999f;

        /// <summary>Below this, a length or a distance is treated as zero instead of divided by.</summary>
        const float Epsilon = 1e-6f;

        /// <summary>Sum of the two bone lengths; 0 when either bone (or the tip under it) is missing.</summary>
        public static float Reach(Transform upper, Transform lower)
        {
            return LengthsOf(upper, lower, out _, out var l1, out var l2) ? l1 + l2 : 0f;
        }

        /// <summary>Whether the target sits inside the chain's reach, dead zone included.</summary>
        public static bool CanReach(Transform upper, Transform lower, Vector3 target)
        {
            if (!LengthsOf(upper, lower, out _, out var l1, out var l2)) return false;
            if (l1 + l2 <= Epsilon) return false;

            return WithinReach(l1, l2, Vector3.Distance(upper.position, target));
        }

        /// <summary>
        /// The same judgement from a shoulder that is not at <c>upper.position</c> right now: an action
        /// that leans her forward moves the shoulder before the chain is solved, so asking from the
        /// upright pose would refuse a target the leaned pose can touch.
        /// </summary>
        public static bool CanReachFrom(Transform upper, Transform lower, Vector3 shoulder, Vector3 target)
        {
            if (!LengthsOf(upper, lower, out _, out var l1, out var l2)) return false;
            if (l1 + l2 <= Epsilon) return false;

            return WithinReach(l1, l2, Vector3.Distance(shoulder, target));
        }

        /// <summary>
        /// The one reachability judgement: a target <paramref name="d"/> away is solvable by a
        /// (l1, l2) chain only inside the arm — the outer bound keeps the 1 mm of headroom — and
        /// outside the inner dead zone. Below |l1 - l2| the tip can never get closer than that
        /// bound, so answering true there would promise an error the chain cannot deliver.
        /// CanReach and Solve both call this so the two can never drift apart.
        /// </summary>
        static bool WithinReach(float l1, float l2, float d)
        {
            return d <= (l1 + l2) * ReachTolerance
                && d >= Mathf.Abs(l1 - l2) * ReachTolerance;
        }

        /// <summary>
        /// Put the tip of <paramref name="lower"/> on <paramref name="target"/> with the elbow bending
        /// toward <paramref name="pole"/>. Returns false when the target is out of reach — further than
        /// the arm, or too close to the shoulder for a (l1, l2) chain to fold onto: the chain is then
        /// left straight along the target direction, both lengths untouched.
        /// Bones usually point along their own local axis, so the rotation is built from the direction
        /// the chain currently lies on rather than from a LookAt — that keeps parent rotation and
        /// scale out of the result.
        /// </summary>
        public static bool Solve(Transform upper, Transform lower, Vector3 target, Vector3 pole)
        {
            if (!LengthsOf(upper, lower, out var tip, out var l1, out var l2)) return false;
            if (l1 <= Epsilon || l2 <= Epsilon) return false;

            var shoulder = upper.position;
            var reach = l1 + l2;
            var toTarget = target - shoulder;
            var distance = toTarget.magnitude;
            var reachable = WithinReach(l1, l2, distance);

            // The upper bone as it stands now: rotating from this axis works whatever local axis the
            // rig uses, and leaves the bone's local scale alone.
            var boneDirection = lower.position - shoulder;
            if (boneDirection.sqrMagnitude <= Epsilon * Epsilon) return false;

            // A target on top of the shoulder carries no direction; keep the current one.
            var aim = distance > Epsilon ? toTarget / distance : boneDirection.normalized;

            // Law of cosines. Out of reach — past the arm, or inside the |l1 - l2| dead zone — means
            // straight, not stretched; the clamp only keeps the reachable branch's division finite.
            var solvedDistance = Mathf.Clamp(distance, Mathf.Abs(l1 - l2) + 1e-4f, reach * ReachTolerance);
            var shoulderAngle = reachable
                ? Mathf.Acos(Mathf.Clamp(
                    (l1 * l1 + solvedDistance * solvedDistance - l2 * l2) / (2f * l1 * solvedDistance),
                    -1f, 1f)) * Mathf.Rad2Deg
                : 0f;

            // Bend plane: the pole with its along-aim part removed. A pole parallel to the aim (or a
            // zero pole) still needs a stable plane, so fall back to a deterministic perpendicular.
            var side = pole - Vector3.Project(pole, aim);
            if (side.sqrMagnitude <= 1e-8f) side = Perpendicular(aim);

            var elbowDirection = Quaternion.AngleAxis(shoulderAngle, Vector3.Cross(aim, side.normalized)) * aim;
            upper.rotation = Quaternion.FromToRotation(boneDirection.normalized, elbowDirection) * upper.rotation;

            // From here on everything is world space: lower.position now sits exactly l1 along elbowDirection.
            var elbowToTarget = target - lower.position;
            var tipDirection = tip.position - lower.position;
            if (elbowToTarget.magnitude > Epsilon && tipDirection.sqrMagnitude > Epsilon * Epsilon)
            {
                lower.rotation = Quaternion.FromToRotation(tipDirection.normalized, elbowToTarget.normalized) * lower.rotation;
            }

            return reachable;
        }

        /// <summary>
        /// The chain's lengths and its tip, or false when either bone (or the tip under it) is missing.
        /// Upper arm, forearm and tip are read from the live transforms, so a caller never re-measures.
        /// </summary>
        static bool LengthsOf(Transform upper, Transform lower, out Transform tip, out float l1, out float l2)
        {
            tip = null;
            l1 = 0f;
            l2 = 0f;
            if (upper == null || lower == null) return false;
            tip = TipOf(lower);
            if (tip == null) return false;

            l1 = Vector3.Distance(upper.position, lower.position);
            l2 = Vector3.Distance(lower.position, tip.position);
            return true;
        }

        /// <summary>The chain's tip: the first child of the lower bone (the hand under the forearm).</summary>
        static Transform TipOf(Transform lower)
        {
            return lower != null && lower.childCount > 0 ? lower.GetChild(0) : null;
        }

        /// <summary>Any unit vector perpendicular to <paramref name="axis"/>; deterministic, never NaN.</summary>
        static Vector3 Perpendicular(Vector3 axis)
        {
            var seed = Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right;
            var side = Vector3.Cross(axis, seed);
            return side.sqrMagnitude > Epsilon * Epsilon ? side.normalized : Vector3.right;
        }
    }
}
