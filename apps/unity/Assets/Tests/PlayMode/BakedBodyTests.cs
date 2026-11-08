using System.Collections;
using Mas3D.Avatar;
using Mas3D.Boot;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mas3D.Tests
{
    /// <summary>
    /// 打包版没有 AssetDatabase：身体只能靠场景里烘好的那份。这一条用例同时钉住四件事——
    /// 场景真的烘了身体、烘进去的那份是睡着的、运行时收编的是同一份实例（不是又 Instantiate 一个）、
    /// 玩家身上只有一个 Me 和一个 PlayerBody，且只有玩家自己的 collider。
    /// </summary>
    public class BakedBodyTests
    {
        [UnityTest]
        public IEnumerator TheSceneCarriesABakedBodyTheBuildCanAdopt()
        {
            SceneFlow.Require();

            // Require() 只保证她那套资源在，这条用例烘的却是 Me.vrm。缺了就在断言前跳过：
            // 少了这一道，干净 clone 会在「根本没烘」的情况下走完整条用例并通过，结论正好相反。
            if (!PlayerBody.PrefabAvailable)
                Assert.Ignore("缺 " + PlayerBody.BodyPrefabPath + "，跳过烘体校验");

            try
            {
                yield return SceneFlow.LoadMain();

                var boot = Object.FindAnyObjectByType<RoomBootstrap>();
                Assert.IsNotNull(boot, "Main.unity 必须带 RoomBootstrap");
                Assert.IsNotNull(boot.Player, "RoomBootstrap.Player 该指向 PlayerMotor");

                var player = boot.Player.gameObject;
                var body = player.GetComponent<PlayerBody>();
                Assert.IsNotNull(body, "Player 上该有 PlayerBody（CliPlayer 烘场景时加的）");
                Assert.IsNotNull(body.Baked, "场景里必须烘一份 Me（Baked 字段）");
                // 落盘那一刻它就是 inactive：叫醒它的只有 Boot() 里的 Spawn()。
                Assert.IsFalse(body.Baked.activeSelf, "烘进场景的身体必须是 inactive，等 Spawn 才激活");

                // Boot() 在 Start 里跑，比 LoadMain() 的协程恢复晚一帧。等 Boot() 接上导演就够：
                // body.Spawn() 在 Boot() 里排在导演之前，所以这一步已经跨过收编。
                yield return SceneFlow.WaitForBoot(boot);

                Assert.IsTrue(body.Spawned, "Boot() 该已经收编了身体");
                Assert.AreSame(body.Baked, body.Avatar.gameObject,
                    "收编必须是同一份实例——另 Instantiate 一个 Me 说明走错了分支");

                Assert.AreEqual(1, player.GetComponentsInChildren<AvatarRig>(true).Length,
                    "玩家层级里只该有一个身体");

                Assert.AreEqual(1, player.GetComponentsInChildren<PlayerBody>(true).Length,
                    "只该有一个 PlayerBody");

                var named = 0;
                foreach (var t in player.GetComponentsInChildren<Transform>(true))
                    if (t.name == PlayerBody.BodyName) named += 1;
                Assert.AreEqual(1, named, "只该有一个 " + PlayerBody.BodyName);

                // CharacterController 自己也是 Collider，所以不能只数「非 CharacterController」：
                // 数总数、钉住唯一那一个是玩家的控制器，再把身体子树单独数成 0。
                var colliders = player.GetComponentsInChildren<Collider>(true);
                Assert.AreEqual(1, colliders.Length, "玩家层级只该有 Player 自己的 CharacterController");
                Assert.AreSame(player.GetComponent<CharacterController>(), colliders[0],
                    "唯一的 collider 必须是 Player 的 CharacterController");
                Assert.AreEqual(0, body.Avatar.GetComponentsInChildren<Collider>(true).Length,
                    "身体子树不许带 collider");

                var height = body.Avatar.Measure().size.y;
                Assert.AreEqual(PlayerBody.TargetHeight, height, 0.02f,
                    "身体高度该是 " + PlayerBody.TargetHeight.ToString("F2") + "，实测 " + height.ToString("F3"));
            }
            finally
            {
                SceneFlow.UnloadMainIfLoaded();
            }
        }
    }
}
