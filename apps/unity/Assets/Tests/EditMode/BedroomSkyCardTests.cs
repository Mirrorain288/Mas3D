using System;
using System.Collections.Generic;
using Mas3D.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Mas3D.Tests
{
    /// <summary>
    /// Verifies that the default bedroom night-sky-card is cropped to the -Z window aperture
    /// (mesh_219), stays strictly inside the -Z wall/eave silhouette, and renders single-sided
    /// facing into the bedroom (+Z) with CullMode.Back so exterior framing is never occluded.
    /// </summary>
    public class BedroomSkyCardTests
    {
        const string MainScenePath = "Assets/Scenes/Main.unity";
        const string WindowMeshName = "mesh_219";

        readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();
        Scene _openedMain;

        [TearDown]
        public void TearDown()
        {
            for (var i = _created.Count - 1; i >= 0; i--)
            {
                if (_created[i] != null) UnityEngine.Object.DestroyImmediate(_created[i]);
            }
            _created.Clear();

            if (_openedMain.IsValid() && _openedMain.isLoaded)
            {
                if (SceneManager.sceneCount > 1)
                {
                    EditorSceneManager.CloseScene(_openedMain, true);
                }
                else
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
            }
            _openedMain = default;
        }

        [Test]
        public void MainSceneHasSingleSidedWindowCroppedSkyCardBehindMinusZWindow()
        {
            _openedMain = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Additive);
            Assert.IsTrue(_openedMain.IsValid(), "无法打开 Main.unity");

            var boot = FindInScene<RoomBootstrap>(_openedMain);
            Assert.IsNotNull(boot, "Main.unity 缺少 RoomBootstrap");
            Assert.IsNotNull(boot.Room, "Main.unity 缺少默认卧室 Room 引用");

            var skyCard = FindSceneRoot(_openedMain, "night-sky-card");
            Assert.IsNotNull(skyCard, "Main.unity 场景根级应存在 night-sky-card");

            var windowObj = RoomDress.FindByName(boot.Room, WindowMeshName);
            Assert.IsNotNull(windowObj, "默认卧室缺少 -Z 窗洞网格 " + WindowMeshName);
            var windowRenderer = windowObj.GetComponent<Renderer>();
            Assert.IsNotNull(windowRenderer, WindowMeshName + " 缺少 Renderer");

            var roomBounds = RoomDress.RendererBounds(boot.Room);
            AssertWindowCroppedSingleSidedCard(skyCard, windowRenderer.bounds, roomBounds);
        }

        [Test]
        public void BuildSkyCardZProducesWindowCroppedSingleSidedQuadFacingIndoorPlusZ()
        {
            var syntheticRoomBounds = new Bounds(
                new Vector3(0f, 1.5425f, 0f),
                new Vector3(4.372f, 3.085f, 4.508f));

            var card = RoomDress.BuildSkyCardZ(syntheticRoomBounds, syntheticRoomBounds.min.z);
            Assert.IsNotNull(card, "BuildSkyCardZ 返回 null");
            _created.Add(card);
            var renderer = card.GetComponent<Renderer>();
            if (renderer != null && renderer.sharedMaterial != null) _created.Add(renderer.sharedMaterial);

            AssertBackCulledPlusZ(card);
            Assert.Less(card.transform.position.z, syntheticRoomBounds.min.z,
                "fallback 星空卡应落在 -Z 外墙外侧");
            Assert.LessOrEqual(syntheticRoomBounds.min.z - card.transform.position.z, 0.15f,
                "fallback 星空卡应紧贴 -Z 外墙放置（<= 0.15 m）");
            Assert.Less(card.transform.localScale.x, syntheticRoomBounds.size.x * 0.65f,
                "fallback 星空卡宽度必须显著小于整面墙宽");
            Assert.Less(card.transform.localScale.y, syntheticRoomBounds.size.y * 0.70f,
                "fallback 星空卡高度必须显著小于整间房高");
        }

        [Test]
        public void BuildWindowSkyCardAnchorsDirectlyToMesh219OnMovedRoom()
        {
            const string roomGlbPath = "Assets/_Local/room/mita-room.glb";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(roomGlbPath);
            Assert.IsNotNull(prefab, $"未找到卧室资源：{roomGlbPath}");

            var room = UnityEngine.Object.Instantiate(prefab);
            room.transform.position = new Vector3(3.5f, 0.2f, 1.5f);
            Physics.SyncTransforms();
            _created.Add(room);

            var windowObj = RoomDress.FindByName(room, RoomDress.ImportedWindowMesh);
            Assert.IsNotNull(windowObj, $"实例化卧室缺少 {RoomDress.ImportedWindowMesh}");
            var windowBounds = RoomDress.RendererBounds(windowObj);
            var roomBounds = RoomDress.RendererBounds(room);

            var card = RoomDress.BuildWindowSkyCard(room, roomBounds);
            Assert.IsNotNull(card, "BuildWindowSkyCard 返回 null");
            _created.Add(card);
            var renderer = card.GetComponent<Renderer>();
            if (renderer != null && renderer.sharedMaterial != null) _created.Add(renderer.sharedMaterial);

            AssertWindowCroppedSingleSidedCard(card, windowBounds, roomBounds);
        }

        static void AssertWindowCroppedSingleSidedCard(GameObject card, Bounds windowBounds, Bounds roomBounds)
        {
            AssertBackCulledPlusZ(card);

            var renderer = card.GetComponent<Renderer>();
            var cardBounds = renderer.bounds;

            Assert.Less(card.transform.position.z, windowBounds.min.z,
                "night-sky-card 必须位于 -Z 窗洞外背（z < window.min.z）");
            Assert.LessOrEqual(windowBounds.min.z - card.transform.position.z, 0.15f,
                "night-sky-card 应紧贴 -Z 外窗背放置（间距 <= 0.15 m），避免侧面斜视悬浮");

            Assert.AreEqual(windowBounds.center.x, cardBounds.center.x, 0.15f,
                "night-sky-card X 中心应与窗洞中心对齐");
            Assert.AreEqual(windowBounds.center.y, cardBounds.center.y, 0.15f,
                "night-sky-card Y 中心应与窗洞中心对齐");

            Assert.LessOrEqual(cardBounds.min.x, windowBounds.min.x + 1e-3f,
                "night-sky-card 左边界应覆盖窗洞左沿");
            Assert.GreaterOrEqual(cardBounds.max.x, windowBounds.max.x - 1e-3f,
                "night-sky-card 右边界应覆盖窗洞右沿");
            Assert.LessOrEqual(cardBounds.min.y, windowBounds.min.y + 1e-3f,
                "night-sky-card 下边界应覆盖窗洞下沿");
            Assert.GreaterOrEqual(cardBounds.max.y, windowBounds.max.y - 1e-3f,
                "night-sky-card 上边界应覆盖窗洞上沿");

            Assert.Greater(cardBounds.min.x, roomBounds.min.x + 0.5f,
                "night-sky-card 左边不得延伸至房间侧墙外缘");
            Assert.Less(cardBounds.max.x, roomBounds.max.x - 0.5f,
                "night-sky-card 右边不得延伸至房间侧墙外缘");
            Assert.GreaterOrEqual(cardBounds.min.y, roomBounds.min.y + 0.05f,
                "night-sky-card 下沿不得探出地板下方");
            Assert.LessOrEqual(cardBounds.max.y, roomBounds.max.y - 0.5f,
                "night-sky-card 上沿不得超出檐口/屋脊外廓");
            Assert.Less(cardBounds.size.x, roomBounds.size.x * 0.65f,
                "night-sky-card 不应再按整墙宽度生成");
            Assert.Less(cardBounds.size.y, roomBounds.size.y * 0.70f,
                "night-sky-card 不应再按整屋高度生成");
        }

        static void AssertBackCulledPlusZ(GameObject card)
        {
            var renderer = card.GetComponent<Renderer>();
            Assert.IsNotNull(renderer, "night-sky-card 缺少 Renderer");
            var mat = renderer.sharedMaterial;
            Assert.IsNotNull(mat, "night-sky-card 缺少材质");
            Assert.IsTrue(mat.HasProperty("_Cull"), "night-sky-card 材质缺少 _Cull 属性");
            Assert.AreEqual((float)CullMode.Back, mat.GetFloat("_Cull"), 1e-3f,
                "night-sky-card 必须启用单面背面剔除（CullMode.Back）");

            var filter = card.GetComponent<MeshFilter>();
            Assert.IsNotNull(filter, "night-sky-card 缺少 MeshFilter");
            Assert.IsNotNull(filter.sharedMesh, "night-sky-card 缺少 Mesh");
            Assert.Greater(filter.sharedMesh.normals.Length, 0, "Quad 缺少法线");

            var worldFrontNormal = card.transform.TransformDirection(filter.sharedMesh.normals[0]).normalized;
            Assert.Greater(Vector3.Dot(worldFrontNormal, Vector3.forward), 0.99f,
                "night-sky-card 正面法线必须朝向室内 +Z，使室外 -Z 回望时被背面剔除");
        }

        static GameObject FindSceneRoot(Scene scene, string rootName)
        {
            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                if (string.Equals(roots[i].name, rootName, StringComparison.Ordinal))
                    return roots[i];
            }
            return null;
        }

        static T FindInScene<T>(Scene scene) where T : Component
        {
            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                var found = roots[i].GetComponentInChildren<T>(true);
                if (found != null) return found;
            }
            return null;
        }
    }
}
