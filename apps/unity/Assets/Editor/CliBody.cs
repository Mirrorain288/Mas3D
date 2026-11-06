using System.IO;
using System.Linq;
using Mas3D;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Mas3D.Editor
{
    /// <summary>
    /// P7 Task 3's two delivery stills of the player's new body. Separate from CliPlayer on purpose:
    /// that entry point owns the five established shots and their exit-code contract, and adding a
    /// sixth framing there would change what the capture gate checks. This one only answers "is there
    /// a person standing at A1, and is he visible in front of the mirror".
    ///
    ///   powershell -ExecutionPolicy Bypass -File tools/unity.ps1 run -- -executeMethod
    ///     Mas3D.Editor.CliBody.Capture -logFile D:\Mas3D\apps\unity\Logs\cli-Body.log
    /// </summary>
    public static class CliBody
    {
        const string ScenePath = "Assets/Scenes/Main.unity";
        const int Width = 1280;
        const int Height = 720;

        public static void Capture()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                Debug.LogError("[body-shot] cannot open " + ScenePath);
                EditorApplication.Exit(1);
                return;
            }

            var boot = Object.FindAnyObjectByType<RoomBootstrap>();
            if (boot == null)
            {
                Debug.LogError("[body-shot] no RoomBootstrap in " + ScenePath);
                EditorApplication.Exit(1);
                return;
            }
            boot.Boot();

            var camera = boot.OutputCamera != null ? boot.OutputCamera : Camera.main;
            var player = boot.Player;
            var body = player != null ? player.GetComponent<PlayerBody>() : null;
            if (camera == null || player == null || body == null || !body.Spawned)
            {
                Debug.LogError("[body-shot] player body did not spawn; check " + PlayerBody.BodyPrefabPath);
                EditorApplication.Exit(1);
                return;
            }

            var table = AnchorTable.Load();
            if (table == null)
            {
                Debug.LogError("[body-shot] anchors.json missing");
                EditorApplication.Exit(1);
                return;
            }

            // No player loop runs in a batch capture, so Cinemachine must let go before a manual pose
            // can survive the render.
            var brain = camera.GetComponent<Unity.Cinemachine.CinemachineBrain>();
            if (brain != null) brain.enabled = false;

            var shotDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Shots"));
            Directory.CreateDirectory(shotDir);
            var target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = target;
            var sceneFov = camera.fieldOfView;
            camera.fieldOfView = 38f;

            // A batch capture has no player loop, so the first CompositeRender after OpenScene still
            // runs against stale ambient/skybox state: the very first frame came back with the shirt
            // and skin tinted teal while the next shot in the same session was correct. Refresh the
            // environment and throw the first frame away before anything is written to disk.
            DynamicGI.UpdateEnvironment();
            camera.Render();
            camera.Render();

            var bounds = Measure(body.gameObject);
            var headLine = body.HeadRendererCount > 0 ? "head=hidden-or-not" : "head=director-missing";
            Debug.Log(string.Format("[body-shot] spawned height={0:F3} minY={1:F3} renderers={2} headRenderers={3} {4}",
                bounds.size.y, bounds.min.y, body.gameObject.GetComponentsInChildren<Renderer>(true).Length,
                body.HeadRendererCount, headLine));

            // --- 1. him standing on A1, seen from his front-left -------------------------------
            var a1 = table.Anchor("A1");
            if (a1 == null)
            {
                Debug.LogError("[body-shot] no A1");
                EditorApplication.Exit(1);
                return;
            }

            var feet = a1.Feet(RoomBootstrap.SpawnLift);
            player.Teleport(feet, a1.facingY);
            body.ApplyNow();
            RefreshSkin(body.gameObject);

            var facing = Quaternion.Euler(0f, a1.facingY, 0f);
            var forward = facing * Vector3.forward;
            var right = facing * Vector3.right;
            var eye = feet + forward * 2.5f + right * 0.7f + Vector3.up * 1.45f;
            var look = feet + Vector3.up * 0.95f;
            Shoot(camera, target, Path.Combine(shotDir, "p7-body-front.png"), "p7-body-front", eye, look, feet, a1.facingY, 38f);

            // --- 2. him in front of the mirror, seen from behind --------------------------------
            var a4 = table.Anchor("A4");
            if (a4 == null)
            {
                Debug.LogError("[body-shot] no A4");
                EditorApplication.Exit(1);
                return;
            }

            // A4 faces -49.7 (away from the glass); the mirror sits east of it, so the shot uses the
            // yaw that actually looks into it rather than the anchor's own facing.
            const float mirrorYaw = 92f;
            var mirrorFeet = a4.Feet(RoomBootstrap.SpawnLift);
            player.Teleport(mirrorFeet, mirrorYaw);
            body.ApplyNow();
            RefreshSkin(body.gameObject);

            var mirrorFacing = Quaternion.Euler(0f, mirrorYaw, 0f);
            var toMirror = mirrorFacing * Vector3.forward;
            var mirrorRight = mirrorFacing * Vector3.right;
            // Three-quarter view: dead-on puts his back across the whole glass, so the camera steps
            // aside far enough that the pane and the person are both readable in one frame.
            var mirrorEye = mirrorFeet - toMirror * 2.1f + mirrorRight * 1.1f + Vector3.up * 1.5f;
            var mirrorLook = mirrorFeet + toMirror * 0.3f + Vector3.up * 0.95f;
            Shoot(camera, target, Path.Combine(shotDir, "p7-mirror.png"), "p7-mirror", mirrorEye, mirrorLook, mirrorFeet, mirrorYaw, 46f);

            camera.fieldOfView = sceneFov;
            camera.targetTexture = null;
            target.Release();
            Object.DestroyImmediate(target);
            EditorApplication.Exit(0);
        }

        static void Shoot(Camera camera, RenderTexture target, string file, string tag,
            Vector3 position, Vector3 lookAt, Vector3 subjectFeet, float subjectYaw, float fov)
        {
            camera.fieldOfView = fov;
            camera.transform.position = position;
            camera.transform.rotation = Quaternion.LookRotation(lookAt - position, Vector3.up);
            camera.Render();
            WriteShot(target, file);
            Debug.Log(string.Format("[body-shot] wrote {0} cam=({1:F2},{2:F2},{3:F2}) look=({4:F2},{5:F2},{6:F2}) feet=({7:F2},{8:F2},{9:F2}) yaw={10:F1}",
                tag, position.x, position.y, position.z, lookAt.x, lookAt.y, lookAt.z,
                subjectFeet.x, subjectFeet.y, subjectFeet.z, subjectYaw));
        }

        /// <summary>
        /// Batch capture never runs a player loop, so the skinned palette the renderer cached stays
        /// where it was when the scene opened (same trap CliPlayer's action shots hit): toggling the
        /// renderer is the documented way to force a re-bake outside play mode.
        /// </summary>
        static void RefreshSkin(GameObject root)
        {
            if (root == null) return;
            var skins = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var skin in skins) skin.enabled = false;
            foreach (var skin in skins) skin.enabled = true;
        }

        static void WriteShot(RenderTexture target, string path)
        {
            RenderTexture.active = target;
            var texture = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            texture.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(path, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
        }

        static Bounds Measure(GameObject root)
        {
            var has = false;
            var bounds = new Bounds(Vector3.zero, Vector3.zero);
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is MeshRenderer == false && renderer is SkinnedMeshRenderer == false) continue;
                if (!has) { bounds = renderer.bounds; has = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            return bounds;
        }
    }
}
