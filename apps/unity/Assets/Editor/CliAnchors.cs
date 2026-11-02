using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Mas3D.Editor
{
    /// <summary>
    /// Recomputes the interaction anchors from the imported geometry, in Unity world space.
    ///   -executeMethod Mas3D.Editor.CliAnchors.Solve
    /// Standing anchors: wall clearance >= 0.35 m and furniture clearance >= 0.45 m for a
    /// 0.30 m capsule, 1.70 m tall. Seat slots: top face of the seat mesh plus one centimetre.
    /// The web table cannot be reused: the web scene yaws the room by -90 degrees and offsets it.
    /// Each anchor starts from a landmark-relative role target and is then snapped onto the
    /// nearest sampled point that actually passes the clearance test, so nothing is hand-picked.
    /// </summary>
    public static class CliAnchors
    {
        public const string RoomPath = "Assets/_Local/room/mita-room.glb";

        const float WallClearance = 0.35f;
        const float FurnitureClearance = 0.45f;
        const float CapsuleRadius = 0.30f;
        const float PlayerHeight = 1.70f;
        const float GridStep = 0.05f;
        const float SnapRadius = 1.20f;
        const float MinAnchorSeparation = 0.60f;

        const string WallMesh = "Mesh_0115out";
        const string SkirtMesh = "Mesh_0116out";
        const string FloorMesh = "Mesh_0140out";
        const string BedMesh = "Mesh_0121out";
        const string BenchMesh = "Mesh_0001out";
        const string DeskMesh = "Mesh_0146out";
        const string WallDeskMesh = "Mesh_0025out";
        const string DrawerMesh = "Mesh_0138out";
        const string MirrorMesh = "Mesh_0014out";
        const string WindowMesh = "Mesh_0106out";
        const string DoorMesh = "Mesh_0009out";

        class Obstacle
        {
            public string name;
            public Bounds bounds;
            public float extraFront;
        }

        class Candidate
        {
            public float x;
            public float z;
            public float wall;
            public float furniture;
            public string nearest;
            public float score;
        }

        class Role
        {
            public string id;
            public string role;
            public float x;
            public float z;
        }

        class Anchor
        {
            public string id;
            public string role;
            public float x;
            public float z;
            public float facingY;
            public float wallClear;
            public string nearest;
            public float nearestClear;
            public float score;
            public float targetDistance;
        }

        class Seat
        {
            public string id;
            public string mesh;
            public float x;
            public float y;
            public float z;
            public float topY;
            public float facingY;
            public string note;
        }

        public static void Solve()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RoomPath);
            if (prefab == null)
            {
                Debug.Log("[anchors] missing room prefab " + RoomPath);
                EditorApplication.Exit(1);
                return;
            }

            var room = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            var bounds = Measure(room);
            room.transform.position += new Vector3(-bounds.center.x, -bounds.min.y, -bounds.center.z);
            bounds = Measure(room);
            Debug.Log($"[anchors] room bounds min={bounds.min:F2} max={bounds.max:F2} size={bounds.size:F2}");

            var byName = new Dictionary<string, Bounds>(StringComparer.Ordinal);
            foreach (var renderer in room.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) continue;
                if (!byName.ContainsKey(renderer.name)) byName[renderer.name] = renderer.bounds;
                else
                {
                    var merged = byName[renderer.name];
                    merged.Encapsulate(renderer.bounds);
                    byName[renderer.name] = merged;
                }
            }
            Debug.Log($"[anchors] renderers={byName.Count}");

            var interior = InteriorRect(byName);
            Debug.Log($"[anchors] interior x={interior.xMin:F2}..{interior.xMax:F2} z={interior.yMin:F2}..{interior.yMax:F2} ({(interior.width):F2} x {(interior.height):F2})");

            var obstacles = CollectObstacles(byName);
            var candidates = Sample(interior, obstacles);
            Debug.Log($"[anchors] obstacles={obstacles.Count} candidates={candidates.Count}");

            var roles = RoleTargets(byName, interior);
            foreach (var r in roles)
                Debug.Log($"[role] {r.role} target x={r.x:F2} z={r.z:F2}");

            var anchors = SnapRoles(roles, candidates, interior);
            var seats = FindSeats(byName);

            var json = BuildJson(interior, obstacles, roles, anchors, seats, bounds);
            var dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Anchors"));
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "anchors.json");
            File.WriteAllText(file, json, new UTF8Encoding(false));

            foreach (var a in anchors)
                Debug.Log($"[anchor] {a.id} {a.role} x={a.x:F2} z={a.z:F2} facing={a.facingY:F1} wall={a.wallClear:F2} nearest={a.nearest} clear={a.nearestClear:F2} drift={a.targetDistance:F2}");
            foreach (var s in seats)
                Debug.Log($"[seat] {s.id} mesh={s.mesh} x={s.x:F2} y={s.y:F2} z={s.z:F2} top={s.topY:F2} facing={s.facingY:F1}");

            Debug.Log("[anchors] wrote " + file);
            EditorApplication.Exit(0);
        }

        static Bounds Measure(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return new Bounds(Vector3.zero, Vector3.zero);
            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        /// <summary>The interior footprint: the skirt shell lines the inside faces of the walls.</summary>
        static Rect InteriorRect(Dictionary<string, Bounds> byName)
        {
            Bounds b;
            if (byName.TryGetValue(SkirtMesh, out b))
                return Rect.MinMaxRect(b.min.x, b.min.z, b.max.x, b.max.z);
            if (byName.TryGetValue(WallMesh, out b))
                return Rect.MinMaxRect(b.min.x + 0.15f, b.min.z + 0.15f, b.max.x - 0.15f, b.max.z - 0.15f);
            return new Rect(-3f, -3f, 6f, 6f);
        }

        static List<Obstacle> CollectObstacles(Dictionary<string, Bounds> byName)
        {
            var list = new List<Obstacle>();
            foreach (var pair in byName)
            {
                var name = pair.Key;
                if (name == FloorMesh || name == WallMesh || name == SkirtMesh) continue;
                var b = pair.Value;
                if (b.max.y <= 0.06f) continue;                 // rug is walkable
                if (b.min.y >= PlayerHeight) continue;          // above the head
                if (b.size.x < 0.02f || b.size.z < 0.02f) continue;
                var obstacle = new Obstacle { name = name, bounds = b };
                if (name == DrawerMesh) obstacle.extraFront = 0.05f;   // spec asks for 0.50 m in front
                list.Add(obstacle);
            }
            return list;
        }

        static float DistanceXZ(Bounds b, float x, float z)
        {
            var dx = Mathf.Max(0f, Mathf.Max(b.min.x - x, x - b.max.x));
            var dz = Mathf.Max(0f, Mathf.Max(b.min.z - z, z - b.max.z));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        static List<Candidate> Sample(Rect interior, List<Obstacle> obstacles)
        {
            var list = new List<Candidate>();
            for (var x = interior.xMin + 0.02f; x <= interior.xMax - 0.02f; x += GridStep)
            {
                for (var z = interior.yMin + 0.02f; z <= interior.yMax - 0.02f; z += GridStep)
                {
                    var wall = Mathf.Min(Mathf.Min(x - interior.xMin, interior.xMax - x), Mathf.Min(z - interior.yMin, interior.yMax - z));
                    if (wall < WallClearance) continue;

                    var nearest = "";
                    var furniture = float.MaxValue;
                    var blocked = false;
                    foreach (var o in obstacles)
                    {
                        var gap = DistanceXZ(o.bounds, x, z) - o.extraFront;
                        if (gap <= 0f) { blocked = true; break; }
                        if (gap < furniture) { furniture = gap; nearest = o.name; }
                    }
                    if (blocked || furniture < FurnitureClearance) continue;

                    list.Add(new Candidate
                    {
                        x = x,
                        z = z,
                        wall = wall,
                        furniture = furniture,
                        nearest = nearest,
                        score = Mathf.Min(wall - WallClearance, furniture - FurnitureClearance),
                    });
                }
            }
            return list;
        }

        static Vector2 Centre(Bounds b)
        {
            return new Vector2(b.center.x, b.center.z);
        }

        static Vector2 Toward(Vector2 from, Vector2 to, float distance)
        {
            var delta = to - from;
            var length = delta.magnitude;
            if (length < 1e-4f) return from;
            return from + delta / length * distance;
        }

        /// <summary>Role targets expressed relative to measured landmarks, never typed coordinates.</summary>
        static List<Role> RoleTargets(Dictionary<string, Bounds> byName, Rect interior)
        {
            var roles = new List<Role>();
            var centre = new Vector2(interior.center.x, interior.center.y);

            Bounds door, bed, bench, desk, window, mirror;
            Bounds? deskWall = null;
            byName.TryGetValue(DoorMesh, out door);
            byName.TryGetValue(BedMesh, out bed);
            byName.TryGetValue(BenchMesh, out bench);
            byName.TryGetValue(DeskMesh, out desk);
            byName.TryGetValue(WindowMesh, out window);
            byName.TryGetValue(MirrorMesh, out mirror);
            if (byName.TryGetValue(WallDeskMesh, out var wd)) deskWall = wd;

            if (byName.ContainsKey(DeskMesh) && deskWall.HasValue)
            {
                var merged = desk;
                merged.Encapsulate(deskWall.Value);
                desk = merged;
            }

            if (byName.ContainsKey(DoorMesh))
            {
                var doorCentre = Centre(door);
                var inside = Toward(doorCentre, centre, 1.30f);
                roles.Add(new Role { role = "door-inside", x = inside.x, z = inside.y });
            }

            if (byName.ContainsKey(BenchMesh) && byName.ContainsKey(BedMesh))
            {
                var benchCentre = Centre(bench);
                var bedCentre = Centre(bed);
                var out1 = Toward(bedCentre, benchCentre, (benchCentre - bedCentre).magnitude + 0.80f);
                roles.Add(new Role { role = "bed-foot", x = out1.x, z = out1.y });
            }

            if (byName.ContainsKey(DeskMesh))
            {
                var deskCentre = Centre(desk);
                var front = Toward(deskCentre, centre, (desk.size.x + desk.size.z) * 0.25f + 0.80f);
                roles.Add(new Role { role = "desk-front", x = front.x, z = front.y });
            }

            if (byName.ContainsKey(WindowMesh) && byName.ContainsKey(BedMesh))
            {
                // the window sits above the headboard, so the only standable spot with that view is
                // the walkway beside the bed: keep the bed's x, step clear of its long side
                var windowCentre = Centre(window);
                roles.Add(new Role { role = "window-side", x = bed.center.x, z = bed.center.z - bed.size.z * 0.5f - 0.90f });
            }

            // the quietest corner: farthest interior corner from the door
            if (byName.ContainsKey(DoorMesh))
            {
                var doorCentre = Centre(door);
                var best = Vector2.zero;
                var bestDistance = -1f;
                foreach (var corner in new[]
                         {
                             new Vector2(interior.xMin + 0.75f, interior.yMin + 0.75f),
                             new Vector2(interior.xMax - 0.75f, interior.yMin + 0.75f),
                             new Vector2(interior.xMin + 0.75f, interior.yMax - 0.75f),
                             new Vector2(interior.xMax - 0.75f, interior.yMax - 0.75f),
                         })
                {
                    var distance = Vector2.Distance(corner, doorCentre);
                    if (distance > bestDistance) { bestDistance = distance; best = corner; }
                }
                roles.Add(new Role { role = "far-corner", x = best.x, z = best.y });
            }

            return roles;
        }

        static List<Anchor> SnapRoles(List<Role> roles, List<Candidate> candidates, Rect interior)
        {
            var anchors = new List<Anchor>();
            var centre = new Vector2(interior.center.x, interior.center.y);
            var taken = new List<Candidate>();

            foreach (var role in roles)
            {
                var target = new Vector2(role.x, role.z);
                Candidate best = null;
                var bestCost = float.MaxValue;
                foreach (var c in candidates)
                {
                    var distance = Vector2.Distance(new Vector2(c.x, c.z), target);
                    if (distance > SnapRadius) continue;
                    var crowded = false;
                    foreach (var t in taken)
                        if (Vector2.Distance(new Vector2(c.x, c.z), new Vector2(t.x, t.z)) < MinAnchorSeparation) { crowded = true; break; }
                    if (crowded) continue;
                    var cost = distance - c.score * 0.5f;   // prefer nearby, then roomier
                    if (cost < bestCost) { bestCost = cost; best = c; }
                }
                if (best == null)
                {
                    Debug.Log($"[anchor] {role.role} has no point within {SnapRadius:F2} m that passes clearance - widen the role offset");
                    continue;
                }
                taken.Add(best);
                anchors.Add(new Anchor
                {
                    id = "A" + (anchors.Count + 1),
                    role = role.role,
                    x = best.x,
                    z = best.z,
                    facingY = Mathf.Atan2(centre.x - best.x, centre.y - best.z) * Mathf.Rad2Deg,
                    wallClear = best.wall,
                    nearest = best.nearest,
                    nearestClear = best.furniture,
                    score = best.score,
                    targetDistance = Vector2.Distance(new Vector2(best.x, best.z), target),
                });
            }
            return anchors;
        }

        static List<Seat> FindSeats(Dictionary<string, Bounds> byName)
        {
            var seats = new List<Seat>();
            Bounds bed, bench;
            var hasBed = byName.TryGetValue(BedMesh, out bed);
            if (byName.TryGetValue(BenchMesh, out bench))
            {
                var facing = hasBed
                    ? Mathf.Atan2(bench.center.x - bed.center.x, bench.center.z - bed.center.z) * Mathf.Rad2Deg
                    : 180f;
                seats.Add(new Seat
                {
                    id = "S1",
                    mesh = BenchMesh,
                    x = bench.center.x,
                    y = bench.max.y + 0.01f,
                    z = bench.center.z,
                    topY = bench.max.y,
                    facingY = facing,
                    note = "bench top + 0.01 m, free standing, faces away from the bed",
                });
            }
            if (hasBed)
            {
                seats.Add(new Seat
                {
                    id = "S2",
                    mesh = BedMesh,
                    x = bed.center.x,
                    y = bed.max.y + 0.01f,
                    z = bed.center.z,
                    topY = bed.max.y,
                    facingY = 0f,
                    note = "bed bounding box top is the duvet and sits above the knee line - measure the mattress face before using it",
                });
            }
            return seats;
        }

        static string BuildJson(Rect interior, List<Obstacle> obstacles, List<Role> roles, List<Anchor> anchors, List<Seat> seats, Bounds room)
        {
            var sb = new StringBuilder();
            var inv = CultureInfo.InvariantCulture;
            sb.Append("{\n");
            sb.Append("  \"space\": \"unity-world\",\n");
            sb.Append($"  \"room\": {{\"min\": [{room.min.x.ToString("F3", inv)}, {room.min.y.ToString("F3", inv)}, {room.min.z.ToString("F3", inv)}], \"max\": [{room.max.x.ToString("F3", inv)}, {room.max.y.ToString("F3", inv)}, {room.max.z.ToString("F3", inv)}]}},\n");
            sb.Append($"  \"interior\": {{\"x0\": {interior.xMin.ToString("F3", inv)}, \"x1\": {interior.xMax.ToString("F3", inv)}, \"z0\": {interior.yMin.ToString("F3", inv)}, \"z1\": {interior.yMax.ToString("F3", inv)}}},\n");
            sb.Append($"  \"thresholds\": {{\"wall\": {WallClearance.ToString("F2", inv)}, \"furniture\": {FurnitureClearance.ToString("F2", inv)}, \"capsuleRadius\": {CapsuleRadius.ToString("F2", inv)}, \"playerHeight\": {PlayerHeight.ToString("F2", inv)}}},\n");

            sb.Append("  \"obstacles\": [\n");
            for (var i = 0; i < obstacles.Count; i++)
            {
                var o = obstacles[i].bounds;
                sb.Append($"    {{\"name\": \"{obstacles[i].name}\", \"min\": [{o.min.x.ToString("F3", inv)}, {o.min.y.ToString("F3", inv)}, {o.min.z.ToString("F3", inv)}], \"max\": [{o.max.x.ToString("F3", inv)}, {o.max.y.ToString("F3", inv)}, {o.max.z.ToString("F3", inv)}], \"extraFront\": {obstacles[i].extraFront.ToString("F2", inv)}}}");
                sb.Append(i == obstacles.Count - 1 ? "\n" : ",\n");
            }
            sb.Append("  ],\n");

            sb.Append("  \"roleTargets\": [\n");
            for (var i = 0; i < roles.Count; i++)
            {
                sb.Append($"    {{\"role\": \"{roles[i].role}\", \"x\": {roles[i].x.ToString("F3", inv)}, \"z\": {roles[i].z.ToString("F3", inv)}}}");
                sb.Append(i == roles.Count - 1 ? "\n" : ",\n");
            }
            sb.Append("  ],\n");

            sb.Append("  \"anchors\": [\n");
            for (var i = 0; i < anchors.Count; i++)
            {
                var a = anchors[i];
                sb.Append($"    {{\"id\": \"{a.id}\", \"role\": \"{a.role}\", \"x\": {a.x.ToString("F3", inv)}, \"z\": {a.z.ToString("F3", inv)}, \"facingY\": {a.facingY.ToString("F1", inv)}, \"wallClear\": {a.wallClear.ToString("F2", inv)}, \"nearest\": \"{a.nearest}\", \"nearestClear\": {a.nearestClear.ToString("F2", inv)}, \"score\": {a.score.ToString("F2", inv)}, \"targetDistance\": {a.targetDistance.ToString("F2", inv)}}}");
                sb.Append(i == anchors.Count - 1 ? "\n" : ",\n");
            }
            sb.Append("  ],\n");

            sb.Append("  \"seats\": [\n");
            for (var i = 0; i < seats.Count; i++)
            {
                var s = seats[i];
                sb.Append($"    {{\"id\": \"{s.id}\", \"mesh\": \"{s.mesh}\", \"x\": {s.x.ToString("F3", inv)}, \"y\": {s.y.ToString("F3", inv)}, \"z\": {s.z.ToString("F3", inv)}, \"topY\": {s.topY.ToString("F3", inv)}, \"facingY\": {s.facingY.ToString("F1", inv)}, \"note\": \"{s.note}\"}}");
                sb.Append(i == seats.Count - 1 ? "\n" : ",\n");
            }
            sb.Append("  ]\n");
            sb.Append("}\n");
            return sb.ToString();
        }
    }
}
