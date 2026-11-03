using System.Collections;
using System.Text.RegularExpressions;
using Mas3D.Avatar;
using Mas3D.Conversation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Mas3D.Tests
{
    /// <summary>
    /// Task 5 of docs/superpowers/plans/2026-11-03-unity-avatar-in-scene.md: the shipped scene has to
    /// carry her — seated on S1, eyes on the player, mood wired to the conversation — and her body has
    /// to stay out of the player's way.
    ///
    /// The scene-load pattern is copied from BridgeSmokeTests.UiSceneShot, minus [Explicit]: these
    /// cases only assert, they never screenshot, so they belong in the default suite. Unlike UiSceneShot
    /// they load Main additively and unload it again: a scene left behind by a test keeps its player on
    /// A2 and her capsule on S1, and the anchor and walk suites that run after this one then report the
    /// wrong body standing in the room.
    /// </summary>
    public class AvatarSceneTests
    {
        const string SceneName = "Main";
        const string ActorName = "Monkia";

        /// <summary>Player capsule, matching CliPlayer: r = 0.30, h = 1.70.</summary>
        const float PlayerRadius = 0.30f;

        /// <summary>
        /// A clean clone has no Assets/_Local (apps/unity/.gitignore: /Assets/_Local/), so the scene
        /// cases would hard-red on a missing model instead of skipping. The 17 MB non-redistributable
        /// VRM stays out of the repository on purpose: missing asset means Ignore, not failure. Fix
        /// round 2 moved the check itself into <see cref="AvatarPrefabFixture"/>, which the rig, seat,
        /// idle and mood fixtures share; the six call sites below stay exactly where they were.
        /// </summary>
        static void RequireAvatarPrefab() => AvatarPrefabFixture.Require();

        [UnityTest]
        public IEnumerator TheShippedSceneSeatsHerOnTheBench()
        {
            RequireAvatarPrefab();
            if (!Application.CanStreamedLevelBeLoaded(SceneName))
                Assert.Ignore("先跑 tools/unity.ps1 player 构建 Main.unity");

            try
            {
                yield return LoadMain();

                AvatarActor actor = null;
                yield return WaitUntilSeated(found => actor = found);

                Assert.IsNotNull(actor, "主场景里必须有 AvatarActor");
                Assert.IsNotNull(actor.Seat, "AvatarActor 该带着 AvatarSeat");
                Assert.IsTrue(actor.Seat.Seated, "她该已经坐在 S1 上");

                var s1 = AnchorTable.Load().Seat("S1");
                Assert.IsNotNull(s1, "anchors.json 里必须有 S1");
                Assert.AreEqual(s1.y + AvatarSeat.HipsGap, actor.Seat.HipsWorld.y, 0.04f,
                    "髋部该落在座面 + HipsGap 上，实测 " + actor.Seat.HipsWorld.y.ToString("F3"));

                Assert.IsNotNull(actor.Idle, "AvatarActor 该带着 AvatarIdle");
                Assert.IsNotNull(actor.Mood, "AvatarActor 该带着 AvatarMood");

                var player = Object.FindAnyObjectByType<PlayerMotor>();
                Assert.IsNotNull(player, "主场景里必须有玩家");
                Assert.IsNotNull(player.Head, "玩家的 Head 是视线的目标");
                Assert.AreSame(player.Head, actor.Idle.GazeTarget, "视线该绑在玩家头上");

                var conversation = Object.FindAnyObjectByType<ConversationController>();
                Assert.IsNotNull(conversation, "主场景里必须有 ConversationController");
                Assert.AreSame(conversation, actor.Mood.Controller, "表情该绑在对话状态上");
            }
            finally
            {
                UnloadMainIfLoaded();
            }
        }

        [UnityTest]
        public IEnumerator ThePlayerSpawnStaysClear()
        {
            RequireAvatarPrefab();
            if (!Application.CanStreamedLevelBeLoaded(SceneName))
                Assert.Ignore("先跑 tools/unity.ps1 player 构建 Main.unity");

            try
            {
                yield return LoadMain();

                AvatarActor actor = null;
                yield return WaitUntilSeated(found => actor = found);
                Assert.IsNotNull(actor, "主场景里必须有 AvatarActor");
                Physics.SyncTransforms();

                var s1 = AnchorTable.Load().Seat("S1");
                Assert.IsNotNull(s1, "anchors.json 里必须有 S1");
                Assert.IsTrue(TouchesActor(new Vector3(s1.x, s1.y, s1.z), 0.05f, 0.10f, 0.20f),
                    "S1 上该有她的碰撞体，否则这条扫描是空转的");

                var a2 = AnchorTable.Load().Anchor("A2");
                Assert.IsNotNull(a2, "anchors.json 里必须有 A2");
                var feet = new Vector3(a2.x, 0.1f, a2.z);
                foreach (var hit in Physics.OverlapCapsule(
                             feet + Vector3.up * 0.3f, feet + Vector3.up * 1.5f, PlayerRadius))
                {
                    Assert.AreNotEqual(ActorName, hit.name,
                        "她的碰撞体压到出生点 A2 了：" + hit.name + " 在 " + hit.bounds.center.ToString("F2"));
                }
            }
            finally
            {
                UnloadMainIfLoaded();
            }
        }

        /// <summary>
        /// The bench sits about 0.9 m from the bed, and the aisle in front of S1 must stay free of her
        /// body. This is a <b>volume</b> assertion, not a reachability assertion: it says the 0.7 m in
        /// front of S1 holds none of her capsule, never that a player can path through the room. The
        /// first assertion is the control: her capsule must really be in the scene, otherwise the aisle
        /// assertion would pass on an avatar with no body at all.
        /// </summary>
        [UnityTest]
        public IEnumerator TheSeatedAvatarLeavesTheAisleVolumeClear()
        {
            RequireAvatarPrefab();
            if (!Application.CanStreamedLevelBeLoaded(SceneName))
                Assert.Ignore("先跑 tools/unity.ps1 player 构建 Main.unity");

            try
            {
                yield return LoadMain();

                AvatarActor actor = null;
                yield return WaitUntilSeated(found => actor = found);
                Assert.IsNotNull(actor, "主场景里必须有 AvatarActor");
                Physics.SyncTransforms();

                var s1 = AnchorTable.Load().Seat("S1");
                Assert.IsNotNull(s1, "anchors.json 里必须有 S1");

                var seat = new Vector3(s1.x, s1.y, s1.z);
                Assert.IsTrue(TouchesActor(seat, 0.05f, 0.10f, 0.20f),
                    "S1 座面上该有她的碰撞体，否则下一条断言是空转的");

                var forward = Quaternion.Euler(0f, s1.facingY, 0f) * Vector3.forward;
                var aisle = new Vector3(s1.x, 0.1f, s1.z) + forward * 0.7f;
                Assert.IsFalse(TouchesActor(aisle, PlayerRadius, 0.3f, 1.5f),
                    "她的胶囊占住了 S1 正前方 0.7 m 的过道体积（体积断言，不是可达性断言）：玩家胶囊从 " +
                    aisle.ToString("F2") + " 起算");
            }
            finally
            {
                UnloadMainIfLoaded();
            }
        }

        /// <summary>
        /// Standing has to take the body away. FitBody places the capsule from the <b>live</b> hips
        /// joint, so a capsule fitted while seated freezes where the seated body was once she stands;
        /// no component tells the actor that Stand happened, so it has to notice on its own. The
        /// negative control at the end pins the other half: sitting down again grows the body back.
        /// </summary>
        [UnityTest]
        public IEnumerator StandingTakesTheBodyAway()
        {
            RequireAvatarPrefab();
            if (!Application.CanStreamedLevelBeLoaded(SceneName))
                Assert.Ignore("先跑 tools/unity.ps1 player 构建 Main.unity");

            try
            {
                yield return LoadMain();

                AvatarActor actor = null;
                yield return WaitUntilSeated(found => actor = found);
                Assert.IsNotNull(actor, "主场景里必须有 AvatarActor");
                Assert.IsNotNull(actor.Body, "落座后该有胶囊");
                Assert.IsNotNull(actor.GetComponent<CapsuleCollider>(), "落座后该有胶囊");

                var a5 = AnchorTable.Load().Anchor("A5");
                Assert.IsNotNull(a5, "anchors.json 里必须有 A5");
                actor.Seat.Stand(new Vector3(a5.x, 0.02f, a5.z), a5.facingY);
                yield return null;
                yield return null;

                Assert.IsNull(actor.Body, "站起后胶囊必须移除，否则玩家在长凳前撞空气");
                Assert.IsNull(actor.GetComponent<CapsuleCollider>(),
                    "站起后胶囊必须移除，否则玩家在长凳前撞空气");

                actor.SitAt("S1");
                yield return null;
                Assert.IsNotNull(actor.Body, "重新落座必须重新长出胶囊");
            }
            finally
            {
                UnloadMainIfLoaded();
            }
        }

        /// <summary>
        /// Spawn is the whole five-layer stack in one call and the only caller is CliPlayer, so no
        /// other case in the suite would notice if it stopped building one of the layers. No scene
        /// here: this is the builder's contract, not the shipped object.
        /// </summary>
        [UnityTest]
        public IEnumerator SpawnBuildsAWholeActor()
        {
            RequireAvatarPrefab();

            AvatarActor actor = null;
            try
            {
                actor = AvatarActor.Spawn();
                Assert.IsNotNull(actor, "Spawn 该建出一个 AvatarActor：" + AvatarRig.DefaultPrefabPath);
                yield return null;

                Assert.IsNotNull(actor.Rig, "Spawn 该挂 AvatarRig");
                Assert.IsNotNull(actor.Seat, "Spawn 该挂 AvatarSeat");
                Assert.IsNotNull(actor.Idle, "Spawn 该挂 AvatarIdle");
                Assert.IsNotNull(actor.Mood, "Spawn 该挂 AvatarMood");
                Assert.IsTrue(actor.Seat.Seated, "Spawn 该把她坐在 S1 上");
                Assert.AreEqual(AvatarActor.DefaultSeatId, actor.SeatId, "Spawn 默认落在 S1");

                var body = actor.Body;
                Assert.IsNotNull(body, "Spawn 该给出身体胶囊");
                Assert.IsFalse(body.isTrigger, "胶囊不能是触发器，玩家的 sweep 只认实体碰撞");
                Assert.AreEqual(AvatarActor.BodyRadius, body.radius, 1e-4f,
                    "胶囊半径该是 BodyRadius，实测 " + body.radius.ToString("F3"));
                Assert.AreEqual(AvatarActor.BodyHeight, body.height, 1e-4f,
                    "胶囊高该是 BodyHeight，实测 " + body.height.ToString("F3"));
                Assert.AreEqual(0.20f, body.radius, 1e-4f, "评审判据钉住半径 0.20");
                Assert.AreEqual(1.05f, body.height, 1e-4f, "评审判据钉住高 1.05");

                var lift = (actor.transform.TransformPoint(body.center) - actor.Seat.HipsWorld).y;
                Assert.AreEqual(AvatarActor.BodyLift, lift, 0.03f,
                    "胶囊中心该落在髋上 0.20 m，实测 " + lift.ToString("F3"));
            }
            finally
            {
                if (actor != null) Object.Destroy(actor.gameObject);
            }

            yield return null;
        }

        /// <summary>
        /// FitBody runs from SitAt and again every time the seated flag flips, so it has to reuse the
        /// collider it already owns. Two capsules would double the player's sweep against her and make
        /// the body reach further than the bench.
        /// </summary>
        [UnityTest]
        public IEnumerator FitBodyTwiceKeepsOneCollider()
        {
            RequireAvatarPrefab();

            AvatarActor actor = null;
            try
            {
                actor = AvatarActor.Spawn();
                Assert.IsNotNull(actor, "Spawn 该建出一个 AvatarActor：" + AvatarRig.DefaultPrefabPath);
                yield return null;

                var first = actor.Body;
                Assert.IsNotNull(first, "第一次 FitBody 该给出胶囊");

                actor.FitBody();
                yield return null;

                Assert.AreSame(first, actor.Body, "重复 FitBody 必须复用同一个胶囊，不能新建一个");
                Assert.AreEqual(1, actor.GetComponents<CapsuleCollider>().Length,
                    "同一个物体上只能有一根胶囊");
            }
            finally
            {
                if (actor != null) Object.Destroy(actor.gameObject);
            }

            yield return null;
        }

        /// <summary>
        /// An actor whose rig is missing has to fail softly: SitAt logs and leaves her standing, and
        /// FitBody must not invent a body from a null hips joint (it would park a capsule at the root,
        /// which sits under the floor in the seated pose). No prefab and no scene — this case is about
        /// the missing pieces, not about the model.
        /// </summary>
        [UnityTest]
        public IEnumerator AnActorWithNoRigSeatsNobodyAndKeepsNoBody()
        {
            var noRig = new GameObject("NoRig");
            try
            {
                var actor = noRig.AddComponent<AvatarActor>();
                noRig.AddComponent<AvatarSeat>();

                LogAssert.Expect(LogType.Error, new Regex("\\[seat\\] no AvatarRig on NoRig"));
                Assert.DoesNotThrow(() => actor.SitAt("S1"), "没有 rig 时 SitAt 不该抛");
                yield return null;

                Assert.IsNotNull(actor.Seat, "SitAt 该认领同物体的 AvatarSeat");
                Assert.IsFalse(actor.Seat.Seated, "没有 rig 就没有坐姿，Seated 必须留 false");
                Assert.IsNull(actor.Body, "没有 rig 就没有髋部，不能长出一根胶囊");
                Assert.IsNull(noRig.GetComponent<CapsuleCollider>(), "没有 rig 时物体上不能出现胶囊");
            }
            finally
            {
                Object.DestroyImmediate(noRig);
            }
        }

        /// <summary>Load the shipped scene next to the test scene, never into it.</summary>
        static IEnumerator LoadMain()
        {
            if (SceneManager.GetSceneByName(SceneName).isLoaded) yield return UnloadMain();
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Additive);
        }

        static IEnumerator UnloadMain()
        {
            var scene = SceneManager.GetSceneByName(SceneName);
            if (!scene.isLoaded) yield break;
            var unload = SceneManager.UnloadSceneAsync(scene);
            if (unload == null) yield break;
            while (!unload.isDone) yield return null;
        }

        /// <summary>Finally blocks cannot yield, so this only asks; the next LoadMain waits it out.</summary>
        static void UnloadMainIfLoaded()
        {
            var scene = SceneManager.GetSceneByName(SceneName);
            if (scene.isLoaded) SceneManager.UnloadSceneAsync(scene);
        }

        /// <summary>Boot, then the actor: wait on the state, not on a frame count.</summary>
        static IEnumerator WaitUntilSeated(System.Action<AvatarActor> result)
        {
            var deadline = Time.realtimeSinceStartup + 10f;
            AvatarActor actor = null;
            while (Time.realtimeSinceStartup < deadline)
            {
                actor = Object.FindAnyObjectByType<AvatarActor>();
                if (actor != null && actor.Seat != null && actor.Seat.Seated) break;
                yield return null;
            }
            result(actor);
        }

        /// <summary>Does a player-sized capsule stood at <paramref name="feet"/> reach her?</summary>
        static bool TouchesActor(Vector3 feet, float radius, float low, float high)
        {
            foreach (var hit in Physics.OverlapCapsule(
                         feet + Vector3.up * low, feet + Vector3.up * high, radius))
            {
                if (hit.name == ActorName) return true;
            }
            return false;
        }
    }
}
