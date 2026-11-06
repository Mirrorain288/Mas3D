using UnityEngine;

namespace Mas3D.Rendering
{
    /// <summary>
    /// MToon 描边分档。MToon10 的 _OutlineWidthMode 默认是 0（关），只写 _OutlineWidth 等于
    /// 什么都没做；模式 1 才是"世界单位"，宽度按米算，换分辨率不变形。
    ///
    /// 参考图（MiSide）的做法是：身体一圈深色细线把角色从背景里拔出来，脸上没有线——脸上有线
    /// 会立刻变成"贴纸"。所以脸/眼/眉/嘴一律关，裙袜鞋细一档，其余身体一档。
    /// </summary>
    public static class AvatarOutlines
    {
        // 世界单位、按米算。换算：1080p、fov 42、2.5 m 距离下 1 m ≈ 563 px，
        // 所以 0.0028 m ≈ 1.58 px（判据 ≥1.5 px），0.0026 m ≈ 1.46 px（布料细一档但不能消失）。
        // 早先的 0.0022 在这个机位只有 1.24 px，画面里等于没有线。
        public const float BodyWidth = 0.0040f;
        public const float ClothWidth = 0.0032f;

        static readonly int WidthId = Shader.PropertyToID("_OutlineWidth");
        static readonly int ModeId = Shader.PropertyToID("_OutlineWidthMode");

        static readonly string[] FaceHints =
            { "face", "eye", "brow", "mouth", "lash", "teeth", "tongue", "cheek", "耳", "眉" };

        static readonly string[] ClothHints =
            { "skirt", "sock", "shoe", "boot", "ribbon", "tie", "裙", "袜", "鞋" };

        /// <summary>Width the material should carry; 0 when this part must not be outlined.</summary>
        public static float WidthFor(string materialName)
        {
            var name = (materialName ?? string.Empty).ToLowerInvariant();
            foreach (var hint in FaceHints)
                if (name.Contains(hint)) return 0f;
            foreach (var hint in ClothHints)
                if (name.Contains(hint)) return ClothWidth;
            return BodyWidth;
        }

        /// <summary>Returns how many materials were rewritten.</summary>
        public static int Apply(GameObject avatar, bool verbose = true)
        {
            if (avatar == null) return 0;

            var touched = 0;
            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                var changed = false;
                for (var i = 0; i < materials.Length; i++)
                {
                    var material = materials[i];
                    if (material == null || !material.HasProperty(WidthId)) continue;

                    var width = WidthFor(material.name);
                    material.SetFloat(WidthId, width);
                    // Mode 1 = world units. Zero width always means "no outline", so the mode is
                    // moot there, but it is set to 0 as well: a stray nonzero width later would
                    // otherwise silently switch the face back on.
                    if (material.HasProperty(ModeId)) material.SetFloat(ModeId, width > 0f ? 1f : 0f);
                    if (verbose)
                        Debug.Log(string.Format("[outline] {0} width={1:F4} mode={2}", material.name, width, width > 0f ? 1 : 0));
                    touched++;
                    changed = true;
                }

                if (changed) renderer.sharedMaterials = materials;
            }

            return touched;
        }

        /// <summary>True when the material already carries a matching width/mode pair (used by tests).</summary>
        public static bool IsOutlinedAs(Material material)
        {
            if (material == null || !material.HasProperty(WidthId)) return false;
            var width = WidthFor(material.name);
            if (!Mathf.Approximately(material.GetFloat(WidthId), width)) return false;
            return !material.HasProperty(ModeId) || Mathf.Approximately(material.GetFloat(ModeId), width > 0f ? 1f : 0f);
        }
    }
}
