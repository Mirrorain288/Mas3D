using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Mas3D;
using Mas3D.Actions;
using Mas3D.Avatar;
using Mas3D.Room;

namespace Mas3D.Tests
{
    public class FlatActionTests
    {
        ActionDirector _director;
        AvatarActor _actor;
        AvatarWalker _walker;
        PlayerMotor _player;
        PlayerCameraRig _camera;
        Camera _lens;

        void BuildHarness()
        {
            AvatarPrefabFixture.Require();
            AnchorTable.SetActiveRelativePath(AnchorTable.FlatRelativePath);
            EdgeTable.Rebuild();

            _actor = AvatarActor.Spawn();
            _walker = _actor.gameObject.AddComponent<AvatarWalker>();
            _walker.Actor = _actor;
            _walker.DriveInUpdate = false;

            var playerGo = new GameObject("Player");
            playerGo.tag = "Player";
            var cc = playerGo.AddComponent<CharacterController>();
            cc.height = 1.70f;
            cc.radius = 0.30f;
            cc.center = new Vector3(0f, 0.85f, 0f);
            _player = playerGo.AddComponent<PlayerMotor>();
            _player.KeyboardInput = true;
            _player.EnsureReady();

            var camGo = new GameObject("CameraRig");
            _camera = camGo.AddComponent<PlayerCameraRig>();
            _lens = new GameObject("TestCamera").AddComponent<Camera>();
            _camera.Build(_player.Head, _lens);
            _camera.MouseInput = true;

            var dirGo = new GameObject("Actions");
            _director = dirGo.AddComponent<ActionDirector>();
            _director.Actor = _actor;
            _director.Walker = _walker;
            _director.Player = _player;
            _director.Camera = _camera;
            _director.KeyboardInput = false;
            _director.DriveInUpdate = false;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_director != null) Object.Destroy(_director.gameObject);
            if (_actor != null) Object.Destroy(_actor.gameObject);
            if (_camera != null) Object.Destroy(_camera.gameObject);
            if (_lens != null) Object.Destroy(_lens.gameObject);
            if (_player != null) Object.Destroy(_player.gameObject);
            _director = null;
            _actor = null;
            _walker = null;
            _camera = null;
            _lens = null;
            _player = null;

            SceneFlow.UnloadMainIfLoaded();
            AnchorTable.ResetActive();
            EdgeTable.Rebuild();
            yield return null;
        }

        static PlayerPosture RequiredPostureFor(string id)
        {
            switch (id)
            {
                case ActionId.PatMe: return PlayerPosture.Crouching;
                case ActionId.Kiss: return PlayerPosture.Kneeling;
                default: return PlayerPosture.Standing;
            }
        }

