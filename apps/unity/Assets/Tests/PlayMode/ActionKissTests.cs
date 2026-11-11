using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UniVRM10;
using Mas3D;
using Mas3D.Actions;
using Mas3D.Avatar;

namespace Mas3D.Tests
{
    public class ActionKissTests
    {
        AvatarActor _actor;
        PlayerMotor _player;
        ActionDirector _director;
        PlayerCameraRig _camera;

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
            _player.SetPosture(PlayerPosture.Kneeling);   // 跪下去亲：eye 1.28 才够到坐着俯身的她
            var camGo = new GameObject("CameraRig");
            _camera = camGo.AddComponent<PlayerCameraRig>();
            var dir = new GameObject("Actions");
            _director = dir.AddComponent<ActionDirector>();
            _director.Actor = _actor;
            _director.Player = _player;
            _director.Camera = _camera;
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
            if (_camera != null) Object.Destroy(_camera.gameObject);
            if (_player != null) Object.Destroy(_player.gameObject);
            yield return null;
        }

        [UnityTest]
        public IEnumerator TheKissClosesToSixCentimetres()
        {
            Assert.Greater(_player.EyeHeight, 1.004f, "跪姿 eye 必须高于座面 0.604 + 0.40 m，否则镜头会掉进坐垫");
            Assert.IsTrue(_director.TryEnterAction(ActionId.Kiss), "跪在她面前时亲吻必须能进场");
            ActionTestDrive.ToHold(_director);   // 1.4 s 的进场走完，接触才算数
            for (var i = 0; i < 20; i++) { _director.Tick(1f / 60f); yield return null; }
            var gap = Vector3.Distance(ActionGeometry.HerHeadCenter(_actor.Rig), PlayerReach.Face(_player));
            Assert.Less(gap, 0.06f, "两张脸最近处必须 ≤ 6 cm");
        }

        [UnityTest]
        public IEnumerator TheFacesAlignWithinFifteenDegrees()
        {
            Assert.IsTrue(_director.TryEnterAction(ActionId.Kiss));
            ActionTestDrive.ToHold(_director);
            for (var i = 0; i < 20; i++) { _director.Tick(1f / 60f); yield return null; }
            var align = ActionGeometry.FaceAlign(_actor.Rig, PlayerReach.Face(_player));
            Assert.Less(align, 15f, "她的面部朝向与她对玩家的连线偏差必须 ≤ 15°");
        }

        [UnityTest]
        public IEnumerator TheEyesAreShutDuringTheKiss()
        {
            Assert.IsTrue(_director.TryEnterAction(ActionId.Kiss));
            for (var i = 0; i < 20; i++) { _director.Tick(1f / 60f); yield return null; }
            Assert.IsTrue(_actor.Idle.EyesClosed, "亲吻期间必须把 EyesClosed 打开");
            Assert.GreaterOrEqual(_actor.Rig.ExpressionWeight(ExpressionKey.Blink), 0.9f, "闭眼强度必须 ≥ 0.9（Idle 的眨眼上限只有 0.85，所以这条只有强制闭眼才能过）");
            _director.Cancel();
            for (var i = 0; i < 20; i++) { _director.Tick(1f / 60f); yield return null; }
            Assert.IsFalse(_actor.Idle.EyesClosed, "松开后必须把 EyesClosed 关回");
            Assert.LessOrEqual(_actor.Rig.ExpressionWeight(ExpressionKey.Blink), 0.86f, "关回之后回到眨眼包络（上限 0.85）");
        }

        [UnityTest]
        public IEnumerator TheKissHoldsTwoSecondsAndThenLetsGo()
        {
            var head = _actor.Rig.Bone(HumanBodyBones.Head);
            var before = head.localRotation;
            Assert.IsTrue(_director.TryEnterAction(ActionId.Kiss));
            ActionTestDrive.ToHold(_director);   // 进场 1.4 s 不记 Held，先走完再数这 2.6 s
            for (var i = 0; i < 156; i++) { _director.Tick(1f / 60f); yield return null; }   // 2.6 s
            Assert.GreaterOrEqual(_director.Held, 2.0f, "接触必须稳定保持 2 s 以上");
            Assert.Greater(Quaternion.Angle(before, head.localRotation), 10f, "进场后她必须真的俯身，否则这条测的是静物");
            _director.Cancel();
            yield return null;
            Assert.Less(Quaternion.Angle(before, head.localRotation), 2f, "Cancel 之后头部必须回到入场前 2° 以内");
            Assert.IsNull(_director.Current, "Cancel 之后必须回到 idle");
        }

