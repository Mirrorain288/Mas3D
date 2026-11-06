using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Mas3D;
using Mas3D.Actions;
using Mas3D.Avatar;

namespace Mas3D.Tests
{
    public class ActionHandsTests
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

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_director != null && _director.Current != null) _director.Cancel();
            if (_director != null) Object.Destroy(_director.gameObject);
            if (_actor != null) Object.Destroy(_actor.gameObject);
            if (_player != null) Object.Destroy(_player.gameObject);
            yield return null;
        }

        float PalmGap(bool left) => Vector3.Distance(
            ActionGeometry.HerPalm(_actor.Rig, left), PlayerReach.Palm(_player, left));

        [UnityTest]
        public IEnumerator ADestroyedActorForgetsTheSnapshot()
        {
            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands), _director.LastFaults);
            Assert.IsTrue(ActionPose.Active, "进场后必须拿着快照");

            // The teardown path: the actor goes first, so ClearPose can only reach a destroyed rig.
            Object.DestroyImmediate(_actor.gameObject);
            yield return null;
            _director.ClearPose();

            Assert.IsFalse(ActionPose.Active, "演员销毁后快照必须清掉，否则下一个演员会继承死掉那具的基线");
            _director.Cancel();
        }

        [UnityTest]
        public IEnumerator BothHandsMeetTheirPalms()
        {
            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands), "她坐在 S1 上，牵手必须能进场");
            _director.Tick(1f / 60f);
            yield return null;
            Assert.Less(PalmGap(true), 0.02f, "左手掌心到她坐姿掌心的距离必须 ≤ 2 cm");
            Assert.Less(PalmGap(false), 0.02f, "右手掌心也必须 ≤ 2 cm");
        }

        [UnityTest]
        public IEnumerator TheHandsHoldForThreeSeconds()
        {
            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands));
            for (var i = 0; i < 240; i++)   // 4 s @60 fps
            {
                _director.Tick(1f / 60f);
                yield return null;
            }
            Assert.GreaterOrEqual(_director.Held, 3.0f, "接触必须稳定保持 3 s 以上");
            Assert.LessOrEqual(_director.ContactError, 0.02f, "全程接触误差不得超过 2 cm");
        }

        [UnityTest]
        public IEnumerator TheContactDoesNotCreepWhileSheBreathes()
        {
            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands));
            var chest = _actor.Rig.Bone(HumanBodyBones.Chest);
            var chest0 = chest.localRotation;
            var samples = new List<Vector3>();
            var swing = 0f;   // 取整段最大偏移：呼吸周期 3.0 s，最后恰好回到同相位，末帧采样会读出 0°
            // 按**模拟时间**跑满一个呼吸周期，不按帧数：AvatarIdle 的时钟是 Time.deltaTime，
            // 批处理里帧可以跑到上万 fps，数 180 帧可能只推进几十毫秒，摆动幅度自然读不满（曾经因此假红）。
            var elapsed = 0f;
            while (elapsed < 3f)
            {
                _director.Tick(1f / 60f);
                yield return null;
                elapsed += Time.deltaTime;
                swing = Mathf.Max(swing, Quaternion.Angle(chest0, chest.localRotation));
                if (elapsed > 2f) samples.Add(_actor.Rig.Bone(HumanBodyBones.LeftHand).position);
            }
            Assert.Greater(swing, 0.2f, "呼吸必须真的在跑，否则这条用例测的是静物");
            var mean = Vector3.zero;
            foreach (var s in samples) mean += s;
            mean /= samples.Count;
            var variance = 0f;
            foreach (var s in samples) variance += (s - mean).sqrMagnitude;
            variance /= samples.Count;
            Assert.Less(variance, 0.000001f, "最后 1 s 手的位置方差必须 ≤ (1 mm)²：呼吸不许把手抖出去");
        }

        [UnityTest]
        public IEnumerator CancelRestoresTheArms()
        {
            var upper = _actor.Rig.Bone(HumanBodyBones.LeftUpperArm);
            var before = upper.localRotation;
            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands));
            for (var i = 0; i < 30; i++) { _director.Tick(1f / 60f); yield return null; }
            Assert.Greater(Quaternion.Angle(before, upper.localRotation), 10f, "进场后左臂必须真的抬起来，否则 Cancel 没有东西可还原");
            _director.Cancel();
            yield return null;
            Assert.Less(Quaternion.Angle(before, upper.localRotation), 1f, "Cancel 之后左肩必须回到入场前 1° 以内");
        }

        [UnityTest]
        public IEnumerator TheActionNeverMovesTheFeet()
        {
            var hips = _actor.Rig.Bone(HumanBodyBones.Hips).position;
            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands));
            for (var i = 0; i < 60; i++) { _director.Tick(1f / 60f); yield return null; }
            Assert.AreEqual(hips.y, _actor.Rig.Bone(HumanBodyBones.Hips).position.y, 0.005f, "牵手只写上半身，髋高不许动");
            Assert.AreEqual(hips.x, _actor.Rig.Bone(HumanBodyBones.Hips).position.x, 0.005f, "髋位也不许动");
        }
    }
}