        [UnityTest]
        public IEnumerator LivingSofaActions1To5CompleteCleanlyOnS1AndS2()
        {
            BuildHarness();
            yield return null;

            var table = AnchorTable.Load();
            Assert.IsNotNull(table, "anchors-flat.json 必须可加载");

            foreach (var seatId in new[] { "S1", "S2" })
            {
                var seat = table.Seat(seatId);
                Assert.IsNotNull(seat, "缺客厅沙发座 " + seatId);
                Assert.IsTrue(seat.intimate, seatId + " 必须允许亲密动作");

                _actor.SitAt(seatId);
                Assert.IsTrue(_actor.Seat.Seated, seatId + " 坐下失败");
                yield return null;

                foreach (var id in ActionId.All)
                {
                    var facing = Quaternion.Euler(0f, seat.facingY, 0f);
                    var startFeet = new Vector3(seat.x, 0f, seat.z) + facing * Vector3.forward * 1.20f;
                    var startYaw = Mathf.Repeat(seat.facingY + 180f, 360f);

                    _player.SetPosture(RequiredPostureFor(id));
                    _player.Teleport(startFeet, startYaw);
                    _player.KeyboardInput = true;
                    _camera.SetOrbit(startYaw, 14f);
                    yield return null;

                    Assert.IsTrue(_director.TryEnterAction(id),
                        seatId + " / " + id + " 入场失败: " + _director.LastFaults);
                    Assert.AreEqual(id, _director.Current);
                    Assert.IsFalse(_player.KeyboardInput, seatId + " / " + id + " 演出态应锁键盘");

                    ActionTestDrive.ToHold(_director);
                    ActionTestDrive.Hold(_director, 0.25f);

                    Assert.LessOrEqual(_director.ContactError, ActionDirector.ContactTolerance,
                        seatId + " / " + id + " 接触误差超标: " + _director.ContactError);

                    if (id == ActionId.SitTogether)
                    {
                        Assert.AreEqual(seatId, _actor.SeatId, "平层沙发并坐应留在原座位 " + seatId);
                        Assert.AreEqual(seat.y + AvatarSeat.HipsGap, _actor.Seat.HipsWorld.y, 0.02f, "沙发并坐髋高应保持坐姿");
                        var partnerX = _player.transform.position.x;
                        Assert.Greater(partnerX, 1.3f, "并坐玩家不得穿进左侧沙发扶手");
                        Assert.Less(partnerX, 3.7f, "并坐玩家不得穿进右侧沙发扶手");
                    }

                    _director.Cancel();
                    yield return null;

                    Assert.IsNull(_director.Current);
                    Assert.IsTrue(_player.KeyboardInput, seatId + " / " + id + " Cancel 后应还键盘");
                    Assert.IsTrue(_actor.Seat.Seated, seatId + " / " + id + " Cancel 后她应仍在座位");
                    Assert.AreEqual(seatId, _actor.SeatId, seatId + " / " + id + " Cancel 后应回原座");
                    Assert.AreEqual(RequiredPostureFor(id), _player.Posture, "Cancel 应恢复进场前站姿");
                }
            }
        }

        [UnityTest]
        public IEnumerator ActionsRefusedWhileAvatarWalkerIsWalking()
        {
            BuildHarness();
            yield return null;

            _actor.SitAt("S1");
            Assert.IsTrue(_actor.Seat.Seated);
            var table = AnchorTable.Load();
            var s1 = table.Seat("S1");
            _player.Teleport(new Vector3(s1.x, 0.02f, s1.z - 0.8f), 0f);
            yield return null;

            Assert.IsTrue(_walker.GoTo("A2"), "启动步行去 A2 失败: " + _walker.LastFaults);
            Assert.IsTrue(_walker.Walking, "必须处于 Walking 状态");

            var beforePos = _player.transform.position;
            var beforeYaw = _player.transform.eulerAngles.y;
            var beforeClear = _director.ClearPoseCount;

            foreach (var id in new[] { ActionId.Hands, ActionId.Kiss, ActionId.SitTogether })
            {
                _player.SetPosture(RequiredPostureFor(id));
                Assert.IsFalse(_director.TryEnterAction(id), "步行中必须拒绝动作 " + id);
                Assert.IsNull(_director.Current);
                Assert.IsTrue(_walker.Walking, "拒绝动作不应打断步行");
                Assert.IsTrue(_player.KeyboardInput, "拒绝时不得锁键盘");
                Assert.Less(Vector3.Distance(beforePos, _player.transform.position), 1e-4f, "拒绝时玩家不得瞬移");
                Assert.AreEqual(beforeYaw, _player.transform.eulerAngles.y, 1e-4f, "拒绝时朝向不得改变");
                Assert.AreEqual(beforeClear, _director.ClearPoseCount, "拒绝时 ClearPoseCount 不得增加");
                Assert.IsFalse(string.IsNullOrEmpty(_director.LastFaults));
            }
        }

