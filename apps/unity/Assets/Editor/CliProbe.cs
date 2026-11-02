using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Mas3D.Editor
{
    /// <summary>
    /// Prints which URP post-processing and renderer-feature types this editor actually ships,
    /// so a polish pass can reference them by name instead of guessing.
    /// </summary>
    public static class CliProbe
    {
        public static void Probe()
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Unity.RenderPipelines.Universal.Runtime");
            Debug.Log("[probe] urp assembly=" + (asm == null ? "missing" : asm.GetName().Version.ToString()));
            if (asm == null) { EditorApplication.Exit(1); return; }

            foreach (var kw in new[] { "Ambient", "WhiteBalance", "LiftGammaGain", "Bloom", "Depth", "Tonemapping" })
            {
                var hits = asm.GetTypes()
                    .Where(t => t.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0 && t.IsPublic)
                    .Select(t => t.FullName)
                    .Take(12)
                    .ToArray();
                Debug.Log("[probe] " + kw + " -> " + (hits.Length == 0 ? "none" : string.Join(", ", hits)));
            }

            var ssao = asm.GetTypes().FirstOrDefault(t => t.Name == "ScreenSpaceAmbientOcclusion");
            if (ssao != null)
            {
                Debug.Log("[probe] ssao=" + ssao.FullName + " base=" + ssao.BaseType.Name);
                var settings = ssao.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(t => t.Name == "Settings");
                if (settings != null)
                {
                    foreach (var f in settings.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
                    {
                        var kind = f.FieldType.IsEnum
                            ? "enum(" + string.Join("|", Enum.GetNames(f.FieldType)) + ")"
                            : f.FieldType.Name;
                        Debug.Log("[probe] ssao.settings " + f.Name + " : " + kind);
                    }
                }
            }
            EditorApplication.Exit(0);
        }
    }
}
