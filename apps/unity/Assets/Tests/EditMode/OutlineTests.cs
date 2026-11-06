using Mas3D.Rendering;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using VRM10.MToon10;

namespace Mas3D.Tests.EditMode
{
    /// <summary>
    /// 描边两件事必须同时成立，少一件画面都是平的：
    /// ① Renderer 资产里挂着 MToonOutline 特性（VRM 包自带的那条，6000 上走 RenderObjectsPass，
    ///    Render Graph 下也能画；并且 SSAO 的 feature 必须**不存在**，见下面那条断言）；
    /// ② 材质上 _OutlineWidthMode=1 且宽度分档（脸/眼/眉/嘴关，裙袜鞋细一档，身体一档）。
    /// </summary>
    public class OutlineTests
    {
        const string RendererPath = "Assets/Settings/Mas3D-URP-Renderer.asset";
        const string AvatarPath = "Assets/_Local/avatars/Monkia.vrm";

        [Test]
        public void TheRendererCarriesAnOutlineFeature()
        {
            var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            Assert.IsNotNull(data, "读不到 " + RendererPath);

            var features = data.rendererFeatures;
            var outline = features.Find(f => f is MToonOutlineRenderFeature);
            Assert.IsNotNull(outline, "Renderer 资产里没有 MToonOutlineRenderFeature");
            Assert.IsTrue(outline.isActive, "描边特性存在但没启用");
            Assert.AreEqual("MToonOutline", outline.name, "特性名要能被出图脚本按名字找到");

            // SSAO 必须**从资产里彻底消失**，不能只是 m_Active: 0：
            // ① 观感：2026-11-06 的 FaceDiag2 逐项对比证明她脸上那道灰带就是 SSAO 画的
            //   （关掉后同机位脸部 p05 从 10 升到 26、p95 从 196 升到 224，粉腮才回来），
            //   而房间几乎看不出差别（床脚交界 p05/p25/median 三档完全一致）。
            // ② 打包：feature 只要还在列表里（哪怕 isActive == false），玩家构建里 URP 仍会构造它；
            //   它的 shader 又因为“未启用”被剥离，于是 TryPrepareResources() 抛 NullReferenceException，
            //   管线每帧重建失败 —— 表现就是 exe 黑屏 / 像打不开（2026-11-06 23:0x 实测 play.log）。
            //   要恢复接触阴影：重新挂 feature 时必须在 Player 设置里保留它的 shader（或至少实机验一次打包件）。
            var ssao = features.Find(f => f is ScreenSpaceAmbientOcclusion);
            Assert.IsNull(ssao, "SSAO 的 feature 不许留在资产里：打包版会因 shader 被剥离而在管线创建时抛 NRE");
        }

        [Test]
        public void HerBodyIsOutlinedButHerFaceIsNot()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPath);
            if (prefab == null) Assert.Ignore("没有 " + AvatarPath + "：干净的 clone 不带这个 17 MB 模型");

            var instance = Object.Instantiate(prefab);
            try
            {
                var touched = AvatarOutlines.Apply(instance, false);
                Assert.Greater(touched, 0, "一件 MToon 材质都没找到");

                var bodies = 0;
                var faces = 0;
                foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
                {
                    foreach (var material in renderer.sharedMaterials)
                    {
                        if (material == null || !material.HasProperty("_OutlineWidth")) continue;

                        var width = material.GetFloat("_OutlineWidth");
                        var mode = material.GetFloat("_OutlineWidthMode");
                        var isFace = AvatarOutlines.WidthFor(material.name) == 0f;
                        if (isFace)
                        {
                            faces++;
                            Assert.AreEqual(0f, width, 1e-6f, material.name + " 是脸/眼/眉/嘴，不许有描边");
                            Assert.AreEqual(0f, mode, 1e-6f, material.name + " 的描边模式该关成 0");
                        }
                        else
                        {
                            bodies++;
                            Assert.Greater(width, 0f, material.name + " 是身体部件，宽度不能是 0");
                            Assert.AreEqual(1f, mode, 1e-6f, material.name + " 的描边要用世界单位（mode 1）");
                        }
                    }
                }

                Assert.Greater(bodies, 0, "没有任何身体部件被描边");
                Assert.Greater(faces, 0, "一个脸部件都没识别出来：分档规则失效了");
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        const string ShaderAsset = "Packages/com.vrmc.vrm/MToon10/Shaders/vrmc_materials_mtoon_urp.shader";

        [Test]
        public void TheOutlinePassIsTheShaderPassMtoonShips()
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderAsset);
            if (shader == null) shader = Shader.Find("VRM10/Universal Render Pipeline/MToon10");
            Assert.IsNotNull(shader, "找不到 MToon10 的 URP shader（" + ShaderAsset + "）");

            // 特性按 LightMode 标签挑 pass（RenderObjectsPass 的 shaderTags = {"MToonOutline"}）：
            // shader 里没有这个 pass，特性就会静默画不出东西。
            //
            // 这里不能只信 Shader.Find + passCount：批处理里 shader 还没编译完时，Shader.Find 会
            // 交回 passCount=1 的占位对象（实测就是这么红的）。所以三路取一：
            // ① 加载到的资产能查到 LightMode=MToonOutline；② 材质 FindPass 认得这个 pass 名；
            // ③ 兜底读源文件，确认这个 pass 真的写在资产里。
            var found = false;
            for (var i = 0; i < shader.passCount; i++)
            {
                if (shader.FindPassTagValue(i, new ShaderTagId("LightMode")).name == "MToonOutline")
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                var material = new Material(shader);
                found = material.FindPass("MToonOutline") >= 0;
                Object.DestroyImmediate(material);
            }

            if (!found)
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(ShaderAsset);
                if (info != null)
                {
                    var onDisk = System.IO.Path.Combine(info.resolvedPath, "MToon10/Shaders/vrmc_materials_mtoon_urp.shader");
                    found = System.IO.File.Exists(onDisk) &&
                            System.IO.File.ReadAllText(onDisk).Contains("\"LightMode\" = \"MToonOutline\"");
                }
            }

            Assert.IsTrue(found, "MToon10 的 URP shader 里没有 MToonOutline 这个 pass（passCount=" + shader.passCount + "）");
        }
    }
}
