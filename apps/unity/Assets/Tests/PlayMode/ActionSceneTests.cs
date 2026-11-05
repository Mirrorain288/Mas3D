using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Mas3D;
using Mas3D.Actions;

namespace Mas3D.Tests
{
    public class ActionSceneTests
    {
        static void RequireScene()
        {
            AvatarPrefabFixture.Require();
            if (!Application.CanStreamedLevelBeLoaded("Main"))
                Assert.Ignore("先跑 tools/unity.ps1 player 构建 Main.unity");
        }

        [UnityTest]
        public IEnumerator TheSceneCarriesADirectorReadyToPerform()
        {
            RequireScene();
            try
            {
                yield return SceneFlow.LoadMain();
                var boot = Object.FindAnyObjectByType<RoomBootstrap>();
                yield return SceneFlow.WaitForBoot(boot);   // Boot() 在 Start 里跑；卡住就是没接线
                Assert.AreSame(boot.Actor, boot.Actions.Actor, "导演必须接上同一个她");
                Assert.AreSame(boot.Player, boot.Actions.Player, "导演必须接上场景里的玩家");
                Assert.AreSame(boot.Rig, boot.Actions.Camera, "导演必须接上同一个相机架");
            }
            finally { SceneFlow.UnloadMainIfLoaded(); }
        }

        [UnityTest]
        public IEnumerator EnteringAnActionFromTheSceneMovesThePlayerToTheMark()
        {
            RequireScene();
            try
            {
                yield return SceneFlow.LoadMain();
                var boot = Object.FindAnyObjectByType<RoomBootstrap>();
                yield return SceneFlow.WaitForBoot(boot);
                Assert.IsTrue(boot.Actions.TryEnterAction(ActionId.Hands), "场景里的她坐在 S1 上，牵手必须能进场");
                yield return null;
                var seat = AnchorTable.Load().Seat("S1");
                var want = new Vector3(seat.x, 0f, seat.z) + Quaternion.Euler(0f, seat.facingY, 0f) * Vector3.forward * 0.62f;
                Assert.AreEqual(want.x, boot.Player.transform.position.x, 0.03f, "玩家必须被摆到她正前方 0.62 m");
                Assert.AreEqual(want.z, boot.Player.transform.position.z, 0.03f, "z 也要对上");
            }
            finally { SceneFlow.UnloadMainIfLoaded(); }
        }

        [UnityTest]
        public IEnumerator CancellingFromTheSceneReturnsThePlayerToTheSpawn()
        {
            RequireScene();
            try
            {
                yield return SceneFlow.LoadMain();
                var boot = Object.FindAnyObjectByType<RoomBootstrap>();
                yield return SceneFlow.WaitForBoot(boot);
                var spawn = boot.Player.transform.position;
                Assert.IsTrue(boot.Actions.TryEnterAction(ActionId.Hands));
                yield return null;
                boot.Actions.Cancel();
                yield return null;
                Assert.Less(Vector3.Distance(spawn, boot.Player.transform.position), 0.05f, "Cancel 之后玩家必须回到出生点 5 cm 以内");
                Assert.IsNull(boot.Actions.Current, "Cancel 之后必须回到 idle");
            }
            finally { SceneFlow.UnloadMainIfLoaded(); }
        }
    }
}
