using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
#if MAS3D_URP
using UnityEngine.Rendering.Universal;
#endif

namespace Mas3D.Editor
{
    /// <summary>
    /// Builds the playable scene: the imported room with its owned materials and ceiling, a
    /// CharacterController player on a verified anchor, and a Cinemachine third-person camera.
    ///   unity.ps1 run -executeMethod Mas3D.Editor.CliPlayer.BuildScene
    /// </summary>
    public static class CliPlayer
    {
        public const string ScenePath = "Assets/Scenes/Main.unity";
        public const string StreamingDir = "Assets/StreamingAssets/mas3d";
        public const string AnchorSource = "Anchors/anchors.json";

        public static void BuildScene()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(CliRoom.RoomPath);
            if (prefab == null)
            {
                Debug.LogError("[player] room prefab missing at " + CliRoom.RoomPath);
                EditorApplication.Exit(1);
                return;
            }

            EnsureFolder("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var room = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            var bounds = Measure(room);
            // The anchor table was solved with the room recentred exactly this way (CliAnchors),
            // so the playable scene has to bake the same shift or every anchor lands off target.
            room.transform.position += new Vector3(-bounds.center.x, -bounds.min.y, -bounds.center.z);
            bounds = Measure(room);
            Debug.Log($"[player] room bounds min={bounds.min:F2} max={bounds.max:F2} size={bounds.size:F2}");

            var materials = RoomMaterials.Rebuild(room, true, false);
            var dressing = RoomDress.Apply(room, true, true, new RoomDress.Tuning());
            RoomDress.ApplyNightSky();
            RoomDress.BuildSkyCard(bounds, bounds.max.x);
            Debug.Log($"[player] materials={materials} eave={dressing.eaveY:F2}");

            var player = BuildPlayer();
            var camera = BuildCamera();
            var bootGo = new GameObject("Session");
            var boot = bootGo.AddComponent<RoomBootstrap>();
            boot.Room = room;
            boot.Player = player.GetComponent<PlayerMotor>();
            boot.Rig = player.GetComponent<PlayerCameraRig>();
            boot.OutputCamera = camera;
            boot.SpawnAnchor = "A2";

            CopyAnchors();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            Debug.Log("[player] scene written: " + ScenePath);
            EditorApplication.Exit(0);
        }

        static GameObject BuildPlayer()
        {
            var go = new GameObject("Player");
            go.tag = "Player";
            var controller = go.AddComponent<CharacterController>();
            controller.height = 1.70f;
            controller.radius = 0.30f;
            controller.center = new Vector3(0f, 0.85f, 0f);
            controller.slopeLimit = 46f;
            controller.stepOffset = 0.25f;
            controller.skinWidth = 0.02f;
            go.AddComponent<PlayerMotor>();
            go.AddComponent<PlayerCameraRig>();
            // A placeholder pose; RoomBootstrap teleports to the solved A2 anchor on play.
            go.transform.position = new Vector3(-0.72f, 0.03f, -0.03f);
            go.transform.rotation = Quaternion.Euler(0f, 86f, 0f);
            return go;
        }

        static Camera BuildCamera()
        {
            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";
            var camera = go.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.02f, 0.02f, 0.035f);
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 120f;
            camera.fieldOfView = 55f;
            go.AddComponent<AudioListener>();
#if MAS3D_URP
            go.AddComponent<UniversalAdditionalCameraData>();
#endif
            go.transform.position = new Vector3(-0.72f, 1.7f, -3f);
            go.transform.rotation = Quaternion.Euler(0f, 0f, 0f);
            return camera;
        }

        static void CopyAnchors()
        {
            EnsureFolder("Assets/StreamingAssets");
            EnsureFolder(StreamingDir);
            var source = Path.GetFullPath(Path.Combine(Application.dataPath, "..", AnchorSource));
            if (!File.Exists(source))
            {
                Debug.LogWarning("[player] anchor table missing at " + source);
                return;
            }
            var target = Path.GetFullPath(Path.Combine(StreamingDir, "anchors.json"));
            File.Copy(source, target, true);
            AssetDatabase.ImportAsset(Path.Combine(StreamingDir, "anchors.json"));
            Debug.Log("[player] anchors copied to " + Path.Combine(StreamingDir, "anchors.json"));
        }


        /// <summary>
        /// Renders the playable scene's own camera without entering play mode: CinemachineBrain has
        /// ManualUpdate for exactly this, so the pose in the PNG is the pose the game would show.
        ///   unity.ps1 run -- -executeMethod Mas3D.Editor.CliPlayer.Capture     (never -nographics)
        /// </summary>
        public static void Capture()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                Debug.LogError("[play] cannot open " + ScenePath);
                EditorApplication.Exit(1);
                return;
            }

            var boot = Object.FindAnyObjectByType<RoomBootstrap>();
            if (boot == null)
            {
                Debug.LogError("[play] no RoomBootstrap in " + ScenePath);
                EditorApplication.Exit(1);
                return;
            }
            boot.Boot();

            var rig = boot.Rig;
            var camera = boot.OutputCamera;
            if (rig == null || camera == null)
            {
                Debug.LogError("[play] rig or camera missing on the bootstrap");
                EditorApplication.Exit(1);
                return;
            }

            var brain = camera.GetComponent<Unity.Cinemachine.CinemachineBrain>();
            if (brain == null) brain = camera.gameObject.AddComponent<Unity.Cinemachine.CinemachineBrain>();

            var shotDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Shots"));
            Directory.CreateDirectory(shotDir);
            var target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = target;

            // One view from the spawn anchor, then quarter turns so the framing and the obstacle
            // avoidance can both be judged.
            float[] yaws = { 0f, 90f, 180f, 270f };
            for (var i = 0; i < yaws.Length; i++)
            {
                rig.SetOrbit(86f + yaws[i], 14f);
                brain.ManualUpdate();
                camera.Render();

                RenderTexture.active = target;
                var texture = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                texture.Apply();
                RenderTexture.active = null;

                var file = Path.Combine(shotDir, "play-0" + (i + 1) + ".png");
                File.WriteAllBytes(file, texture.EncodeToPNG());
                Object.DestroyImmediate(texture);
                var eye = boot.Player != null && boot.Player.Head != null ? boot.Player.Head.position.ToString("F2") : "-";
                Debug.Log(string.Format("[play] wrote {0} cam={1:F2} eye={2}", file, camera.transform.position, eye));
            }

            camera.targetTexture = null;
            target.Release();
            Object.DestroyImmediate(target);
            EditorApplication.Exit(0);
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

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = path.Substring(0, path.LastIndexOf('/'));
            var leaf = path.Substring(path.LastIndexOf('/') + 1);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}