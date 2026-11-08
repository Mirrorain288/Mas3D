using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mas3D.Tests
{
    /// <summary>
    /// 玩家身体的朝向判据。身体是玩家根的子物体，PlayerBody 只写 localPosition、从不写 rotation，
    /// 所以「身体朝哪」完全由 PlayerMotor 对根节点的转身决定。旧的 Tick 只在 wish≠0 时把根转向
    /// 移动方向，于是：站住转鼠标身体不动；平移/后退时身体转 90–180°，肩部与头发横穿镜头挡住视野。
    /// 两条用例分别钉住这两种表现：朝向只跟镜头意图 yaw，不跟移动方向。
    /// </summary>
    public class PlayerFacingTests
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

        /// <summary>按模拟时间跑 seconds 秒，每帧喂同一个 wish。</summary>
        void Run(float seconds, Vector2 wish)
        {
            var elapsed = 0f;
            while (elapsed < seconds)
            {
                var dt = Mathf.Min(Time.deltaTime, 0.05f);
                _motor.Tick(dt, wish);
                elapsed += dt;
            }
        }

        /// <summary>
        /// 站着不动把镜头转到 90°：身体必须跟着转过去。TurnRate 270 °/s，0.5 s 足够走完 90°。
        /// 旧实现 wish==0 时整段不转，实测残差 90°，这条会精确红在下面的断言上。
        /// </summary>
        [UnityTest]
        public IEnumerator TheBodyTurnsToTheViewEvenWhenStandingStill()
        {
            _rig.SetOrbit(90f, 14f);
            yield return null;

            Run(0.5f, Vector2.zero);

            var gap = Mathf.Abs(Mathf.DeltaAngle(_playerObject.transform.eulerAngles.y, 90f));
            Assert.Less(gap, 1.5f,
                "站住转镜头后身体与视线差 " + gap.ToString("F1") + "°：朝向没有跟镜头");
        }

        /// <summary>
        /// 镜头朝 0°（朝 +Z），wish 指向 +X（角色向右手边平移）：身体要一直朝 0°，不许转向移动方向。
        /// 旧实现 0.5 s 内把根拧到 90°，肩部横在机位与房间之间。
        /// </summary>
        [UnityTest]
        public IEnumerator StrafingKeepsTheBodyFacingTheView()
        {
            _rig.SetOrbit(0f, 14f);
            yield return null;

            var start = _playerObject.transform.position;
            Run(0.5f, new Vector2(1f, 0f));

            var gap = Mathf.Abs(Mathf.DeltaAngle(_playerObject.transform.eulerAngles.y, 0f));
            Assert.Less(gap, 1.5f,
                "平移时身体与视线差 " + gap.ToString("F1") + "°：跟着移动方向转身了");
            Assert.Greater(_playerObject.transform.position.x - start.x, 0.05f,
                "身体朝视线方向站着，但人没有真的向右平移");
        }
    }
}
