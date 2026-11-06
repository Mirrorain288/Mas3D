using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mas3D.Room
{
    /// <summary>
    /// One standing spot the walk graph knows about: an anchor from anchors.json, a seat's standing
    /// point, or a hand placed corner. `Feet` is where the avatar root goes, so a walker can compare
    /// two nodes without knowing what kind of thing they are.
    /// </summary>
    public sealed class WalkNode
    {
        internal WalkNode(string id, Vector3 feet, float facingY, string seatId)
        {
            Id = id;
            Feet = feet;
            FacingY = facingY;
            SeatId = seatId;
        }

        public string Id { get; private set; }

        /// <summary>Root position when she stands here; y is the floor the root sits on.</summary>
        public Vector3 Feet { get; private set; }

        /// <summary>Facing she should end up with when this node is the destination.</summary>
        public float FacingY { get; private set; }

        /// <summary>The seat this node stands in front of, or null for a plain standing spot.</summary>
        public string SeatId { get; private set; }

        public bool IsSeat { get { return !string.IsNullOrEmpty(SeatId); } }

        public override string ToString()
        {
            return Id + "(" + Feet.x.ToString("F2") + ", " + Feet.z.ToString("F2") + ")";
        }
    }

    /// <summary>One straight segment between two nodes. Length is flat distance: she walks on the floor.</summary>
    public sealed class WalkEdge
    {
        internal WalkEdge(WalkNode from, WalkNode to)
        {
            From = from;
            To = to;
            var delta = to.Feet - from.Feet;
            delta.y = 0f;
            Length = delta.magnitude;
        }

        public WalkNode From { get; private set; }
        public WalkNode To { get; private set; }
        public float Length { get; private set; }

        public override string ToString()
        {
            return From.Id + "-" + To.Id + "(" + Length.ToString("F2") + " m)";
        }
    }

    /// <summary>
    /// The walk graph: nodes are the places she is allowed to stand, edges are the straight runs
    /// between them. Hand written on purpose — the room has five anchors and two seats, so a table
    /// that a human can read beats a NavMesh build step (spec section 8.2 rules out NavMesh anyway).
    ///
    /// The bed splits the room in two: the only floor between the halves is the strip left of it
    /// (x &lt; 0.353), and the strip right of it is 0.077 m wide. A straight A2-A3 edge would sweep
    /// through the bed, so the graph goes around the bed's two left corners (W1, W2) instead of
    /// pretending she can walk over it. Every edge is proven against the real room colliders in
    /// AvatarWalkTests; if one of them stops fitting, the table changes, the assertion does not.
    /// </summary>
    public static class EdgeTable
    {
        /// <summary>
        /// The shortest step off a seat. 0.70 clears the bench (its front face is at z = 0.566 for
        /// S1) and lands inside the aisle the player already walks through. A seat that faces
        /// something other than floor needs more than this - see <see cref="SeatStands"/>.
        /// </summary>
        public const float SeatStandGap = 0.70f;

        /// <summary>
        /// Where her feet land when she gets up, as a signed distance along the seat's facing.
        /// S1 is the bench: it faces the aisle, so one step forward is floor. S2 is the bed: its
        /// facing runs along the mattress, and 0.70 forward is still mattress (the bed spans
        /// z = -0.981 .. 0.931, top 0.906), so she cannot stand in front of it at all - she steps
        /// off the foot of the bed instead. That puts her 0.344 m south of the bed, just past the
        /// 0.30 sweep radius AvatarWalkTests uses; that clearance is why the number is 1.30.
        /// </summary>
        static readonly Dictionary<string, float> SeatStands =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                { "S1", SeatStandGap },
                { "S2", -1.30f },
            };

        /// <summary>The floor the avatar root stands on. Everything in this room is 0.02 above the rug.</summary>
        public const float FeetHeight = 0.02f;

        static readonly string[] AnchorIds = { "A1", "A2", "A3", "A4", "A5" };
        static readonly string[] SeatIds = { "S1", "S2" };

        /// <summary>The two corners around the bed. Both pairs are edges, not nodes she can be sent to by name.</summary>
        static readonly Vector3 CornerW1 = new Vector3(-0.40f, FeetHeight, -1.40f);
        static readonly Vector3 CornerW2 = new Vector3(-0.55f, FeetHeight, 1.25f);

        /// <summary>
        /// Undirected connectivity. A4 and S2 hang off A1 because the wardrobe and the mirror between
        /// them leave no corridor of their own; that is also why S2 has exactly one way in.
        /// </summary>
        static readonly string[,] Links =
        {
            { "S1", "A2" },
            { "A2", "W2" },
            { "W2", "A3" },
            { "A2", "A5" },
            { "A5", "A3" },
            { "A2", "W1" },
            { "W1", "A1" },
            { "A1", "A4" },
            { "A1", "S2" },
            { "S2", "A4" },
        };

        static List<WalkNode> _nodes;
        static List<WalkEdge> _edges;
        static Dictionary<string, WalkNode> _byId;

        public static IReadOnlyList<WalkNode> Nodes
        {
            get
            {
                Build();
                return _nodes;
            }
        }

        /// <summary>One entry per undirected link; <see cref="Neighbours"/> makes them directional.</summary>
        public static IReadOnlyList<WalkEdge> Edges
        {
            get
            {
                Build();
                return _edges;
            }
        }

        /// <summary>Sum of every undirected edge; the size of the graph in metres.</summary>
        public static float TotalLength
        {
            get
            {
                Build();
                var sum = 0f;
                foreach (var edge in _edges) sum += edge.Length;
                return sum;
            }
        }

        /// <summary>The same instance for the same id, so callers can compare nodes by reference.</summary>
        public static WalkNode Node(string id)
        {
            Build();
            if (string.IsNullOrEmpty(id)) return null;
            WalkNode node;
            return _byId.TryGetValue(id, out node) ? node : null;
        }

        /// <summary>Every edge leaving <paramref name="id"/>, rewritten so From is that node.</summary>
        public static List<WalkEdge> Neighbours(string id)
        {
            Build();
            var result = new List<WalkEdge>();
            var node = Node(id);
            if (node == null) return result;

            foreach (var edge in _edges)
            {
                if (edge.From == node) result.Add(new WalkEdge(node, edge.To));
                else if (edge.To == node) result.Add(new WalkEdge(node, edge.From));
            }

            return result;
        }

        /// <summary>
        /// Shortest node list from one place to another, both ends included. Null when the graph has
        /// no path; a single node when she is already there. Dijkstra rather than A*: nine nodes.
        /// </summary>
        public static List<WalkNode> Path(string fromId, string toId)
        {
            var from = Node(fromId);
            var to = Node(toId);
            if (from == null || to == null) return null;
            if (from == to) return new List<WalkNode> { from };

            var distance = new Dictionary<WalkNode, float>();
            var previous = new Dictionary<WalkNode, WalkNode>();
            var open = new List<WalkNode>(_nodes);
            foreach (var node in _nodes) distance[node] = float.PositiveInfinity;
            distance[from] = 0f;

            while (open.Count > 0)
            {
                WalkNode best = null;
                var bestDistance = float.PositiveInfinity;
                foreach (var candidate in open)
                {
                    var value = distance[candidate];
                    if (value >= bestDistance) continue;
                    best = candidate;
                    bestDistance = value;
                }

                if (best == null) break;
                open.Remove(best);
                if (best == to) break;

                foreach (var edge in Neighbours(best.Id))
                {
                    if (!open.Contains(edge.To)) continue;
                    var candidate = bestDistance + edge.Length;
                    if (candidate >= distance[edge.To]) continue;
                    distance[edge.To] = candidate;
                    previous[edge.To] = best;
                }
            }

            if (float.IsInfinity(distance[to])) return null;

            var path = new List<WalkNode>();
            WalkNode current = to;
            while (current != null)
            {
                path.Add(current);
                if (current == from) break;
                WalkNode step;
                current = previous.TryGetValue(current, out step) ? step : null;
            }

            if (path.Count == 0 || path[path.Count - 1] != from) return null;
            path.Reverse();
            return path;
        }

        /// <summary>Walked length of a node list; segments that are not edges contribute nothing.</summary>
        public static float LengthOf(IList<WalkNode> path)
        {
            if (path == null) return 0f;
            var sum = 0f;
            for (var i = 0; i + 1 < path.Count; i++)
            {
                foreach (var edge in Neighbours(path[i].Id))
                {
                    if (edge.To != path[i + 1]) continue;
                    sum += edge.Length;
                    break;
                }
            }

            return sum;
        }

        static void Build()
        {
            if (_nodes != null) return;

            var nodes = new List<WalkNode>();
            var byId = new Dictionary<string, WalkNode>(StringComparer.OrdinalIgnoreCase);

            var table = AnchorTable.Load();
            if (table == null)
            {
                Debug.LogError("[walk] anchors.json 读不到，走位图只剩绕床拐点");
            }
            else
            {
                foreach (var id in AnchorIds)
                {
                    var anchor = table.Anchor(id);
                    if (anchor == null)
                    {
                        Debug.LogError("[walk] anchors.json 里没有锚点 " + id);
                        continue;
                    }

                    Add(nodes, byId, new WalkNode(anchor.id, new Vector3(anchor.x, FeetHeight, anchor.z), anchor.facingY, null));
                }

                foreach (var id in SeatIds)
                {
                    var seat = table.Seat(id);
                    if (seat == null)
                    {
                        Debug.LogError("[walk] anchors.json 里没有座 " + id);
                        continue;
                    }

                    // How far she steps off a seat, and which way, is per seat: the bench faces the
                    // aisle, the bed's facing runs along the mattress (see SeatStands). Standing on
                    // the mattress with the floor height under her is the one thing we must not do.
                    float along;
                    if (!SeatStands.TryGetValue(seat.id, out along))
                    {
                        Debug.LogError("[walk] 座 " + seat.id + " 没有站立点，边表会缺这个节点");
                        continue;
                    }

                    var forward = Quaternion.Euler(0f, seat.facingY, 0f) * Vector3.forward;
                    var feet = new Vector3(seat.x, FeetHeight, seat.z) + forward * along;
                    Add(nodes, byId, new WalkNode(seat.id, feet, seat.facingY, seat.id));
                }
            }

            Add(nodes, byId, new WalkNode("W1", CornerW1, 0f, null));
            Add(nodes, byId, new WalkNode("W2", CornerW2, 0f, null));

            var edges = new List<WalkEdge>();
            for (var i = 0; i < Links.GetLength(0); i++)
            {
                WalkNode from;
                WalkNode to;
                if (!byId.TryGetValue(Links[i, 0], out from) || !byId.TryGetValue(Links[i, 1], out to))
                {
                    Debug.LogError("[walk] 边 " + Links[i, 0] + "-" + Links[i, 1] + " 指向不存在的节点");
                    continue;
                }

                edges.Add(new WalkEdge(from, to));
            }

            _nodes = nodes;
            _byId = byId;
            _edges = edges;
        }

        static void Add(List<WalkNode> nodes, Dictionary<string, WalkNode> byId, WalkNode node)
        {
            if (byId.ContainsKey(node.Id))
            {
                Debug.LogError("[walk] 节点 " + node.Id + " 重复定义");
                return;
            }

            nodes.Add(node);
            byId[node.Id] = node;
        }
    }
}
