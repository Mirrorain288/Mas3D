using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Mas3D.Room;

namespace Mas3D.Tests
{
    /// <summary>
    /// Task 2 of docs/superpowers/plans/2026-11-05-unity-p7-voice-walking-body.md: the walk graph is
    /// hand written data, so its truth (every anchor reachable, both seats have a way in, facings come
    /// from anchors.json, seat nodes stand on the floor just off the seat) is pure enough to pin in EditMode.
    /// The PlayMode sweep proves the geometry; this file proves the shape.
    /// </summary>
    public class EdgeTableTests
    {
        static readonly string[] Anchors = { "A1", "A2", "A3", "A4", "A5" };
        static readonly string[] Seats = { "S1", "S2" };

        [Test]
        public void EveryAnchorAndSeatIsANode()
        {
            var table = AnchorTable.Load();
            Assert.IsNotNull(table, "anchors.json 读不到，边表无处可依");

            foreach (var id in Anchors)
            {
                var node = EdgeTable.Node(id);
                Assert.IsNotNull(node, "锚点 " + id + " 必须是走位图里的节点");
                Assert.IsNull(node.SeatId, "锚点 " + id + " 不是座");
                Assert.AreEqual(0.02f, node.Feet.y, 1e-3f, "锚点脚底该贴地");
            }

            foreach (var id in Seats)
            {
                var node = EdgeTable.Node(id);
                Assert.IsNotNull(node, "座 " + id + " 必须是走位图里的节点");
                Assert.AreEqual(id, node.SeatId, "座节点必须记住自己对应哪张座");
            }
        }

        [Test]
        public void FacingsComeFromTheAnchorTable()
        {
            var table = AnchorTable.Load();
            foreach (var id in Anchors)
            {
                var anchor = table.Anchor(id);
                Assert.IsNotNull(anchor, "anchors.json 缺锚点 " + id);
                Assert.AreEqual(anchor.facingY, EdgeTable.Node(id).FacingY, 1e-3f,
                    "锚点 " + id + " 的到达朝向必须照抄 anchors.json");
            }

            foreach (var id in Seats)
            {
                var seat = table.Seat(id);
                Assert.IsNotNull(seat, "anchors.json 缺座 " + id);
                Assert.AreEqual(seat.facingY, EdgeTable.Node(id).FacingY, 1e-3f,
                    "座 " + id + " 站起来的朝向必须照抄 anchors.json");
            }
        }

        [Test]
        public void SeatNodesStandOnTheFloorOffTheSeat()
        {
            var table = AnchorTable.Load();
            foreach (var id in Seats)
            {
                var seat = table.Seat(id);
                var node = EdgeTable.Node(id);
                var forward = Quaternion.Euler(0f, seat.facingY, 0f) * Vector3.forward;
                var offset = new Vector3(node.Feet.x - seat.x, 0f, node.Feet.z - seat.z);
                var along = Vector3.Dot(offset, forward);
                var sideways = offset - forward * along;

                Assert.AreEqual(0.02f, node.Feet.y, 1e-3f, "座 " + id + " 的站立点该在地面高度");
                Assert.Less(sideways.magnitude, 1e-3f,
                    "座 " + id + " 的站立点该落在座的正前或正后方，不能偏到侧边");
                Assert.GreaterOrEqual(Mathf.Abs(along), EdgeTable.SeatStandGap - 1e-3f,
                    "座 " + id + " 的站立点至少离座 " + EdgeTable.SeatStandGap.ToString("F2") + " m");
            }

            // The bench faces the aisle: one step forward is floor. The bed's facing runs along the
            // mattress, so it is the one seat she has to leave backwards, off the foot of the bed.
            Assert.AreEqual(EdgeTable.SeatStandGap, Along("S1"), 1e-3f, "长凳前方就是过道，该往前一步起身");
            Assert.Less(Along("S2"), 0f, "S2 是床，站立点在床尾一侧；往前一步会站在床面上");
        }

