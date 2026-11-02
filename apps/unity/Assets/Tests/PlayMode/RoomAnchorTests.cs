using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.TestTools;

namespace Mas3D.Tests
{
    /// <summary>
    /// Spec 5.15 (3) / 5.17: every standing anchor must be reachable by the player capsule
    /// without touching the room shell or any furniture. The anchor table is produced by
    /// Mas3D.Editor.CliAnchors.Solve (tools/unity.ps1 anchors); this test proves the table
    /// against real Unity colliders rather than the analytic AABB the solver uses.
    /// </summary>
    public class RoomAnchorTests
    {
        const string RoomPath = "Assets/_Local/room/mita-room.glb";
        const string AnchorsPath = "Anchors/anchors.json";
        const float CapsuleRadius = 0.30f;
        const float PlayerHeight = 1.70f;
        // A seated body is narrower than a standing capsule, and its hips straddle the bench.
        const float SeatedRadius = 0.22f;

        // The floor is walked on, not collided with; everything else must stay out of the capsule.
        static readonly HashSet<string> Walkable = new HashSet<string>(StringComparer.Ordinal) { "Mesh_0140out" };

        // Rugs and other flat dressing sit under the feet and are stepped over, not into:
        // the solver applies the same rule (top <= 0.06 m is ground cover, not an obstacle).
        static bool IsGroundCover(Collider collider) => collider.bounds.size.y <= 0.07f;

        [Serializable] class JsonRoom { public float[] min; public float[] max; }
        [Serializable] class JsonInterior { public float x0; public float x1; public float z0; public float z1; }
        [Serializable] class JsonThresholds { public float wall; public float furniture; public float capsuleRadius; public float playerHeight; }
        [Serializable] class JsonAnchor { public string id; public string role; public float x; public float z; public float facingY; public float wallClear; public string nearest; public float nearestClear; }
        [Serializable] class JsonSeat { public string id; public string mesh; public float x; public float y; public float z; public float topY; public float facingY; }
        [Serializable] class JsonTable { public JsonRoom room; public JsonInterior interior; public JsonThresholds thresholds; public JsonAnchor[] anchors; public JsonSeat[] seats; }

        GameObject _room;
        readonly List<MeshCollider> _colliders = new List<MeshCollider>();

        [TearDown]
        public void TearDown()
        {
            foreach (var c in _colliders) if (c) UnityEngine.Object.DestroyImmediate(c);
            _colliders.Clear();
            if (_room) UnityEngine.Object.DestroyImmediate(_room);
            _room = null;
        }

        static string AnchorsFile()
        {
#if UNITY_EDITOR
            return Path.Combine(Path.GetDirectoryName(Application.dataPath), AnchorsPath);
#else
            return Path.Combine(Application.persistentDataPath, AnchorsPath);
#endif
        }

        static JsonTable LoadTable()
        {
            var path = AnchorsFile();
            Assert.IsTrue(File.Exists(path), "anchors.json missing; run tools/unity.ps1 anchors first: " + path);
            return JsonUtility.FromJson<JsonTable>(File.ReadAllText(path));
        }

        /// <summary>Instantiate the room, drop it onto the floor, and give every mesh a static collider.</summary>
        void BuildRoom()
        {
#if UNITY_EDITOR
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RoomPath);
            Assert.IsNotNull(prefab, "room prefab missing: " + RoomPath);
            _room = UnityEngine.Object.Instantiate(prefab);
#else
            Assert.Ignore("PlayMode anchor test needs the Editor asset database (glTFast prefab).");
#endif
            _room.name = "room-under-test";

            var bounds = ComputeBounds(_room);
            _room.transform.position += new Vector3(-bounds.center.x, -bounds.min.y, -bounds.center.z);
            Physics.SyncTransforms();

            foreach (var filter in _room.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!filter.sharedMesh) continue;
                var collider = filter.gameObject.AddComponent<MeshCollider>();
                collider.sharedMesh = filter.sharedMesh;
                collider.convex = false;
                _colliders.Add(collider);
            }
            Physics.SyncTransforms();
            // Guard against a vacuous pass: an empty physics scene would make every overlap query fail open.
            Assert.Greater(_colliders.Count, 20, "expected the room meshes to carry colliders");
            Debug.Log(string.Format("[anchors-test] colliders={0} anchors-planned", _colliders.Count));
        }

        static Bounds ComputeBounds(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            Assert.Greater(renderers.Length, 0, "room has no renderers");
            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        [UnityTest]
        public System.Collections.IEnumerator StandingAnchorsClearTheCapsule()
        {
            var table = LoadTable();
            BuildRoom();
            yield return null;

            Assert.Greater(table.anchors.Length, 0, "no anchors in anchors.json");
            var hits = new Collider[16];
            foreach (var anchor in table.anchors)
            {
                var bottom = new Vector3(anchor.x, CapsuleRadius + 0.02f, anchor.z);
                var top = new Vector3(anchor.x, PlayerHeight - CapsuleRadius, anchor.z);
                var count = Physics.OverlapCapsuleNonAlloc(bottom, top, CapsuleRadius, hits, ~0, QueryTriggerInteraction.Ignore);

                var offenders = new List<string>();
                for (var i = 0; i < count; i++)
                {
                    var name = hits[i].gameObject.name;
                    if (Walkable.Contains(name) || IsGroundCover(hits[i])) continue;
                    offenders.Add(name);
                }
                Assert.AreEqual(0, offenders.Count,
                    string.Format("anchor {0} ({1}, {2}) overlaps {3}", anchor.id, anchor.x.ToString("F2"), anchor.z.ToString("F2"), string.Join(", ", offenders.ToArray())));
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator SeatsRestOnTheirSeatWithoutPenetration()
        {
            var table = LoadTable();
            BuildRoom();
            yield return null;

            Assert.Greater(table.seats.Length, 0, "no seats in anchors.json");
            var hits = new Collider[16];
            foreach (var seat in table.seats)
            {
                // Seated capsule: hips just above the seat top, head 0.72 m above it.
                var bottom = new Vector3(seat.x, seat.y + 0.10f, seat.z);
                var top = new Vector3(seat.x, seat.y + 0.72f, seat.z);
                var count = Physics.OverlapCapsuleNonAlloc(bottom, top, SeatedRadius, hits, ~0, QueryTriggerInteraction.Ignore);

                var offenders = new List<string>();
                for (var i = 0; i < count; i++)
                {
                    var name = hits[i].gameObject.name;
                    // the seat itself, ground cover, and anything below hip height is under the body
                    if (Walkable.Contains(name) || name == seat.mesh || IsGroundCover(hits[i])) continue;
                    if (hits[i].bounds.max.y <= seat.y + 0.12f) continue;
                    offenders.Add(name);
                }
                Assert.AreEqual(0, offenders.Count,
                    string.Format("seat {0} on {1} overlaps {2}", seat.id, seat.mesh, string.Join(", ", offenders.ToArray())));
            }
        }
    }
}
