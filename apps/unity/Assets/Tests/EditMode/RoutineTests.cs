using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Mas3D.Avatar;
using Mas3D.Conversation;
using Mas3D.Room;

namespace Mas3D.Tests
{
    public class RoutineTests
    {
        GameObject _actorGo;
        AvatarActor _actor;
        AvatarWalker _walker;
        AvatarRoutine _routine;
        AnchorTable _table;

        TurnPhase _phase;
        bool _typing;
        bool _acting;

        [SetUp]
        public void SetUp()
        {
            AnchorTable.SetActiveRelativePath(AnchorTable.FlatRelativePath);
            EdgeTable.Rebuild();
            _table = AnchorTable.Load();

            _actorGo = new GameObject("RoutineActor");
            _actor = _actorGo.AddComponent<AvatarActor>();
            _actor.Seat = _actorGo.AddComponent<AvatarSeat>();

            var s1Node = EdgeTable.Node("S1");
            Assert.IsNotNull(s1Node, "anchors-flat 应构建出 S1 路网节点");
            _actorGo.transform.position = s1Node.Feet;
            _actorGo.transform.rotation = Quaternion.Euler(0f, s1Node.FacingY, 0f);

            _walker = _actorGo.AddComponent<AvatarWalker>();
            _walker.Actor = _actor;
            _walker.DriveInUpdate = false;
            _walker.PhaseSource = () => _phase;
            Assert.IsTrue(_walker.ResnapToNearestNode(), "RoutineTests 起手必须落到 S1 节点");

            _routine = _actorGo.AddComponent<AvatarRoutine>();
            _routine.Walker = _walker;
            _routine.DriveInUpdate = false;
            _routine.IdleSeconds = 60f;
            _routine.PhaseSource = () => _phase;
            _routine.TypingOverride = () => _typing;
            _routine.ActionBusyOverride = () => _acting;

            _phase = TurnPhase.Idle;
            _typing = false;
            _acting = false;
        }

        [TearDown]
        public void TearDown()
        {
            if (_actorGo != null) Object.DestroyImmediate(_actorGo);
            AnchorTable.ResetActive();
            EdgeTable.Rebuild();
        }

        [Test]
        public void WaitsForIdleThresholdBeforeStartingWalkAndSkipsBathroom()
        {
            _routine.Tick(59f);
            Assert.IsFalse(_walker.Walking, "未达到等待阈值前不得触发换区");
            Assert.AreEqual(0, _routine.TransitionsTriggered);

            _routine.Tick(2f);
            Assert.IsTrue(_walker.Walking, "达到空闲阈值后必须自主触发一次换区");
            Assert.AreEqual(1, _routine.TransitionsTriggered);
            Assert.AreEqual(0f, _routine.Timer, 1e-5f);
            Assert.AreNotEqual("S1", _routine.LastPickedTarget, "不能挑当前所在点位作为目标");

            var pickedRooms = new HashSet<string>();
            for (var cycle = 0; cycle < 4; cycle++)
            {
                var targetId = _walker.Target;
                var anchor = _table.Anchor(targetId);
                var seat = _table.Seat(targetId);
                var room = anchor != null ? anchor.room : seat != null ? seat.room : null;
                Assert.AreNotEqual("bathroom", room, "自主轮换不得挑选卫浴区点位: " + targetId);
                if (!string.IsNullOrEmpty(room)) pickedRooms.Add(room);

                // Walk to destination deterministically in EditMode (no door collider scene here).
                for (var step = 0; step < 3000 && _walker.Walking; step++)
                    _walker.Tick(0.05f);
                Assert.IsFalse(_walker.Walking, "应完成当前自主步行");
                Assert.AreEqual(targetId, _walker.At);

                _routine.Tick(61f);
                Assert.IsTrue(_walker.Walking);
                Assert.AreNotEqual(targetId, _walker.Target, "下一轮不能原地踏步");
            }

            Assert.GreaterOrEqual(pickedRooms.Count, 2, "多轮静谧流转应覆盖多个非卫浴生活区");
        }

        [Test]
        public void DialogueThinkingInputFocusActionBusyAndDisabledAllBlockAndResetTimer()
        {
            _routine.Tick(40f);
            Assert.AreEqual(40f, _routine.Timer, 1e-4f);

            // Conversation Thinking interrupts timer countdown immediately.
            _phase = TurnPhase.Thinking;
            _routine.Tick(70f);
            Assert.IsFalse(_walker.Walking, "对话 Thinking 期间绝不能自主走动");
            Assert.AreEqual(0f, _routine.Timer, 1e-5f, "Thinking 必须将空闲计时器清零");

            _phase = TurnPhase.Idle;
            _routine.Tick(40f);
            Assert.IsFalse(_walker.Walking, "被重置后重计时，40s 不应直接触发 60s 阈值");

            // Chat input focus blocks & resets timer.
            _typing = true;
            _routine.Tick(70f);
            Assert.IsFalse(_walker.Walking, "玩家在输入框打字聚焦时不得走动");
            Assert.AreEqual(0f, _routine.Timer, 1e-5f);
            _typing = false;

            // Active pair action blocks & resets timer.
            _acting = true;
            _routine.Tick(70f);
            Assert.IsFalse(_walker.Walking, "双人互动演出进行中不得走动");
            Assert.AreEqual(0f, _routine.Timer, 1e-5f);
            _acting = false;

            // Disabled switch blocks & resets timer.
            _routine.Enabled = false;
            _routine.Tick(70f);
            Assert.IsFalse(_walker.Walking, "Enabled=false 时不得触发换区");
            Assert.AreEqual(0f, _routine.Timer, 1e-5f);
        }
    }
}
