using UnityEngine;

namespace Mas3D
{
    /// <summary>
    /// Proxy points for a player with no skeleton: the body is one capsule and one head transform,
    /// so the palms and the crown of the head are derived from the head and the body's own axes.
    /// Actions and tests both read these points instead of each deriving their own.
    /// </summary>
    public static class PlayerReach
    {
        // 0.62 m of standing room puts her seat centre 0.62 m behind his chest; at PalmForward 0.35 the
        // palms landed 0.27 m from that centre, i.e. right on her belly, and her forearms closed into a
        // clasp that read as "her own hands folded", not as being held. Pulling the marker back to 0.24
        // and widening it to 0.22 moves the contact 0.38 m out and 0.44 m apart, so both arms reach
        // forward and open. PalmDrop stays 0.62: PlayerPostureTests pins the palm to eye - 0.62.
        const float PalmForward = 0.24f;
        const float PalmSideways = 0.22f;
        const float PalmDrop = 0.62f;

        const float RaisedForward = 0.30f;
        const float RaisedSideways = 0.12f;
        const float RaisedDrop = 0.32f;

        const float CrownLift = 0.16f;

        /// <summary>Where the player's hand hangs: ahead of the eye, to one side, below it.</summary>
        public static Vector3 Palm(PlayerMotor player, bool left) =>
            FromHead(player, PalmForward, left ? PalmSideways : -PalmSideways, -PalmDrop);

        /// <summary>Where the player's hand reaches when it is lifted in front of the chest.</summary>
        public static Vector3 Raised(PlayerMotor player, bool left) =>
            FromHead(player, RaisedForward, left ? RaisedSideways : -RaisedSideways, -RaisedDrop);

        /// <summary>The top of the player's head, for a hand that has to find it.</summary>
        public static Vector3 Crown(PlayerMotor player)
        {
            var head = HeadOf(player);
            return head == null ? Vector3.zero : head.position + Vector3.up * CrownLift;
        }

        const float FaceForward = 0.10f;
        const float FaceDrop = 0.04f;

        /// <summary>
        /// Where the player's face is for a kiss: a hand's width ahead of the eye and 4 cm below it, so
        /// two heads that meet touch nose to nose instead of colliding at the brow.
        /// </summary>
        public static Vector3 Face(PlayerMotor player)
        {
            var head = HeadOf(player);
            if (head == null) return Vector3.zero;
            var body = player.transform;
            return head.position + body.forward * FaceForward - body.up * FaceDrop;
        }

        /// <summary>The direction the player's body faces, independent of where the camera looks.</summary>
        public static Vector3 Facing(PlayerMotor player) =>
            player == null ? Vector3.zero : player.transform.forward;

        static Vector3 FromHead(PlayerMotor player, float forward, float sideways, float up)
        {
            var head = HeadOf(player);
            if (head == null) return Vector3.zero;
            var body = player.transform;
            return head.position + body.forward * forward + body.right * sideways + Vector3.up * up;
        }

        static Transform HeadOf(PlayerMotor player) => player == null ? null : player.Head;
    }
}
