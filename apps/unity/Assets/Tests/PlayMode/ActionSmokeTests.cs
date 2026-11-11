using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Mas3D;
using Mas3D.Actions;

namespace Mas3D.Tests
{
    /// <summary>
    /// 一次回归里最贵的那一段：真实场景 + 真实导演，把五件动作各演一遍再各复位一次。
    /// 它不重复逐动作用例的几何判据（接触误差另有专门的用例），它证明的是「接线没断、
    /// 复位没漏」——这正是发布前最容易坏、又最不容易被单点用例发现的两处。
    ///
    /// 标记落 Logs/smoke-actions-marker.txt：Debug.Log 不进 NUnit XML，Unity CLI 也把测试进程
    /// 日志收在别处，文件是唯一活到脚本那一侧的通道。
    /// </summary>
    public class ActionSmokeTests
    {
        const string MarkerRelative = "Logs/smoke-actions-marker.txt";
        const float Step = 1f / 60f;
        const int StepsPerAction = 90;    // 1.5 s：三个动作的判据都要 >= 1 s，取 1.5 s 留余量
        const float MinHeld = 1.4f;

        [UnityTest]
        public IEnumerator EveryActionPlaysOnceThroughTheScene()
        {
            SceneFlow.Require();
            try
            {
                yield return SceneFlow.LoadMain();
                var boot = Object.FindAnyObjectByType<RoomBootstrap>();
                yield return SceneFlow.WaitForBoot(boot);

                var director = boot.Actions;
                var player = boot.Player;
                var spawn = player.transform.position;
                director.DriveInUpdate = false;   // 手动 Tick：Update 也推一次会让 1.5 s 变成 3 s

                var steps = new (string Id, PlayerPosture Posture)[]
                {
                    (ActionId.Hands, PlayerPosture.Standing),
                    (ActionId.PatHer, PlayerPosture.Standing),
                    (ActionId.PatMe, PlayerPosture.Crouching),
                    (ActionId.Kiss, PlayerPosture.Kneeling),
                    (ActionId.SitTogether, PlayerPosture.Standing),
                };

                // 加第六个动作时这张表必须跟着长：否则冒烟永远「五个都 ok」而新动作从未跑过。
                var ids = new string[steps.Length];
                for (var i = 0; i < steps.Length; i++) ids[i] = steps[i].Id;
                CollectionAssert.AreEquivalent(ActionId.All, ids);

                var hand = boot.Actor.Rig.Bone(HumanBodyBones.RightHand);
                var s1 = AnchorTable.Load().Seat("S1");

                foreach (var step in steps)
                {
                    // 站姿是前置条件：pat-me 要蹲、kiss 要跪，导演会拒绝而不是硬塞。
                    player.SetPosture(step.Posture);
                    yield return null;

                    // 驱动的证据要「进场前 vs 演完」比：取消后再比就被复位抹平了。
                    var handBefore = hand.position;
                    var isKiss = step.Id == ActionId.Kiss;
                    var chest = isKiss ? boot.Actor.Rig.Bone(HumanBodyBones.Chest) : null;
                    var chestBefore = isKiss ? chest.localRotation : Quaternion.identity;

                    Assert.IsTrue(director.TryEnterAction(step.Id),
                        step.Id + " 在真实场景里进不了场：" + director.LastFaults);
                    Assert.AreEqual(step.Id, director.Current, "进场后 Current 必须是 " + step.Id);

                    // Held 只在 Hold 里记账：先把进场走完（hands 的 2.6 s 最长），再数这 1.5 s。
                    // 进场可以是走过去的，所以"离开出生点"要在走完之后量，而不是进场那一帧。
                    ActionTestDrive.ToHold(director);
                    Assert.Greater(Vector3.Distance(spawn, player.transform.position), 0.05f,
                        step.Id + " 进场后玩家必须离开出生点（证明 plan 真的被摆过）");
                    for (var i = 0; i < StepsPerAction; i++) director.Tick(Step);

                    Assert.GreaterOrEqual(director.Held, MinHeld,
                        step.Id + " 只保持了 " + director.Held.ToString("F2") + " s，接触没稳住");
                    Assert.LessOrEqual(director.ContactError, ActionDirector.ContactTolerance,
                        step.Id + " 的接触误差超了 " + ActionDirector.ContactTolerance + " m");

                    // Held 只证明「时间过去了」，它不证明驱动层跑过：删掉任何一个 DriveX 分支，
                    // Held 照常涨到 1.5 s、ContactError 保持 0、marker 照写 ok。所以这里必须看见
                    // 骨骼真的被搬动过。
                    if (isKiss)
                    {
                        // 门槛是 5 而不是 10：§5.20 的 kiss 表里 Chest 只有 7°（局部），10 门永远红；
                        // 5 仍在噪声之上，而 DriveKiss 一旦不跑这一格就是 0。
                        var chestTurn = Quaternion.Angle(chestBefore, chest.localRotation);
                        Assert.Greater(chestTurn, 5f,
                            "kiss 期间她的躯干必须被摆过（实测 " + chestTurn.ToString("F1") + "°，驱动层没跑的话这里不会动）");
                    }
                    else if (step.Id != ActionId.SitTogether)
                        Assert.Greater(Vector3.Distance(handBefore, hand.position), 0.03f,
                            step.Id + " 期间她的右手必须被搬动过（驱动层没跑的话这里不会动）");

                    SmokeMarker.Write(MarkerRelative, "[smoke] action " + step.Id + " held=" + director.Held.ToString("F2") +
                        " gap=" + director.ContactError.ToString("F3") + " ok");

                    director.Cancel();
                    yield return null;

                    Assert.IsNull(director.Current, "Cancel 之后必须回到 idle");
                    Assert.IsTrue(player.KeyboardInput, "Cancel 之后键盘必须还给玩家");
                    Assert.Less(Vector3.Distance(spawn, player.transform.position), 0.05f,
                        "Cancel 之后玩家必须回到进场前的位置 5 cm 以内");
                    Assert.AreEqual(step.Posture, player.Posture, "Cancel 要把进场前那个姿势还回来");
                }

                // Seated 在 S1 与床垫上同为 true，断言它等于什么都没说：必须看髋部落在哪。
                var hips = boot.Actor.Seat.HipsWorld;
                Assert.Less(new Vector2(hips.x - s1.x, hips.z - s1.z).magnitude, 0.02f,
                    "五个动作跑完，她必须回到 S1（坐在床垫上也算 Seated）");
            }
            finally { SceneFlow.UnloadMainIfLoaded(); }
        }

    }
}
