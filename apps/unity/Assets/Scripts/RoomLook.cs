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

        public static void Apply(float postExposure = PostExposure, float saturation = Saturation)
        {
            Shader.SetGlobalFloat("_RoomExposure", Mathf.Pow(2f, postExposure));
            Shader.SetGlobalFloat("_RoomSaturation", Mathf.Clamp01(1f + saturation / 100f));
        }
    }
}
