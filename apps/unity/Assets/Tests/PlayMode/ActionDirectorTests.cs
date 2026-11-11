using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Mas3D;
using Mas3D.Actions;
using Mas3D.Avatar;

namespace Mas3D.Tests
{
    public class ActionDirectorTests
    {
        ActionDirector _director;
        AvatarActor _actor;
        PlayerMotor _player;
        PlayerCameraRig _camera;
        Camera _lens;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            AvatarPrefabFixture.Require();
            _actor = AvatarActor.Spawn();
            var go = new GameObject("Player");
            go.AddComponent<CharacterController>();
            _player = go.AddComponent<PlayerMotor>();
            // 真实默认值：进场"从可走到被锁"才有意义，Cancel 也才能证明交还的是进场前的值
            _player.KeyboardInput = true;
            _player.EnsureReady();
            var camGo = new GameObject("CameraRig");
            _camera = camGo.AddComponent<PlayerCameraRig>();
            // 真的建一次机位：Output 为 null 时 PlayerCameraRig.Update 直接返回，鼠标接管与还原
            // 这两条通道就永远走不到，等于没夹具。
            _lens = new GameObject("TestCamera").AddComponent<Camera>();
            _camera.Build(_player.Head, _lens);
            _camera.MouseInput = true;
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
            if (_director != null) Object.Destroy(_director.gameObject);
            if (_actor != null) Object.Destroy(_actor.gameObject);
            if (_camera != null) Object.Destroy(_camera.gameObject);
            if (_lens != null) Object.Destroy(_lens.gameObject);
            if (_player != null) Object.Destroy(_player.gameObject);
            yield return null;
        }

        [Test]
        public void EveryActionHasAnId()
        {
            CollectionAssert.AreEquivalent(
                new[] { "hands", "pat-her", "pat-me", "kiss", "sit-together" },
                ActionId.All, "五个动作 id 必须齐");
        }

