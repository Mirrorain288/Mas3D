using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Mas3D.Room;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Mas3D.Editor
{
    /// <summary>
    /// Solves multi-room walk anchors, living sofa seats, doorway waypoints, and edge links directly
    /// against baked Flat.prefab colliders (1.5x scale), verifying 0.30 m capsule clearance across
    /// every segment with zero bathroom nodes. Writes Anchors/anchors-flat.json and StreamingAssets.
    /// </summary>
    public static class CliFlatAnchors
    {
        public const string FlatPrefabPath = "Assets/_Local/room/Flat.prefab";
        const float CapsuleRadius = 0.30f;
        const float SampleRadius = 0.32f;
        const float CapsuleHeight = 1.70f;
        const float FeetHeight = 0.02f;
        const float FloorCutoffY = 0.06f;
        const float CeilingCutoffY = 1.85f;
        const float GridStep = 0.05f;

        static readonly Rect BedroomRect = Rect.MinMaxRect(-7.60f, -4.15f, -1.85f, 1.85f);
        static readonly Rect LivingRect = Rect.MinMaxRect(-1.45f, -4.15f, 6.40f, 3.45f);
        static readonly Rect KitchenRect = Rect.MinMaxRect(6.80f, -3.25f, 11.05f, 4.50f);

        struct ResolvedPoint
        {
            public string id;
            public string role;
            public string room;
            public Vector3 feet;
            public float facingY;
        }

        public static void Solve()
        {
            var prevBackfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            try
            {
                RunSolve();
            }
            finally
            {
                Physics.queriesHitBackfaces = prevBackfaces;
            }
        }

        static void RunSolve()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(FlatPrefabPath);
            if (prefab == null)
            {
                Debug.LogError("[flatanchors] missing prefab: " + FlatPrefabPath);
                EditorApplication.Exit(1);
                return;
            }

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var flat = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            flat.name = "Flat-Solver";
            flat.transform.position = Vector3.zero;
            flat.SetActive(true);

            // Measure closed doorway centers FIRST (before swinging doors open) so flanking nodes align
            // directly with the architectural doorway opening, then swing living doors open (-90 deg)
            // so the physics sweep also verifies clearance around the opened door panels.
            var doors = InteractiveDoor.FindActive(flat);
            InteractiveDoor doorBL = null;
            InteractiveDoor doorLK = null;
            foreach (var d in doors)
            {
                d.SetOpen(false, true);
                if (string.Equals(d.DoorId, "Door_BedroomLiving", StringComparison.Ordinal))
                    doorBL = d;
                else if (string.Equals(d.DoorId, "Door_LivingKitchen", StringComparison.Ordinal))
                    doorLK = d;
            }
            Physics.SyncTransforms();

            if (doorBL == null || doorLK == null)
            {
                Debug.LogError("[flatanchors] missing expected InteractiveDoor components in Flat.prefab");
                EditorApplication.Exit(1);
                return;
            }

            var blCenter = doorBL.InteractionCenter;
            var lkCenter = doorLK.InteractionCenter;

            doorBL.SetOpen(true, true);
            doorLK.SetOpen(true, true);
            Physics.SyncTransforms();

            var byName = CollectRendererBounds(flat);
            if (!byName.TryGetValue("mesh_218.002", out var sofaCushion))
            {
                Debug.LogError("[flatanchors] missing living sofa cushion mesh_218.002");
                EditorApplication.Exit(1);
                return;
            }
            if (!byName.TryGetValue("mesh_216", out var bedBounds))
            {
                Debug.LogError("[flatanchors] missing bedroom bed mesh_216");
                EditorApplication.Exit(1);
                return;
            }

            // Probe actual top cushion surface & seat Z on mesh_218.002 (sofa faces -Z toward living rug & TV).
            var sofaSeatX = Mathf.Clamp(sofaCushion.center.x, sofaCushion.min.x + 0.90f, sofaCushion.max.x - 0.55f);
            var sofaPairX = sofaSeatX - 0.70f;
            var sofaSeatZ = Mathf.Lerp(sofaCushion.min.z, sofaCushion.max.z, 0.27f);
            var sofaTopY = SampleSurfaceY(sofaSeatX, sofaSeatZ, sofaCushion.max.y + 0.15f, sofaCushion.max.y);
            var sofaFrontStandZ = FindClearStandAlong(
                new Vector3(sofaSeatX, FeetHeight, sofaSeatZ),
                Vector3.back,
                0.70f,
                1.60f,
                out var sofaStandGap);

            if (sofaStandGap <= 0f)
            {
                Debug.LogError("[flatanchors] failed to find clear standing spot in front of living sofa");
                EditorApplication.Exit(1);
                return;
            }

            // Peripheral bedroom bed seat (non-intimate, pose = "bed"), stepping out past foot/bench (+Z) or side (+X).
            var bedCenterFloor = new Vector3(bedBounds.center.x, FeetHeight, bedBounds.center.z);
            var bedFacing = 0f;
            var bedStandPos = FindClearStandAlong(bedCenterFloor, Vector3.forward, 1.40f, 3.40f, out var bedStandGap, out var bedFailLog);
            if (bedStandGap <= 0f)
            {
                bedFacing = 90f;
                bedStandPos = FindClearStandAlong(bedCenterFloor, Vector3.right, 0.90f, 2.80f, out bedStandGap, out var sideLog);
                bedFailLog = bedFailLog + " | +X: " + sideLog;
            }
            if (bedStandGap <= 0f)
            {
                Debug.LogError("[flatanchors] failed to find clear standing spot beside bedroom bed: " + bedFailLog);
                EditorApplication.Exit(1);
                return;
            }

            // Default player spawn: directly in front of S1 on the open living rug, facing Monika (+Z / 0 deg).
            var spawnTarget = new Vector2(sofaSeatX, sofaFrontStandZ.z - 0.65f);
            var spawnFeet = SnapFree(spawnTarget, LivingRect, 0.80f, SampleRadius);
            var spawnFacing = YawBetween(spawnFeet, new Vector3(sofaSeatX, FeetHeight, sofaSeatZ));

            var seats = new List<SeatEntry>
            {
                new SeatEntry
                {
                    id = "S1",
                    mesh = "mesh_218.002",
                    room = "living",
                    pose = "sofa",
                    standGap = sofaStandGap,
                    intimate = true,
                    x = sofaSeatX,
                    y = sofaTopY + 0.01f,
                    z = sofaSeatZ,
                    topY = sofaTopY,
                    facingY = 180f,
                },
                new SeatEntry
                {
                    id = "S2",
                    mesh = "mesh_218.002",
                    room = "living",
                    pose = "sofa",
                    standGap = sofaStandGap,
                    intimate = true,
                    x = sofaPairX,
                    y = sofaTopY + 0.01f,
                    z = sofaSeatZ,
                    topY = sofaTopY,
                    facingY = 180f,
                },
                new SeatEntry
                {
                    id = "S3",
                    mesh = "mesh_216",
                    room = "bedroom",
                    pose = "bed",
                    standGap = bedStandGap,
                    intimate = false,
                    x = bedBounds.center.x,
                    y = bedBounds.max.y + 0.01f,
                    z = bedBounds.center.z,
                    topY = bedBounds.max.y,
                    facingY = bedFacing,
                },
            };

            // Doorway flanking pairs along closed doorway center Z, at least 1.05 m off the door plane so
            // both the 0.91 m swung leaf and the 0.30 m capsule clear the panel and door frame cleanly.
            var dBlB = SolveDoorFlank("D_BL_B", "bedroom", new Vector2(blCenter.x - 1.05f, blCenter.z), BedroomRect, 90f);
            var dBlL = SolveDoorFlank("D_BL_L", "living", new Vector2(blCenter.x + 1.05f, blCenter.z), LivingRect, 270f);
            var dLkL = SolveDoorFlank("D_LK_L", "living", new Vector2(lkCenter.x - 1.05f, lkCenter.z), LivingRect, 90f);
            var dLkK = SolveDoorFlank("D_LK_K", "kitchen", new Vector2(lkCenter.x + 1.05f, lkCenter.z), KitchenRect, 270f);

            // Living room anchors & routing waypoints.
            var aLivCenter = SolveAnchor("A1", "living-sofa-front", "living", new Vector2(sofaSeatX, sofaFrontStandZ.z - 0.55f), LivingRect, 0f);
            var aLivWindow = SolveAnchor("A2", "living-window", "living", new Vector2(3.15f, -3.10f), LivingRect, 180f);
            var wLivEast = SolveWaypoint("W_LIV_E", "living", new Vector2(5.15f, -1.35f), LivingRect, 90f);
            var wLivWest = SolveWaypoint("W_LIV_W", "living", new Vector2(0.05f, -1.25f), LivingRect, 270f);

            // Bedroom anchors (reading area, window/shelf, center aisle; zero bathroom nodes).
            var aBedCenter = SolveAnchor("A3", "bedroom-center", "bedroom", new Vector2(bedStandPos.x + 0.45f, -1.10f), BedroomRect, 270f);
            var aBedRead = SolveAnchor("A4", "bedroom-reading", "bedroom", new Vector2(-3.25f, 0.70f), BedroomRect, 45f);
            var aBedWindow = SolveAnchor("A5", "bedroom-window", "bedroom", new Vector2(-5.45f, 0.25f), BedroomRect, 0f);

            // Dining / kitchen anchors (south of dining table & north of kitchen counter, clearing open Door_LivingKitchen leaf).
            var aDinTable = SolveAnchor("A6", "dining-table", "kitchen", new Vector2(8.80f, 0.45f), KitchenRect, 0f);
            var aKitCounter = SolveAnchor("A7", "kitchen-counter", "kitchen", new Vector2(9.45f, -0.25f), KitchenRect, 180f);

            var allNodes = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
            allNodes["S1"] = new Vector3(sofaFrontStandZ.x, FeetHeight, sofaFrontStandZ.z);
            allNodes["S2"] = new Vector3(sofaPairX, FeetHeight, sofaFrontStandZ.z);
            allNodes["S3"] = new Vector3(bedStandPos.x, FeetHeight, bedStandPos.z);

            var anchors = new[] { aLivCenter, aLivWindow, aBedCenter, aBedRead, aBedWindow, aDinTable, aKitCounter };
            foreach (var a in anchors) allNodes[a.id] = a.feet;

            var waypoints = new[] { dBlB, dBlL, dLkL, dLkK, wLivEast, wLivWest };
            foreach (var w in waypoints) allNodes[w.id] = w.feet;

            var links = new[]
            {
                // Living loop & sofa seats
                new LinkEntry { a = "S1", b = "S2" },
                new LinkEntry { a = "S1", b = "A1" },
                new LinkEntry { a = "S2", b = "A1" },
                new LinkEntry { a = "A1", b = "A2" },
                new LinkEntry { a = "A1", b = "W_LIV_W" },
                new LinkEntry { a = "S2", b = "W_LIV_W" },
                new LinkEntry { a = "W_LIV_W", b = "D_BL_L" },
                new LinkEntry { a = "A1", b = "W_LIV_E" },
                new LinkEntry { a = "A2", b = "W_LIV_E" },
                new LinkEntry { a = "W_LIV_E", b = "D_LK_L" },

                // Cross-doorway Bedroom <-> Living
                new LinkEntry { a = "D_BL_B", b = "D_BL_L", doorId = "Door_BedroomLiving" },

                // Bedroom hubs (all strictly east of x=-7.80, bathroom forbidden)
                new LinkEntry { a = "D_BL_B", b = "A3" },
                new LinkEntry { a = "D_BL_B", b = "A4" },
                new LinkEntry { a = "A3", b = "S3" },
                new LinkEntry { a = "A3", b = "A4" },
                new LinkEntry { a = "A3", b = "A5" },
                new LinkEntry { a = "A4", b = "A5" },

                // Cross-doorway Living <-> Kitchen
                new LinkEntry { a = "D_LK_L", b = "D_LK_K", doorId = "Door_LivingKitchen" },

                // Dining & Kitchen
                new LinkEntry { a = "D_LK_K", b = "A6" },
                new LinkEntry { a = "D_LK_K", b = "A7" },
                new LinkEntry { a = "A6", b = "A7" },
            };

            // Verify every single point and edge against real Flat.prefab physics colliders!
            var failures = new List<string>();
            if (!IsFreePoint(spawnFeet.x, spawnFeet.z, CapsuleRadius, out var spawnHit))
                failures.Add("defaultSpawn blocked by " + spawnHit);

            foreach (var kv in allNodes)
            {
                if (!IsFreePoint(kv.Value.x, kv.Value.z, CapsuleRadius, out var hit))
                    failures.Add($"node {kv.Key} ({kv.Value.x:F2},{kv.Value.z:F2}) blocked by {hit}");
                if (kv.Value.x <= -7.80f)
                    failures.Add($"node {kv.Key} fell into bathroom region x={kv.Value.x:F2}");
            }

            foreach (var link in links)
            {
                if (!allNodes.TryGetValue(link.a, out var pA) || !allNodes.TryGetValue(link.b, out var pB))
                {
                    failures.Add($"link {link.a}-{link.b} missing node");
                    continue;
                }
                if (!SweepEdge(pA, pB, out var hitName))
                    failures.Add($"edge {link.a} ({pA.x:F2},{pA.z:F2}) -> {link.b} ({pB.x:F2},{pB.z:F2}) hit {hitName}");
            }

            if (failures.Count > 0)
            {
                foreach (var f in failures) Debug.LogError("[flatanchors] FAIL: " + f);
                EditorApplication.Exit(1);
                return;
            }

            var table = new AnchorTable
            {
                space = "flat",
                interior = new InteriorRect
                {
                    x0 = BedroomRect.xMin,
                    x1 = KitchenRect.xMax,
                    z0 = Mathf.Min(BedroomRect.yMin, LivingRect.yMin),
                    z1 = Mathf.Max(LivingRect.yMax, KitchenRect.yMax),
                },
                defaultSpawn = new SpawnEntry
                {
                    id = "player-living",
                    room = "living",
                    x = Round3(spawnFeet.x),
                    y = FeetHeight,
                    z = Round3(spawnFeet.z),
                    facingY = Round1(spawnFacing),
                },
                anchors = BuildAnchorEntries(anchors),
                seats = RoundSeats(seats),
                waypoints = BuildWaypointEntries(waypoints),
                links = links,
            };

            var json = Serialize(table);
            var anchorsDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Anchors"));
            Directory.CreateDirectory(anchorsDir);
            var anchorsOut = Path.Combine(anchorsDir, "anchors-flat.json");
            File.WriteAllText(anchorsOut, json, new UTF8Encoding(false));

            var streamingDir = Path.Combine(Application.streamingAssetsPath, "mas3d");
            Directory.CreateDirectory(streamingDir);
            var streamingOut = Path.Combine(streamingDir, "anchors-flat.json");
            File.WriteAllText(streamingOut, json, new UTF8Encoding(false));

            Debug.Log($"[flatanchors] ok anchors={anchors.Length} seats={seats.Count} waypoints={waypoints.Length} links={links.Length} sweptHits=0");
            Debug.Log($"[flatanchors] wrote {anchorsOut} & {streamingOut}");
            EditorApplication.Exit(0);
        }

        static Dictionary<string, Bounds> CollectRendererBounds(GameObject root)
        {
            var dict = new Dictionary<string, Bounds>(StringComparer.Ordinal);
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                if (!dict.ContainsKey(r.name)) dict[r.name] = r.bounds;
                else
                {
                    var m = dict[r.name];
                    m.Encapsulate(r.bounds);
                    dict[r.name] = m;
                }
            }
            return dict;
        }

        static float SampleSurfaceY(float x, float z, float startY, float fallbackY)
        {
            if (Physics.Raycast(new Vector3(x, startY, z), Vector3.down, out var hit, 1.5f))
                return hit.point.y;
            return fallbackY;
        }

        static Vector3 FindClearStandAlong(Vector3 seatFloor, Vector3 direction, float minGap, float maxGap, out float resolvedGap)
        {
            return FindClearStandAlong(seatFloor, direction, minGap, maxGap, out resolvedGap, out _);
        }

        static Vector3 FindClearStandAlong(Vector3 seatFloor, Vector3 direction, float minGap, float maxGap, out float resolvedGap, out string blockers)
        {
            var dir = new Vector3(direction.x, 0f, direction.z).normalized;
            var seen = new List<string>();
            for (var gap = minGap; gap <= maxGap + 1e-4f; gap += 0.05f)
            {
                var pt = seatFloor + dir * gap;
                if (IsFreePoint(pt.x, pt.z, SampleRadius, out var blocker))
                {
                    resolvedGap = Round3(gap);
                    blockers = string.Empty;
                    return new Vector3(Round3(pt.x), FeetHeight, Round3(pt.z));
                }
                if (seen.Count < 4) seen.Add($"{gap:F2}:{blocker}");
            }
            resolvedGap = -1f;
            blockers = string.Join(",", seen);
            return seatFloor;
        }

        static ResolvedPoint SolveDoorFlank(string id, string room, Vector2 target, Rect roomRect, float facingY)
        {
            var snapped = SnapFree(target, roomRect, 0.70f, SampleRadius);
            return new ResolvedPoint { id = id, role = "door-flank", room = room, feet = snapped, facingY = facingY };
        }

        static ResolvedPoint SolveAnchor(string id, string role, string room, Vector2 target, Rect roomRect, float facingY)
        {
            var snapped = SnapFree(target, roomRect, 1.25f, SampleRadius);
            return new ResolvedPoint { id = id, role = role, room = room, feet = snapped, facingY = facingY };
        }

        static ResolvedPoint SolveWaypoint(string id, string room, Vector2 target, Rect roomRect, float facingY)
        {
            var snapped = SnapFree(target, roomRect, 1.25f, SampleRadius);
            return new ResolvedPoint { id = id, role = "waypoint", room = room, feet = snapped, facingY = facingY };
        }

        static Vector3 SnapFree(Vector2 target, Rect rect, float maxSearchRadius, float radius)
        {
            var best = new Vector3(target.x, FeetHeight, target.y);
            var bestDistSq = float.PositiveInfinity;
            var found = false;

            var minX = Mathf.Max(rect.xMin, target.x - maxSearchRadius);
            var maxX = Mathf.Min(rect.xMax, target.x + maxSearchRadius);
            var minZ = Mathf.Max(rect.yMin, target.y - maxSearchRadius);
            var maxZ = Mathf.Min(rect.yMax, target.y + maxSearchRadius);

            for (var x = minX; x <= maxX + 1e-4f; x += GridStep)
            {
                for (var z = minZ; z <= maxZ + 1e-4f; z += GridStep)
                {
                    var d2 = (x - target.x) * (x - target.x) + (z - target.y) * (z - target.y);
                    if (d2 > maxSearchRadius * maxSearchRadius || d2 >= bestDistSq) continue;
                    if (!IsFreePoint(x, z, radius, out _)) continue;
                    bestDistSq = d2;
                    best = new Vector3(Round3(x), FeetHeight, Round3(z));
                    found = true;
                }
            }

            if (!found)
                Debug.LogWarning($"[flatanchors] could not snap target ({target.x:F2}, {target.y:F2}) inside {rect}");
            return best;
        }

        static string ColliderLabel(Collider col)
        {
            if (col == null) return "<null>";
            var parent = col.transform.parent != null ? col.transform.parent.name + "/" : "";
            return parent + col.name;
        }

        static bool IsFreePoint(float x, float z, float radius, out string blocker)
        {
            var p0 = new Vector3(x, FeetHeight + radius + 0.08f, z);
            var p1 = new Vector3(x, FeetHeight + CapsuleHeight - radius, z);
            var hits = Physics.OverlapCapsule(p0, p1, radius, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < hits.Length; i++)
            {
                if (IsObstacleCollider(hits[i]))
                {
                    blocker = ColliderLabel(hits[i]);
                    return false;
                }
            }
            blocker = null;
            return true;
        }

        static bool SweepEdge(Vector3 fromFeet, Vector3 toFeet, out string blocker)
        {
            var delta = new Vector3(toFeet.x - fromFeet.x, 0f, toFeet.z - fromFeet.z);
            var dist = delta.magnitude;
            if (dist < 1e-4f) { blocker = null; return true; }

            var p0 = new Vector3(fromFeet.x, FeetHeight + CapsuleRadius + 0.08f, fromFeet.z);
            var p1 = new Vector3(fromFeet.x, FeetHeight + CapsuleHeight - CapsuleRadius, fromFeet.z);
            var dir = delta / dist;

            var hits = Physics.CapsuleCastAll(p0, p1, CapsuleRadius, dir, dist, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < hits.Length; i++)
            {
                if (IsObstacleCollider(hits[i].collider))
                {
                    blocker = $"{ColliderLabel(hits[i].collider)}@{hits[i].point:F2}";
                    return false;
                }
            }

            var steps = Mathf.Max(2, Mathf.CeilToInt(dist / 0.15f));
            for (var s = 0; s <= steps; s++)
            {
                var pt = Vector3.Lerp(fromFeet, toFeet, s / (float)steps);
                if (!IsFreePoint(pt.x, pt.z, CapsuleRadius, out blocker)) return false;
            }

            blocker = null;
            return true;
        }

        static bool IsObstacleCollider(Collider col)
        {
            if (col == null || !col.enabled) return false;
            var name = col.gameObject.name;
            if (string.Equals(name, "LivingCeilingSeal", StringComparison.Ordinal)) return false;
            var b = col.bounds;
            if (b.max.y <= FloorCutoffY) return false;
            if (b.min.y >= CeilingCutoffY) return false;
            return true;
        }

        static AnchorEntry[] BuildAnchorEntries(ResolvedPoint[] points)
        {
            var arr = new AnchorEntry[points.Length];
            for (var i = 0; i < points.Length; i++)
            {
                arr[i] = new AnchorEntry
                {
                    id = points[i].id,
                    role = points[i].role,
                    room = points[i].room,
                    x = Round3(points[i].feet.x),
                    z = Round3(points[i].feet.z),
                    facingY = Round1(points[i].facingY),
                    wallClear = CapsuleRadius,
                    nearestClear = CapsuleRadius,
                };
            }
            return arr;
        }

        static SeatEntry[] RoundSeats(List<SeatEntry> list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                list[i].x = Round3(list[i].x);
                list[i].y = Round3(list[i].y);
                list[i].z = Round3(list[i].z);
                list[i].topY = Round3(list[i].topY);
                list[i].facingY = Round1(list[i].facingY);
                list[i].standGap = Round3(list[i].standGap);
            }
            return list.ToArray();
        }

        static WaypointEntry[] BuildWaypointEntries(ResolvedPoint[] points)
        {
            var arr = new WaypointEntry[points.Length];
            for (var i = 0; i < points.Length; i++)
            {
                arr[i] = new WaypointEntry
                {
                    id = points[i].id,
                    room = points[i].room,
                    x = Round3(points[i].feet.x),
                    z = Round3(points[i].feet.z),
                    facingY = Round1(points[i].facingY),
                };
            }
            return arr;
        }

        static string Serialize(AnchorTable table)
        {
            return JsonUtility.ToJson(table, true) + "\n";
        }

        static float YawBetween(Vector3 from, Vector3 to)
        {
            var dx = to.x - from.x;
            var dz = to.z - from.z;
            return Mathf.Repeat(Mathf.Atan2(dx, dz) * Mathf.Rad2Deg, 360f);
        }

        static float Round3(float v) => Mathf.Round(v * 1000f) * 0.001f;
        static float Round1(float v) => Mathf.Round(v * 10f) * 0.1f;
    }
}
