using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Mas3D.Editor
{
    /// <summary>
    /// Turns on screen space ambient occlusion and points it at the depth buffer.
    ///
    /// The room cannot use realtime shadows: in this editor build the URP shadow path corrupts
    /// texture sampling, which is what painted the bed atlas across every wall. SSAO only
    /// multiplies ambient light, and our surface shader reads it directly, so it restores the
    /// contact darkening under furniture without touching the maps.
    /// </summary>
    public static class RoomAocclusion
    {
        public const string PipelinePath = "Assets/Settings/Mas3D-URP.asset";

        public static bool Configure(float intensity = 0.6f, float radius = 0.15f, float falloff = 150f)
        {
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            if (pipeline == null)
            {
                Debug.LogError("[ssao] pipeline asset missing at " + PipelinePath);
                return false;
            }

            var pipelineSo = new SerializedObject(pipeline);
            var depth = pipelineSo.FindProperty("m_RequireDepthTexture");
            if (depth != null && !depth.boolValue)
            {
                depth.boolValue = true;
                pipelineSo.ApplyModifiedProperties();
                Debug.Log("[ssao] depth texture enabled");
            }

            var rendererList = pipelineSo.FindProperty("m_RendererDataList");
            var data = rendererList.GetArrayElementAtIndex(0).objectReferenceValue as ScriptableRendererData;
            if (data == null)
            {
                Debug.LogError("[ssao] renderer data missing");
                return false;
            }

            var feature = data.rendererFeatures.OfType<ScreenSpaceAmbientOcclusion>().FirstOrDefault();
            if (feature == null)
            {
                feature = ScriptableObject.CreateInstance<ScreenSpaceAmbientOcclusion>();
                feature.name = "SSAO";
                AssetDatabase.AddObjectToAsset(feature, data);
                data.rendererFeatures.Add(feature);
                EditorUtility.SetDirty(data);
            }

            feature.SetActive(true);

            var so = new SerializedObject(feature);
            SetEnumByName(so, "m_Settings.Source", "Depth", "Normal");
            SetEnumByName(so, "m_Settings.AOMethod", "Intersection", null);
            SetEnumByName(so, "m_Settings.Samples", "High", null);
            SetEnumByName(so, "m_Settings.BlurQuality", "High", null);
            SetFloat(so, "m_Settings.Intensity", intensity);
            SetFloat(so, "m_Settings.Radius", radius);
            SetFloat(so, "m_Settings.Falloff", falloff);
            SetFloat(so, "m_Settings.DirectLightingStrength", 0f);
            SetBool(so, "m_Settings.Downsample", false);
            SetBool(so, "m_Settings.AfterOpaque", true);
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(feature);
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();

            Debug.Log($"[ssao] configured source={so.FindProperty("m_Settings.Source").enumValueIndex} intensity={intensity} radius={radius} falloff={falloff}");
            return true;
        }

        static void SetFloat(SerializedObject so, string path, float value)
        {
            var p = so.FindProperty(path);
            if (p == null) { Debug.Log("[ssao] no field " + path); return; }
            p.floatValue = value;
        }

        static void SetBool(SerializedObject so, string path, bool value)
        {
            var p = so.FindProperty(path);
            if (p == null) { Debug.Log("[ssao] no field " + path); return; }
            p.boolValue = value;
        }

        /// <summary>Picks an enum entry by label. Names differ between URP versions, so the
        /// choice is made from the labels the editor itself reports.</summary>
        static void SetEnumByName(SerializedObject so, string path, string want, string reject)
        {
            var p = so.FindProperty(path);
            if (p == null) { Debug.Log("[ssao] no field " + path); return; }
            var labels = p.enumDisplayNames;
            Debug.Log("[ssao] " + path + " -> " + string.Join("|", labels));
            for (var i = 0; i < labels.Length; i++)
            {
                var label = labels[i];
                if (label.IndexOf(want, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (reject != null && label.IndexOf(reject, System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
                p.enumValueIndex = i;
                return;
            }
        }
    }
}
