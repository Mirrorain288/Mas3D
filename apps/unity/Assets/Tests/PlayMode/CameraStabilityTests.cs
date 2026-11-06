using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mas3D.Tests
{
    /// <summary>
    /// S3 相机骨架的判据。三条用例各钉住一条「结构漏到画面」的通道：
    /// ① 绕圈走时，Cinemachine 取景那一刻 Pivot 的朝向必须等于我们要求的轨道 yaw —— 相机不吃玩家的转身；
    /// ② 行走方向来自意图 yaw，不来自渲染相机的欧拉角 —— 否则阻尼滞后会被喂回移动方向，
    ///    机器自己追自己，画面持续左右摆；
    /// ③ 鼠标锁住（MouseInput = false）时姿态仍要每帧写回：Esc/对话里玩家转身不许把镜头一起拖走。
    /// 时间一律按模拟时间累加（Time.deltaTime），不数帧 —— 批处理一帧可能只有几十微秒。
    /// </summary>
    public class CameraStabilityTests
    {
        GameObject _playerObject;
        GameObject _cameraObject;
        PlayerMotor _motor;
        PlayerCameraRig _rig;
        Camera _camera;

        [SetUp]
        public void SetUp()
        {
            _playerObject = new GameObject("Player");
            _playerObject.tag = "Player";
            _playerObject.AddComponent<CharacterController>();
            _motor = _playerObject.AddComponent<PlayerMotor>();
            _motor.KeyboardInput = false;     // 用例自己调 Tick，不许 Update 再偷偷走一步
            _motor.EnsureReady();
            _rig = _playerObject.AddComponent<PlayerCameraRig>();

            _cameraObject = new GameObject("Main Camera");
            _cameraObject.tag = "MainCamera";
            _camera = _cameraObject.AddComponent<Camera>();
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.nearClipPlane = 0.05f;

            _rig.Build(_motor.Head, _camera);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_playerObject);
            UnityEngine.Object.DestroyImmediate(_cameraObject);
        }

        /// <summary>
        /// 每秒 120° 的圆，走两秒。读数取在「本帧最后一次转身之后」：那正是 Cinemachine 在
        /// LateUpdate 取景时看到的姿态。旧结构里 Pivot 挂在 head 下，head 被 TurnRate 拧着转，
        /// 残差就是这一帧转掉的角度（60 fps、120 °/s ≈ 2°，远超 0.5°）；符号还会在过零后翻转。
        /// </summary>
        [UnityTest]
        public IEnumerator WalkingInACircleKeepsThePivotOnTheAngleWeAskedFor()
        {
            _rig.MouseInput = true;           // 真实路径：鼠标输入开着，姿态在 Update 里写
            _rig.SetOrbit(0f, 14f);
            yield return null;

            var elapsed = 0f;
            var worst = 0f;
            var flips = 0;
            var lastSign = 0;
            var heading = 0f;

            while (elapsed < 2f)
            {
                heading += 120f * Time.deltaTime;
                var radians = heading * Mathf.Deg2Rad;
                _motor.Tick(Time.deltaTime, new Vector2(Mathf.Cos(radians), Mathf.Sin(radians)));

                var residual = Mathf.DeltaAngle(_rig.Pivot.eulerAngles.y, _rig.Yaw);
                worst = Mathf.Max(worst, Mathf.Abs(residual));
                var sign = Mathf.Abs(residual) < 1e-3f ? 0 : (residual > 0f ? 1 : -1);
                if (sign != 0 && lastSign != 0 && sign != lastSign) flips++;
                if (sign != 0) lastSign = sign;

                yield return null;
                elapsed += Time.deltaTime;
            }

            Assert.Less(worst, 0.5f,
                "取景时 Pivot 与 Yaw 的残差最大 " + worst.ToString("F2") + "°：Pivot 还在吃玩家的转身");
            Assert.AreEqual(0, flips, "残差翻转过 " + flips + " 次：画面会左右摆");
            Assert.Greater(elapsed, 1.9f, "两秒没走完，用例根本没跑起来");
        }

        /// <summary>
        /// 把渲染相机移出场外再读方向：方向只许来自 rig.IntentYaw。
        /// 旧实现读 Camera.main.eulerAngles.y，相机不在场上时退化成 0°，这条会红。
        /// </summary>
        [UnityTest]
        public IEnumerator TheWishDirectionFollowsTheIntentYawNotTheRenderedCamera()
        {
            _rig.MouseInput = false;
            _rig.SetOrbit(90f, 0f);
            _motor.AxisInput = () => new Vector2(1f, 0f);   // 注入「往右推到底」的原始轴
            _cameraObject.SetActive(false);                 // 渲染相机不在场上，谁也借不到它的 yaw
            yield return null;

            Assert.AreEqual(90f, _rig.IntentYaw, 0.01f, "前置：意图 yaw 就是 90°");
            var wish = _motor.ReadWish();
            var expected = new Vector2(0f, -1f);            // 绕 Y 转 90°，+x 落到 -z
            Assert.Less(Vector2.Distance(wish, expected), 0.01f,
                "W 的方向必须来自意图 yaw = 90°，实际 " + wish.ToString("F3"));
        }

        /// <summary>
        /// 鼠标锁住时姿态仍要每帧写回（设计 §5.7：MouseInput 只锁输入，不再跳过姿态）。
        /// 玩家原地转身 120°，Pivot 的世界 yaw 必须还是我们给的那个 30°。
        /// </summary>
        [UnityTest]
        public IEnumerator AMouseLockedRigKeepsThePivotOutOfTheTurningHead()
        {
            _rig.MouseInput = false;
            _rig.SetOrbit(30f, 12f);
            yield return null;

            _motor.Teleport(_playerObject.transform.position, 120f);
            for (var i = 0; i < 3; i++) yield return null;

            var residual = Mathf.DeltaAngle(_rig.Pivot.eulerAngles.y, 30f);
            Assert.Less(Mathf.Abs(residual), 0.5f,
                "鼠标锁住时 Pivot 仍被 head 拖着转了 " + residual.ToString("F2") + "°");
        }

        // ---- Task 4：真第一人称 ----

        /// <summary>
        /// 鼠标上移 10 个单位（Sensitivity 2.2）= 抬 22°。
        ///
        /// 判据故意写成"视线朝上"而不是"Pitch 变大"：Pitch 走 Quaternion.Euler 的 X 轴，正数是低头，
        /// 所以抬视线对应 Pitch = −22。上一版用例把 Pitch 变正当成正确，AddOrbit 里的加号就被这条
        /// 用例保护着，实机上鼠标上移反而低头（2026-11-06 反馈）。数值判据必须落在最终朝向上。
        /// </summary>
        [UnityTest]
        public IEnumerator MovingTheMouseUpToRaisesTheCamera()
        {
            _rig.SetOrbit(0f, 0f);
            yield return null;

            _rig.AddOrbit(0f, 10f);
            yield return null;

            var forward = Quaternion.Euler(_rig.Pitch, _rig.Yaw, 0f) * Vector3.forward;
            Assert.Greater(forward.y, 0.1f,
                "鼠标上移必须把视线抬起来，现在 forward.y = " + forward.y.ToString("F3"));
            Assert.That(Mathf.Abs(_rig.Pitch + 22f), Is.LessThan(0.5f),
                "Sensitivity 2.2 × 10 单位 = 抬 22°（Pitch = −22），实际 " + _rig.Pitch.ToString("F2"));
        }

        /// <summary>
        /// 最近距离（0.6 m）真的贴到头：Distance 不再被 0.9 的老下限夹住，boom 在 0.15 s 内
        /// 收到头里，相机与 head 相距 8 cm 内。按模拟时间等收敛，不数帧 —— 批处理一帧可能几十微秒。
        /// </summary>
        [UnityTest]
        public IEnumerator TheClosestDistancePutsTheCameraAtTheHead()
        {
            _rig.SetDistance(PlayerCameraRig.FirstPersonDistance);
            Assert.AreEqual(0.6f, _rig.Distance, 0.001f, "最近距离必须能到 0.6 m，旧下限是 0.9");
            Assert.IsTrue(_rig.IsFirstPerson, "0.6 m 必须算第一人称");

            var elapsed = 0f;
            var gap = float.PositiveInfinity;
            while (elapsed < 1.5f)
            {
                yield return null;
                elapsed += Time.deltaTime;
                gap = Vector3.Distance(_rig.CameraPosition, _motor.Head.position);
                if (gap <= 0.06f) break;
            }

            Assert.Less(gap, 0.08f,
                "第一人称相机没贴到头：相差 " + gap.ToString("F3") + " m（等了 " + elapsed.ToString("F2") + " s）");
        }

        /// <summary>
        /// 藏头的阈值必须比「算第一人称」的那条线更松：正好站在 1.2 m 这条线上时，
        /// 相机已经在头里，藏头阈值若也是 1.2（严格小于）就会漏出后脑勺。
        /// </summary>
        [Test]
        public void TheHeadThresholdIsSlackerThanTheFirstPersonThreshold()
        {
            Assert.Greater(PlayerBody.NearDistance, PlayerCameraRig.FirstPersonAtOrBelow,
                "PlayerBody.NearDistance（" + PlayerBody.NearDistance.ToString("F2") +
                "）必须大于 PlayerCameraRig.FirstPersonAtOrBelow（" +
                PlayerCameraRig.FirstPersonAtOrBelow.ToString("F2") + "），否则 1.2 m 那一档会露出后脑勺");
        }    }
}
