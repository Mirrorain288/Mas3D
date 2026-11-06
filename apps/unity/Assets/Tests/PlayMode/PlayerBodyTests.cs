using System.Linq;
using System.Collections;
using Mas3D.Avatar;
using Mas3D.Boot;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mas3D.Tests
{
    /// <summary>
    /// Task 3 of the P7 plan: the player stops being an empty capsule. These cases pin the four
    /// things a body has to get right and nothing else — it is visible, it owns no collider (the
    /// CharacterController is the only thing that may collide), it stands on the player's feet and
    /// turns with him, and its head gets out of the way when the boom is pressed against the wall.
    /// The VRM is an imported asset, so the same asset-missing guard the avatar tests use applies:
    /// no Me.vrm on this machine means Ignore, never a green run without a body.
    /// </summary>
    public class PlayerBodyTests
    {
        GameObject _player;
        GameObject _cameraGo;
        PlayerMotor _motor;
        PlayerCameraRig _rig;
        PlayerBody _body;
        Camera _camera;

        [SetUp]
        public void SetUp()
        {
            _player = new GameObject("Player");
            _player.tag = "Player";
            _player.AddComponent<CharacterController>();
            _motor = _player.AddComponent<PlayerMotor>();
            _motor.KeyboardInput = false;   // nothing here moves him but the test

            _rig = _player.AddComponent<PlayerCameraRig>();

            _cameraGo = new GameObject("Main Camera");
            _cameraGo.tag = "MainCamera";
            _camera = _cameraGo.AddComponent<Camera>();
            _camera.clearFlags = CameraClearFlags.SolidColor;

            _body = _player.AddComponent<PlayerBody>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_cameraGo) Object.DestroyImmediate(_cameraGo);
            if (_player) Object.DestroyImmediate(_player);
            _cameraGo = null;
            _player = null;
            _camera = null;
            _motor = null;
            _rig = null;
            _body = null;
        }

        static void RequireBodyAsset()
        {
            if (!PlayerBody.PrefabAvailable)
                Assert.Ignore("缺 " + PlayerBody.BodyPrefabPath + "：玩家身体用例需要这台机器上导入好的 VRM");
        }

        void AttachAndSpawn()
        {
            _body.Attach(_motor, _motor.Head, _rig);
            Assert.IsTrue(_body.Spawn(), "Spawn() 失败：" + PlayerBody.BodyPrefabPath);
            _body.ApplyNow();
        }

        Renderer[] Visible()
        {
            return _player.GetComponentsInChildren<Renderer>(true)
                .Where(r => r != null && r.enabled && r.gameObject.activeInHierarchy)
                .ToArray();
        }

        /// <summary>Union of the renderers a camera would actually see - the honest height of the body.</summary>
        Bounds VisibleBounds()
        {
            var renderers = Visible();
            Assert.Greater(renderers.Length, 0, "身体没有任何可见 Renderer");
            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        static Vector2 Flat(Vector3 point) => new Vector2(point.x, point.z);

        [Test]
        public void APlayerBodyCarriesOneColliderAndManyRenderers()
        {
            RequireBodyAsset();
            AttachAndSpawn();

            // The body is scenery: a second collider on the player would fight CharacterController.Move
            // and shove the player through furniture from the inside.
            var colliders = _player.GetComponentsInChildren<Collider>(true);
            Assert.AreEqual(1, colliders.Length, "玩家层级只能有 CharacterController 一个碰撞体");
            Assert.IsInstanceOf<CharacterController>(colliders[0], "唯一的碰撞体必须是 CharacterController");
            Assert.AreEqual(0, _body.Avatar.GetComponentsInChildren<Collider>(true).Length,
                "身体自己带上了碰撞体");

            // The VRM imports as three skinned renderers (Face, Body, Hair) carrying 16 material slots
            // between them — that is the whole model, so "at least five renderers" was never reachable
            // with this asset. The honest version of the same rule: several renderers AND a material
            // count that only a real rigged model can produce.
            var renderers = Visible();
            var slots = renderers.Sum(r => r.sharedMaterials.Length);
            Assert.GreaterOrEqual(renderers.Length, 3, "可见 Renderer 只有 " + renderers.Length + " 个");
            Assert.GreaterOrEqual(slots, 5, "材质槽只有 " + slots + " 个，这不是一套完整模型");

            var bounds = VisibleBounds();
            Assert.That(bounds.size.y, Is.InRange(1.5f, 1.9f),
                "身体世界高度 " + bounds.size.y.ToString("F2") + " m 不在玩家的 1.5-1.9 m 区间里");
            Assert.That(bounds.min.y, Is.InRange(_player.transform.position.y - 0.05f, _player.transform.position.y + 0.10f),
                "身体没有站在玩家脚底：min.y=" + bounds.min.y.ToString("F3")
                + " feet=" + _player.transform.position.y.ToString("F3"));

            var gap = Vector2.Distance(Flat(_body.Avatar.transform.position), Flat(_player.transform.position));
            Assert.LessOrEqual(gap, 0.02f, "身体 root 与玩家的 xz 差 " + gap.ToString("F3") + " m");

            Debug.Log(string.Format("[body] renderers={0} slots={1} height={2:F3} minY={3:F3} gap={4:F4}",
                renderers.Length, slots, bounds.size.y, bounds.min.y, gap));
        }

        [Test]
        public void TheBodyFollowsATeleport()
        {
            RequireBodyAsset();
            AttachAndSpawn();

            var table = AnchorTable.Load();
            Assert.IsNotNull(table, "anchors.json missing; run tools/unity.ps1 anchors first");
            var a1 = table.Anchor("A1");
            Assert.IsNotNull(a1, "anchor A1 missing");

            _motor.Teleport(a1.Feet(RoomBootstrap.SpawnLift), a1.facingY);
            _body.ApplyNow();
            Physics.SyncTransforms();

            var gap = Vector2.Distance(Flat(_body.Avatar.transform.position), Flat(_player.transform.position));
            Assert.LessOrEqual(gap, 0.02f, "Teleport 后身体落在 " + gap.ToString("F3") + " m 之外");

            // The model must face where the player faces. A VRM whose forward is -z would pass every
            // position assertion above and still walk backwards, so the yaw is its own assertion.
            var yawGap = Mathf.Abs(Mathf.DeltaAngle(_body.Avatar.transform.eulerAngles.y, _player.transform.eulerAngles.y));
            Assert.LessOrEqual(yawGap, 1f, "身体朝向与玩家差 " + yawGap.ToString("F2") + " 度");

            var bounds = VisibleBounds();
            Assert.That(bounds.min.y, Is.InRange(_player.transform.position.y - 0.05f, _player.transform.position.y + 0.10f),
                "Teleport 到 A1 后身体没有落在脚底：min.y=" + bounds.min.y.ToString("F3"));

            Debug.Log(string.Format("[body] teleport A1 gap={0:F4} yawGap={1:F2} minY={2:F3} player=({3:F2},{4:F2})",
                gap, yawGap, bounds.min.y, _player.transform.position.x, _player.transform.position.z));
        }

        [UnityTest]
        public IEnumerator TheHeadIsHiddenWhenTheCameraComesClose()
        {
            RequireBodyAsset();
            AttachAndSpawn();

            _rig.SetDistance(2.8f);
            yield return null;
            Assert.IsFalse(_body.HeadHidden, "第三人称距离下头不该藏起来");
            var far = Visible().Length;
            Assert.GreaterOrEqual(_body.HeadRendererCount, 1, "一个头部 renderer 都没识别出来");
            Assert.Greater(far, _body.HeadRendererCount, "头把整个身体都算进去了");

            _rig.SetDistance(1.0f);
            yield return null;
            Assert.IsTrue(_body.HeadHidden, "镜头压到 1.0 m 时头必须藏起来");
            foreach (var head in _body.HeadRenderers)
                Assert.IsFalse(head.enabled, "头部 renderer 还开着：" + head.name);
            var near = Visible().Length;
            Assert.AreEqual(far - _body.HeadRendererCount, near, "藏掉的 renderer 数不对");
            Assert.Greater(near, 0, "头藏起来之后身体也不见了——第三人称要看得见自己");

            _rig.SetDistance(2.8f);
            yield return null;
            Assert.IsFalse(_body.HeadHidden, "退到 2.8 m 后头没有恢复");
            Assert.AreEqual(far, Visible().Length, "恢复后可见 renderer 数不对");

            Debug.Log(string.Format("[body] head renderers={0} visible far={1} near={2}",
                _body.HeadRendererCount, far, near));
        }

        [Test]
        public void TheBodySinksWithTheCrouchedPosture()
        {
            RequireBodyAsset();
            AttachAndSpawn();

            var standing = _body.Avatar.transform.localPosition.y;
            Assert.AreEqual(0f, standing, 0.001f, "站着时身体应当正好落在脚底");

            _motor.SetPosture(PlayerPosture.Crouching);
            _body.ApplyNow();
            var (crouchEye, _, _) = PlayerMotor.Metrics(PlayerPosture.Crouching);
            var (standEye, _, _) = PlayerMotor.Metrics(PlayerPosture.Standing);
            var wantCrouch = crouchEye - standEye;
            Assert.AreEqual(wantCrouch, _body.Avatar.transform.localPosition.y, 0.02f,
                "蹲下的整体下沉没有跟眼睛走");

            _motor.SetPosture(PlayerPosture.Sitting);
            _body.ApplyNow();
            var (sitEye, _, _) = PlayerMotor.Metrics(PlayerPosture.Sitting);
            Assert.AreEqual(sitEye - standEye, _body.Avatar.transform.localPosition.y, 0.02f,
                "坐下的整体下沉没有跟眼睛走");

            _motor.SetPosture(PlayerPosture.Standing);
            _body.ApplyNow();
            Assert.AreEqual(0f, _body.Avatar.transform.localPosition.y, 0.001f, "站起来后身体没有回到脚底");

            Debug.Log(string.Format("[body] sink crouch={0:F3} sit={1:F3}", wantCrouch, sitEye - standEye));
        }

        [Test]
        public void APlayerWithoutABodyKeepsNoRenderer()
        {
            // No Attach, no Spawn: this is what -nobody 1 and every deterministic still rely on.
            Assert.IsFalse(_body.Spawn(), "没 Attach 就 Spawn 应当被拒");
            Assert.AreEqual(0, _player.GetComponentsInChildren<Renderer>(true).Length,
                "没挂身体时玩家层级不该有 Renderer");
            Assert.IsNull(_body.Avatar, "失败的 Spawn 不该留下半截模型");
        }

        [Test]
        public void TheNobodyFlagTurnsTheBodyOff()
        {
            Assert.IsTrue(BootArgs.Parse(new[] { "-nobody", "1" }).NoBody, "-nobody 1 没有解析出来");
            Assert.IsTrue(BootArgs.Parse(new[] { "-nobody" }).NoBody, "单独的 -nobody 也应当算开");
            Assert.IsFalse(BootArgs.Parse(new string[0]).NoBody, "默认必须带身体");
            Assert.IsFalse(BootArgs.Parse(new[] { "-selftest", "x.log" }).NoBody, "-selftest 不该带出 -nobody");
        }
    }
}
