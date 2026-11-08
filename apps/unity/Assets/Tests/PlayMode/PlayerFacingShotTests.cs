using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mas3D.Tests
{
    /// <summary>
    /// 朝向修复的画面证据（m21591）。两条默认套件用例（PlayerFacingTests）量的是角度，
    /// 这一条把真场景 + 真身体画进 PNG：平移时身体必须背对镜头（看到后背），不许侧过来横在画面里；
    /// 站着把镜头转到 120° 时身体要跟着转过去。
    /// [Explicit]：不进默认套件，单独点跑 —— 跟 BridgeSmokeTests.UiSceneShot 同一档。
    ///   tools\unity.ps1 run -- -executeMethod ... 不行（要在 PlayMode 里），直接：
    ///   unity.exe test apps\unity --mode PlayMode --filter Mas3D.Tests.PlayerFacingShotTests
    /// </summary>
    public class PlayerFacingShotTests
    {
        /// <summary>按 60 fps 跑 frames 帧，每帧喂同一个 wish（KeyboardInput 关掉，别让 Update 抢）。</summary>
        static IEnumerator Drive(PlayerMotor motor, Vector2 wish, int frames)
        {
            for (var i = 0; i < frames; i++)
            {
                motor.Tick(1f / 60f, wish);
                yield return null;
            }
        }

        static IEnumerator Shoot(Camera camera, string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            if (File.Exists(file)) File.Delete(file);

            var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = rt;
            for (var i = 0; i < 4; i++) yield return null;

            RenderTexture.active = rt;
            var texture = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            texture.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(file, texture.EncodeToPNG());

            camera.targetTexture = null;
            Object.Destroy(texture);
            rt.Release();
        }

        [UnityTest]
        [Explicit]
        public IEnumerator TheBodyStaysFacingAwayFromTheLensWhileStrafing()
        {
            SceneFlow.Require();
            yield return SceneFlow.LoadMain();
            var boot = Object.FindAnyObjectByType<RoomBootstrap>();
            yield return SceneFlow.WaitForBoot(boot);

            var motor = boot.Player;
            var rig = boot.Rig;
            var camera = boot.OutputCamera;
            Assert.IsNotNull(motor, "Main.unity 里必须有 PlayerMotor");
            Assert.IsNotNull(rig, "Main.unity 里必须有 PlayerCameraRig");
            Assert.IsNotNull(camera, "Main.unity 里必须有输出相机");

            var shots = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Shots"));
            try
            {
                motor.KeyboardInput = false;      // 演出/用例自己喂 wish
                rig.SetOrbit(0f, 14f);
                yield return null;
                yield return null;

                // 先把身体转到镜头方向（场景出生朝向不一定是 0°，TurnRate 270 °/s 要十几帧才收敛）。
                yield return Drive(motor, Vector2.zero, 45);
                Assert.Less(Mathf.Abs(Mathf.DeltaAngle(motor.transform.eulerAngles.y, 0f)), 1f,
                    "前置：站住时身体没有转向镜头");

                // 镜头朝 0°，人向 +X 平移：身体要保持 0°（背对镜头），不许拧向 90°。
                yield return Drive(motor, new Vector2(1f, 0f), 18);
                var strafeGap = Mathf.Abs(Mathf.DeltaAngle(motor.transform.eulerAngles.y, 0f));
                yield return Shoot(camera, Path.Combine(shots, "facing-strafe.png"));
                Assert.Less(strafeGap, 2f,
                    "平移时身体与视线差 " + strafeGap.ToString("F1") + "°：身体侧过来横在机位前");

                // 站着把镜头转到 120°：身体要跟着转，画面里仍然只有后背。
                rig.SetOrbit(120f, 14f);
                yield return null;
                yield return Drive(motor, Vector2.zero, 30);
                var turnGap = Mathf.Abs(Mathf.DeltaAngle(motor.transform.eulerAngles.y, 120f));
                yield return Shoot(camera, Path.Combine(shots, "facing-turn.png"));
                Assert.Less(turnGap, 2f,
                    "站住转镜头后身体与视线差 " + turnGap.ToString("F1") + "°：朝向没有跟镜头");
            }
            finally
            {
                SceneFlow.UnloadMainIfLoaded();
            }
        }
    }
}
