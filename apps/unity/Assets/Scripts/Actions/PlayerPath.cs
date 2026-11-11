using UnityEngine;

namespace Mas3D.Actions
{
    /// <summary>
    /// The straight ground path the player walks during an approach. The entry eases along it with a
    /// smoothstep, so the feet start and stop without a step, and the facing turns on the same curve.
    /// A settle action never builds one: it places the player on the mark in a single frame.
    /// </summary>
    public struct PlayerPath
    {
        public Vector3 From;
        public Vector3 To;
        public float FromYaw;
        public float ToYaw;

        /// <summary>Ground distance between the two marks, in metres.</summary>
        public float Length;

        public static PlayerPath Between(Vector3 from, float fromYaw, Vector3 to, float toYaw)
        {
            var flat = new Vector2(to.x - from.x, to.z - from.z);
            return new PlayerPath
            {
                From = from,
                To = to,
                FromYaw = fromYaw,
                ToYaw = toYaw,
                Length = flat.magnitude,
            };
        }

        /// <summary>
        /// Position and facing at <paramref name="u"/> of the entry, 0..1. The smoothstep eases the
        /// walk out of the start and into the mark; the facing takes the shortest way round.
        /// </summary>
        public void Evaluate(float u, out Vector3 feet, out float yaw)
        {
            u = Mathf.Clamp01(u);
            var s = u * u * (3f - 2f * u);
            feet = Vector3.Lerp(From, To, s);
            yaw = Mathf.LerpAngle(FromYaw, ToYaw, s);
        }
    }
}
