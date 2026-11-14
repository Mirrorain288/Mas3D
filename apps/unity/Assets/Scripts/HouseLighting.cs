using UnityEngine;
using UnityEngine.Rendering;

namespace Mas3D
{
    /// <summary>
    /// Ambient for the multi-room flat. The lights and window cards are baked into House.prefab, but
    /// RenderSettings is scene state and cannot live in a prefab, so this component restores it when
    /// the house spawns. Values mirror the editor rig in Assets/Editor/HouseLook.cs.
    /// </summary>
    public sealed class HouseLighting : MonoBehaviour
    {
        public float Ambient = 1f;

        void Awake()
        {
            Configure(Ambient);
        }

        public static void Configure(float ambient)
        {
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.26f, 0.27f, 0.36f) * ambient;
            RenderSettings.ambientEquatorColor = new Color(0.24f, 0.20f, 0.24f) * ambient;
            RenderSettings.ambientGroundColor = new Color(0.12f, 0.10f, 0.14f) * ambient;
            RenderSettings.fog = false;
        }
    }
}
