using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Mas3D.Tests
{
    /// <summary>
    /// 渲染管线归位（设计文档 G1）：颜色空间、相机后处理、Volume 本体、shader 不再自带调色。
    /// 这些是"少一条就静默回退"的开关，光看图看不出来是配置错了还是灯光没调好，所以钉成用例。
    /// </summary>
    public class RoomLookTests
    {
        [Test]
        public void TheProjectRendersInLinear()
        {
            Assert.AreEqual(ColorSpace.Linear, PlayerSettings.colorSpace,
                "项目必须是 Linear：Gamma 下 URP 的 Volume 后处理与 MToon 的线性值都不成立");
        }

        [Test]
        public void TheMainCameraRunsPostProcessing()
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                "Assets/Scenes/Main.unity", UnityEditor.SceneManagement.OpenSceneMode.Single);
            try
            {
                Assert.IsTrue(scene.IsValid(), "Main.unity 必须能打开");

                var camera = Object.FindAnyObjectByType<Camera>();
                Assert.IsNotNull(camera, "Main.unity 里必须有相机");

                var data = camera.GetComponent<UniversalAdditionalCameraData>();
                Assert.IsNotNull(data, "相机缺少 UniversalAdditionalCameraData");
                Assert.IsTrue(data.renderPostProcessing, "相机的 renderPostProcessing 必须打开，否则 ACES 与调色整段不生效");
            }
            finally
            {
                UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                    UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
            }
        }

        // 2026-11-06 踩到的坑：渲染器资产是代码 CreateInstance 出来的，postProcessData 一直是 null。
        // URP 遇到 null 会**静默**跳过整条后处理链——相机 renderPostProcessing=true、Volume 栈里
        // 也能读到 −0.55 的曝光，日志和用例都看不出问题，实际画面完全没过 ACES。
        // 那次调色扫参全部白做（曝光从 −0.2 调到 −3.0，像素一模一样）。所以这条必须钉死。
        [Test]
        public void TheRendererIsWiredToPostProcessData()
        {
            var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/Mas3D-URP-Renderer.asset");
            Assert.IsNotNull(data, "找不到渲染器资产");

            var property = new SerializedObject(data).FindProperty("postProcessData");
            Assert.IsNotNull(property, "渲染器资产里没有 postProcessData 字段（URP 版本变了？）");
            Assert.IsNotNull(property.objectReferenceValue,
                "postProcessData 为空时 URP 静默跳过全部后处理：ACES / 曝光 / 白平衡 / Bloom / 暗角一个都不生效");
        }

        [Test]
        public void RoomLookBuildsOneGlobalVolumeWithAces()
        {
            RoomLook.Apply(0.35f, 42f, 12f, 0.2f, 0.1f);

            var volumes = Object.FindObjectsByType<Volume>(FindObjectsSortMode.None);
            Volume look = null;
            foreach (var volume in volumes)
            {
                if (volume.isGlobal && volume.sharedProfile != null && volume.sharedProfile.name == "Mas3D-Look") look = volume;
            }

            Assert.IsNotNull(look, "RoomLook.Apply 之后场景里必须有一个全局 Volume");
            Assert.IsNotNull(look.sharedProfile, "全局 Volume 必须带 profile");

            ColorAdjustments colours;
            Assert.IsTrue(look.sharedProfile.TryGet(out colours), "profile 里必须有 ColorAdjustments");
            Assert.AreEqual(0.35f, colours.postExposure.value, 1e-4f, "曝光要按调用参数写进去");
            Assert.AreEqual(42f, colours.saturation.value, 1e-4f, "饱和度要按调用参数写进去");

            Tonemapping tonemap;
            Assert.IsTrue(look.sharedProfile.TryGet(out tonemap), "profile 里必须有 Tonemapping");
            Assert.AreEqual(TonemappingMode.ACES, tonemap.mode.value, "必须是 ACES，跟参考图同一条曲线");

            // 幂等：再调一次不应该多出一个 Volume。
            RoomLook.Apply();
            var again = 0;
            foreach (var volume in Object.FindObjectsByType<Volume>(FindObjectsSortMode.None))
            {
                if (volume.isGlobal && volume.sharedProfile != null && volume.sharedProfile.name == "Mas3D-Look") again++;
            }
            Assert.AreEqual(1, again, "重复 Apply 只能有一个 Mas3D-Look Volume");
        }

        [TestCase("Assets/Shaders/RoomSurface.shader")]
        [TestCase("Assets/Shaders/RoomMirror.shader")]
        public void TheRoomShadersNoLongerGradeThemselves(string path)
        {
            var source = File.ReadAllText(path);
            Assert.IsFalse(source.Contains("_RoomExposure"), path + " 不应再读 _RoomExposure：曝光与 ACES 归 Volume");
            Assert.IsFalse(source.Contains("_RoomSaturation"), path + " 不应再读 _RoomSaturation：饱和度归 Volume");
            Assert.IsFalse(source.Contains("2.51"), path + " 不应再有手写 ACES 曲线（常数 2.51 是它的指纹）");
        }
    }
}