        [UnityTest]
        public IEnumerator ActionsRefusedWhenSeatedOnNonIntimateBedroomBedS3()
        {
            BuildHarness();
            yield return null;

            var table = AnchorTable.Load();
            var s3 = table.Seat("S3");
            Assert.IsNotNull(s3, "anchors-flat.json 必须有 S3");
            Assert.IsFalse(s3.intimate, "S3 卧室大床不得标记 intimate");

            _actor.SitAt("S3");
            Assert.IsTrue(_actor.Seat.Seated, "应能坐到 S3");
            yield return null;

            var facing = Quaternion.Euler(0f, s3.facingY, 0f);
            var frontFeet = new Vector3(s3.x, 0.02f, s3.z) + facing * Vector3.forward * 0.62f;
            var frontYaw = Mathf.Repeat(s3.facingY + 180f, 360f);

            var beforeClear = _director.ClearPoseCount;
            foreach (var id in ActionId.All)
            {
                _player.SetPosture(RequiredPostureFor(id));
                _player.Teleport(frontFeet, frontYaw);
                yield return null;

                var beforePos = _player.transform.position;
                Assert.IsFalse(_director.TryEnterAction(id), "非亲密床位 S3 必须拒绝动作 " + id);
                Assert.IsNull(_director.Current);
                Assert.IsTrue(_player.KeyboardInput);
                Assert.Less(Vector3.Distance(beforePos, _player.transform.position), 1e-4f, "拒绝时玩家不得瞬移");
                Assert.AreEqual(beforeClear, _director.ClearPoseCount, "拒绝时 ClearPoseCount 不得增加");
                Assert.IsTrue(_actor.Seat.Seated);
                Assert.AreEqual("S3", _actor.SeatId);
                Assert.IsFalse(string.IsNullOrEmpty(_director.LastFaults));
            }
        }

        [UnityTest]
        public IEnumerator DefaultMainSceneBootsInFlatLivingRoomFaceToFace()
        {
            yield return SceneFlow.LoadMain();
            try
            {
                var boot = Object.FindAnyObjectByType<RoomBootstrap>();
                yield return SceneFlow.WaitForBoot(boot);
                Assert.IsNotNull(boot.FlatRoom, "Main.unity 必须携带 FlatRoom");
                Assert.IsTrue(boot.FlatRoom.activeSelf, "默认进入 Main.unity 必须激活 FlatRoom");
                Assert.AreEqual(AnchorTable.FlatRelativePath, AnchorTable.ActiveRelativePath,
                    "默认主场景必须切换至 anchors-flat.json");

                var sky = GameObject.Find("night-sky-card");
                Assert.IsNull(sky, "默认平层主场景不得保持 night-sky-card 激活");
                var roomLights = GameObject.Find("room-lights");
                Assert.IsNull(roomLights, "默认平层主场景不得保持卧室 room-lights 激活");

                var table = AnchorTable.Load();
                Assert.IsNotNull(table);
                Assert.IsNotNull(table.defaultSpawn);
                Assert.AreEqual("living", table.defaultSpawn.room);

                var dist = Vector2.Distance(
                    new Vector2(boot.Player.transform.position.x, boot.Player.transform.position.z),
                    new Vector2(table.defaultSpawn.x, table.defaultSpawn.z));
                Assert.Less(dist, 0.15f, "玩家开场必须落在客厅大沙发前方 defaultSpawn");
                Assert.AreEqual("S1", boot.Actor.SeatId, "她开场必须坐在客厅大沙发 S1");
                Assert.IsTrue(boot.Actor.Seat.Seated);

                Assert.IsTrue(boot.Actions.TryEnterAction(ActionId.Hands),
                    "默认平层开场直接按 1 牵手必须成功: " + boot.Actions.LastFaults);
                ActionTestDrive.ToHold(boot.Actions);
                Assert.LessOrEqual(boot.Actions.ContactError, ActionDirector.ContactTolerance);
                boot.Actions.Cancel();

                Assert.IsTrue(boot.Actions.TryEnterAction(ActionId.SitTogether),
                    "默认平层开场直接按 5 并坐必须成功: " + boot.Actions.LastFaults);
                ActionTestDrive.ToHold(boot.Actions);
                Assert.LessOrEqual(boot.Actions.ContactError, ActionDirector.ContactTolerance);
                boot.Actions.Cancel();
            }
            finally
            {
                SceneFlow.UnloadMainIfLoaded();
            }
        }
    }
}
