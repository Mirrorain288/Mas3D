using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Mas3D.Editor
{
    /// <summary>
    /// Re-assembles the exploded dollhouse into a walkable flat.
    ///
    /// 无门.fbx / mita-nodoor.glb is one whole home, but the source lays the rooms out tens of
    /// metres apart (measured: ground floor x -54..-33, kitchen x +43..+55 and 18 m up), which is
    /// how C4D kept them apart while authoring. Nothing can walk from one to the next like that.
    ///
    /// So: cluster the renderers by their XZ footprint, name each cluster from the object names it
    /// contains, then translate whole clusters so they sit side by side around the origin:
    ///
    ///        bath | bedroom | hall | kitchen
    ///      (-x)      (0,0)     (+x)    (+2x)
    ///
    /// Everything is measured at runtime; no coordinate from the source file is hard-coded, because
    /// the same mesh reads at a different scale depending on which transform the query walks.
    /// </summary>
    public static class RoomLayout
    {
        public class Cluster
        {
            public string Kind;                 // bedroom / bath / hall / kitchen
            public readonly List<Renderer> Members = new List<Renderer>();
            public Bounds Bounds;
            public float FootprintArea;
            public int Triangles;

            /// <summary>The room's walkable rectangle: the union of its Floor* meshes. Walls overhang the
            /// floor by their thickness, so abutting whole bounds leaves a metre-wide hole between rooms —
            /// abutting floors instead is what makes the doorways meet.</summary>
            public Bounds Floor;
            public bool HasFloor;
        }

        static readonly (string kind, string[] tokens)[] Kinds =
        {
            ("kitchen", new[] { "kitchen", "fridge", "stove" }),
            ("bath",    new[] { "bath", "toilet", "shower", "sink", "washbasin" }),
            ("bedroom", new[] { "bedroom", "bed", "wardrobe", "curtain", "mattress" }),
        };

        /// <summary>How far apart (metres) two renderers may sit and still count as one room.</summary>
        const float JoinGap = 0.35f;

        public static List<Cluster> BuildClusters(GameObject room)
        {
            var renderers = room.GetComponentsInChildren<Renderer>(true);
            var parent = Enumerable.Range(0, renderers.Length).ToArray();
            int Find(int i)
            {
                while (parent[i] != i) i = parent[i] = parent[parent[i]];
                return i;
            }
            void Union(int a, int b)
            {
                a = Find(a); b = Find(b);
                if (a != b) parent[b] = a;
            }

            for (var i = 0; i < renderers.Length; i++)
            {
                var bi = renderers[i].bounds;
                bi.Expand(JoinGap);
                for (var j = i + 1; j < renderers.Length; j++)
                {
                    if (bi.Intersects(renderers[j].bounds)) Union(i, j);
                }
            }

            var map = new Dictionary<int, Cluster>();
            for (var i = 0; i < renderers.Length; i++)
            {
                var key = Find(i);
                if (!map.TryGetValue(key, out var cluster))
                {
                    cluster = new Cluster { Bounds = renderers[i].bounds };
                    map[key] = cluster;
                }
                cluster.Members.Add(renderers[i]);
                cluster.Bounds.Encapsulate(renderers[i].bounds);
            }

            foreach (var cluster in map.Values)
            {
                cluster.FootprintArea = cluster.Bounds.size.x * cluster.Bounds.size.z;
                foreach (var r in cluster.Members)
                {
                    var filter = r.GetComponent<MeshFilter>();
                    if (filter != null && filter.sharedMesh != null) cluster.Triangles += filter.sharedMesh.triangles.Length / 3;
                }
                cluster.Kind = Classify(cluster);

                var floor = new Bounds(cluster.Bounds.center, Vector3.zero);
                foreach (var r in cluster.Members)
                {
                    if (!r.name.StartsWith("Floor", System.StringComparison.OrdinalIgnoreCase) &&
                        !r.name.StartsWith("Room", System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (cluster.HasFloor) floor.Encapsulate(r.bounds);
                    else { floor = r.bounds; cluster.HasFloor = true; }
                }
                cluster.Floor = cluster.HasFloor ? floor : cluster.Bounds;
            }
            return map.Values.OrderByDescending(c => c.FootprintArea).ToList();
        }

        /// <summary>Name a cluster by the object names inside it; the largest room wins ties.</summary>
        static string Classify(Cluster cluster)
        {
            var counts = new Dictionary<string, int>();
            foreach (var r in cluster.Members)
            {
                var lower = r.name.ToLowerInvariant();
                foreach (var (kind, tokens) in Kinds)
                {
                    if (!tokens.Any(t => lower.Contains(t))) continue;
                    counts[kind] = counts.TryGetValue(kind, out var c) ? c + 1 : 1;
                    break;
                }
            }
            if (counts.Count == 0) return "hall";
            return counts.OrderByDescending(kv => kv.Value).First().Key;
        }

        /// <summary>
        /// Slide each cluster into the flat. Rooms keep their own metres, so a 7 m bedroom stays 7 m;
        /// only the gap between rooms is rewritten. Returns the final bounds of the assembled flat.
        /// </summary>
        /// <summary>Where the bedroom landed after the last <see cref="Assemble"/>. The spawn point is
        /// picked from it, so it must survive the call rather than be recomputed by every caller.</summary>
        public static Bounds LastBedroom;

        public static Bounds Assemble(GameObject room, out string report)
        {
            var clusters = BuildClusters(room);
            var bedroom = Pick(clusters, "bedroom") ?? clusters.FirstOrDefault();

            // Lay the rooms out on one line along x, each against the previous one's floor edge.
            // Floors, not whole bounds: walls overhang the floor, and abutting bounds leaves a hole
            // exactly at the doorway, which reads as a black pit you fall into.
            var order = new[] { "bath", "bedroom", "hall", "kitchen" };
            var placed = new List<(Cluster cluster, Vector3 anchor)>();

            var bedroomFloor = bedroom != null ? bedroom.Floor : new Bounds(Vector3.zero, new Vector3(7f, 0f, 7f));
            var bedroomHalf = bedroomFloor.size.x * 0.5f;
            var cursor = -bedroomHalf;

            foreach (var kind in order)
            {
                var cluster = kind == "bedroom" ? bedroom : Pick(clusters, kind);
                if (cluster == null) continue;
                if (kind == "bedroom")
                {
                    placed.Add((cluster, new Vector3(cursor, 0f, 0f)));
                    cursor = -bedroomHalf + bedroomFloor.size.x;
                    continue;
                }
                placed.Add((cluster, new Vector3(cursor, 0f, 0f)));
                cursor += cluster.Floor.size.x;
            }
            // Bath goes to the left of the bedroom rather than after the kitchen.
            var bath = placed.FirstOrDefault(p => p.cluster.Kind == "bath");
            if (bath.cluster != null)
            {
                placed.RemoveAll(p => p.cluster.Kind == "bath");
                placed.Insert(0, (bath.cluster, new Vector3(-bedroomHalf - bath.cluster.Floor.size.x, 0f, 0f)));
            }

            var lines = new List<string>();
            var result = new Bounds(Vector3.zero, Vector3.zero);
            var first = true;
            foreach (var (cluster, anchor) in placed)
            {
                var delta = new Vector3(anchor.x - cluster.Floor.min.x, -cluster.Bounds.min.y, -cluster.Floor.center.z);
                foreach (var r in cluster.Members) r.transform.position += delta;
                var moved = cluster.Bounds;
                moved.center += delta;
                var floor = cluster.Floor;
                floor.center += delta;
                lines.Add($"[rooms] {cluster.Kind,-8} n={cluster.Members.Count,3} tris={cluster.Triangles,6} " +
                          $"size=({cluster.Bounds.size.x:F2},{cluster.Bounds.size.y:F2},{cluster.Bounds.size.z:F2}) " +
                          $"floor x {floor.min.x:F2}..{floor.max.x:F2} z {floor.min.z:F2}..{floor.max.z:F2}");
                if (ReferenceEquals(cluster, bedroom)) LastBedroom = moved;
                if (first) { result = moved; first = false; } else result.Encapsulate(moved);
            }

            var dropped = clusters.Where(c => !placed.Any(p => ReferenceEquals(p.cluster, c))).ToList();
            foreach (var c in dropped)
            {
                lines.Add($"[rooms] drop {c.Kind} n={c.Members.Count} size=({c.Bounds.size.x:F1},{c.Bounds.size.y:F1},{c.Bounds.size.z:F1})");
                foreach (var r in c.Members) r.enabled = false;
            }

            // Anything outside the assembled footprint is a stray from another wing; leaving it on
            // would stretch the scene bounds over empty space and wreck the camera framing.
            var strays = 0;
            var limit = result;
            limit.Expand(1.0f);
            foreach (var r in room.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled) continue;
                if (limit.Contains(r.bounds.center)) continue;
                r.enabled = false;
                strays++;
            }
            if (strays > 0) lines.Add($"[rooms] stray off {strays} renderers");

            report = string.Join("\n", lines);
            return result;
        }

        static Cluster Pick(List<Cluster> clusters, string kind) =>
            clusters.Where(c => c.Kind == kind).OrderByDescending(c => c.FootprintArea).FirstOrDefault();
    }
}
