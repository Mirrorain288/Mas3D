using System;
using System.Collections;
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
    /// Spec 5.15: the player must be able to walk the room with a real CharacterController, and the
    /// third-person camera must stay inside the shell instead of clipping through the walls.
    /// Movement is stepped by hand (PlayerMotor.Tick) so the result does not depend on frame timing.
    /// </summary>
    public class PlayerWalkTests
    {
        const string RoomPath = "Assets/_Local/room/mita-room.glb";
        const string AnchorsPath = "Anchors/anchors.json";
        const float Step = 1f / 60f;
        const float CapsuleRadius = 0.30f;
        const float PlayerHeight = 1.70f;

        static readonly HashSet<string> Walkable = new HashSet<string>(StringComparer.Ordinal) { "Mesh_0140out" };

        GameObject _room;
        GameObject _player;
        Camera _camera;
        PlayerMotor _motor;
        PlayerCameraRig _rig;
        AnchorTable _table;
        readonly List<Collider> _roomColliders = new List<Collider>();

        bool _hitBackfaces;

        [SetUp]
        public void SetUp()
        {
            // The imported floor mesh winds downward, so a downward ray only finds it when
            // backface queries are on. Overlap queries (the anchor tests) do not care.
            _hitBackfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
        }

        [TearDown]
        public void TearDown()
        {
            Physics.queriesHitBackfaces = _hitBackfaces;
            if (_rig) UnityEngine.Object.DestroyImmediate(_rig.gameObject);
            if (_player) UnityEngine.Object.DestroyImmediate(_player);
            if (_camera) UnityEngine.Object.DestroyImmediate(_camera.gameObject);
            if (_room) UnityEngine.Object.DestroyImmediate(_room);
            _rig = null;
            _player = null;
            _camera = null;
            _room = null;
            _motor = null;
        }

        /// <summary>Prefer the shipped StreamingAssets copy; fall back to the solved table in the repo.</summary>
        static AnchorTable Table()
        {
            var table = AnchorTable.Load();
            if (table == null || table.anchors == null || table.anchors.Length == 0)
                table = AnchorTable.Load(Path.Combine(Path.GetDirectoryName(Application.dataPath), AnchorsPath));
            Assert.IsNotNull(table, "anchors.json missing; run tools/unity.ps1 anchors first");
            Assert.Greater(table.anchors.Length, 0, "anchors.json has no anchors");
            return table;
        }

        void BuildRoom()
        {
#if UNITY_EDITOR
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RoomPath);
            Assert.IsNotNull(prefab, "room prefab missing: " + RoomPath);
            _room = UnityEngine.Object.Instantiate(prefab);
#else
            Assert.Ignore("PlayMode walk test needs the Editor asset database (glTFast prefab).");
#endif
            _room.name = "room-under-test";
            var bounds = Bounds(_room);
            _room.transform.position += new Vector3(-bounds.center.x, -bounds.min.y, -bounds.center.z);
            Physics.SyncTransforms();

            var attached = RoomColliders.Attach(_room);
            Assert.Greater(attached, 20, "expected the room meshes to carry colliders");
            foreach (var collider in _room.GetComponentsInChildren<Collider>(true)) _roomColliders.Add(collider);
            Physics.SyncTransforms();
            Debug.Log(string.Format("[walk-test] colliders={0}", attached));
        }

        void BuildPlayer(AnchorEntry anchor)
        {
            _player = new GameObject("Player");
            _player.tag = "Player";
            _player.AddComponent<CharacterController>();
            _motor = _player.AddComponent<PlayerMotor>();
            _motor.KeyboardInput = false;   // the test drives Tick directly
            _rig = _player.AddComponent<PlayerCameraRig>();
            _motor.Teleport(anchor.Feet(RoomBootstrap.SpawnLift), anchor.facingY);

            var cameraGo = new GameObject("Main Camera");
            cameraGo.tag = "MainCamera";
            _camera = cameraGo.AddComponent<Camera>();
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.nearClipPlane = 0.05f;
            _camera.farClipPlane = 120f;
            Physics.SyncTransforms();
        }

        static Bounds Bounds(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            Assert.Greater(renderers.Length, 0, "room has no renderers");
            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        static bool IsGroundCover(Collider collider) => collider.bounds.size.y <= 0.07f;

        /// <summary>Everything the player capsule is currently touching that it has no business touching.</summary>
        string Intrusions()
        {
            var feet = _player.transform.position;
            var bottom = new Vector3(feet.x, feet.y + CapsuleRadius + 0.02f, feet.z);
            var top = new Vector3(feet.x, feet.y + PlayerHeight - CapsuleRadius - 0.02f, feet.z);
            var hits = new Collider[32];
            var count = Physics.OverlapCapsuleNonAlloc(bottom, top, CapsuleRadius - 0.01f, hits, ~0, QueryTriggerInteraction.Ignore);
            var offenders = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var hit = hits[i];
                if (hit == null) continue;
                if (hit.transform.IsChildOf(_player.transform)) continue;   // its own capsule
                if (Walkable.Contains(hit.gameObject.name) || IsGroundCover(hit)) continue;
                if (hit.bounds.max.y <= feet.y + 0.08f) continue;           // stepped over
                offenders.Add(hit.gameObject.name);
            }
            return string.Join(", ", offenders.ToArray());
        }

        static Vector2 Flat(Vector3 point) => new Vector2(point.x, point.z);

        /// <summary>Walk the waypoint route at a fixed step, checking every frame that nothing intrudes.</summary>
        void WalkTo(Vector2 target, float budgetSeconds, string label)
        {
            var elapsed = 0f;
            var guard = 0;
            while (elapsed < budgetSeconds)
            {
                var position = Flat(_player.transform.position);
                if (Vector2.Distance(position, target) <= 0.12f) return;
                var wish = (target - position).normalized;
                _motor.Tick(Step, wish);
                Physics.SyncTransforms();
                elapsed += Step;
                guard++;
                if (guard % 5 == 0)
                {
                    var intrusions = Intrusions();
                    Assert.AreEqual(string.Empty, intrusions, label + " penetrated " + intrusions
                        + " at " + position.x.ToString("F2") + "," + position.y.ToString("F2"));
                }
            }
            Assert.Fail(label + " did not reach " + target.x.ToString("F2") + "," + target.y.ToString("F2")
                + " within " + budgetSeconds + " s");
        }

        void AssertInsideInterior(Vector3 point, float margin, string label)
        {
            var interior = _table.interior;
            Assert.IsTrue(point.x >= interior.x0 + margin && point.x <= interior.x1 - margin
                && point.z >= interior.z0 + margin && point.z <= interior.z1 - margin,
                label + " left the room: " + point.x.ToString("F2") + "," + point.y.ToString("F2") + "," + point.z.ToString("F2"));
        }

        [UnityTest]
        public IEnumerator PlayerWalksFromTheBedFootToTheDeskFront()
        {
            _table = Table();
            BuildRoom();
            var a2 = _table.Anchor("A2");
            var a3 = _table.Anchor("A3");
            Assert.IsNotNull(a2, "anchor A2 missing");
            Assert.IsNotNull(a3, "anchor A3 missing");
            BuildPlayer(a2);
            yield return null;

            // Bed footprint spans x 0.35..3.13 at z -0.98..0.93, so the straight line A2 -> A3 cuts
            // the corner. Walk around the bed's north-west corner the way a player would.
            WalkTo(new Vector2(a2.x, a3.z), 4f, "leg 1 (north)");
            WalkTo(new Vector2(a3.x, a3.z), 4f, "leg 2 (east)");

            var end = Flat(_player.transform.position);
            var gap = Vector2.Distance(end, new Vector2(a3.x, a3.z));
            Assert.LessOrEqual(gap, 0.35f, "stopped " + gap.ToString("F2") + " m short of A3");
            // gravity needs a few steps above the floor before the controller reports ground
            for (var i = 0; i < 8; i++) { _motor.Tick(Step, Vector2.zero); Physics.SyncTransforms(); }
            Assert.IsTrue(_motor.Grounded, "player never became grounded");
            AssertInsideInterior(_player.transform.position, 0.30f, "walk");
            Debug.Log(string.Format("[walk-test] reached A3 gap={0:F2} grounded={1}", gap, _motor.Grounded));
        }

        [UnityTest]
        public IEnumerator ThirdPersonCameraStaysInsideTheShell()
        {
            _table = Table();
            BuildRoom();
            var a2 = _table.Anchor("A2");
            BuildPlayer(a2);
            _rig.Build(_motor.Head, _camera);
            _rig.SetOrbit(a2.facingY, 14f);

            // Cinemachine resolves the pose in LateUpdate, so let the brain evaluate a few frames.
            for (var i = 0; i < 8; i++) yield return null;

            var eye = _camera.transform.position;
            AssertInsideInterior(eye, 0.05f, "camera");
            Assert.Greater(eye.y, 0.40f, "camera dropped under the floor");
            Assert.Less(eye.y, 2.90f, "camera climbed through the flat ceiling");

            var head = _motor.Head.position;
            Assert.IsFalse(Physics.Linecast(head, eye, out var blocked, ~0, QueryTriggerInteraction.Ignore),
                "camera looks through " + (blocked.collider != null ? blocked.collider.gameObject.name : "?"));
            Assert.Greater(Vector3.Distance(eye, head), 0.5f, "camera collapsed onto the head");
            Debug.Log(string.Format("[walk-test] camera at {0:F2},{1:F2},{2:F2} distance={3:F2}",
                eye.x, eye.y, eye.z, Vector3.Distance(eye, head)));
        }

        [UnityTest]
        public IEnumerator EveryAnchorSpawnsOnTheFloor()
        {
            _table = Table();
            BuildRoom();
            yield return null;

            foreach (var anchor in _table.anchors)
            {
                var origin = new Vector3(anchor.x, 1.50f, anchor.z);
                var hit = Physics.Raycast(origin, Vector3.down, out var info, 3f, ~0, QueryTriggerInteraction.Ignore);
                Assert.IsTrue(hit, "no floor under anchor " + anchor.id);
                Assert.Less(info.point.y, 0.10f, "anchor " + anchor.id + " spawns above y=" + info.point.y.ToString("F2"));
                Assert.Greater(info.point.y, -0.05f, "anchor " + anchor.id + " spawns below the floor");
            }
        }

        [UnityTest]
        public IEnumerator PlayerCannotWalkThroughTheWall()
        {
            _table = Table();
            BuildRoom();
            var a2 = _table.Anchor("A2");
            BuildPlayer(a2);
            yield return null;

            // Straight north from the bed foot: open floor until the far wall. The wall has to stop
            // the capsule, which only works because RoomColliders gives the shell a second, inward
            // facing collider (the imported box winds outward).
            WalkTo(new Vector2(a2.x, _table.interior.z1 - 0.30f), 6f, "wall approach");
            var end = _player.transform.position;
            Assert.Less(end.z, _table.interior.z1 - 0.25f,
                "player reached z=" + end.z.ToString("F2") + "; the wall did not stop it");
            Assert.Greater(end.z, a2.z + 1.5f, "player never travelled toward the wall");
            Assert.Greater(end.y, -0.5f, "player fell out of the room");
            Debug.Log(string.Format("[walk-test] wall stop z={0:F2} y={1:F2}", end.z, end.y));
        }
    }
}