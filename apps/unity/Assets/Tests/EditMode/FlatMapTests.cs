using System;
using System.Collections.Generic;
using Mas3D.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Mas3D.Tests
{
    /// <summary>
    /// Task 2.1 EditMode tests verifying real upward-triangle projected floor area filtering
    /// so sparse ceiling downlight bezels (mesh_303.002) and downward-facing ceilings
    /// (mesh_193 / mesh_188.001) are never misclassified as [floor] in CliFlat.Map().
    /// </summary>
    public class FlatMapTests
    {
        readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            for (var i = _created.Count - 1; i >= 0; i--)
            {
                if (_created[i] != null) UnityEngine.Object.DestroyImmediate(_created[i]);
            }
            _created.Clear();
        }

        GameObject CreateQuadObject(string name, float width, float depth, float y, bool upwardFacing)
        {
            var go = new GameObject(name);
            _created.Add(go);
            go.transform.position = new Vector3(0f, y, 0f);

            var mesh = new Mesh { name = name + "_mesh" };
            _created.Add(mesh);
            mesh.vertices = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(0f, 0f, depth),
                new Vector3(width, 0f, depth),
                new Vector3(width, 0f, 0f),
            };
            mesh.triangles = upwardFacing
                ? new[] { 0, 1, 2, 0, 2, 3 }
                : new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();

            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            go.AddComponent<MeshRenderer>();
            return go;
        }

        [Test]
        public void UpwardFloorQuadIsAcceptedWithTrueProjectedArea()
        {
            var floor = CreateQuadObject("SyntheticFloor", 2.5f, 2.0f, 0f, upwardFacing: true);
            var renderer = floor.GetComponent<Renderer>();
            var filter = floor.GetComponent<MeshFilter>();

            var area = CliFlat.ComputeUpwardProjectedArea(filter.sharedMesh, renderer.transform.localToWorldMatrix);
            Assert.AreEqual(5.0f, area, 1e-3f);
            Assert.IsTrue(CliFlat.IsWalkableFloorCandidate(renderer, out var candidateArea));
            Assert.AreEqual(5.0f, candidateArea, 1e-3f);
        }

        [Test]
        public void DownwardCeilingQuadIsRejectedDespiteLargeFootprint()
        {
            var ceiling = CreateQuadObject("SyntheticCeiling_193", 4.0f, 4.0f, 2.745f, upwardFacing: false);
            var renderer = ceiling.GetComponent<Renderer>();
            var filter = ceiling.GetComponent<MeshFilter>();

            var area = CliFlat.ComputeUpwardProjectedArea(filter.sharedMesh, renderer.transform.localToWorldMatrix);
            Assert.AreEqual(0f, area, 1e-4f);
            Assert.IsFalse(CliFlat.IsWalkableFloorCandidate(renderer, out var candidateArea));
            Assert.AreEqual(0f, candidateArea, 1e-4f);
        }

        [Test]
        public void DispersedHollowBezelRingIsRejectedEvenWithLargeAabb()
        {
            var go = new GameObject("SyntheticBezels_303_002");
            _created.Add(go);
            go.transform.position = new Vector3(0f, 2.65f, 0f);

            // Simulate 4 sparse ceiling downlight bezels spanning a 5m x 4m AABB (20 m^2 AABB)
            // whose actual upward triangle area totals only 4 * 0.04 = 0.16 m^2 (< 1.5 m^2).
            var origins = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(5f, 0f, 0f),
                new Vector3(0f, 0f, 4f),
                new Vector3(5f, 0f, 4f),
            };
            var verts = new Vector3[origins.Length * 4];
            var tris = new int[origins.Length * 6];
            for (var i = 0; i < origins.Length; i++)
            {
                var o = origins[i];
                var vBase = i * 4;
                verts[vBase + 0] = o + new Vector3(0f, 0f, 0f);
                verts[vBase + 1] = o + new Vector3(0f, 0f, 0.2f);
                verts[vBase + 2] = o + new Vector3(0.2f, 0f, 0.2f);
                verts[vBase + 3] = o + new Vector3(0.2f, 0f, 0f);
                var tBase = i * 6;
                tris[tBase + 0] = vBase + 0;
                tris[tBase + 1] = vBase + 1;
                tris[tBase + 2] = vBase + 2;
                tris[tBase + 3] = vBase + 0;
                tris[tBase + 4] = vBase + 2;
                tris[tBase + 5] = vBase + 3;
            }

            var mesh = new Mesh { name = "BezelsMesh" };
            _created.Add(mesh);
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();

            var aabbArea = renderer.bounds.size.x * renderer.bounds.size.z;
            Assert.Greater(aabbArea, 18f, "AABB 投影面积故意大到足以触发旧逻辑误报");
            Assert.IsFalse(CliFlat.IsWalkableFloorCandidate(renderer, out var candidateArea),
                "离散空心灯框真实朝上面积极小，不得判为 [floor]");
            Assert.AreEqual(0.16f, candidateArea, 1e-3f);
        }

        [Test]
        public void FlatPrefabExcludesDownlightBezelsAndDownwardCeilingsFromFloorCandidates()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(CliFlat.Output);
            if (prefab == null) Assert.Ignore("缺少 Flat.prefab，先运行平层烘焙");

            var flat = UnityEngine.Object.Instantiate(prefab);
            _created.Add(flat);
            flat.transform.position = Vector3.zero;
            flat.SetActive(true);

            var byName = new Dictionary<string, Renderer>(StringComparer.Ordinal);
            foreach (var r in flat.GetComponentsInChildren<Renderer>(true))
            {
                if (r.enabled && !byName.ContainsKey(r.name)) byName[r.name] = r;
            }

            var falseFloors = new[] { "mesh_303.002", "mesh_193", "mesh_188.001" };
            foreach (var name in falseFloors)
            {
                Assert.IsTrue(byName.TryGetValue(name, out var r), "Flat.prefab 应包含网格: " + name);
                Assert.IsFalse(CliFlat.IsWalkableFloorCandidate(r, out var upArea),
                    $"{name} 不应误报为 [floor], upArea={upArea:F3}");
            }

            var realFloors = new[] { "mesh_307.002", "mesh_397.004", "mesh_185.001", "mesh_429.002" };
            foreach (var name in realFloors)
            {
                Assert.IsTrue(byName.TryGetValue(name, out var r), "Flat.prefab 应包含真实地面: " + name);
                Assert.IsTrue(CliFlat.IsWalkableFloorCandidate(r, out var upArea),
                    $"真实地面 {name} 应保留在 [floor] 中, upArea={upArea:F3}");
                Assert.Greater(upArea, CliFlat.MinFloorUpwardArea);
            }
        }
    }
}
