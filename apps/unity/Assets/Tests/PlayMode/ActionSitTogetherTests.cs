using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Mas3D;
using Mas3D.Actions;
using Mas3D.Avatar;

namespace Mas3D.Tests
{
    public class ActionSitTogetherTests
    {
        AvatarActor _actor;
        PlayerMotor _player;
        ActionDirector _director;
        AnchorTable _table;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            AvatarPrefabFixture.Require();
            _table = AnchorTable.Load();
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
            // 先收尾再拆对象：她把姿态借给动作，销毁路径上的 OnDisable 再 Cancel 一次就会碰已死的 rig。
            if (_director != null && _director.Current != null) _director.Cancel();
            if (_director != null) Object.Destroy(_director.gameObject);
            if (_actor != null) Object.Destroy(_actor.gameObject);
            if (_player != null) Object.Destroy(_player.gameObject);
            yield return null;
        }

        static readonly HumanBodyBones[] Legs =
        {
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
            HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
        };

        [UnityTest]
        public IEnumerator TheBedPoseKeepsEveryLegBoneAboveTheMattress()
        {
            var seat = _table.Seat("S2");
            _actor.SitOnBed("S2");
            yield return null;
            foreach (var bone in Legs)
            {
                var p = _actor.Rig.Bone(bone).position;
                Assert.GreaterOrEqual(p.y, seat.topY - 0.01f, bone + " 陷进床垫了：腿必须在床面之上");
            }
        }

        [UnityTest]
        public IEnumerator SittingOnTheBedSnapsTheHips()
        {
            var seat = _table.Seat("S2");
            _actor.SitOnBed("S2");
            yield return null;
            Assert.AreEqual(seat.topY + AvatarSeat.HipsGap, _actor.Seat.HipsWorld.y, 0.01f, "髋高必须等于床面 + HipsGap（±1 cm）");
            Assert.AreEqual(seat.x, _actor.Seat.HipsWorld.x, 0.02f, "髋位必须落在床面中线槽位上");
            Assert.AreEqual(seat.z, _actor.Seat.HipsWorld.z, 0.02f, "z 也要对上");
        }

        [UnityTest]
        public IEnumerator ThePlayerSitsOnTheSecondSlot()
        {
            Assert.IsTrue(_director.TryEnterAction(ActionId.SitTogether), "床面两个槽位都空着时，并肩坐必须能进场");
            yield return null;
            var seat = _table.Seat("S2");
            Assert.AreEqual(PlayerPosture.Sitting, _player.Posture, "玩家必须切成坐姿");
            Assert.AreEqual(seat.topY + 0.50f, _player.Head.position.y, 0.02f, "坐下后眼高必须是床面 + 0.50 m");
            // 字面量，不读 BedPair：导演内部调的就是同一个函数，自己跟自己比会把 0.70 改成 0.45 也放过。
            Assert.AreEqual(seat.x + 0.70f, _player.transform.position.x, 0.02f, "玩家必须坐在她右手边 0.70 m 的床面槽位上");
            Assert.AreEqual(seat.z, _player.transform.position.z, 0.02f, "z 也要对上");
            Assert.AreEqual(seat.facingY, _player.transform.eulerAngles.y, 2f, "两人必须朝同一个方向坐");
        }

        [UnityTest]
        public IEnumerator ABedSheCannotSitOnIsRefusedNotHalfEntered()
        {
            // 坐不上床以前是静默的：LogError 之后玩家照样被摆到床边、Current 置位，
            // DriveSitTogether 永远量到 1.5 m 的差距，界面却写着"演出中"。
            Object.Destroy(_actor.Seat);
            yield return null;
            LogAssert.Expect(LogType.Error, new Regex("no AvatarSeat on"));
            var feet = _player.transform.position;
            var yaw = _player.transform.eulerAngles.y;

            Assert.IsFalse(_director.TryEnterAction(ActionId.SitTogether), "她坐不上床时必须拒绝，不能把玩家摆到床边");
            Assert.IsNull(_director.Current, "拒绝之后不许停在一个空动作上");
            Assert.IsTrue(_player.KeyboardInput, "拒绝不许锁键盘");
            StringAssert.Contains("could not sit", _director.LastFaults, "拒绝理由必须说清是她的床姿没成");

            var after = _player.transform.position;
            Assert.Less(new Vector2(after.x - feet.x, after.z - feet.z).magnitude, 0.01f, "拒绝不许挪玩家");
            Assert.AreEqual(yaw, _player.transform.eulerAngles.y, 0.01f, "拒绝不许转玩家");
        }

        [UnityTest]
        public IEnumerator TheTwoBodiesStayFortyCentimetresApart()
        {
            Assert.IsTrue(_director.TryEnterAction(ActionId.SitTogether));
            yield return null;
            var hers = _actor.Seat.HipsWorld;
            var his = _player.transform.position;
            var gap = new Vector2(hers.x - his.x, hers.z - his.z).magnitude;
            Assert.GreaterOrEqual(gap, 0.40f, "两人髋位的水平间距必须 ≥ 0.40 m：胶囊半径 0.30 + 她身体 0.20，再近就穿模了");
        }
    }
}