        static float Along(string seatId)
        {
            var seat = AnchorTable.Load().Seat(seatId);
            var node = EdgeTable.Node(seatId);
            var forward = Quaternion.Euler(0f, seat.facingY, 0f) * Vector3.forward;
            return Vector3.Dot(new Vector3(node.Feet.x - seat.x, 0f, node.Feet.z - seat.z), forward);
        }

        [Test]
        public void EveryAnchorIsReachableFromEverySeat()
        {
            foreach (var seat in Seats)
            {
                foreach (var anchor in Anchors)
                {
                    var path = EdgeTable.Path(seat, anchor);
                    Assert.IsNotNull(path, seat + " 走到 " + anchor + " 没有路");
                    Assert.AreSame(EdgeTable.Node(seat), path[0], seat + " 的路径必须从座节点开始");
                    Assert.AreSame(EdgeTable.Node(anchor), path[path.Count - 1],
                        seat + " 到 " + anchor + " 的路径必须在目标锚点结束");
                }
            }
        }

        [Test]
        public void BothSeatsHaveAnApproach()
        {
            foreach (var seat in Seats)
            {
                Assert.Greater(EdgeTable.Neighbours(seat).Count, 0, "座 " + seat + " 没有进路，她坐上去就下不来");
            }

            var between = EdgeTable.Path("S1", "S2");
            Assert.IsNotNull(between, "两张座之间必须能走");
            Assert.Greater(EdgeTable.LengthOf(between), 0f, "两张座之间的路不能是零长");
        }

        [Test]
        public void EveryEdgeJoinsRealNodesAndAppearsTwiceOnePerEnd()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in EdgeTable.Edges)
            {
                Assert.IsNotNull(edge.From, "边的起点不能为空");
                Assert.IsNotNull(edge.To, "边的终点不能为空");
                Assert.AreNotSame(edge.From, edge.To, "边不能自己连自己");
                Assert.AreSame(edge.From, EdgeTable.Node(edge.From.Id), "边的起点必须是图里的节点实例");
                Assert.AreSame(edge.To, EdgeTable.Node(edge.To.Id), "边的终点必须是图里的节点实例");

                var key = string.CompareOrdinal(edge.From.Id, edge.To.Id) < 0
                    ? edge.From.Id + "-" + edge.To.Id
                    : edge.To.Id + "-" + edge.From.Id;
                Assert.IsTrue(seen.Add(key), "重复的边：" + key);
                Assert.Greater(edge.Length, 0.01f, "边 " + key + " 太短，没有意义");
            }

            foreach (var edge in EdgeTable.Edges)
            {
                var forward = EdgeTable.Neighbours(edge.From.Id).Exists(e => e.To == edge.To && e.From == edge.From);
                var backward = EdgeTable.Neighbours(edge.To.Id).Exists(e => e.To == edge.From && e.From == edge.To);
                Assert.IsTrue(forward && backward, "边 " + edge.From.Id + "-" + edge.To.Id + " 必须双向可走");
            }
        }

        [Test]
        public void PathsOnlyWalkEdgesAndTheirLengthAddsUp()
        {
            var path = EdgeTable.Path("S1", "A3");
            Assert.IsNotNull(path, "S1 到 A3 没有路");
            Assert.GreaterOrEqual(path.Count, 2, "跨节点的路至少有两个节点");

            var sum = 0f;
            for (var i = 0; i + 1 < path.Count; i++)
            {
                var a = path[i];
                var b = path[i + 1];
                var edge = EdgeTable.Neighbours(a.Id).Find(e => e.To == b);
                Assert.IsNotNull(edge, "路径里 " + a.Id + "->" + b.Id + " 不是一条边");
                sum += edge.Length;
            }

            Assert.AreEqual(sum, EdgeTable.LengthOf(path), 1e-4f, "路径长度必须等于各段之和");
            Debug.Log("[walk] S1->A3 length=" + sum.ToString("F3") + " m, nodes=" + path.Count +
                      ", graph total=" + EdgeTable.TotalLength.ToString("F3") + " m, edges=" + EdgeTable.Edges.Count);
        }
    }
}