        [UnityTest]
        public IEnumerator TheKissFramesItsCloseUpInsteadOfSittingInHerHair()
        {
            // At 0.31 m her capsule surface is 0.115 m from his head, so the room's 0.20 m clearance
            // sphere overlaps it, Cinemachine collapses the boom to 0.085 m and the shot is nothing but
            // hair. The kiss therefore runs a thin sphere, a quarter-turn orbit and a shorter boom.
            var roomDistance = _camera.Distance;
            var roomRadius = _camera.CameraRadius;
            var mouse = _camera.MouseInput;
            Assert.IsTrue(mouse, "夹具起手必须放开鼠标，进场才谈得上接管");
            Assert.IsTrue(_director.TryEnterAction(ActionId.Kiss));
            var seat = AnchorTable.Load().Seat(AvatarActor.DefaultSeatId);

            // Literal values, not the constants: asserting ActionDirector.KissCameraRadius against
            // itself stays green when someone retunes the constant to the collapsing 0.20.
            Assert.AreEqual(0.06f, _camera.CameraRadius, 0.001f, "亲吻必须换用 0.06 m 的细避障球，否则相机球一开始就压在她身上");
            Assert.AreEqual(1.9f, _camera.Distance, 0.001f, "亲吻必须把摇臂收短到 1.9 m");
            Assert.AreEqual(180f, _camera.Yaw, 0.01f, "亲吻的机位必须转四分之一圈到 180°，正对两人侧面");
            Assert.IsFalse(_camera.MouseInput, "演出期间必须收走鼠标：不然玩家动一下鼠标，这套机位当场被拖走");

            _director.Cancel();
            yield return null;
            Assert.AreEqual(roomRadius, _camera.CameraRadius, 0.001f, "Cancel 必须把房间的避障球还回去");
            Assert.AreEqual(roomDistance, _camera.Distance, 0.001f, "Cancel 必须把摇臂长度还回去");
            Assert.AreEqual(mouse, _camera.MouseInput, "Cancel 必须把鼠标控制权还回去");
        }

        [UnityTest]
        public IEnumerator KissingOnTwoFeetIsRefusedNotForcedDown()
        {
            // The posture gate is the contract Task 5 pinned for pat-me: a refusal must never move the
            // player on their behalf. Kiss is the second posture in that contract and had no case.
            _player.SetPosture(PlayerPosture.Standing);
            yield return null;
            var feet = _player.transform.position;
            var yaw = _player.transform.eulerAngles.y;

            Assert.IsFalse(_director.TryEnterAction(ActionId.Kiss), "站着够不到坐着的她，必须拒绝进场");
            Assert.IsNull(_director.Current, "拒绝之后不许停在一个空动作上");
            StringAssert.Contains("需要跪下", _director.LastFaults, "拒绝理由必须说清要跪下");
            Assert.AreEqual(PlayerPosture.Standing, _player.Posture, "拒绝不许偷偷把玩家按跪下");

            // Horizontal only: the controller settles a few millimetres vertically between frames, and a
            // Teleport would move her at least 0.3 m in the plane.
            var after = _player.transform.position;
            Assert.Less(new Vector2(after.x - feet.x, after.z - feet.z).magnitude, 0.01f, "拒绝不许把玩家挪到标记点上");
            Assert.AreEqual(yaw, _player.transform.eulerAngles.y, 0.01f, "拒绝不许转玩家");

            _player.SetPosture(PlayerPosture.Kneeling);
            yield return null;
            Assert.IsTrue(_director.TryEnterAction(ActionId.Kiss), "跪回去之后必须能进场，否则上一条只是在测一扇永远关着的门");
        }
    }
}