        [UnityTest]
        public IEnumerator EnteringHandsLocksThePlayerAndMarksTheSpot()
        {
            yield return null;
            Assert.IsTrue(_player.KeyboardInput, "夹具起手必须能走，进场才谈得上锁");
            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands), "她坐在 S1 上，牵手必须能进场");
            Assert.AreEqual(ActionId.Hands, _director.Current, "Current 必须是 hands");
            Assert.IsFalse(_player.KeyboardInput, "演出态必须锁住键盘");
            var seat = AnchorTable.Load().Seat("S1");
            // 进场是走过去的，不是瞬移：落点和朝向在 Hold 的第一帧才成立（走不到的部分由收尾的
            // 一步贴上）。时钟也只在 Hold 里记接触，所以同一时刻量完再验记账。
            ActionTestDrive.ToHold(_director);
            var want = new Vector3(seat.x, 0f, seat.z) + Quaternion.Euler(0f, seat.facingY, 0f) * Vector3.forward * 0.62f;
            Assert.AreEqual(want.x, _player.transform.position.x, 0.02f, "玩家必须被摆到她正前方 0.62 m");
            Assert.AreEqual(want.z, _player.transform.position.z, 0.02f, "x/z 都要对上");
            Assert.AreEqual(90f, _player.transform.eulerAngles.y, 1f, "玩家必须面向她（她朝 -X，所以玩家朝 +X）");
            var held = _director.Held;
            _director.Tick(0.5f);
            Assert.AreEqual(held + 0.5f, _director.Held, 1e-4f, "Tick 必须把时间记进 Held");
        }

        [UnityTest]
        public IEnumerator CancelRestoresThePlayerAndTheCamera()
        {
            yield return null;

            // 先蹲下、挪到别处，让"还原"确实有东西可还原
            _player.SetPosture(PlayerPosture.Crouching);
            _player.Teleport(new Vector3(0.4f, 0f, -0.3f), 37f);
            yield return null;
            var wasSpot = _player.transform.position;
            var wasPlayerYaw = _player.transform.eulerAngles.y;
            var wasCameraYaw = _camera.Yaw;
            var wasCameraPitch = _camera.Pitch;

            // 进场前键盘已经是关的：Cancel 必须交还"它拿到的那个值"，而不是强行打开
            _player.KeyboardInput = false;
            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands));
            Assert.AreEqual(PlayerPosture.Standing, _player.Posture, "进场后必须换成站姿");

            _director.Cancel();
            yield return null;

            Assert.IsNull(_director.Current, "Cancel 之后必须回到 idle");
            Assert.IsFalse(_player.KeyboardInput, "Cancel 必须把键盘交还给进场前的那个人，而不是强行打开");
            Assert.AreEqual(PlayerPosture.Crouching, _player.Posture, "Cancel 之后姿势必须还原成 Crouching");
            Assert.Less((_player.transform.position - wasSpot).magnitude, 1e-3f, "Cancel 之后位置必须回到原地");
            Assert.AreEqual(wasPlayerYaw, _player.transform.eulerAngles.y, 1e-3f, "朝向必须还原");
            Assert.AreEqual(wasCameraYaw, _camera.Yaw, 1e-3f, "相机 yaw 必须还原");
            Assert.AreEqual(wasCameraPitch, _camera.Pitch, 1e-3f, "相机 pitch 必须还原");
            Assert.AreEqual(1, _director.ClearPoseCount, "演出结束必须把手交还给姿势层一次");

            // 二次 Cancel 必须安全，且空闲时不许再碰姿势层（否则 Task 4 会把陈旧姿势写回她身上）
            _director.Cancel();
            Assert.AreEqual(1, _director.ClearPoseCount, "空闲时 Cancel 不许调用 ClearPose");

            // 反方向：真实进场恒为 true（PlayerMotor.cs:21 与 Main.unity 都是 1）。
            // 这一段只有「快照 + 归还」能过：强制 true 会在上面 false 分支红，完全不还原会在这里红。
            _player.KeyboardInput = true;
            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands), "站在她面前第二次进场也必须成功");
            Assert.IsFalse(_player.KeyboardInput, "进场必须锁住键盘");
            _director.Cancel();
            Assert.IsTrue(_player.KeyboardInput, "进场前能走，Cancel 后就必须还能走");
        }

        [UnityTest]
        public IEnumerator DisablingTheDirectorReleasesThePlayer()
        {
            // Esc 与 OnDisable 是玩家被锁住时仅有的两条退路；Esc 要注入输入，OnDisable 这里就能压。
            yield return null;
            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands), "牵手必须能进场");
            Assert.IsFalse(_player.KeyboardInput, "进场后键盘必须被锁");
            _director.enabled = false;
            Assert.IsNull(_director.Current, "关掉导演必须当场取消演出");
            Assert.IsTrue(_player.KeyboardInput, "关掉导演必须把键盘还回来，否则玩家永久动不了");
            _director.enabled = true;
            yield return null;
        }

        [UnityTest]
        public IEnumerator AnUnknownIdIsRefusedAndChangesNothing()
        {
            yield return null;
            Assert.IsFalse(_director.TryEnterAction("dance"), "未知动作必须拒绝");
            Assert.IsNull(_director.Current, "被拒绝时不许进入任何状态");
            Assert.IsTrue(_player.KeyboardInput, "被拒绝时不许锁键盘");
            StringAssert.StartsWith("unknown action", _director.LastFaults, "未知 id 必须报 unknown action，不能混进未实现分支");
        }

        [UnityTest]
        public IEnumerator ASecondEntryIsIgnoredWhileOneRuns()
        {
            yield return null;
            Assert.IsTrue(_director.TryEnterAction(ActionId.Hands));
            ActionTestDrive.ToHold(_director);   // Held 只在 Hold 里记账
            var held = _director.Held;
            _director.Tick(0.5f);
            // 同一个动作、同样可达：唯一的理由只能是"已经在演出"
            Assert.IsFalse(_director.TryEnterAction(ActionId.Hands), "动作进行中再按同一个动作也必须被忽略");
            Assert.AreEqual(ActionId.Hands, _director.Current, "Current 不许被第二次调用改掉");
            Assert.AreEqual(held + 0.5f, _director.Held, 1e-4f, "第二次进场不许把 Held 归零");
            StringAssert.StartsWith("busy", _director.LastFaults, "第二次进场必须报 busy");
        }

        [UnityTest]
        public IEnumerator AnUnreachableEntryIsRefused()
        {
            yield return null;
            // 把她放到房间另一头：任何手臂都够不着她，入场必须被拒绝而不是把她拖过来
            _actor.transform.position += new Vector3(4f, 0f, 0f);
            yield return null;
            var beforeSpot = _player.transform.position;
            var beforeYaw = _player.transform.eulerAngles.y;
            var beforeCameraYaw = _camera.Yaw;
            Assert.IsFalse(_director.TryEnterAction(ActionId.Hands), "够不着时必须返回 false");
            Assert.IsNull(_director.Current, "被拒绝后仍是 idle");
            Assert.IsTrue(_player.KeyboardInput, "被拒绝后玩家还能走");
            Assert.Less((_player.transform.position - beforeSpot).magnitude, 1e-4f, "被拒绝时玩家一步都不许被挪");
            Assert.AreEqual(beforeYaw, _player.transform.eulerAngles.y, 1e-4f, "被拒绝时朝向不许变");
            Assert.AreEqual(beforeCameraYaw, _camera.Yaw, 1e-4f, "被拒绝时相机不许动");
            StringAssert.StartsWith(ActionId.Hands + ": out of reach", _director.LastFaults, "拒绝理由必须是够不着");
        }
    }
}
