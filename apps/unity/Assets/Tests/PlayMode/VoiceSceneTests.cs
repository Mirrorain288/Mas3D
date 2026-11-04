using System.Collections;
using Mas3D.UI;
using Mas3D.Voice;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Mas3D.Tests
{
    /// <summary>
    /// Task 5 of docs/superpowers/plans/2026-11-03-unity-voice-p3.md: the shipped scene has to hand
    /// the dialogue panel to the voice layer, otherwise 按住 V 在真场景里什么都录不到 —— 那一段接线
    /// 只在 CliPlayer 里写一次，没有任何别的用例会看见它断掉。
    ///
    /// 加载方式照抄 <see cref="AvatarSceneTests"/>：additive 进 "Main"、finally 里卸载。
    /// 用 Single 加载又不收尾，场景里的 Player 会留在 A2、她的胶囊留在 S1，后面的锚点与步行用例
    /// 会报「房间里有个人」——一次就撞出 4 个假失败。
    /// </summary>
    public class VoiceSceneTests
    {
        const string SceneName = "Main";

        [UnityTest]
        public IEnumerator TheSceneBindsVoiceToTheDialoguePanel()
        {
            if (!Application.CanStreamedLevelBeLoaded(SceneName))
                Assert.Ignore("先跑 tools/unity.ps1 player 构建 Main.unity");

            try
            {
                yield return LoadMain();

                // Boot 在 Start 里跑，接上 Voice 要等它这一帧走完；等状态，不等帧数。
                //
                // 等的必须是 Recorder/Client：Voice 组件本身与它的 Panel 都是场景序列化出来的，
                // 只断言它们等于“场景里有个组件”，删掉 RoomBootstrap 的 Bind 也照样绿。
                // 这两个字段是普通 C# 类的实例，Unity 不序列化它们 —— 非 null 只可能来自 Bind。
                RoomBootstrap boot = null;
                var deadline = Time.realtimeSinceStartup + 10f;
                while (Time.realtimeSinceStartup < deadline)
                {
                    boot = Object.FindAnyObjectByType<RoomBootstrap>();
                    if (boot != null && boot.Voice != null
                        && boot.Voice.Recorder != null && boot.Voice.Client != null) break;
                    yield return null;
                }

                Assert.IsNotNull(boot, "主场景里必须有 RoomBootstrap");
                Assert.IsNotNull(boot.Voice, "RoomBootstrap 该在 Boot 里接上 VoiceInput");

                var panel = Object.FindAnyObjectByType<DialoguePanel>();
                Assert.IsNotNull(panel, "主场景里必须有对话面板");
                Assert.AreSame(panel, boot.Voice.Panel, "语音层该拿着场景里那块面板，而不是另建一块");
                Assert.IsNotNull(boot.Voice.Settings, "语音层该拿到桥接设置（令牌从桥接写盘的文件读）");

                Assert.IsNotNull(boot.Voice.Recorder, "Bind 要装上真麦克风录音器：这是 Boot 真跑过的证据");
                Assert.IsInstanceOf<MicrophoneRecorder>(boot.Voice.Recorder, "场景里接的是真麦克风，不是测试替身");
                Assert.IsNotNull(boot.Voice.Client, "Bind 要备好 ASR 客户端");
                StringAssert.EndsWith("/mas3d/api/asr", boot.Voice.Client.Endpoint,
                    "ASR 端点由桥接的 host/port 推导，路径固定 /mas3d/api/asr");
                Assert.AreEqual(boot.Voice.Settings.Token ?? string.Empty, boot.Voice.Client.Token,
                    "令牌要从桥接设置抄进客户端，否则桥接会 401");
            }
            finally
            {
                UnloadMainIfLoaded();
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
    }
}
