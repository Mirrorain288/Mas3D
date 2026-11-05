using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Mas3D.Tests
{
    /// <summary>
    /// Loads and unloads the shipped scene for the cases that need it. Extracted from
    /// <see cref="AvatarSceneTests"/> when the action cases started needing the same scene: one copy of
    /// the additive dance is easier to keep honest than two.
    ///
    /// Additive is not a preference. <see cref="LoadSceneMode.Single"/> drops whatever the test scene
    /// was carrying, and the scene's own player and her capsule then leak into the next fixture — that
    /// already produced four false failures. Every caller loads inside a try and unloads in the finally,
    /// because a scene left behind puts her body on S1 for the anchor and walk suites that run after.
    /// </summary>
    internal static class SceneFlow
    {
        /// <summary>The scene CliPlayer builds.</summary>
        internal const string SceneName = "Main";

        /// <summary>
        /// The shared precondition: the non-redistributable VRM has to be there, and the scene has to
        /// have been built at least once. Both are Ignore, never failure — a clean clone has neither.
        /// </summary>
        internal static void Require()
        {
            AvatarPrefabFixture.Require();
            if (!Application.CanStreamedLevelBeLoaded(SceneName))
                Assert.Ignore("先跑 tools/unity.ps1 player 构建 Main.unity");
        }

        /// <summary>Load the shipped scene next to the test scene, never into it.</summary>
        /// <summary>
        /// The unload a finally block could only request. A second <c>UnloadSceneAsync</c> on a scene
        /// that is already going returns null, so without this handle the next LoadMain would load into
        /// a scene that is still on its way out.
        /// </summary>
        static AsyncOperation _unloading;

        internal static IEnumerator LoadMain()
        {
            // Wait out any unload a previous finally only asked for, then make sure the scene is gone
            // before loading into it again.
            while (_unloading != null && !_unloading.isDone) yield return null;
            _unloading = null;
            if (SceneManager.GetSceneByName(SceneName).isLoaded) yield return UnloadMain();
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Additive);
        }

        /// <summary>
        /// Waits for Boot() to wire the director, with a bound: an unbounded WaitUntil turns a broken
        /// wiring into a hung suite (every later case times out) instead of one red case.
        /// </summary>
        internal static IEnumerator WaitForBoot(RoomBootstrap boot)
        {
            for (var i = 0; i < 120 && boot != null && boot.Actions == null; i++) yield return null;
            Assert.IsNotNull(boot, "Main.unity 必须带 RoomBootstrap");
            Assert.IsNotNull(boot.Actions, "Boot() 没接上 ActionDirector：等了 120 帧仍是 null");
        }

        /// <summary>Unload it and wait for the unload to finish.</summary>
        internal static IEnumerator UnloadMain()
        {
            var scene = SceneManager.GetSceneByName(SceneName);
            if (!scene.isLoaded) yield break;
            _unloading = SceneManager.UnloadSceneAsync(scene);
            if (_unloading == null) yield break;
            while (!_unloading.isDone) yield return null;
            _unloading = null;
        }

        /// <summary>Finally blocks cannot yield, so this only asks; the next LoadMain waits it out.</summary>
        internal static void UnloadMainIfLoaded()
        {
            var scene = SceneManager.GetSceneByName(SceneName);
            if (scene.isLoaded) _unloading = SceneManager.UnloadSceneAsync(scene);
        }
    }
}
