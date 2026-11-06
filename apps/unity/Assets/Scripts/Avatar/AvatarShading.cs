using UnityEngine;

namespace Mas3D.Avatar
{
    /// <summary>
    /// MToon 明暗分界：VRM 自带的 _ShadingShiftFactor=1 / _ShadingToonyFactor=1 会让
    /// mtoon_linearstep(-1+1, 1-1, x) 的上下界重合，返回恒 1 —— 整张脸永远走 litColor，
    /// shade 段根本不存在，于是她在夜景里像一张塑料贴纸（参考图 MiSide 脸上有明显两段色阶）。
    ///
    /// 这里把参数写进**场景里自己的材质副本**，不改 VRM 资产：资产是导入产物，改了会在
    /// 下次重新导入时丢掉；场景副本才会被 CliPlayer.BuildScene 烘进 Main.unity，打包版也带着。
    /// </summary>
    public static class AvatarShading
    {
        public const float Shift = -0.15f;
        public const float Toony = 0.9f;

        /// <summary>VRM 的 _MTOON_PARAMETERMAP 打开时要采样 shift 贴图；这张贴图多数材质是空的，
        /// 空了就恒取 1，等于把上面的负数 shift 抵消掉。关掉关键字，纯参数生效。</summary>
        public const string ParameterMapKeyword = "_MTOON_PARAMETERMAP";

        static readonly int ShiftId = Shader.PropertyToID("_ShadingShiftFactor");
        static readonly int ToonyId = Shader.PropertyToID("_ShadingToonyFactor");

        /// <summary>Returns how many materials were rewritten (0 when the avatar has no MToon).</summary>
        public static int Apply(GameObject avatar)
        {
            if (avatar == null) return 0;

            var touched = 0;
            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                var changed = false;
                for (var i = 0; i < materials.Length; i++)
                {
                    var source = materials[i];
                    if (source == null || !source.HasProperty(ShiftId)) continue;

                    // Own copy: the source is an asset inside the imported VRM prefab.
                    var copy = Object.Instantiate(source);
                    copy.name = source.name + " (room)";
                    copy.SetFloat(ShiftId, Shift);
                    if (copy.HasProperty(ToonyId)) copy.SetFloat(ToonyId, Toony);
                    copy.DisableKeyword(ParameterMapKeyword);
                    materials[i] = copy;
                    touched++;
                    changed = true;
                }

                if (changed) renderer.sharedMaterials = materials;
            }

            return touched;
        }

        /// <summary>True when the material already carries the shipped values (used by tests).</summary>
        public static bool IsShaded(Material material)
        {
            return material != null
                   && material.HasProperty(ShiftId)
                   && Mathf.Approximately(material.GetFloat(ShiftId), Shift)
                   && (!material.HasProperty(ToonyId) || Mathf.Approximately(material.GetFloat(ToonyId), Toony))
                   && !material.IsKeywordEnabled(ParameterMapKeyword);
        }
    }
}
