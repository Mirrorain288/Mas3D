using System;
using System.Collections;
using System.Collections.Generic;
using Mas3D.Avatar;
using Mas3D.Conversation;
using Mas3D.Room;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mas3D.Tests
{
    public class FlatNavigationTests
    {
        const string FlatPrefabPath = "Assets/_Local/room/Flat.prefab";
        const float SweepRadius = 0.30f;
        const float SweepStep = 0.05f;
        const float TickSeconds = 1f / 60f;

        GameObject _flat;
        AvatarActor _actor;
        AvatarWalker _walker;
        TurnPhase _phase;
        bool _prevBackfaces;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _prevBackfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;

            AnchorTable.SetActiveRelativePath(AnchorTable.FlatRelativePath);
            EdgeTable.Rebuild();

#if UNITY_EDITOR
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(FlatPrefabPath);
            Assert.IsNotNull(prefab, "缺少 Flat.prefab: " + FlatPrefabPath);
            _flat = UnityEngine.Object.Instantiate(prefab);
#else
            Assert.Ignore("FlatNavigationTests 需要在 Editor 下实例化 Flat.prefab");
#endif
            _flat.name = "Flat-NavTest";
            _flat.transform.position = Vector3.zero;
            _flat.SetActive(true);
            Physics.SyncTransforms();

            AvatarPrefabFixture.Require();
            _actor = AvatarActor.Spawn();
            Assert.IsNotNull(_actor);
            _actor.Idle.enabled = false;
            _phase = TurnPhase.Idle;

            _walker = _actor.gameObject.AddComponent<AvatarWalker>();
            _walker.Actor = _actor;
            _walker.DoorLookupRoot = _flat.transform;
            _walker.PhaseSource = () => _phase;
            _walker.DriveInUpdate = false;
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Physics.queriesHitBackfaces = _prevBackfaces;
            if (_actor != null) UnityEngine.Object.DestroyImmediate(_actor.gameObject);
            if (_flat != null) UnityEngine.Object.DestroyImmediate(_flat);
            _walker = null;
            _actor = null;
            _flat = null;
            AnchorTable.ResetActive();
            EdgeTable.Rebuild();
            yield return null;
        }

        InteractiveDoor FindDoor(string doorId)
        {
            var doors = InteractiveDoor.FindActive(_flat);
            for (var i = 0; i < doors.Length; i++)
            {
                if (string.Equals(doors[i].DoorId, doorId, StringComparison.Ordinal)) return doors[i];
            }
            Assert.Fail("找不到门: " + doorId);
            return null;
        }

        static bool IsFlatNonObstacle(Collider col, Transform selfRoot)
        {
            if (col == null || !col.enabled) return true;
            if (selfRoot != null && col.transform.IsChildOf(selfRoot)) return true;
            if (string.Equals(col.gameObject.name, "LivingCeilingSeal", StringComparison.Ordinal)) return true;
            var b = col.bounds;
            if (b.max.y <= 0.06f) return true;
            if (b.min.y >= 1.85f) return true;
            return false;
        }

        [UnityTest]
        public IEnumerator EveryFlatEdgeClearsRealCollidersWithThirtyCmCapsule()
        {
            var doorBL = FindDoor("Door_BedroomLiving");
            var doorLK = FindDoor("Door_LivingKitchen");
            doorBL.SetOpen(true, true);
            doorLK.SetOpen(true, true);
            Physics.SyncTransforms();
            yield return null;

            Assert.GreaterOrEqual(EdgeTable.Edges.Count, 15, "平层走位边太少");
            var hits = new Collider[32];
            var checkedPoints = 0;

            foreach (var edge in EdgeTable.Edges)
            {
                var steps = Mathf.Max(1, Mathf.CeilToInt(edge.Length / SweepStep));
                var offenders = new List<string>();
                for (var i = 0; i <= steps; i++)
                {
                    var point = Vector3.Lerp(edge.From.Feet, edge.To.Feet, i / (float)steps);
                    var bottom = new Vector3(point.x, 0.40f, point.z);
                    var top = new Vector3(point.x, 1.40f, point.z);
                    var count = Physics.OverlapCapsuleNonAlloc(bottom, top, SweepRadius, hits, ~0, QueryTriggerInteraction.Ignore);
                    checkedPoints++;

                    for (var k = 0; k < count; k++)
                    {
                        if (IsFlatNonObstacle(hits[k], _actor.transform)) continue;
                        var label = (hits[k].transform.parent != null ? hits[k].transform.parent.name + "/" : "") + hits[k].gameObject.name;
                        if (!offenders.Contains(label)) offenders.Add(label);
                    }
                }

                Assert.AreEqual(0, offenders.Count,
                    $"边 {edge.From.Id}-{edge.To.Id} 扫掠撞到 {string.Join(", ", offenders)}");
            }

            Debug.Log($"[flat-walk] swept {EdgeTable.Edges.Count} edges at {checkedPoints} points cleanly");
        }

        [UnityTest]
        public IEnumerator WalksThroughClosedBedroomDoorUsingHandleTwoBoneIk()
        {
            var doorBL = FindDoor("Door_BedroomLiving");
            doorBL.SetOpen(false, true);
            Physics.SyncTransforms();

            var handlePos = doorBL.HandleWorldPosition();
            var lHand = _actor.Rig.Bone(HumanBodyBones.LeftHand);
            var rHand = _actor.Rig.Bone(HumanBodyBones.RightHand);
            Assert.IsNotNull(lHand);
            Assert.IsNotNull(rHand);

            Assert.IsTrue(_walker.GoTo("A4"), "S1 到卧室阅读区 A4 应规划成功: " + _walker.LastFaults);

            bool sawDoorOpeningState = false;
            float bestHandleProximity = float.PositiveInfinity;
            Vector3 pausedRootPos = Vector3.zero;
            var pausedMoveAccumulator = 0f;

            for (var i = 0; i < 4000 && _walker.Walking; i++)
            {
                var before = _actor.transform.position;
                _walker.Tick(TickSeconds);
                if (doorBL.IsMoving) doorBL.Tick(TickSeconds, new Bounds());

                if (_walker.OpeningDoor)
                {
                    if (!sawDoorOpeningState)
                    {
                        sawDoorOpeningState = true;
                        pausedRootPos = before;
                    }
                    else
                    {
                        pausedMoveAccumulator += Vector3.Distance(before, _actor.transform.position);
                    }

                    Assert.AreSame(doorBL, _walker.ActiveDoor);
                    var nearestHand = Mathf.Min(
                        Vector3.Distance(lHand.position, handlePos),
                        Vector3.Distance(rHand.position, handlePos));
                    if (nearestHand < bestHandleProximity) bestHandleProximity = nearestHand;
                }
            }

            Assert.IsTrue(sawDoorOpeningState, "经过闭合的 Door_BedroomLiving 时必须进入 OpeningDoor 状态并推门");
            Assert.Less(pausedMoveAccumulator, 0.08f, "推门期间根位置应基本停住等候门扇摆开");
            Assert.Less(bestHandleProximity, 0.75f, "推门期间单臂末端应朝门把手位置伸展: best=" + bestHandleProximity.ToString("F3"));
            Assert.IsFalse(_walker.Walking);
            Assert.IsFalse(_walker.OpeningDoor);
            Assert.AreEqual("A4", _walker.At);
            Assert.IsTrue(doorBL.IsOpen, "Door_BedroomLiving 完成后必须处于打开状态");
            yield return null;
        }

        [UnityTest]
        public IEnumerator CrossesAlreadyOpenKitchenDoorWithoutPausingToOpen()
        {
            var doorLK = FindDoor("Door_LivingKitchen");
            doorLK.SetOpen(true, true);
            Physics.SyncTransforms();

            Assert.IsTrue(_walker.GoTo("A1"), _walker.LastFaults);
            for (var i = 0; i < 3000 && _walker.Walking; i++) _walker.Tick(TickSeconds);
            Assert.AreEqual("A1", _walker.At);

            Assert.IsTrue(_walker.GoTo("A6"), _walker.LastFaults);
            bool everEnteredOpening = false;
            for (var i = 0; i < 4000 && _walker.Walking; i++)
            {
                _walker.Tick(TickSeconds);
                if (_walker.OpeningDoor) everEnteredOpening = true;
            }

            Assert.IsFalse(everEnteredOpening, "已敞开的餐厨门不应重复进入推门停顿状态");
            Assert.IsFalse(_walker.Walking);
            Assert.AreEqual("A6", _walker.At);
            yield return null;
        }

        [UnityTest]
        public IEnumerator OpensClosedDoorThenYieldsSafelyWhenPlayerBlocksBehindDoor()
        {
            var doorBL = FindDoor("Door_BedroomLiving");
            doorBL.SetOpen(false, true);
            Physics.SyncTransforms();

            var flankB = EdgeTable.Node("D_BL_B").Feet;
            var player = new GameObject("Player-Blocker");
            player.tag = "Player";
            player.transform.position = flankB;
            var col = player.AddComponent<CharacterController>();
            col.radius = 0.30f;
            col.height = 1.70f;
            col.center = new Vector3(0f, 0.85f, 0f);
            Physics.SyncTransforms();

            try
            {
                Assert.IsTrue(_walker.GoTo("A4"), _walker.LastFaults);
                for (var i = 0; i < 4000 && _walker.Walking; i++)
                {
                    _walker.Tick(TickSeconds);
                    if (doorBL.IsMoving) doorBL.Tick(TickSeconds, col.bounds);
                }

                Assert.IsTrue(doorBL.IsOpen, "玩家在门后挡路时，莫妮卡先推开门并打开到位");
                Assert.IsFalse(_walker.Walking, "见玩家挡在门后必须停步让行");
                Assert.Greater(_actor.transform.position.x, flankB.x + 0.35f,
                    "不可穿模越过站在门后的玩家身躯: monika.x=" + _actor.transform.position.x.ToString("F2"));
                StringAssert.Contains("玩家", _walker.LastFaults);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(player);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator ThinkingPhaseInterruptsCrossRoomWalkAndRoutineYields()
        {
            var routine = _actor.gameObject.AddComponent<AvatarRoutine>();
            var phase = Conversation.TurnPhase.Idle;
            _walker.PhaseSource = () => phase;
            routine.Walker = _walker;
            routine.DriveInUpdate = false;
            routine.IdleSeconds = 60f;
            routine.PhaseSource = () => phase;

            // Open interior doors cleanly so path crosses multiple nodes until interrupted.
            var doorBL = FindDoor("Door_BedroomLiving");
            var doorLK = FindDoor("Door_LivingKitchen");
            doorBL.SetOpen(true, true);
            doorLK.SetOpen(true, true);
            Physics.SyncTransforms();

            routine.Tick(61f);
            Assert.IsTrue(_walker.Walking, "空闲到时应从 S1 触发跨区移动");
            var startedTarget = _walker.Target;
            Assert.AreNotEqual("S1", startedTarget);

            // Step into the walk cleanly, then raise Thinking while still on an intermediate leg.
            for (var i = 0; i < 15 && _walker.Walking; i++) _walker.Tick(TickSeconds);
            Assert.IsTrue(_walker.Walking);

            phase = Conversation.TurnPhase.Thinking;
            for (var i = 0; i < 600 && _walker.Walking; i++) _walker.Tick(TickSeconds);

            Assert.IsFalse(_walker.Walking, "进入 Thinking 阶段后应在当前段末节点平滑停步");
            Assert.IsFalse(_walker.Arrived, "被说话抢占中断的跨区步行不得报告已抵达最终目标");

            // While still Thinking, AvatarRoutine must never start a new walk.
            routine.Tick(70f);
            Assert.IsFalse(_walker.Walking);
            Assert.AreEqual(0f, routine.Timer, 1e-5f);

            UnityEngine.Object.DestroyImmediate(routine);
            yield return null;
        }
    }
}
