using System.Collections;
using Mas3D.Avatar;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mas3D.Tests
{
    /// <summary>
    /// 参考图（docs/superpowers/specs/assets/2026-11-06-reference.png）的脸是两段色阶：
    /// 受光面亮粉、鼻侧与下颌一段暖棕。我们的 VRM 原本 shift=1 / toony=1，线性台阶上下界
    /// 重合，恒返回 1，于是整张脸只有 litColor —— 夜景里发白、发灰。
    /// </summary>
    public class AvatarShadingTests
    {
        [UnityTest]
        public IEnumerator HerShadeBandIsNoLongerSwallowedByTheLitColor()
        {
            SceneFlow.Require();
            try
            {
                yield return SceneFlow.LoadMain();

                AvatarActor actor = null;
                var deadline = Time.realtimeSinceStartup + 10f;
                while (actor == null && Time.realtimeSinceStartup < deadline)
                {
                    actor = Object.FindAnyObjectByType<AvatarActor>();
                    yield return null;
                }

                Assert.IsNotNull(actor, "主场景里必须有 AvatarActor");

                // 资产原件：Apply 之后这些实例一个都不该再挂在渲染器上。
                var originals = new System.Collections.Generic.HashSet<Material>();
                foreach (var renderer in actor.GetComponentsInChildren<Renderer>(true))
                    foreach (var material in renderer.sharedMaterials)
                        if (material != null) originals.Add(material);

                var touched = AvatarShading.Apply(actor.gameObject);
                Assert.Greater(touched, 0, "她身上一件 MToon 材质都没找到，这条用例什么也没证明");

                var checkedMaterials = 0;
                foreach (var renderer in actor.GetComponentsInChildren<Renderer>(true))
                {
                    foreach (var material in renderer.sharedMaterials)
                    {
                        if (material == null || !material.HasProperty("_ShadingShiftFactor")) continue;
                        checkedMaterials++;
                        Assert.AreEqual(AvatarShading.Shift, material.GetFloat("_ShadingShiftFactor"), 1e-4f,
                            material.name + " 的 shift 没写进去：shade 段仍然被吞掉");
                        Assert.AreEqual(AvatarShading.Toony, material.GetFloat("_ShadingToonyFactor"), 1e-4f,
                            material.name + " 的 toony 没写进去：台阶上下界仍然重合");
                        Assert.IsFalse(material.IsKeywordEnabled(AvatarShading.ParameterMapKeyword),
                            material.name + " 还在采样空的 shift 贴图，会把负数 shift 抵消回 1");
                        Assert.IsFalse(originals.Contains(material),
                            "材质必须是场景副本，不能原地改 VRM 资产（重新导入会被覆盖）");
                    }
                }

                Assert.Greater(checkedMaterials, 0, "没有可断言的 MToon 材质");
                Assert.GreaterOrEqual(touched, checkedMaterials, "Apply 报的改写数少于实际材质数");
            }
            finally
            {
                SceneFlow.UnloadMainIfLoaded();
            }
        }
    }
}
