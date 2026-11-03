using UnityEngine;

namespace Mas3D
{
    /// <summary>
    /// The room shaders grade themselves (ACES curve + exposure + saturation) because a URP
    /// volume profile never reaches the play camera the same way it reaches the editor captures.
    /// Those knobs are shader globals, so they reset to zero for every new process: a build that
    /// never calls this renders the whole interior black while unlit sky cards stay visible.
    /// The values mirror RoomDress.Tuning so the editor captures and the running build match.
    /// </summary>
    public static class RoomLook
    {
        public const float PostExposure = -1.2f;
        public const float Saturation = 60f;

        /// <summary>
        /// The room shaders grade themselves, the VRM avatar does not: MToon knows nothing about
        /// _RoomExposure, so at equal light it renders about 1/0.435 = 2.3x brighter than the
        /// surfaces around it and a front-lit view clips into blocks of pure yellow and magenta.
        /// Every light and the ambient probe are therefore scaled by this factor and the room
        /// exposure is divided by it, which leaves the room's own brightness untouched
        /// (albedo * light * k * exposure / k) while the avatar drops to the room's level.
        /// </summary>
        public const float LightScale = 0.5f;

        /// <summary>Light scale applied to the scene lights and to the ambient probe.</summary>
        public static float Scale(float intensity) => intensity * LightScale;

        /// <summary>Same scale for the trilight ambient probe, which also feeds the avatar.</summary>
        public static Color Scale(Color ambient) => ambient * LightScale;

        /// <summary>Room-shader gain that keeps the room looking the same after <see cref="LightScale"/>.</summary>
        public static float Exposure(float postExposure) => Mathf.Pow(2f, postExposure) / LightScale;

        public static void Apply(float postExposure = PostExposure, float saturation = Saturation)
        {
            Shader.SetGlobalFloat("_RoomExposure", Exposure(postExposure));
            Shader.SetGlobalFloat("_RoomSaturation", Mathf.Clamp01(1f + saturation / 100f));
        }
    }
}
