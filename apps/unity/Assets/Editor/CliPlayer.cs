using System.IO;
using System.Linq;
using Mas3D.Actions;
using Mas3D.Avatar;
using Mas3D.Conversation;
using Mas3D.Net;
using Mas3D.UI;
using Mas3D.Voice;
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

            // 按住 V 说话用的那一层。挂在同一个 Session 物体上：面板是 BuildDialogue 建的那一块，
            // 令牌要到运行时才读（BridgeSettings.TokenFilePath 在真机上才算得出来），所以这里只存引用。
            var voice = bootGo.AddComponent<VoiceInput>();
            voice.Panel = dialogue;
            boot.Voice = voice;

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

            // The scene's own lens. The avatar shot below borrows the camera at fov 38, and the action
            // shots have to go back to what the game would show rather than inherit that framing.
            var sceneFov = camera.fieldOfView;

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

            // `-actions 1` renders one shot per action through the same camera the game uses. The
            // director is stepped by hand here: an editor-side render never enters play mode, so its
            // Update never runs. The stance is the caller's to choose — TryEnterAction refuses pat-me
            // and the kiss rather than pressing the player into a posture (see ResolvePlan).
            var refused = 0;
            if (HasFlag("-actions"))
            {
                refused = CaptureActions(boot, camera, brain, target, shotDir, sceneFov);
            }

            camera.targetTexture = null;
            target.Release();
            Object.DestroyImmediate(target);
            // A refused action leaves no new PNG behind; exiting 0 would let the gate sign off on the
            // previous run's picture for an action that never played.
            EditorApplication.Exit(refused > 0 ? 2 : 0);
        }


        /// <summary>
        /// One PNG per action, driven from the scene's own bootstrap. Ninety fixed steps at 1/60 is
        /// 1.5 s of the action's own clock — long enough for the pose to settle and for the contact
        /// hold to credit, short enough that the five shots stay one batch.
        /// </summary>
        static int CaptureActions(RoomBootstrap boot, Camera camera,
            Unity.Cinemachine.CinemachineBrain brain, RenderTexture target, string shotDir, float sceneFov)
        {
            var actions = boot.Actions;
            if (actions == null || boot.Player == null)
            {
                Debug.LogError("[act] no ActionDirector or player on the bootstrap: Boot() did not wire them");
                EditorApplication.Exit(1);
                return 0;
            }

            // Old frames would outlive a refusal: the loop only writes on success, so a shot from the
            // last run would still sit there looking current.
            var stale = Directory.GetFiles(shotDir, "action-*.png");
            foreach (var old in stale) File.Delete(old);
            Debug.Log("[act] cleared " + stale.Length + " stale action shots");

            // The avatar shot left Cinemachine switched off and the lens at fov 38; the action shots
            // are the camera the game would show, so the brain drives again from the scene's fov.
            brain.enabled = true;
            camera.fieldOfView = sceneFov;
            var refused = 0;

            foreach (var id in ActionId.All)
            {
                // pat-me is solved over a crouched player and the kiss needs his face 0.33 m lower than
                // standing puts it; both refuse rather than forcing a stance, so the shot picks it.
                boot.Player.SetPosture(id == ActionId.Kiss ? PlayerPosture.Kneeling
                    : id == ActionId.PatMe ? PlayerPosture.Crouching
                    : PlayerPosture.Standing);

                if (!actions.TryEnterAction(id))
                {
                    Debug.LogError("[act] " + id + " refused: " + actions.LastFaults);
                    refused++;
                    continue;
                }

                // In play mode two Updates run every frame: the director's and the idle's. A batch
                // capture has no player loop, so ticking only the director leaves the idle asleep - and
                // the idle is what writes the blink expression, so the kiss rendered with her eyes wide
                // open. Ticking both makes the frame the one the game would show.
                for (var step = 0; step < 90; step++)
                {
                    actions.Tick(1f / 60f);
                    if (boot.Actor != null && boot.Actor.Idle != null) boot.Actor.Idle.Tick(1f / 60f);
                }
                Physics.SyncTransforms();
                PushLids(boot.Actor != null ? boot.Actor.gameObject : null, id == ActionId.Kiss ? 1f : 0f);
                RefreshSkin(boot.Actor != null ? boot.Actor.gameObject : null);
                brain.ManualUpdate();
                camera.Render();

                // Three of the five are unreadable from the game camera: the kiss camera rides a head
                // 1.4 cm from hers, and hands / pat-her play out between the player's head and her body,
                // where a third-person boom sees her back at 2.8 m. Those three still get the game
                // frame, under a name that says which camera it came from, and a witness frame on top.
                var witnessed = id == ActionId.Hands || id == ActionId.PatHer || id == ActionId.Kiss;
                var file = Path.Combine(shotDir, "action-" + id + (witnessed ? "-gamecam" : "") + ".png");
                WriteShot(target, file);
                var cam = camera.transform;
                var pivot = boot.Rig != null && boot.Rig.Pivot != null ? boot.Rig.Pivot : null;
                var vcam = boot.Rig != null && boot.Rig.VCam != null ? boot.Rig.VCam.transform : null;
                Debug.Log(string.Format("[act] {0} held={1:F2} gap={2:F3} cam=({3:F2},{4:F2},{5:F2}) fwd=({6:F2},{7:F2},{8:F2}) pivot={9} vcam={10} wrote={11}",
                    id, actions.Held, actions.ContactError,
                    cam.position.x, cam.position.y, cam.position.z,
                    cam.forward.x, cam.forward.y, cam.forward.z,
                    pivot == null ? "null" : pivot.position.ToString("F2"),
                    vcam == null ? "null" : vcam.position.ToString("F2"),
                    file));
                // What the boom had to dodge, if anything: the kiss collapsed to a hand's width
                // against her own capsule before the clearance sphere and the camera angle were
                // chosen for it, and a silent collapse is invisible in a screenshot.
                if (boot.Rig != null && boot.Rig.VCam != null)
                {
                    var follow = boot.Rig.VCam.GetComponent<Unity.Cinemachine.CinemachineThirdPersonFollow>();
                    var hit = follow != null ? follow.CurrentObstacle : null;
                    Debug.Log(string.Format("[act] {0} dist={1:F2} rigYaw={2:F1} obstacle={3}",
                        id, boot.Rig.Distance, boot.Rig.Yaw,
                        hit == null ? "none" : hit.name + "/" + hit.gameObject.tag));
                }

                if (id == ActionId.Hands) WriteHandsWitness(boot, camera, target, shotDir);
                else if (id == ActionId.PatHer) WritePatWitness(boot, camera, target, shotDir);
                else if (id == ActionId.Kiss) WriteKissWitness(boot, camera, target, shotDir);

                // Sit-together puts her on the mattress; every id ends where it began, or the next
                // marker is solved against a body that is no longer where the plan thought it was.
                actions.Cancel();
                Physics.SyncTransforms();
            }

            if (refused > 0)
                Debug.LogError("[act] " + refused + " of " + ActionId.All.Length + " actions refused; the shots for those ids are missing");
            return refused;
        }

        /// <summary>
        /// The kiss leaves his face 1.4 cm from hers, and the game camera rides his head, so its frame
        /// is the inside of her hair: it cannot answer the only question that shot exists for — are
        /// the two faces meeting, or is her forehead jammed into his nose? This witness camera stands
        /// off to the side of the contact axis and looks at the meeting point. The player has no body
        /// mesh, so the point his head occupies is where his face would be.
        /// </summary>
        static void WriteKissWitness(RoomBootstrap boot, Camera camera, RenderTexture target, string shotDir)
        {
            var head = boot.Actor != null && boot.Actor.Rig != null
                ? boot.Actor.Rig.Bone(HumanBodyBones.Head) : null;
            if (head == null || boot.Player == null || boot.Player.Head == null) return;

            // Her head is aimed at his face (the rig holds that within 3.4 degrees), so the only camera
            // that can show her shut lids is one standing *on* that aim line - and straight down it her
            // long hair hangs over her face. This one sits 55 cm out and 33 degrees to her right, which
            // parts the hair: the face, both shut lids and the 1.3 cm meeting distance all land in frame.
            // The player has no mesh (BuildPlayer is a capsule, a motor and a camera rig), so his side of
            // the meeting exists only as the empty space the camera stands in.
            var axis = head.forward;
            var side = head.right;
            var face = head.position + axis * 0.09f;
            // Stay level with her face: her aim axis tilts down onto the kneeling player, and following it
            // put the camera 0.32 m under her chin with the ceiling behind her. The horizontal part of
            // the same 33-degree shoulder offset keeps her face against the room instead.
            var level = new Vector3(axis.x, 0f, axis.z).normalized;
            var at = face + (level * 0.85f + side * 0.55f).normalized * 0.46f - Vector3.up * 0.17f;
            var faces = Vector3.Distance(PlayerReach.Face(boot.Player), face);
            WriteWitness(camera, target, Path.Combine(shotDir, "action-kiss.png"), "kiss", at, face, "faces=" + faces.ToString("F3"));
        }

        /// <summary>
        /// Hands are solved between his palms and hers, 0.24 m in front of his chest and 0.62 m below
        /// his eye: from the game camera that whole exchange is hidden behind her own shoulders. The
        /// witness stands 0.95 m behind the palms and 0.95 m to his right, at chest height, so the two
        /// forearms and the four palms are seen side-on against her body.
        /// </summary>
        static void WriteHandsWitness(RoomBootstrap boot, Camera camera, RenderTexture target, string shotDir)
        {
            var player = boot.Player;
            if (player == null || player.Head == null) return;

            var focus = (PlayerReach.Palm(player, true) + PlayerReach.Palm(player, false)) * 0.5f;
            var body = player.transform;
            var at = focus - body.forward * 0.80f + body.right * 0.80f + Vector3.up * 0.40f;
            WriteWitness(camera, target, Path.Combine(shotDir, "action-hands.png"), "hands", at, focus, null);
        }

        /// <summary>
        /// Pat-her is her head top meeting a palm that has no mesh, so the frame has to carry the bow:
        /// the witness sits 0.70 m behind him and 0.80 m to his right, 0.62 m above her head top, and
        /// looks down at the head top, which reads as a head offered under a hand.
        /// </summary>
        static void WritePatWitness(RoomBootstrap boot, Camera camera, RenderTexture target, string shotDir)
        {
            var rig = boot.Actor != null ? boot.Actor.Rig : null;
            var player = boot.Player;
            if (rig == null || player == null || player.Head == null) return;

            // Raised and off to his right, as the review asked: her bowed head, her arms resting in her
            // lap (they used to reach her own ear and read as her covering her head), and the 0.62 m of
            // empty air above her head top where the palm the geometry holds her to actually is. The
            // player has no mesh, so that hand can only exist as that air.
            var focus = ActionGeometry.HerHeadTop(rig);
            var at = focus - player.transform.forward * 0.70f + player.transform.right * 0.80f + Vector3.up * 0.62f;
            WriteWitness(camera, target, Path.Combine(shotDir, "action-pat-her.png"), "pat-her", at, focus, null);
        }

        /// <summary>
        /// VRM10 pushes expression weights into the face blend shapes from its own Update, and a batch
        /// capture never runs one: AvatarIdle forcing EyesClosed = 1 leaves the runtime weight at 1 while
        /// every one of the face's 57 shapes stays at 0, so the kiss rendered with her eyes wide open -
        /// the one thing that shot exists to prove. The Blink expression owns the three lid shapes below;
        /// writing them here makes the frame the one play mode would composite, and the log line is the
        /// evidence that it did.
        /// </summary>
        static void PushLids(GameObject root, float weight)
        {
            if (root == null) return;

            var written = 0;
            foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = skin.sharedMesh;
                if (mesh == null) continue;
                for (var i = 0; i < mesh.blendShapeCount; i++)
                {
                    var name = mesh.GetBlendShapeName(i);
                    if (name != "Fcl_EYE_Close" && name != "Fcl_EYE_Close_L" && name != "Fcl_EYE_Close_R") continue;
                    skin.SetBlendShapeWeight(i, Mathf.Clamp01(weight) * 100f);
                    written++;
                }
            }

            Debug.Log("[act] lids=" + weight.ToString("F2") + " shapes=" + written);
        }

        /// <summary>A fixed camera for one frame: bypasses the brain, renders, writes, and hands control back.</summary>
        static void WriteWitness(Camera camera, RenderTexture target, string file, string tag,
            Vector3 position, Vector3 lookAt, string extra)
        {
            var brain = camera.GetComponent<Unity.Cinemachine.CinemachineBrain>();
            if (brain != null) brain.enabled = false;

            camera.transform.position = position;
            camera.transform.rotation = Quaternion.LookRotation(lookAt - position, Vector3.up);
            camera.Render();
            WriteShot(target, file);
            Debug.Log(string.Format("[act] {0} witness wrote={1} at=({2:F2},{3:F2},{4:F2}) look=({5:F2},{6:F2},{7:F2}){8}",
                tag, file, position.x, position.y, position.z, lookAt.x, lookAt.y, lookAt.z,
                string.IsNullOrEmpty(extra) ? "" : " " + extra));

            if (brain != null) brain.enabled = true;
        }

        /// <summary>
        /// Forces the skinned meshes to re-bake their bone palette. A batch capture never runs a
        /// player loop, so the palette the renderer cached stays where it was when the scene was
        /// opened: posing bones then calling Camera.Render() paints the pose from before the
        /// action, which made hands and pat-her come out pixel-identical. Disabling and re-enabling
        /// the renderer is the documented way to make it recalculate outside play mode.
        /// </summary>
        static void RefreshSkin(GameObject root)
        {
            if (root == null) return;

            var skins = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var skin in skins) skin.enabled = false;
            foreach (var skin in skins) skin.enabled = true;
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