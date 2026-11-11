using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Mas3D;
using Mas3D.Actions;
using Mas3D.Avatar;

namespace Mas3D.Tests
{
    public class ActionPatTests
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

        [UnityTest]
        public IEnumerator PattingHerBringsTheHandToHerHeadTop()
        {
            Assert.IsTrue(_director.TryEnterAction(ActionId.PatHer), "站在她面前 0.62 m 处伸手摸头必须能进场：" + _director.LastFaults);
            // 接触按 Hold 的强度成立，所以先把 0.9 s 的进场走完；下面那五帧仍然是进场后的头五帧，
            // 量的还是呼吸的外来写者去抖窗口（0.25 s）有没有把前倾拉直。
            ActionTestDrive.ToHold(_director);

            // The lean has to survive the very next frames, not just the frame it was written on:
            // the breath owns Chest/Spine and its foreign-writer debounce is 0.25 s, so a breath that
            // ignores the action's claim would silently stand her back up for the first fifteen frames
            // and only adopt the lean afterwards. Five frames is well inside that window.
            for (var i = 0; i < 5; i++) { _director.Tick(1f / 60f); yield return null; }
            var early = Vector3.Distance(PlayerReach.Raised(_player, false), ActionGeometry.HerHeadTop(_actor.Rig));
            Assert.Less(early, 0.03f, "前五帧里她不能又被呼吸拉直（摸头残差 ≤ 3 cm）");

            for (var i = 0; i < 25; i++) { _director.Tick(1f / 60f); yield return null; }
            var raised = PlayerReach.Raised(_player, false);
            var gap = Vector3.Distance(raised, ActionGeometry.HerHeadTop(_actor.Rig));
            Assert.Less(gap, 0.03f, "玩家指尖到她头顶必须 ≤ 3 cm（她可以低头凑过来）");
        }

        [UnityTest]
        public IEnumerator HerHeadTakesThePat()
        {
            var head = _actor.Rig.Bone(HumanBodyBones.Head);
            var before = head.localRotation;
            Assert.IsTrue(_director.TryEnterAction(ActionId.PatHer), _director.LastFaults);
            // 头的瞄准跟着 IK 权重进场：0.9 s 的前 70% 权重还是 0，0.5 s 时头只有表里的 6°。
            // 走到 Hold 再量，才量得到 BowHeadTo，而不是一张静态的姿势表。
            ActionTestDrive.ToHold(_director);
            for (var i = 0; i < 30; i++) { _director.Tick(1f / 60f); yield return null; }
            // 3° alone is satisfied by the table's own Head (6,0,0) entry, so it cannot tell the aim
            // from a static table. The aim drives the head to ~39° away from the neck; anything under
            // 20° means BowHeadTo stopped working and only the table is posing her.
            var neck = _actor.Rig.Bone(HumanBodyBones.Neck);
            Assert.Greater(Quaternion.Angle(neck.rotation, head.rotation), 20f, "被摸时头的朝向必须真被送到掌心（头-颈夹角 > 20°，表里只有 6°）");
            Assert.Greater(Quaternion.Angle(before, head.localRotation), 3f, "被摸时头部必须有 ≥ 3° 的受力反馈，不能僵硬不动");
            _director.Cancel();
            yield return null;
            Assert.Less(Quaternion.Angle(before, head.localRotation), 1f, "Cancel 之后头部必须回到入场前 1° 以内");
        }

        [UnityTest]
        public IEnumerator PattingMeNeedsACrouchedPlayer()
        {
            var spot = _player.transform.position;
            var yaw = _player.transform.eulerAngles.y;
            Assert.IsFalse(_director.TryEnterAction(ActionId.PatMe), "站姿头顶 1.78 m 超出她坐姿伸手的 1.74 m，必须拒绝");
            Assert.IsNull(_director.Current, "被拒绝时不许进入任何状态");
            StringAssert.Contains("需要蹲下", _director.LastFaults, "拒绝理由必须点名要蹲下，别用一句泛泛的够不着");
            Assert.AreEqual(PlayerPosture.Standing, _player.Posture, "被拒绝时不许偷偷把玩家按下去");
            Assert.AreEqual(spot, _player.transform.position, "被拒绝时不许挪动玩家");
            Assert.AreEqual(yaw, _player.transform.eulerAngles.y, 0.01f, "被拒绝时不许转动玩家");
            _player.SetPosture(PlayerPosture.Crouching);
            yield return null;
            Assert.IsTrue(_director.TryEnterAction(ActionId.PatMe), "蹲下后头顶降到 1.21 m，必须能进场：" + _director.LastFaults);
        }

        [UnityTest]
        public IEnumerator HerHandFindsTheCrownWhenCrouched()
        {
            _player.SetPosture(PlayerPosture.Crouching);
            yield return null;
            Assert.IsTrue(_director.TryEnterAction(ActionId.PatMe), _director.LastFaults);
            ActionTestDrive.ToHold(_director);   // 接触只在 Hold 里成立
            for (var i = 0; i < 30; i++) { _director.Tick(1f / 60f); yield return null; }
            var gap = Vector3.Distance(ActionGeometry.HerPalm(_actor.Rig, false), PlayerReach.Crown(_player));
            Assert.Less(gap, 0.03f, "她的手到玩家头顶必须 ≤ 3 cm");
        }

        [UnityTest]
        public IEnumerator AThreeFramePatLeavesNoLeanBehind()
        {
            var chest = _actor.Rig.Bone(HumanBodyBones.Chest);
            var spine = _actor.Rig.Bone(HumanBodyBones.Spine);
            var chestBefore = chest.localRotation;
            var spineBefore = spine.localRotation;

            Assert.IsTrue(_director.TryEnterAction(ActionId.PatHer), _director.LastFaults);
            for (var i = 0; i < 3; i++) { _director.Tick(1f / 60f); yield return null; }
            _director.Cancel();
            for (var i = 0; i < 30; i++) yield return null;

            // Short than RecaptureDebounce (0.25 s): the breath must not have adopted the lean as its
            // new resting pose. 3° is the breath's own peak-to-peak swing (2 x 1.1 deg); a welded lean
            // is 24 deg on the spine and 12 deg on the chest, so the two cannot be confused.
            Assert.Less(Quaternion.Angle(chestBefore, chest.localRotation), 3f, "拍三帧就取消后，呼吸基线不许把前倾焊住（Chest）");
            Assert.Less(Quaternion.Angle(spineBefore, spine.localRotation), 3f, "拍三帧就取消后，呼吸基线不许把前倾焊住（Spine）");
        }

        [UnityTest]
        public IEnumerator TheFreeArmKeepsItsBaseline()
        {
            var free = _actor.Rig.Bone(HumanBodyBones.LeftUpperArm);
            var before = free.localRotation;
            Assert.IsTrue(_director.TryEnterAction(ActionId.PatHer), _director.LastFaults);
            for (var i = 0; i < 30; i++) { _director.Tick(1f / 60f); yield return null; }
            Assert.Less(Quaternion.Angle(before, free.localRotation), 1f, "摸头只用右手，左肩不许跟着动");
        }
    }
}
