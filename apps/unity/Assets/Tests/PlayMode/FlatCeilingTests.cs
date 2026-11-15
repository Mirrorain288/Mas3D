using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Mas3D.Tests
{
    /// <summary>
    /// Task 1.1 & Task 2.2 PlayMode verification for default-bedroom isolation in flat mode
    /// and physical ceiling containment above the living room and vestibule.
    /// </summary>
    public class FlatCeilingTests
    {
        const string FlatPrefabPath = "Assets/_Local/room/Flat.prefab";
        const float MaxSingleStoryCeilingY = 2.90f;

        GameObject _flat;
        bool _prevBackfaces;

        [SetUp]
        public void SetUp()
        {
            _prevBackfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
        }

        [TearDown]
        public void TearDown()
        {
            Physics.queriesHitBackfaces = _prevBackfaces;
            if (_flat != null) UnityEngine.Object.DestroyImmediate(_flat);
            _flat = null;
            SceneFlow.UnloadMainIfLoaded();
        }

        [UnityTest]
        public IEnumerator DefaultBootActivatesFlatAndDeactivatesBedroomLightsAndSkyCard()
        {
            SceneFlow.Require();
            yield return SceneFlow.LoadMain();

            var boot = UnityEngine.Object.FindAnyObjectByType<RoomBootstrap>();
            Assert.IsNotNull(boot, "Main.unity 必须包含 RoomBootstrap");
            Assert.IsNotNull(boot.FlatRoom, "Main.unity 必须保留 FlatRoom 接线");
            yield return SceneFlow.WaitForBoot(boot);

            var roomLights = FindSceneRoot(boot.gameObject.scene, "room-lights");
            var nightSkyCard = FindSceneRoot(boot.gameObject.scene, "night-sky-card");
            Assert.IsNotNull(roomLights, "Main.unity 场景根部应存在 room-lights");
            Assert.IsNotNull(nightSkyCard, "Main.unity 场景根部应存在 night-sky-card");

            Assert.AreSame(boot.FlatRoom, boot.Room, "平层主舞台模式下 Room 应指向 FlatRoom");
            Assert.IsTrue(boot.FlatRoom.activeInHierarchy, "默认平层启动后 FlatRoom 必须处于激活状态");
            Assert.IsFalse(roomLights.activeSelf, "平层启动后必须停用场景根级 room-lights");
            Assert.IsFalse(nightSkyCard.activeSelf, "平层启动后必须停用场景根级 night-sky-card");
            if (boot.HouseRoom != null) Assert.IsFalse(boot.HouseRoom.activeInHierarchy, "默认启动不得激活 HouseRoom");
            Assert.AreEqual(AnchorTable.FlatRelativePath, AnchorTable.ActiveRelativePath, "默认应启用 anchors-flat.json");

            yield return SceneFlow.UnloadMain();
        }

        [UnityTest]
        public IEnumerator ShowBedroomRestoresBedroomLightsAndSkyCardAndRoundTripsToFlat()
        {
            SceneFlow.Require();
            yield return SceneFlow.LoadBedroom();

            var boot = UnityEngine.Object.FindAnyObjectByType<RoomBootstrap>();
            Assert.IsNotNull(boot, "Main.unity 必须包含 RoomBootstrap");

            var defaultRoom = boot.Room;
            Assert.IsNotNull(defaultRoom);
            Assert.IsNotNull(boot.FlatRoom);
            Assert.AreNotSame(defaultRoom, boot.FlatRoom, "默认卧室与平层对象不应为同一实例");

            var roomLights = FindSceneRoot(boot.gameObject.scene, "room-lights");
            var nightSkyCard = FindSceneRoot(boot.gameObject.scene, "night-sky-card");
            Assert.IsNotNull(roomLights);
            Assert.IsNotNull(nightSkyCard);

            Assert.IsTrue(defaultRoom.activeInHierarchy, "ShowBedroom 后应恢复默认卧室激活");
            Assert.IsTrue(roomLights.activeInHierarchy, "ShowBedroom 后应恢复 room-lights 激活");
            Assert.IsTrue(nightSkyCard.activeInHierarchy, "ShowBedroom 后应恢复 night-sky-card 激活");
            Assert.IsFalse(boot.FlatRoom.activeInHierarchy, "ShowBedroom 后必须停用 FlatRoom");
            Assert.AreEqual(AnchorTable.DefaultRelativePath, AnchorTable.ActiveRelativePath, "卧室模式应切回 anchors.json");

            boot.ShowFlat();
            yield return null;

            Assert.AreSame(boot.FlatRoom, boot.Room, "切回平层后 Room 应重新指向 FlatRoom");
            Assert.IsTrue(boot.FlatRoom.activeInHierarchy);
            Assert.IsFalse(defaultRoom.activeSelf);
            Assert.IsFalse(roomLights.activeSelf);
            Assert.IsFalse(nightSkyCard.activeSelf);
            Assert.AreEqual(AnchorTable.FlatRelativePath, AnchorTable.ActiveRelativePath);

            yield return SceneFlow.UnloadMain();
        }

        [UnityTest]
        public IEnumerator LivingRoomAndVestibuleAreSealedOverheadAtSingleStoryHeight()
        {
#if UNITY_EDITOR
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(FlatPrefabPath);
            Assert.IsNotNull(prefab, "缺少平层预制体: " + FlatPrefabPath);
            _flat = UnityEngine.Object.Instantiate(prefab);
#else
            Assert.Ignore("FlatCeilingTests 需要在 Editor 环境下加载 Flat.prefab");
#endif
            _flat.name = "Flat-CeilingTest";
            _flat.transform.position = Vector3.zero;
            _flat.SetActive(true);
            Physics.SyncTransforms();
            yield return null;

            var sealTransform = FindChildRecursive(_flat.transform, "LivingCeilingSeal");
            Assert.IsNotNull(sealTransform, "Flat.prefab 必须包含客厅及门厅物理封顶 LivingCeilingSeal");
            Assert.IsNull(sealTransform.GetComponent<Renderer>(), "LivingCeilingSeal 不得挂载 Renderer，以保持 flat-wide 俯视透明");
            var sealBox = sealTransform.GetComponent<BoxCollider>();
            Assert.IsNotNull(sealBox, "LivingCeilingSeal 必须包含 BoxCollider");
            Assert.IsTrue(sealBox.enabled);
            Assert.Greater(sealBox.bounds.min.y, 2.55f);
            Assert.Less(sealBox.bounds.max.y, MaxSingleStoryCeilingY);

            var floorRenderer = FindRendererByName(_flat.transform, "mesh_307.002");
            Assert.IsNotNull(floorRenderer, "Flat.prefab 应包含客厅与门厅地面 mesh_307.002");
            var b = floorRenderer.bounds;

            var walkableCount = 0;
            var sealOnlySpots = new List<string>();
            var minCeilingHitY = float.MaxValue;
            var maxCeilingHitY = float.MinValue;

            for (var x = b.min.x + 0.15f; x <= b.max.x - 0.15f; x += 0.25f)
            {
                for (var z = b.min.z + 0.15f; z <= b.max.z - 0.15f; z += 0.25f)
                {
                    if (!Physics.Raycast(new Vector3(x, 0.8f, z), Vector3.down, out var downHit, 1.5f)) continue;
                    if (downHit.point.y < -0.10f || downHit.point.y > 0.15f || downHit.normal.y < 0.7f) continue;

                    walkableCount++;

                    var upOrigin = new Vector3(x, 1.0f, z);
                    Assert.IsTrue(Physics.Raycast(upOrigin, Vector3.up, out var upHit, 12f),
                        $"客厅/门厅上方不得出现漏顶虚空: ({x:F2}, {z:F2})");
                    Assert.LessOrEqual(upHit.point.y, MaxSingleStoryCeilingY,
                        $"客厅/门厅上行碰撞超出单层高度: ({x:F2}, {z:F2}) hit={upHit.collider.name} y={upHit.point.y:F3}");

                    minCeilingHitY = Mathf.Min(minCeilingHitY, upHit.point.y);
                    maxCeilingHitY = Mathf.Max(maxCeilingHitY, upHit.point.y);

                    var allUp = Physics.RaycastAll(upOrigin, Vector3.up, 5f);
                    var hasMeshCeiling = false;
                    for (var i = 0; i < allUp.Length; i++)
                    {
                        if (allUp[i].collider != sealBox && allUp[i].point.y <= MaxSingleStoryCeilingY && allUp[i].point.y >= 2.2f)
                        {
                            hasMeshCeiling = true;
                            break;
                        }
                    }
                    if (!hasMeshCeiling && sealOnlySpots.Count < 16)
                    {
                        sealOnlySpots.Add($"({x:F2},{z:F2})->seal@{upHit.point.y:F2}");
                    }
                }
            }

            Assert.GreaterOrEqual(walkableCount, 200, "客厅与门厅应采出足够多可行走地面采样点");

            var capsuleTested = 0;
            const float capsuleRadius = 0.30f;
            const float capsuleHeight = 1.70f;
            for (var x = b.min.x + 0.45f; x <= b.max.x - 0.45f; x += 0.50f)
            {
                for (var z = b.min.z + 0.45f; z <= b.max.z - 0.45f; z += 0.50f)
                {
                    if (!Physics.Raycast(new Vector3(x, 0.8f, z), Vector3.down, out var downHit, 1.5f)) continue;
                    if (downHit.point.y < -0.10f || downHit.point.y > 0.15f) continue;

                    var p1 = new Vector3(x, capsuleRadius + 0.12f, z);
                    var p2 = new Vector3(x, capsuleHeight - capsuleRadius + 0.12f, z);
                    if (Physics.CheckCapsule(p1, p2, capsuleRadius)) continue;

                    capsuleTested++;
                    Assert.IsTrue(Physics.CapsuleCast(p1, p2, capsuleRadius, Vector3.up, out var capHit, 6f),
                        $"站立胶囊上行扫描在 ({x:F2}, {z:F2}) 未被封顶阻挡");
                    var maxHeadY = p2.y + capsuleRadius + capHit.distance;
                    Assert.LessOrEqual(maxHeadY, MaxSingleStoryCeilingY,
                        $"站立胶囊在 ({x:F2}, {z:F2}) 越过单层吊顶: headY={maxHeadY:F3} hit={capHit.collider.name}");
                }
            }

            Assert.GreaterOrEqual(capsuleTested, 20, "应在客厅及门厅空旷区域完成足够多胶囊上行封顶校验");
            Debug.Log($"[ceiling-test] walkable={walkableCount} capsule={capsuleTested} ceilingHitY={minCeilingHitY:F3}..{maxCeilingHitY:F3} sealOnlyCount={sealOnlySpots.Count} samples=[{string.Join(", ", sealOnlySpots)}]");
        }

        static Transform FindChildRecursive(Transform root, string name)
        {
            if (root == null) return null;
            if (string.Equals(root.name, name, StringComparison.Ordinal)) return root;
            for (var i = 0; i < root.childCount; i++)
            {
                var found = FindChildRecursive(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        static Renderer FindRendererByName(Transform root, string name)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r.enabled && string.Equals(r.name, name, StringComparison.Ordinal)) return r;
            }
            return null;
        }

        static GameObject FindSceneRoot(Scene scene, string rootName)
        {
            Assert.IsTrue(scene.IsValid(), "场景句柄无效");
            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                if (string.Equals(roots[i].name, rootName, StringComparison.Ordinal))
                    return roots[i];
            }
            return null;
        }
    }
}
