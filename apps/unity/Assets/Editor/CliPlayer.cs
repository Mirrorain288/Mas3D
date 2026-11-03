using System.IO;
using System.Linq;
using Mas3D.Avatar;
using Mas3D.Conversation;
using Mas3D.Net;
using Mas3D.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;
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
        public const string PanelSettingsPath = "Assets/UI/DialoguePanelSettings.asset";
        public const string DialogueTreePath = "Assets/UI/Dialogue.uxml";

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

            // The panel and the bridge live on the same Session object as the bootstrap, so the
            // scene is one self-contained object graph: the player builds nothing at runtime.
            var dialogue = BuildDialogue();
            var conversation = bootGo.AddComponent<ConversationController>();
            conversation.Panel = dialogue;
            var bridge = bootGo.AddComponent<DialogueBridge>();
            bridge.Controller = conversation;
            bridge.Panel = dialogue;
            boot.Dialogue = conversation;

            var tokenPath = BridgeSettings.TokenFilePath;
            Debug.Log(File.Exists(tokenPath)
                ? "[player] dialogue wired, token present at " + tokenPath
                : "[player] dialogue wired, no token at " + tokenPath + " -> offline fallback copy");

            CopyAnchors();

            // She is scene geometry, not a runtime spawn: the Actors object is empty at play time and
            // only exists so the hierarchy reads as a room with people in it. Anchors first, because
            // SitAt reads the seat off the streaming copy.
            var actors = new GameObject("Actors");
            var actor = AvatarActor.Spawn();
            if (actor == null)
            {
                Debug.LogError("[player] avatar spawn failed: " + AvatarRig.DefaultPrefabPath);
                EditorApplication.Exit(1);
                return;
            }
            actor.transform.SetParent(actors.transform, true);

            var table = AnchorTable.Load();
            var seat = table != null ? table.Seat(AvatarActor.DefaultSeatId) : null;
            if (seat == null)
            {
                Debug.LogError("[player] no " + AvatarActor.DefaultSeatId + " in the anchor table");
                EditorApplication.Exit(1);
                return;
            }

            // Judge the seat by the hips joint, never by the root: sitting drops the root below the
            // floor plate, so a root-height check would pass while she floats or sinks.
            var hips = actor.Seat.HipsWorld.y;
            var want = seat.y + AvatarSeat.HipsGap;
            if (Mathf.Abs(hips - want) > 0.02f)
            {
                Debug.LogError(string.Format("[player] seat missed on {0}: hips={1:F3} want={2:F3}", seat.id, hips, want));
                EditorApplication.Exit(1);
                return;
            }
            boot.Actor = actor;
            Debug.Log(string.Format("[player] actor seated on {0}: hips={1:F3} seat.y={2:F3} gap={3:F3}", seat.id, hips, seat.y, AvatarSeat.HipsGap));

            // Save her at rest, not seated: AvatarSeat.Seated is runtime-only, and applying the
            // sitting table to an already-saved sitting pose would fold the legs twice (AvatarPose
            // caches the first pose it sees as the rest pose). AvatarActor.Start seats her on load.
            var park = table.Anchor("A5");
            if (park == null)
            {
                Debug.LogError("[player] no A5 in the anchor table, cannot park the actor at rest");
                EditorApplication.Exit(1);
                return;
            }
            actor.Seat.Stand(park.Feet(), park.facingY);
            actor.FitBody();
            Debug.Log(string.Format("[player] actor parked at rest on A5 ({0:F2}, {1:F2}), seats herself on {2}", park.x, park.z, actor.SeatId));

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            Debug.Log("[player] scene written: " + ScenePath);
            EditorApplication.Exit(0);
        }

        /// <summary>
        /// The dialogue panel as the scene carries it: a UIDocument (uxml + panel settings) plus the
        /// view component. The object starts inactive because UIDocument logs an error if it wakes
        /// without settings, which is what this code is still in the middle of assigning.
        /// </summary>
        static DialoguePanel BuildDialogue()
        {
            var settings = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(DialogueTreePath);
            if (settings == null || tree == null)
            {
                Debug.LogError("[player] dialogue assets missing: " + PanelSettingsPath + " / " + DialogueTreePath);
                EditorApplication.Exit(1);
                return null;
            }

            var go = new GameObject("Dialogue");
            go.SetActive(false);
            var document = go.AddComponent<UIDocument>();
            document.panelSettings = settings;
            document.visualTreeAsset = tree;
            var panel = go.AddComponent<DialoguePanel>();
            panel.Document = document;
            go.SetActive(true);
            Debug.Log("[player] dialogue panel built: " + settings.name + " + " + tree.name);
            return panel;
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

            // The scene stores her at rest (AvatarSeat.Seated does not survive serialisation, so the
            // sitting pose is applied on load); an editor-side render has no Awake, so seat her here.
            var avatar = Object.FindAnyObjectByType<AvatarActor>();
            if (avatar != null) avatar.SitAt(avatar.SeatId);

            var shotDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Shots"));
            Directory.CreateDirectory(shotDir);
            var target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = target;

            // `-diagLights 1` renders the spawn view once per light group with that group off, so a
            // colour that only shows up from one side can be traced to the lamp responsible instead
            // of guessed at. Diagnostic only; the five delivery shots are written afterwards.
            if (HasFlag("-diagLights"))
            {
                var groups = new (string name, string[] objects)[]
                {
                    ("moon", new[] { "moon" }),
                    ("lamp", new[] { "bedside-lamp" }),
                    ("glow", new[] { "ceiling-glow-1", "ceiling-glow-2" }),
                    ("floor", new[] { "floor-bounce" }),
                    ("bounce", new[] { "bounce" }),
                };

                var all = Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                var names = string.Join(",", all.Select(l => l.name + "=" + l.intensity.ToString("F2")));
                Debug.Log("[play] lights " + names);

                rig.SetOrbit(86f, 14f);
                brain.ManualUpdate();
                foreach (var group in groups)
                {
                    var off = all.Where(l => group.objects.Any(n => l.name.StartsWith(n))).ToArray();
                    foreach (var light in off) light.enabled = false;
                    camera.Render();
                    WriteShot(target, Path.Combine(shotDir, "diag-off-" + group.name + ".png"));
                    foreach (var light in off) light.enabled = true;
                    Debug.Log("[play] diag off " + group.name + " (" + off.Length + " lights)");
                }
            }

            // One view from the spawn anchor, then quarter turns so the framing and the obstacle
            // avoidance can both be judged.
            float[] yaws = { 0f, 90f, 180f, 270f };
            for (var i = 0; i < yaws.Length; i++)
            {
                rig.SetOrbit(86f + yaws[i], 14f);
                brain.ManualUpdate();
                camera.Render();

                var file = Path.Combine(shotDir, "play-0" + (i + 1) + ".png");
                WriteShot(target, file);
                var eye = boot.Player != null && boot.Player.Head != null ? boot.Player.Head.position.ToString("F2") : "-";
                Debug.Log(string.Format("[play] wrote {0} cam={1:F2} eye={2}", file, camera.transform.position, eye));
            }

            // Fifth shot: her on the bench, 2.4 m forward, 1.1 m to her right and 1.35 m up, fov 38,
            // so the seat depth, the legs and the arm clearance can all be judged from one picture.
            var table = AnchorTable.Load();
            var seat = table != null ? table.Seat(AvatarActor.DefaultSeatId) : null;
            if (seat == null)
            {
                Debug.LogWarning("[play] no " + AvatarActor.DefaultSeatId + " in the anchor table, avatar shot skipped");
            }
            else
            {
                // Cinemachine owns the camera in play; a manual pose means it has to let go first.
                brain.enabled = false;
                var facing = Quaternion.Euler(0f, seat.facingY, 0f);
                var forward = facing * Vector3.forward;
                var right = facing * Vector3.right;
                var seatPoint = new Vector3(seat.x, seat.y, seat.z);

                // Three-quarter from her front (she faces -X, so the camera sits in front and to her
                // right): a straight front view hides the seat depth, a side view hides the arms.
                var eye = seatPoint + forward * 2.4f + right * 1.1f + Vector3.up * 1.35f;
                var lookAt = seatPoint + Vector3.up * 0.37f;
                camera.fieldOfView = 38f;
                camera.transform.position = eye;
                camera.transform.rotation = Quaternion.LookRotation(lookAt - eye, Vector3.up);
                camera.Render();

                var file = Path.Combine(shotDir, "avatar-seated.png");
                WriteShot(target, file);
                Debug.Log(string.Format("[play] wrote {0} fov={1:F0} eye={2} look={3}", file, camera.fieldOfView, eye.ToString("F2"), lookAt.ToString("F2")));
            }

            camera.targetTexture = null;
            target.Release();
            Object.DestroyImmediate(target);
            EditorApplication.Exit(0);
        }

        /// <summary>Reads the target back and writes it as a PNG.</summary>
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

        static bool HasFlag(string flag) =>
            System.Environment.GetCommandLineArgs().Any(a => string.Equals(a, flag, System.StringComparison.OrdinalIgnoreCase));

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