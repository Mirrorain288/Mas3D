using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Mas3D.Avatar
{
    /// <summary>
    /// 虹膜换图：VRM 自带的虹膜贴图是荧光黄绿（hue 184.9、饱和度 0.976），在夜景里读起来像
    /// 两粒 LED。参考立绘量出来的是低饱和青绿（hue 166.7、饱和度 0.473），所以这里换成一张
    /// 离线重画过的贴图（node tools/eyes-recolor.mjs 生成，放进 Assets/_Local/avatars/eyes）。
    ///
    /// 换图同样落在**场景副本**上，理由和 AvatarShading 一样：VRM 资产是导入产物，改它会在
    /// 下次重新导入时丢掉；场景副本才会被 CliPlayer.BuildScene 烘进 Main.unity。
    ///
    /// 另外两处会让白高光消失：_Color 是乘算（调暗就整体压）、_ShadeColor 是暗部色（太黑会把
    /// 背光那半个虹膜压成墨绿）；参数贴图关键字打开时 shift 纹理多数是空的，会把明暗界抹平。
    /// 三样都在这份副本上摆正，look-compare 的「虹膜高光 ≥ 0.90」才有得可量。
    /// </summary>
    public static class AvatarEyes
    {
        /// <summary>重画后的虹膜贴图（1024x512，与 VRM 内嵌的那张同尺寸，UV 直接复用）。</summary>
        public const string IrisTexturePath = "Assets/_Local/avatars/eyes/iris.png";

        /// <summary>副本名的后缀：第二次调用靠「_MainTex 已经是这张图」认出自己，后缀是给人看的佐证。</summary>
        public const string CopySuffix = " (eyes)";

        /// <summary>名字里带这个的材质就是虹膜（VRM 里叫 N00_000_00_EyeIris_00_EYE）。</summary>
        public const string IrisMaterialHint = "EyeIris";

        /// <summary>描边材质（AvatarOutlines 从原材质克隆）名字里带这个，不能被当成虹膜。</summary>
        public const string OutlineHint = "Outline";

        public const string ParameterMapKeyword = "_MTOON_PARAMETERMAP";

        /// <summary>_Color 的最小通道下限：低于它就乘掉高光。</summary>
        public const float MinLit = 0.98f;

        /// <summary>_ShadeColor 的最小通道下限：低于它暗部会把高光吃掉。</summary>
        public const float MinShade = 0.55f;

        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int ShadeColorId = Shader.PropertyToID("_ShadeColor");
        // MToon10 的暗部贴图叫 _ShadeTex，MToon 0.x 叫 _ShadeTexture，两种都写。
        // 用字符串表派生属性 id，测试才能断言「暗部贴图也换了」而不必另抄一份常量。
        // 对外只读：调用方不该能改这张表（改了属性 id 表不会跟着变）。
        static readonly string[] ShadeNames = { "_ShadeTex", "_ShadeTexture" };
        public static readonly IReadOnlyList<string> ShadeTextureNames = Array.AsReadOnly(ShadeNames);
        static readonly int[] ShadeTexIds = Array.ConvertAll(ShadeNames, Shader.PropertyToID);

        public static Texture2D LoadIris()
        {
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(IrisTexturePath);
#else
            // 打包版不需要按路径找图：BuildScene 已经把副本连同贴图引用烘进场景。
            return null;
#endif
        }

        /// <summary>
        /// 名字里带 EyeIris 的才是虹膜；带 Outline 的是描边生成物（AvatarOutlines 从虹膜克隆出来的
        /// 那份），换贴图会把描边也染成虹膜图，所以排除掉。
        /// </summary>
        public static bool IsIrisMaterial(Material material)
        {
            if (material == null || string.IsNullOrEmpty(material.name)) return false;
            if (material.name.IndexOf(OutlineHint, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return material.name.IndexOf(IrisMaterialHint, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Returns how many materials were rewritten (0 when the avatar has no iris material).</summary>
        public static int Apply(GameObject avatar, Texture2D iris)
        {
            if (avatar == null || iris == null) return 0;

            var touched = 0;
            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                var changed = false;
                for (var i = 0; i < materials.Length; i++)
                {
                    var source = materials[i];
                    if (!IsIrisMaterial(source)) continue;

                    // 幂等：这份副本已经是这张图了，不再套一层。
                    if (source.mainTexture == iris) continue;

                    var copy = UnityEngine.Object.Instantiate(source);
                    copy.name = source.name + CopySuffix;
                    copy.mainTexture = iris;
                    // MToon 的暗部走 _ShadeTexture：只换 _MainTex 的话，虹膜落在阴影里时画面上仍是
                    // 原来那张荧光黄绿（实测第一次换成这样，量出来饱和度 0.966 一点没动）。
                    foreach (var shadeTexId in ShadeTexIds)
                        if (copy.HasProperty(shadeTexId) && copy.GetTexture(shadeTexId) != null)
                            copy.SetTexture(shadeTexId, iris);
                    Brighten(copy);
                    materials[i] = copy;
                    touched++;
                    changed = true;
                }

                if (changed) renderer.sharedMaterials = materials;
            }

            return touched;
        }

        static void Brighten(Material material)
        {
            if (material.HasProperty(ColorId))
            {
                var colour = material.GetColor(ColorId);
                colour = new Color(
                    Mathf.Max(colour.r, MinLit),
                    Mathf.Max(colour.g, MinLit),
                    Mathf.Max(colour.b, MinLit),
                    colour.a);
                material.SetColor(ColorId, colour);
            }

            if (material.HasProperty(ShadeColorId))
            {
                // 暗部给一点冷调（青绿），不然背光那半个虹膜会变成一块灰。
                var shade = material.GetColor(ShadeColorId);
                shade = new Color(
                    Mathf.Max(shade.r, MinShade * 0.92f),
                    Mathf.Max(shade.g, MinShade),
                    Mathf.Max(shade.b, MinShade * 0.97f),
                    shade.a);
                material.SetColor(ShadeColorId, shade);
            }

            material.DisableKeyword(ParameterMapKeyword);
        }
    }
}
