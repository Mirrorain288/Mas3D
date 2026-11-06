using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Mas3D.Actions;
using Mas3D.Avatar;
using Mas3D.Conversation;
using Mas3D.Room;

namespace Mas3D.Tests
{
    /// <summary>
    /// Task 2 of docs/superpowers/plans/2026-11-05-unity-p7-voice-walking-body.md.
    ///
    /// Two kinds of proof live here:
    /// 1. the edge table against the real room colliders (RoomColliders.Attach, the same colliders the
    ///    player sweeps against), because a hand written graph is only worth anything if the segments
    ///    fit the furniture;
    /// 2. the walker's observable behaviour: when it refuses, how long it takes, where the hips and the
    ///    facing end up, what a mid-walk phase change does, and that stand/sit leaves exactly one body.
    ///
    /// The avatar has no walk clip (see the brief): the root translates and the existing idle keeps
    /// breathing. These tests therefore pin the contract the scene can honour, not an animation.
    /// </summary>
    public class AvatarWalkTests
    {
        const string RoomPath = "Assets/_Local/room/mita-room.glb";
        const float SweepRadius = 0.30f;
        const float SweepStep = 0.05f;
        const float TickSeconds = 1f / 60f;

        static readonly HashSet<string> Walkable = new HashSet<string>(new[] { "Mesh_0140out" });

        AvatarActor _actor;
        AvatarWalker _walker;
        TurnPhase _phase;

        GameObject _room;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            AvatarPrefabFixture.Require();
            _actor = AvatarActor.Spawn();
            Assert.IsNotNull(_actor, "Spawn 该建出 AvatarActor：" + AvatarRig.DefaultPrefabPath);
            Assert.IsTrue(_actor.Seat.Seated, "起手她该坐在 S1 上");

            // The idle is hand driven in tests: its Update would tick on wall-clock frames while the
            // walker is driven by Tick(1/60), and the two clocks would drift apart.
            _actor.Idle.enabled = false;
            _phase = TurnPhase.Idle;

