using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Mas3D;
using Mas3D.Actions;
using Mas3D.Avatar;

namespace Mas3D.Tests
{
    /// <summary>
    /// The phase clock: an action enters, holds, and leaves, and only the hold may credit contact.
    /// The fixture is the hands one copied from ActionHandsTests, because hands is the longest entry
    /// (2.6 s) and therefore the one whose clock is easiest to read.
    /// </summary>
    public class ActionPhaseTests
    {
        AvatarActor _actor;
        PlayerMotor _player;
        ActionDirector _director;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            AvatarPrefabFixture.Require();
            _actor = AvatarActor.Spawn();
            var go = new GameObject("Player");
            go.AddComponent<CharacterController>();
            _player = go.AddComponent<PlayerMotor>();
            _player.KeyboardInput = true;   // 起手必须能走：否则「Cancel 归还」与「什么都不做」同值
            _player.EnsureReady();
            var dir = new GameObject("Actions");
            _director = dir.AddComponent<ActionDirector>();
            _director.Actor = _actor;
            _director.Player = _player;
            _director.KeyboardInput = false;
            _director.DriveInUpdate = false;
            yield return null;
        }

        /// <summary>
        /// The IK owns both upper arms and both hands outright - the hands table writes neither. If
        /// the blend snapshots them from the previous frame's blended value, it starts from itself and
        /// the whole leave keeps the arm at full reach until Cancel snaps it. Sampled mid-leave on
        /// purpose: after ClearPose the pop would hide the bug.
        /// </summary>
        [UnityTest]
        public IEnumerator TheLeaveHandsTheUpperArmBackBeforeTheCancel()
        {
            var upper = _actor.Rig.Bone(HumanBodyBones.LeftUpperArm);
            var hand = _actor.Rig.Bone(HumanBodyBones.LeftHand);
            var upperHome = upper.localRotation;
            var handHome = hand.localRotation;

            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands), _director.LastFaults);
            ActionTestDrive.ToHold(_director);
            Assert.IsTrue(_director.RequestLeave(), "the hold must be leavable");

            ActionTestDrive.Hold(_director, 1.3f);   // 81% of the 1.6 s leave: the weight is already 0
            Assert.That(Quaternion.Angle(upperHome, upper.localRotation), Is.LessThan(5f),
                "the upper arm is IK-only, so the blend must hand it back to the entry pose");
            Assert.That(Quaternion.Angle(handHome, hand.localRotation), Is.LessThan(5f),
                "the hand is IK-only too: it must be back on its entry rotation before the cancel");
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_director != null && _director.Current != null) _director.Cancel();
            if (_director != null) Object.Destroy(_director.gameObject);
            if (_actor != null) Object.Destroy(_actor.gameObject);
            if (_player != null) Object.Destroy(_player.gameObject);
            yield return null;
        }

        /// <summary>The director the fixture just built, already able to play hands.</summary>
        ActionDirector HandsDirector() => _director;

        [Test]
        public void EnterRunsForItsNominalTimeThenHolds()
        {
            var d = HandsDirector();
            Assert.That(d.TryEnterAction(ActionId.Hands), Is.True, d.LastFaults);
            Assert.That(d.Phase, Is.EqualTo(ActionPhase.Enter));
            ActionTestDrive.Hold(d, 2.5f);
            Assert.That(d.Phase, Is.EqualTo(ActionPhase.Enter), "2.5 s of a 2.6 s entry");

            // One oversized step crosses the end by 0.4 s. The hold must inherit exactly that
            // overdue fraction: restarting it at zero would drop action time, and the hold clock
            // is what the micro-motion layer is driven by.
            d.Tick(0.5f);
            Assert.That(d.Phase, Is.EqualTo(ActionPhase.Hold));
            Assert.That(d.PhaseTime, Is.EqualTo(0.4f).Within(1e-3f),
                "the overdue fraction crosses the flip, it is not thrown away");
            Assert.That(d.PhaseDuration, Is.EqualTo(0f), "a hold has no nominal length");
            Assert.That(d.Held, Is.EqualTo(0f), "the frame that flips into the hold credits no contact");
        }

        /// <summary>保持段不停表：翻面后继续走时间轴，权重恒为满；空闲权重为 0。</summary>
        [Test]
        public void TheHoldKeepsItsClockAndFullWeight()
        {
            var d = HandsDirector();
            Assert.That(d.PhaseWeight, Is.EqualTo(0f).Within(1e-4f), "idle claims nothing");

            Assert.That(d.TryEnterAction(ActionId.Hands), Is.True, d.LastFaults);
            ActionTestDrive.ToHold(d);
            float carried = d.PhaseTime;
            Assert.That(d.PhaseWeight, Is.EqualTo(1f).Within(1e-4f), "a hold is always full strength");

            ActionTestDrive.Hold(d, 0.5f);
            Assert.That(d.PhaseTime, Is.GreaterThan(0.4f), "the hold keeps its own clock running");
            Assert.That(d.PhaseTime, Is.GreaterThan(carried), "the hold clock never rewinds");
            Assert.That(d.PhaseWeight, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(d.PhaseDuration, Is.EqualTo(0f));
        }

        [Test]
        public void HeldOnlyCountsInsideHold()
        {
            var d = HandsDirector();
            Assert.That(d.TryEnterAction(ActionId.Hands), Is.True, d.LastFaults);
            ActionTestDrive.Hold(d, 2.55f);
            Assert.That(d.Held, Is.EqualTo(0f), "an entry never credits contact");
            ActionTestDrive.Hold(d, 1.2f);
            Assert.That(d.Held, Is.GreaterThan(0.5f));
        }

        [Test]
        public void EscapeRequestsALeaveAndTheEndOfItRestores()
        {
            var d = HandsDirector();
            Assert.That(d.TryEnterAction(ActionId.Hands), Is.True, d.LastFaults);
            ActionTestDrive.ToHold(d);
            Assert.That(d.RequestLeave(), Is.True);
            Assert.That(d.Phase, Is.EqualTo(ActionPhase.Leave));
            ActionTestDrive.Hold(d, 0.8f);
            Assert.That(d.Current, Is.EqualTo(ActionId.Hands), "half a leave is still a leave");
            ActionTestDrive.Hold(d, 1.0f);
            Assert.That(d.Current, Is.Null);
            Assert.That(d.Phase, Is.EqualTo(ActionPhase.None));
            Assert.That(d.ClearPoseCount, Is.EqualTo(1));
        }

        /// <summary>进场中按 ESC：权重不跳变，退场走满后玩家回到原位、回正朝向，且只交还一次姿势。</summary>
        [Test]
        public void EscapeMidEnterKeepsTheWeightAndStillRestoresExactly()
        {
            var d = HandsDirector();
            var spot = _player.transform.position;
            var yaw = _player.transform.eulerAngles.y;

            Assert.That(d.TryEnterAction(ActionId.Hands), Is.True, d.LastFaults);
            // 2.5 s in, the entry is near full strength but not yet there: an escape that restarted
            // the leave at 1 would snap her up, and one that restarted it at 0 would snap her down.
            ActionTestDrive.Hold(d, 2.5f);
            Assert.That(d.Phase, Is.EqualTo(ActionPhase.Enter), "2.5 s into a 2.6 s entry");

            float before = d.PhaseWeight;
            Assert.That(before, Is.GreaterThan(0.5f), "the interruption has to happen where weight exists");
            Assert.That(d.RequestLeave(), Is.True);
            Assert.That(d.PhaseWeight, Is.EqualTo(before).Within(1e-4f),
                "the leave starts exactly where the entry was interrupted");

            ActionTestDrive.Hold(d, ActionTestDrive.Step);
            Assert.That(Mathf.Abs(d.PhaseWeight - before), Is.LessThanOrEqualTo(0.05f),
                "leaving mid-entry continues from the weight it interrupted (was " + before.ToString("F3")
                + ", now " + d.PhaseWeight.ToString("F3") + ")");

            ActionTestDrive.ToLeaveEnd(d);
            Assert.That(d.Current, Is.Null);
            Assert.That(d.Phase, Is.EqualTo(ActionPhase.None));
            Assert.That(Vector3.Distance(_player.transform.position, spot), Is.LessThan(0.02f),
                "the player is back on the spot the action took him from");
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(_player.transform.eulerAngles.y, yaw)), Is.LessThan(1f),
                "and facing the way he was");
            Assert.That(d.ClearPoseCount, Is.EqualTo(1), "one action hands the pose back exactly once");
        }

        /// <summary>RequestLeave 只答应一次；空闲时一律 false。</summary>
        [Test]
        public void RequestLeaveIsIdempotentAndIdleAnswersFalse()
        {
            var d = HandsDirector();
            Assert.That(d.RequestLeave(), Is.False, "there is nothing to leave while idle");
            Assert.That(d.Phase, Is.EqualTo(ActionPhase.None));

            Assert.That(d.TryEnterAction(ActionId.Hands), Is.True, d.LastFaults);
            Assert.That(d.RequestLeave(), Is.True);
            Assert.That(d.RequestLeave(), Is.False, "a second Escape must not restart the leave");
            Assert.That(d.Phase, Is.EqualTo(ActionPhase.Leave));
        }

        /// <summary>退场结束后再 Tick：不抛、不复活动作、不再交还姿势。</summary>
        [Test]
        public void TickAfterTheLeaveEndedIsInert()
        {
            var d = HandsDirector();
            Assert.That(d.TryEnterAction(ActionId.Hands), Is.True, d.LastFaults);
            ActionTestDrive.ToHold(d);
            Assert.That(d.RequestLeave(), Is.True);
            ActionTestDrive.ToLeaveEnd(d);

            int clears = d.ClearPoseCount;
            Assert.That(clears, Is.EqualTo(1));

            Assert.DoesNotThrow(() => d.Tick(6f), "a long step after the leave must not throw");
            d.Tick(1f / 60f);
            Assert.That(d.Current, Is.Null, "an idle director never comes back to life");
            Assert.That(d.Phase, Is.EqualTo(ActionPhase.None));
            Assert.That(d.ClearPoseCount, Is.EqualTo(clears), "an idle tick hands nothing back");
            Assert.That(d.Held, Is.EqualTo(0f));
        }

        [Test]
        public void CancelStaysInstantaneous()
        {
            var d = HandsDirector();
            Assert.That(d.TryEnterAction(ActionId.Hands), Is.True, d.LastFaults);
            ActionTestDrive.Hold(d, 0.3f);
            d.Cancel();
            Assert.That(d.Current, Is.Null);
            Assert.That(d.ClearPoseCount, Is.EqualTo(1));
        }

        [Test]
        public void TheWeightCurveIsFlatUntilSeventyPercent()
        {
            Assert.That(PerformanceTiming.IkWeight(ActionPhase.Enter, 0f), Is.EqualTo(0f).Within(1e-4),
                "an entry starts at nothing");
            Assert.That(PerformanceTiming.IkWeight(ActionPhase.Enter, 0.7f), Is.EqualTo(0f).Within(1e-4));
            Assert.That(PerformanceTiming.IkWeight(ActionPhase.Enter, 1f), Is.EqualTo(1f).Within(1e-4));
            Assert.That(PerformanceTiming.IkWeight(ActionPhase.Enter, 2f), Is.EqualTo(1f).Within(1e-4),
                "u above one is clamped, not extrapolated");
            Assert.That(PerformanceTiming.IkWeight(ActionPhase.Enter, -1f), Is.EqualTo(0f).Within(1e-4),
                "u below zero is clamped too");
            Assert.That(PerformanceTiming.IkWeight(ActionPhase.Leave, 0f), Is.EqualTo(1f).Within(1e-4));
            Assert.That(PerformanceTiming.IkWeight(ActionPhase.Leave, 0.3f), Is.EqualTo(0f).Within(1e-4));
            Assert.That(PerformanceTiming.IkWeight(ActionPhase.Hold, 0f), Is.EqualTo(1f).Within(1e-4));
            Assert.That(PerformanceTiming.IkWeight(ActionPhase.None, 0f), Is.EqualTo(0f).Within(1e-4),
                "idle claims nothing, whatever the progress says");
            Assert.That(PerformanceTiming.IkWeight(ActionPhase.None, 0.5f), Is.EqualTo(0f).Within(1e-4));
            Assert.That(PerformanceTiming.IkWeight(ActionPhase.None, 1f), Is.EqualTo(0f).Within(1e-4));
        }
    }
}
