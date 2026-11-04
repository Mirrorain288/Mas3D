using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mas3D.Tests
{
    public class PlayerPostureTests
    {
        static PlayerMotor MakePlayer(Vector3 feet, float yaw)
        {
            var go = new GameObject("Player");
            go.AddComponent<CharacterController>();
            var motor = go.AddComponent<PlayerMotor>();
            motor.KeyboardInput = false;
            motor.EnsureReady();
            motor.Teleport(feet, yaw);
            return motor;
        }

        [UnityTest]
        public IEnumerator CrouchingLowersTheEyeAndShortensTheCapsule()
        {
            var player = MakePlayer(Vector3.zero, 0f);
            try
            {
                player.SetPosture(PlayerPosture.Crouching);
                yield return null;
                var controller = player.GetComponent<CharacterController>();
                Assert.AreEqual(1.05f, player.Head.localPosition.y, 0.001f, "蹲下后眼高必须是 1.05 m");
                Assert.AreEqual(1.10f, controller.height, 0.001f, "蹲下后胶囊高度必须是 1.10 m");
                Assert.AreEqual(0.55f, controller.center.y, 0.001f, "胶囊中心必须跟着降到 0.55 m");
            }
            finally { Object.DestroyImmediate(player.gameObject); }
        }

        [UnityTest]
        public IEnumerator KneelingPutsTheEyeAt128AndClearsTheSeat()
        {
            var player = MakePlayer(Vector3.zero, 0f);
            try
            {
                player.SetPosture(PlayerPosture.Kneeling);
                yield return null;
                var controller = player.GetComponent<CharacterController>();
                Assert.AreEqual(1.28f, player.Head.localPosition.y, 0.001f, "跪下后眼高必须是 1.28 m");
                Assert.AreEqual(1.32f, controller.height, 0.001f, "跪姿胶囊高度必须是 1.32 m");
                Assert.AreEqual(0.66f, controller.center.y, 0.001f, "胶囊中心必须跟着降到 0.66 m");
                Assert.Greater(player.Head.position.y, 1.004f, "跪姿 eye 必须高于座面 topY 0.604 + 0.40 m：否则镜头会掉进坐垫");
            }
            finally { Object.DestroyImmediate(player.gameObject); }
        }

        [UnityTest]
        public IEnumerator StandingBackRestoresTheMetrics()
        {
            var player = MakePlayer(Vector3.zero, 0f);
            try
            {
                player.SetPosture(PlayerPosture.Sitting);
                player.SetPosture(PlayerPosture.Standing);
                yield return null;
                Assert.AreEqual(1.62f, player.Head.localPosition.y, 0.001f, "站起来眼高必须回到 1.62 m");
                Assert.AreEqual(1.70f, player.GetComponent<CharacterController>().height, 0.001f, "胶囊高度必须回到 1.70 m");
            }
            finally { Object.DestroyImmediate(player.gameObject); }
        }

        [UnityTest]
        public IEnumerator ThePalmsHangInFrontOfTheEyeAndAnArmSpanApart()
        {
            var player = MakePlayer(new Vector3(1f, 0f, 2f), 90f);   // 面向 +X
            try
            {
                yield return null;
                var eye = player.Head.position;
                var left = PlayerReach.Palm(player, true);
                var right = PlayerReach.Palm(player, false);
                // Task 8 moved the marker from 0.35 forward / 0.32 apart to 0.24 / 0.44: at the old
                // numbers the sink put her hands on her own belly and the capture read as her clasping
                // her own hands. The forward reach and the sideways spread are the two numbers that
                // decide that reading, so they stay pinned here, exactly as tightly as before.
                Assert.AreEqual(0.24f, left.x - eye.x, 0.01f, "掌心必须在眼前 0.24 m");
                Assert.AreEqual(eye.y - 0.62f, left.y, 0.01f, "掌心必须在眼下 0.62 m");
                Assert.AreEqual(0.44f, right.z - left.z, 0.01f, "两只手必须左右分开 0.44 m");
                Assert.AreEqual(1.00f, left.y, 0.01f, "站姿掌心高度约 1.00 m：她坐着正好够");
            }
            finally { Object.DestroyImmediate(player.gameObject); }
        }

        [UnityTest]
        public IEnumerator StandingPutsTheCrownOutOfHerReach()
        {
            var player = MakePlayer(Vector3.zero, 0f);
            try
            {
                player.SetPosture(PlayerPosture.Crouching);
                yield return null;
                Assert.Less(PlayerReach.Crown(player).y, 1.40f, "蹲下后头顶必须低于 1.40 m：她坐着才够得到");

                player.SetPosture(PlayerPosture.Standing);
                yield return null;
                Assert.Greater(PlayerReach.Crown(player).y, 1.74f, "站立头顶 1.78 m 超出她坐姿伸手能到的 1.74 m：pat-me 必须先蹲下");
            }
            finally { Object.DestroyImmediate(player.gameObject); }
        }
    }
}