            _walker = _actor.gameObject.AddComponent<AvatarWalker>();
            _walker.Actor = _actor;
            _walker.PhaseSource = () => _phase;
            _walker.DriveInUpdate = false;
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_room != null) Object.DestroyImmediate(_room);
            _room = null;
            RoomColliders.Forget();
            if (_actor != null) Object.Destroy(_actor.gameObject);
            _walker = null;
            _actor = null;
            yield return null;
        }

        float WalkToTheEnd(int maxTicks = 4000)
        {
            var ticks = 0;
            while (_walker.Walking && ticks < maxTicks)
            {
                _walker.Tick(TickSeconds);
                ticks++;
            }
            Assert.IsFalse(_walker.Walking, "走了 " + ticks + " tick 还没到，maxTicks=" + maxTicks);
            return ticks;
        }

        static float FlatError(Vector3 a, Vector3 b)
        {
            var dx = a.x - b.x;
            var dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        static float YawError(float actual, float expected)
        {
            var delta = Mathf.Repeat(actual - expected + 180f, 360f) - 180f;
            return Mathf.Abs(delta);
        }

        // ---------------------------------------------------------------- edge geometry

        [UnityTest]
        public IEnumerator EveryEdgeClearsTheRealRoom()
        {
            BuildRoom();
            yield return null;

            Assert.Greater(EdgeTable.Edges.Count, 5, "边表太小，扫掠会空转");
            var hits = new Collider[32];
            var checkedPoints = 0;

            foreach (var edge in EdgeTable.Edges)
            {
                var steps = Mathf.Max(1, Mathf.CeilToInt(edge.Length / SweepStep));
                var offenders = new List<string>();

                for (var i = 0; i <= steps; i++)
                {
                    var t = i / (float)steps;
                    var point = Vector3.Lerp(edge.From.Feet, edge.To.Feet, t);
                    // The brief's two sample heights, as the two ends of the sweep capsule: radius 0.30
                    // spans y 0.00-1.30, which is the player body the edges have to fit.
                    var bottom = new Vector3(point.x, 0.30f, point.z);
                    var top = new Vector3(point.x, 1.00f, point.z);
                    var count = Physics.OverlapCapsuleNonAlloc(bottom, top, SweepRadius, hits, ~0, QueryTriggerInteraction.Ignore);
                    checkedPoints++;

                    for (var k = 0; k < count; k++)
                    {
                        if (SkinnedRoomPart(hits[k])) continue;
                        var name = hits[k].gameObject.name;
                        if (!offenders.Contains(name)) offenders.Add(name);
                    }
                }

                Assert.AreEqual(0, offenders.Count,
                    "边 " + edge.From.Id + "-" + edge.To.Id + "（长 " + edge.Length.ToString("F2") +
                    " m）扫掠撞到 " + string.Join(", ", offenders.ToArray()) +
                    "；过不去就改边表，不许放宽断言");
            }

            Debug.Log("[walk] swept " + EdgeTable.Edges.Count + " edges at " + checkedPoints + " points, radius " +
                      SweepRadius.ToString("F2") + ", all clear");
        }

        /// <summary>
        /// Floor, rugs and our own body do not count as obstacles. The thin-part rule needs both halves:
        /// thin AND resting on the floor. Height alone also excused Mesh_0181out - 6 cm thick, floating
        /// at y = 0.93 - and a walker that ignores a chest-high shelf walks straight through it.
        /// </summary>
        bool SkinnedRoomPart(Collider collider)
        {
            if (collider == null) return true;
            if (_actor != null && collider.transform.IsChildOf(_actor.transform)) return true;
            if (Walkable.Contains(collider.gameObject.name)) return true;
            if (collider.transform.parent != null && Walkable.Contains(collider.transform.parent.name)) return true;

            var bounds = collider.bounds;
            return bounds.size.y <= 0.07f && bounds.min.y <= 0.07f;
        }

        void BuildRoom()
        {
#if UNITY_EDITOR
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(RoomPath);
            Assert.IsNotNull(prefab, "房间 prefab 不在：" + RoomPath);
            _room = Object.Instantiate(prefab);
#else
            Assert.Ignore("PlayMode 走位扫掠要用编辑器的 glTFast prefab。");
#endif
            _room.name = "walk-room-under-test";

            var bounds = ComputeBounds(_room);
            _room.transform.position += new Vector3(-bounds.center.x, -bounds.min.y, -bounds.center.z);
            Physics.SyncTransforms();

            var count = RoomColliders.Attach(_room);
            Physics.SyncTransforms();
            Assert.Greater(count, 20, "房间该有二十多个网格碰撞体，否则扫掠是空转");
            Debug.Log("[walk] room colliders=" + count);
        }

        static Bounds ComputeBounds(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            Assert.Greater(renderers.Length, 0, "房间没有 renderer");
            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        // ---------------------------------------------------------------- graph + walking

        [UnityTest]
        public IEnumerator EveryAnchorIsReachableFromSitStandWalk()
        {
            var anchors = new[] { "A1", "A2", "A3", "A4", "A5" };
            foreach (var id in anchors)
            {
                Assert.IsTrue(_walker.GoTo(id), "从 " + _walker.At + " 去 " + id + " 被拒：" + _walker.LastFaults);
                WalkToTheEnd();
                Assert.AreEqual(id, _walker.At, "该停在 " + id);
                Assert.Less(FlatError(_actor.Seat.HipsWorld, EdgeTable.Node(id).Feet), 0.05f,
                    id + " 的髋位落在 " + _actor.Seat.HipsWorld.ToString("F3") +
                    "，目标 " + EdgeTable.Node(id).Feet.ToString("F3"));
            }

            // The body of this case is frame-free on purpose: every walk is driven by Tick(1/60) so
            // the wall clock never enters the measurement. The yield only makes it a UnityTest.
            yield return null;
        }

        [UnityTest]
        public IEnumerator ThinkingRefusesANewTarget()
        {
            _phase = TurnPhase.Thinking;
            var seated = _actor.Seat.Seated;
            var before = _actor.transform.position;

            Assert.IsFalse(_walker.GoTo("A3"), "Thinking 时候她的嘴在动，腿不能动");
            Assert.IsFalse(_walker.Walking, "拒绝必须是真的没启动");
            Assert.AreEqual(seated, _actor.Seat.Seated, "拒绝不能把她从座上拽起来");
            Assert.Less(FlatError(before, _actor.transform.position), 1e-4f, "拒绝不能挪哪怕一点");

            for (var i = 0; i < 30; i++) _walker.Tick(TickSeconds);
            Assert.IsFalse(_walker.Walking, "拒绝之后 Tick 也不能偷偷走起来");

            _phase = TurnPhase.Idle;
            Assert.IsTrue(_walker.GoTo("A3"), "回到 Idle 就该能走：" + _walker.LastFaults);
            yield return null;
        }

        [UnityTest]
        public IEnumerator AnIdleAvatarWalksToTheAnchorFacingIt()
        {
            var path = EdgeTable.Path("S1", "A3");
            Assert.IsNotNull(path, "S1 到 A3 没有路");
            var expectedSeconds = EdgeTable.LengthOf(path) / AvatarWalker.Speed;

            Assert.IsTrue(_walker.GoTo("A3"), "Idle 时该能走：" + _walker.LastFaults);
            var ticks = WalkToTheEnd();
            var actualSeconds = ticks * TickSeconds;

            var hips = _actor.Seat.HipsWorld;
            var anchor = EdgeTable.Node("A3");
            Assert.Less(FlatError(hips, anchor.Feet), 0.05f,
                "髋位 xz 误差该 ≤ 0.05 m，实测 " + FlatError(hips, anchor.Feet).ToString("F3"));
            Assert.Less(YawError(_actor.transform.eulerAngles.y, anchor.FacingY), 5f,
                "到达朝向误差该 ≤ 5°，实测 " + YawError(_actor.transform.eulerAngles.y, anchor.FacingY).ToString("F2") +
                "（" + _actor.transform.eulerAngles.y.ToString("F1") + " vs " + anchor.FacingY.ToString("F1") + "）");
            Assert.LessOrEqual(Mathf.Abs(actualSeconds - expectedSeconds), expectedSeconds * 0.10f,
                "耗时 " + actualSeconds.ToString("F2") + " s，按 " + AvatarWalker.Speed.ToString("F2") +
                " m/s 该 " + expectedSeconds.ToString("F2") + " s，误差上限 10%");
            Assert.IsFalse(_actor.Seat.Seated, "走到 A3 是站着，不是坐着");
            Debug.Log("[walk] S1->A3 length=" + EdgeTable.LengthOf(path).ToString("F3") + " m, ticks=" + ticks +
                      ", actual=" + actualSeconds.ToString("F2") + " s, expected=" + expectedSeconds.ToString("F2") +
                      " s, hips error=" + FlatError(hips, anchor.Feet).ToString("F3") + " m, yaw error=" +
                      YawError(_actor.transform.eulerAngles.y, anchor.FacingY).ToString("F2"));
            yield return null;
        }

        [UnityTest]
        public IEnumerator AnsweredMidWalkStopsAtTheNextAnchor()
        {
            Assert.IsTrue(_walker.GoTo("A3"), _walker.LastFaults);

            var ticks = 0;
            while (_walker.Walking && _walker.Legs == 0 && ticks < 1200)
            {
                _walker.Tick(TickSeconds);
                ticks++;
            }
            Assert.AreEqual(1, _walker.Legs, "第一段该走完了");

            _phase = TurnPhase.Answered;
            while (_walker.Walking && ticks < 4000)
            {
                _walker.Tick(TickSeconds);
                ticks++;
            }

            Assert.IsFalse(_walker.Walking, "Answered 之后到段末必须停");
            Assert.AreEqual("W2", _walker.At, "只许走完当前段，不许接新目标（该停在 W2，实际 " + _walker.At + "）");
            Assert.IsFalse(_walker.GoTo("A5"), "说话中不许接新目标");
            Assert.Less(FlatError(_actor.transform.position, EdgeTable.Node("W2").Feet), 0.05f,
                "停的位置该是 W2 的脚底点");
            yield return null;
        }

        [UnityTest]
        public IEnumerator SittingStandingAndSittingAgainLeavesOneBody()
        {
            Assert.IsTrue(_walker.GoTo("A3"), _walker.LastFaults);
            WalkToTheEnd();

            Assert.IsFalse(_actor.Seat.Seated, "走到 A3 该站着");
            var standing = _actor.Body;
            Assert.IsNotNull(standing, "站着也得有胶囊，否则玩家从她身上穿过去");
            Assert.AreEqual(1, _actor.GetComponents<CapsuleCollider>().Length, "任何时刻只能有一根胶囊");
            Assert.AreEqual(AvatarActor.BodyRadius, standing.radius, 1e-3f, "站姿胶囊半径该是 BodyRadius");
            Assert.AreEqual(AvatarActor.BodyHeight, standing.height, 1e-3f, "站姿胶囊高该是 BodyHeight");
            var standingLift = _actor.transform.TransformPoint(standing.center) - _actor.Seat.HipsWorld;
            Assert.AreEqual(AvatarActor.BodyLift, standingLift.y, 0.03f,
                "站姿胶囊该按站姿髋高放，实测上抬 " + standingLift.y.ToString("F3"));

            Assert.IsTrue(_walker.GoTo("S1"), _walker.LastFaults);
            WalkToTheEnd();

            Assert.IsTrue(_actor.Seat.Seated, "回 S1 该坐下");
            var seated = _actor.Body;
            Assert.IsNotNull(seated, "坐下后该有胶囊");
            Assert.AreSame(standing, seated, "胶囊该复用同一根，不是新长一根");
            Assert.AreEqual(1, _actor.GetComponents<CapsuleCollider>().Length, "坐下后也只能有一根胶囊");
            var seatedLift = _actor.transform.TransformPoint(seated.center) - _actor.Seat.HipsWorld;
            Assert.AreEqual(AvatarActor.BodyLift, seatedLift.y, 0.03f,
                "坐下后胶囊该按座面重放，实测上抬 " + seatedLift.y.ToString("F3"));

            // The idle baseline must describe the pose she is in now: a standing-rest baseline would
            // erase the seated torso lean (+2 deg on the chest) within one breath.
            var chest = _actor.Rig.Bone(HumanBodyBones.Chest);
            Assert.IsNotNull(chest, "模型上没有 Chest 骨");
            var seatedChest = chest.localRotation;
            for (var i = 0; i < 45; i++) _actor.Idle.Tick(1f / 30f);
            var drift = Quaternion.Angle(seatedChest, chest.localRotation);
            Assert.Less(drift, 1.5f, "坐下后 idle 基线不是坐姿：胸口前倾被抹掉 " + drift.ToString("F2") + "°");
            yield return null;
        }

        /// <summary>
        /// S2 is the mattress. A chair sit lifts her hips to seat.y + HipsGap; the bed pose lifts them
        /// to topY + HipsGap (0.906 instead of 0.916) and comes with the bed table. The two rules are a
        /// centimetre apart, so the assertions below can tell them apart: the distance from the chair
        /// rule is exactly what proves which table ran.
        /// </summary>
        [UnityTest]
        public IEnumerator TheBedSeatUsesTheBedPoseNotTheChairPose()
        {
            var table = AnchorTable.Load();
            Assert.IsNotNull(table, "anchors.json 该在 StreamingAssets 里");
            var bed = table.Seat("S2");
            Assert.IsNotNull(bed, "S2 该是那张床");
            var bedRule = bed.topY + AvatarSeat.HipsGap;
            var chairRule = bed.y + AvatarSeat.HipsGap;
            Assert.Greater(Mathf.Abs(bedRule - chairRule), 0.005f,
                "床的 topY 与锚点 y 差得不够远，这条用例分辨不出两种姿势");

            Assert.IsTrue(_walker.GoTo("S2"), _walker.LastFaults);
            WalkToTheEnd();

            Assert.AreEqual("S2", _walker.At, "该停在 S2");
            Assert.IsTrue(_actor.Seat.Seated, "到了 S2 该坐下去");

            var hips = _actor.Seat.HipsWorld.y;
            Assert.AreEqual(bedRule, hips, 0.005f,
                "髋高该按床表 topY + HipsGap = " + bedRule.ToString("F3") + "，实测 " + hips.ToString("F3"));
            Assert.Greater(Mathf.Abs(hips - chairRule), 0.005f,
                "髋高 " + hips.ToString("F3") + " 离椅表 " + chairRule.ToString("F3") +
                " 太近：走的是椅子姿势，不是床上的姿势");

            // S2 has only A1 and A4, so getting up has to replan from the bed.
            Assert.IsTrue(_walker.GoTo("A1"), "从床上下来该能走去 A1：" + _walker.LastFaults);
            WalkToTheEnd();
            Assert.AreEqual("A1", _walker.At, "该走到 A1");
            Assert.IsFalse(_actor.Seat.Seated, "离开床之后该站着");
            Debug.Log("[walk] bed sit hips=" + hips.ToString("F3") + " bed rule=" + bedRule.ToString("F3") +
                      " chair rule=" + chairRule.ToString("F3"));
            yield return null;
        }

        [UnityTest]
        public IEnumerator ThePlayerBlocksHerWithoutBeingPushed()
        {
            var player = new GameObject("PlayerProbe");
            var controller = player.AddComponent<CharacterController>();
            controller.radius = 0.30f;
            controller.height = 1.70f;
            controller.center = new Vector3(0f, 0.85f, 0f);
            player.transform.position = EdgeTable.Node("A2").Feet;
            Physics.SyncTransforms();
            var playerStart = player.transform.position;

            try
            {
                Assert.IsTrue(_walker.GoTo("A3"), _walker.LastFaults);
                WalkToTheEnd(2000);

                Assert.IsTrue(_walker.Blocked, "该被玩家挡住并停下");
                Assert.IsFalse(_walker.Walking, "被挡住就不该继续走");
                Assert.Less(FlatError(playerStart, player.transform.position), 1e-4f,
                    "她撞到玩家时只能自己停，不能把玩家顶开");

                // She stopped part way along an edge, so the node she last passed is not where she is.
                // Claiming S1 would plan the next walk from a place she never stood, and the sweep
                // test only proves node-to-node lines.
                Assert.IsNull(_walker.At, "被挡在两条节点之间时 At 必须是空的");
                Assert.IsFalse(_walker.GoTo("A3"), "不知道她在图上的哪，就不许规划路线");
                StringAssert.Contains("不知道", _walker.LastFaults);

                // Her stand point is S1's node, so an explicit re-read hands her back to the graph.
                Assert.IsTrue(_walker.ResnapToNearestNode(), "她就停在 S1 旁边，该能吸附回一个节点");
                Assert.AreEqual("S1", _walker.At, "吸附后该认出她在 S1");
                Assert.IsTrue(_walker.GoTo("A3"), "吸附之后该能重新规划");
            }
            finally
            {
                Object.Destroy(player);
            }
            yield return null;
        }

        /// <summary>
        /// A stop in the middle of a long edge leaves her nowhere near a node. Resnapping must then say
        /// no instead of inventing the closest one: a route planned from a node 1.3 m away would start
        /// on a line the edge sweep never proved.
        /// </summary>
        [UnityTest]
        public IEnumerator AMidEdgeStopFarFromAnyNodeStaysOffGraph()
        {
            var a2 = EdgeTable.Node("A2").Feet;
            var a5 = EdgeTable.Node("A5").Feet;
            var along = a5 - a2;
            along.y = 0f;
            along.Normalize();
            var block = a2 + along * 1.90f;

            var player = new GameObject("PlayerProbeMidEdge");
            var controller = player.AddComponent<CharacterController>();
            controller.radius = 0.30f;
            controller.height = 1.70f;
            controller.center = new Vector3(0f, 0.85f, 0f);
            player.transform.position = new Vector3(block.x, 0.02f, block.z);
            Physics.SyncTransforms();

            try
            {
                Assert.IsTrue(_walker.GoTo("A5"), _walker.LastFaults);
                WalkToTheEnd(3000);

                Assert.IsTrue(_walker.Blocked, "该被玩家挡在 A2-A5 中间");
                Assert.IsNull(_walker.At, "半路被挡时 At 必须是空的");
                StringAssert.Contains("玩家", _walker.LastFaults);

                var stop = _actor.transform.position;
                Assert.Greater(FlatError(stop, a2), AvatarWalker.NodeWindow,
                    "停的位置该离 A2 比吸附窗口更远，实测 " + FlatError(stop, a2).ToString("F2") + " m");
                Assert.Greater(FlatError(stop, a5), AvatarWalker.NodeWindow,
                    "停的位置该离 A5 比吸附窗口更远，实测 " + FlatError(stop, a5).ToString("F2") + " m");

                Assert.IsFalse(_walker.ResnapToNearestNode(),
                    "两个节点都在 " + AvatarWalker.NodeWindow.ToString("F2") + " m 之外，不该硬塞一个最近的");
                Assert.IsNull(_walker.At, "吸不上就继续承认不知道");
                Assert.IsFalse(_walker.GoTo("A2"), "还是不知道她在哪，仍然不许规划路线");
                StringAssert.Contains("不知道", _walker.LastFaults);
            }
            finally
            {
                Object.Destroy(player);
            }
            yield return null;
        }

        // ---------------------------------------------------------------- seating, disabling, resnap guards

        /// <summary>
        /// ActionDirector sits her on the bed with a direct SitOnBed call, so the walker never saw a GoTo
        /// and never recorded "S2". AvatarActor.SeatId is written once in Spawn and still says S1, so
        /// trusting it would plan the way home from the bench: the seat under her root is the only thing
        /// that says where she actually is.
        /// </summary>
        [UnityTest]
        public IEnumerator ABedSitBySomeoneElseIsRecognisedAsTheBed()
        {
            Assert.IsTrue(_actor.SitOnBed(ActionDirector.BedSeatId), "外部直接把她按到床上该成功");
            Assert.IsTrue(_actor.Seat.Seated, "SitOnBed 之后该是坐着");
            Assert.AreEqual(AvatarActor.DefaultSeatId, _actor.SeatId,
                "SeatId 还停在 Spawn 时写下的 S1 —— 这正是不能拿它认座的原因");

            Assert.IsTrue(_walker.ResnapToNearestNode(), "她坐在床上，吸附该能认出这个座");
            Assert.AreEqual(ActionDirector.BedSeatId, _walker.At, "认座要看她实际压着哪个座，不是 Spawn 的 SeatId");

            var s1Stand = EdgeTable.Node("S1").Feet;
            var s2Stand = EdgeTable.Node("S2").Feet;
            Assert.IsTrue(_walker.GoTo("A1"), "从床上下来该能走去 A1：" + _walker.LastFaults);

            // GoTo stands her up and re-places the root before the first tick, so this frame already
            // shows which seat the route started from.
            var started = _actor.transform.position;
            Assert.Less(FlatError(started, s2Stand), 0.05f,
                "起手该站在 S2 的站立点（床脚 " + s2Stand.ToString("F3") + "），实测 " + started.ToString("F3"));
            Assert.Greater(FlatError(started, s1Stand), 1f,
                "起手位置不许落在 S1 附近：那是认错座之后把她从床上瞬移回长凳的地方");

            WalkToTheEnd();
            Assert.AreEqual("A1", _walker.At, "该走到 A1，实际停在 " + _walker.At);
            Assert.IsFalse(_actor.Seat.Seated, "离开床之后该站着");
            yield return null;
        }

        /// <summary>
        /// A seated body whose root is on neither seat is seated on nothing the graph knows. Naming a
        /// seat anyway (the Spawn SeatId, or the nearest one) routes her from a place she is not.
        /// </summary>
        [UnityTest]
        public IEnumerator ASeatSheIsNotActuallyOnLeavesHerOffTheGraph()
        {
            Assert.IsTrue(_actor.SitOnBed(ActionDirector.BedSeatId), "先把她按到床上");
            _actor.transform.position = new Vector3(6f, 0.9f, 6f);
            Physics.SyncTransforms();
            var parked = _actor.transform.position;
            var seated = _actor.Seat.Seated;

            Assert.IsFalse(_walker.ResnapToNearestNode(), "两个座都在 4 m 开外，认座该给不出答案");
            Assert.IsNull(_walker.At, "认不出来就承认不知道");
            Assert.IsFalse(_walker.GoTo("A1"), "不知道她在哪，不许规划路线");
            StringAssert.Contains("不知道", _walker.LastFaults);
            Assert.Less(FlatError(parked, _actor.transform.position), 1e-4f, "拒绝不能挪哪怕一点");
            Assert.AreEqual(seated, _actor.Seat.Seated, "拒绝也不能改她的坐/站状态");
            yield return null;
        }

        /// <summary>
        /// Disabling the walker mid-edge is a pause, not an arrival. The node she last finished is not
        /// where the object comes back, so keeping it would let the next GoTo plan a line the edge sweep
        /// never proved.
        /// </summary>
        [UnityTest]
        public IEnumerator ADisabledMidWalkComesBackOffTheGraph()
        {
            Assert.IsTrue(_walker.GoTo("A5"), _walker.LastFaults);
            for (var i = 0; i < 60; i++) _walker.Tick(TickSeconds);
            Assert.IsTrue(_walker.Walking, "跑满一秒该还走在 A2-A5 中间");
            Assert.IsNotNull(_walker.Target, "走位途中该有目标");
            var paused = _actor.transform.position;

            _walker.gameObject.SetActive(false);
            yield return null;
            _walker.gameObject.SetActive(true);
            yield return null;

            Assert.IsFalse(_walker.Walking, "关掉之后不许还声称在走");
            Assert.IsNull(_walker.At, "半路被关掉，At 必须空着");
            Assert.IsNull(_walker.Target, "半路被关掉，目标也该清掉");
            Assert.IsFalse(_walker.GoTo("A1"), "没重新认路之前不许规划路线");
            StringAssert.Contains("不知道", _walker.LastFaults);
            Assert.IsFalse(_walker.ResnapToNearestNode(), "半路离两个节点都超过 0.5 m，吸附也该说不");
            Assert.IsNull(_walker.At);
            Assert.Less(FlatError(paused, _actor.transform.position), 1e-4f, "关掉再开回来不能让她挪位置");
        }

        /// <summary>
        /// Resnapping only makes sense when she is standing still: mid-edge her root is on no node, so
        /// believing the nearest one would turn a swept, walked line into a straight line nobody proved.
        /// </summary>
        [UnityTest]
        public IEnumerator ResnapDuringAWalkIsRefusedAndChangesNothing()
        {
            Assert.IsTrue(_walker.GoTo("A5"), _walker.LastFaults);
            for (var i = 0; i < 60; i++) _walker.Tick(TickSeconds);

            Assert.IsTrue(_walker.Walking, "该还在走");
            var at = _walker.At;
            var target = _walker.Target;
            var here = _actor.transform.position;

            Assert.IsFalse(_walker.ResnapToNearestNode(), "走位途中不许吸附");
            Assert.AreEqual(at, _walker.At, "拒绝吸附不能改 At");
            Assert.AreEqual(target, _walker.Target, "拒绝吸附不能改 Target");
            Assert.IsTrue(_walker.Walking, "拒绝吸附不能把她停下来");
            Assert.Less(FlatError(here, _actor.transform.position), 1e-4f, "拒绝吸附不能挪位置");

            WalkToTheEnd();
            Assert.AreEqual("A5", _walker.At, "拒绝吸附之后这条路线还得走完");
            yield return null;
        }

        /// <summary>
        /// The thin-part exemption is for things lying on the floor (slab, rug), not for anything 7 cm
        /// thick: Mesh_0181out is 6 cm tall and floats at y = 0.93, and a walker that ignores it walks
        /// straight through a chest-high shelf.
        /// </summary>
        [UnityTest]
        public IEnumerator AThinPartThatFloatsIsNotExemptFromTheSweep()
        {
            BuildRoom();
            yield return null;

            Collider shelf = null;
            Collider floor = null;
            foreach (var candidate in _room.GetComponentsInChildren<Collider>(true))
            {
                if (candidate.gameObject.name == "Mesh_0181out") shelf = candidate;
                if (floor == null && candidate.bounds.size.y <= 0.07f && candidate.bounds.min.y <= 0.07f)
                {
                    floor = candidate;
                }
            }

            Assert.IsNotNull(shelf, "房间里该有那块悬空的矮件 Mesh_0181out");
            Assert.Less(shelf.bounds.size.y, 0.07f, "它必须真的薄，否则证明不了旧规则的口子");
            Assert.Greater(shelf.bounds.min.y, 0.07f, "它必须真的悬空，否则豁免它本来就是对的");
            Assert.IsFalse(SkinnedRoomPart(shelf), "薄但悬空的件不是地面，不许豁免");

            Assert.IsNotNull(floor, "房间里该有贴地的薄件（地板或地毯）");
            Assert.IsTrue(SkinnedRoomPart(floor), "贴地的薄件才是豁免对象");

            Debug.Log("[walk] thin part rule: shelf minY=" + shelf.bounds.min.y.ToString("F3") +
                      " h=" + shelf.bounds.size.y.ToString("F3") +
                      " floor minY=" + floor.bounds.min.y.ToString("F3") +
                      " h=" + floor.bounds.size.y.ToString("F3"));
        }

        /// <summary>
        /// She can walk to a seat and still fail to sit (a seat missing from anchors.json, a bed that
        /// will not take the pose). Leaving Arrived true with Seated false then makes the next route
        /// start from a seat she is not on, so the arrival has to be withdrawn instead. The seat
        /// judgement is injected the way PhaseSource is: EdgeTable is the only source of seat nodes and
        /// this round does not touch it.
        /// </summary>
        [UnityTest]
        public IEnumerator ASeatThatRefusesLeavesHerOffTheGraph()
        {
            _walker.SeatAttempt = seatId => false;

            Assert.IsTrue(_walker.GoTo("S2"), "路线本身是通的：" + _walker.LastFaults);
            WalkToTheEnd(3000);

            Assert.IsFalse(_walker.Walking, "走完了就是走完了，坐不下不等于还在走");
            Assert.IsFalse(_walker.Arrived, "没坐下来就不算到了");
            Assert.IsNull(_walker.At, "姿态不对就不能声称停在 S2");
            StringAssert.Contains("坐不下去", _walker.LastFaults);
            Assert.IsFalse(_actor.Seat.Seated, "她确实没坐上");

            var stood = _actor.transform.position;
            Assert.IsFalse(_walker.GoTo("A1"), "位置算不知道，就先别规划");
            StringAssert.Contains("不知道", _walker.LastFaults);
            Assert.Less(FlatError(stood, _actor.transform.position), 1e-4f, "拒绝不能挪哪怕一点");
            yield return null;
        }
    }
}
