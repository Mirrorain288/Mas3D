using System.Collections.Generic;
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
            // After BuildPlayer and before anything else touches the player: BakeBody writes into the
            // PlayerBody component BuildPlayer just added.
            BakeBody(player);
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
            // MToon 的明暗分界要跟着场景走：VRM 资产默认 shift=1/toony=1 会让 shade 段消失。
            var shaded = AvatarShading.Apply(actor.gameObject);
            Debug.Log("[player] avatar shading materials=" + shaded);
            // 虹膜贴图换成重画过的那张：VRM 自带的是荧光黄绿（饱和度 0.98），夜景里像两粒 LED。
            // 同样落在场景副本上，幂等——重复 BuildScene 不会再套一层。
            var iris = AvatarEyes.LoadIris();
            var eyes = AvatarEyes.Apply(actor.gameObject, iris);
            Debug.Log("[player] iris materials=" + eyes + " texture=" + (iris != null ? iris.name : "missing"));
            var outlined = Mas3D.Rendering.AvatarOutlines.Apply(actor.gameObject, HasFlag("-outlineVerbose"));
            Debug.Log("[player] avatar outline materials=" + outlined);
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
            // The scene carries the component, not RoomBootstrap: BuildScene bakes the body into this
            // one, and Boot() then finds it instead of adding a second PlayerBody to the same player.
            go.AddComponent<PlayerBody>();
            // A placeholder pose; RoomBootstrap teleports to the solved A2 anchor on play.
            go.transform.position = new Vector3(-0.72f, 0.03f, -0.03f);
            go.transform.rotation = Quaternion.Euler(0f, 86f, 0f);
            return go;
        }

        // Bakes the VRM into the scene: the packaged build has no Editor/Vrm10Importer, so the body
        // must already exist as a hierarchy inside Main.unity. Inactive on purpose — RoomBootstrap
        // adopts and shows it only when the player actually spawns (and never under -nobody 1).
        static void BakeBody(GameObject player)
        {
            var body = player.GetComponent<PlayerBody>() ?? player.AddComponent<PlayerBody>();
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerBody.BodyPrefabPath);
            if (source == null)
            {
                Debug.LogWarning("[player] body missing at " + PlayerBody.BodyPrefabPath + " -> 打包版玩家只剩胶囊");
                return;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
            instance.name = PlayerBody.BodyName;
            instance.transform.SetParent(player.transform, false);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;
            instance.SetActive(false);

            var rig = instance.GetComponent<AvatarRig>() ?? instance.AddComponent<AvatarRig>();
            rig.EnsureBound();
            body.Baked = instance;
            Debug.Log(string.Format("[player] body baked renderers={0} height={1:F3}",
                instance.GetComponentsInChildren<Renderer>(true).Length, rig.Measure().size.y));
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
            // 出图必须走后处理：ACES 与 ColorAdjustments 现在全在 RoomLook 的全局 Volume 里，
            // 相机不开 post 就只剩未经色调映射的线性值（白床单会直接糊掉）。
            go.AddComponent<UniversalAdditionalCameraData>().renderPostProcessing = true;
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

            // 出图必须和运行时走同一条调色链：Volume 是 Boot() 里用代码建的，编辑器里没有
            // Update 循环，VolumeManager 可能还没把它算进栈，所以这里把它打出来核对。
            var volumes = Object.FindObjectsByType<UnityEngine.Rendering.Volume>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var volume in volumes)
            {
                var profile = volume.sharedProfile;
                UnityEngine.Rendering.Universal.ColorAdjustments colours = null;
                var grading = profile != null && profile.TryGet(out colours);
                Debug.Log(string.Format("[play] volume {0} global={1} weight={2:F2} profile={3} colourAdjustments={4} post={5}",
                    volume.name, volume.isGlobal, volume.weight, profile != null ? profile.name : "null", grading,
                    grading ? colours.postExposure.value.ToString("F2") : "-"));
            }

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
                    ("glow", new[] { "ceiling-glow-1", "ceiling-glow-2", "ceiling-glow-3", "ceiling-glow-4" }),
                    ("fill", new[] { "fill" }),
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
            // 批处理里没有 Update 循环：Cinemachine 的阻尼只跟着 ManualUpdate 前进。
            // 先空转两帧把相机收敛到目标机位，否则第一张 play-01 拍的是「还在飞」的相机，
            // 与后面同机位重拍的 no-shadow-01 构图差出几十个像素——配对守卫会先把它抓出来（C1 复查时命中 36%）。
            rig.SetOrbit(86f, 14f);
            brain.ManualUpdate();
            camera.Render();
            brain.ManualUpdate();
            camera.Render();

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

            // `-hideplayer 1`：只拍房间。play-01 那对阴影图里，一整个玩家身体就站在灯下，
            // 判据被它一个人撑着——家具到底有没有在地板上落影，只能把身体藏起来单独看一张。
            // 只藏玩家身体的 renderer（连影子一起退出渲染），Monika 不是玩家，保持可见。
            //
            // 藏起来必须还回来：同一趟 capture 后面还有 no-shadow-01、avatar-seated、foot/face/eyes
            // 要带着玩家拍。漏一次还原，那几张就全成了空场景，play-01 那对更会变成「有玩家 vs 没玩家」，
            // 判据从此恒绿（评审 C1：no-shadow-01 与 room-noshadow-01 一度逐字节相同）。
            var hidePlayer = HasFlag("-hideplayer");
            var hiddenBody = new List<Renderer>();

            void HidePlayer()
            {
                var body = boot.Player != null ? boot.Player.GetComponent<PlayerBody>() : null;
                if (body == null || body.Avatar == null)
                {
                    // 找不到身体还继续拍，room 那对就会退化成「两张都带玩家」而照样 PASS ——
                    // 判据在降级场景下盖章比直接失败更糟，所以这里硬退出。
                    Debug.LogError("[play] -hideplayer 找不到玩家身体，room 对照图会失去意义");
                    EditorApplication.Exit(1);
                    return;
                }

                foreach (var renderer in body.Avatar.GetComponentsInChildren<Renderer>(true))
                {
                    if (!renderer.enabled) continue;
                    renderer.enabled = false;
                    hiddenBody.Add(renderer);
                }
            }

            void ShowPlayer()
            {
                var restored = 0;
                foreach (var renderer in hiddenBody)
                {
                    if (renderer == null) continue;
                    renderer.enabled = true;
                    restored++;
                }

                // 日志里留一行「还回来了几个」：后面任何一张图少了玩家，翻 capture 日志就能看出是哪一步开始丢的。
                if (restored > 0) Debug.Log("[play] player body restored (" + restored + " renderers)");
                hiddenBody.Clear();
            }

            // 房间单独一张：和 play-01 同机位、同灯，只把玩家身体拿掉，拍完立刻还原。
            if (hidePlayer)
            {
                HidePlayer();
                rig.SetOrbit(86f, 14f);
                brain.ManualUpdate();
                camera.Render();
                var roomShot = Path.Combine(shotDir, "room-shadow-01.png");
                WriteShot(target, roomShot);
                Debug.Log("[play] wrote " + roomShot + " (player hidden, " + hiddenBody.Count + " renderers)");
                ShowPlayer();
            }

            // `-shadows 0`：把两盏会投影的灯关掉阴影，再用同一个机位渲一张 play-01 的对照图。
            // 阴影是不是真的落到地板上，只能靠两张图的像素差说话（R17 要求 >3%）。
            if (HasFlag("-noShadows"))
            {
                foreach (var light in Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    light.shadows = LightShadows.None;

                // Two angles: the doorway view (play-01) has little floor in frame, the reverse
                // view (play-03) looks straight at the bed and the rug, so the shadow footprint
                // covers far more pixels there.
                foreach (var yaw in new[] { 0f, 180f })
                {
                    // 同上：换机位后要收敛两帧，配对图才真正同构图。
                    rig.SetOrbit(86f + yaw, 14f);
                    brain.ManualUpdate();
                    camera.Render();
                    brain.ManualUpdate();
                    camera.Render();
                    var plain = Path.Combine(shotDir, "no-shadow-0" + (yaw == 0f ? 1 : 3) + ".png");
                    WriteShot(target, plain);
                    Debug.Log("[play] wrote " + plain + " (shadows off)");

                    // 房间那一对必须和 no-shadow-01 同机位、同灯，所以顺序只能是：
                    // 先拍带玩家的 no-shadow-01（上面那张），再藏起来拍 room-noshadow-01，拍完立刻还原。
                    if (hidePlayer && yaw == 0f)
                    {
                        HidePlayer();
                        camera.Render();
                        var roomPlain = Path.Combine(shotDir, "room-noshadow-01.png");
                        WriteShot(target, roomPlain);
                        Debug.Log("[play] wrote " + roomPlain + " (player hidden, shadows off)");
                        ShowPlayer();
                    }
                }
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

                // Eight shots around her feet, low and close to the floor: the look-compare script
                // measures the spread of floor brightness across them. A stray light or a burnt
                // patch under her shows up as one bright angle and fails the 5% criterion.
                var feet = seatPoint + Vector3.down * seat.y + Vector3.up * 0.02f;
                camera.fieldOfView = 55f;
                for (var i = 0; i < 8; i++)
                {
                    var angle = i * Mathf.PI * 2f / 8f;
                    var offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 0.85f;
                    camera.transform.position = feet + offset + Vector3.up * 0.32f;
                    camera.transform.rotation = Quaternion.LookRotation(feet - camera.transform.position, Vector3.up);
                    camera.Render();
                    var footFile = Path.Combine(shotDir, "foot-0" + (i + 1) + ".png");
                    WriteShot(target, footFile);
                }
                Debug.Log("[play] wrote 8 foot shots around " + feet.ToString("F2"));
            }

            // A face-only close-up: look-compare measures the cheek tint and the peak brightness
            // on skin pixels, and on a full-body frame the white blouse passes the same colour
            // filter, which pins the "peak" at the shirt instead of the cheek.
            if (boot.Actor != null && boot.Actor.Rig != null)
            {
                var head = Mas3D.Actions.ActionGeometry.HerHeadCenter(boot.Actor.Rig);
                var facing = Mas3D.Actions.ActionGeometry.HerFacing(boot.Actor.Rig);
                var side = Vector3.Cross(Vector3.up, facing).normalized;
                var eye = head + facing * 0.62f + side * 0.10f + Vector3.up * 0.02f;
                var look = head + Vector3.up * 0.015f;
                camera.fieldOfView = 30f;
                camera.transform.position = eye;
                camera.transform.rotation = Quaternion.LookRotation(look - eye, Vector3.up);
                camera.Render();
                var faceFile = Path.Combine(shotDir, "face.png");
                WriteShot(target, faceFile);
                Debug.Log("[play] wrote " + faceFile + " fov=30");

                // 眼部特写：虹膜判据（饱和度、色相、高光簇、外圈暗带）必须在出图上量。
                // face.png 里虹膜只有 25 px 宽，外圈那两三个像素早被抗锯齿抹平，量出来没有意义；
                // 所以再拍一张 0.30 m、fov 18 的特写，机位对准两只眼骨的中点，两只虹膜各约 90 px。
                var leftEye = boot.Actor.Rig.Bone(HumanBodyBones.LeftEye);
                var rightEye = boot.Actor.Rig.Bone(HumanBodyBones.RightEye);
                if (leftEye != null && rightEye != null)
                {
                    var eyesMid = (leftEye.position + rightEye.position) * 0.5f;
                    var eyesPosition = eyesMid + facing * 0.30f + Vector3.up * 0.004f;
                    camera.fieldOfView = 18f;
                    camera.transform.position = eyesPosition;
                    camera.transform.rotation = Quaternion.LookRotation(eyesMid - eyesPosition, Vector3.up);
                    camera.Render();
                    var eyesFile = Path.Combine(shotDir, "eyes.png");
                    WriteShot(target, eyesFile);
                    Debug.Log(string.Format("[play] wrote {0} fov=18 eye={1} look={2}", eyesFile, eyesPosition.ToString("F3"), eyesMid.ToString("F3")));
                }
                else
                {
                    Debug.LogWarning("[play] 找不到左右眼骨，跳过 eyes.png");
                }

                // 调色到底有没有进渲染：VolumeManager 的栈要等第一次 Render 之后才有内容，
                // 所以放在这里读。栈里的值和 profile 里的值不一致，说明 Volume 没被相机捞进去。
                var extraCamera = camera.GetUniversalAdditionalCameraData();
                var stack = UnityEngine.Rendering.VolumeManager.instance.stack;
                var stackColours = stack != null ? stack.GetComponent<ColorAdjustments>() : null;
                Debug.Log(string.Format("[play] camera post={0} mask={1} stackActive={2} stackPost={3}",
                    extraCamera != null && extraCamera.renderPostProcessing,
                    extraCamera != null ? extraCamera.volumeLayerMask.value : -1,
                    stackColours != null && stackColours.active,
                    stackColours != null ? stackColours.postExposure.value.ToString("F2") : "-"));
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
        /// Skin-tone A/B: the same seated camera, one PNG per colour/lights variant.
        ///   unity.ps1 run -- -executeMethod Mas3D.Editor.CliPlayer.Skin       (never -nographics)
        /// 不是交付图，只用来选"冷暖"：每张只动光色/滤镜/白平衡，曝光、对比、几何与姿势都不动。
        /// </summary>
        public static void Skin()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid()) { Debug.LogError("[skin] cannot open " + ScenePath); EditorApplication.Exit(1); return; }

            var boot = Object.FindAnyObjectByType<RoomBootstrap>();
            if (boot == null) { Debug.LogError("[skin] no RoomBootstrap in " + ScenePath); EditorApplication.Exit(1); return; }
            boot.Boot();

            var camera = boot.OutputCamera;
            if (camera == null) { Debug.LogError("[skin] no output camera"); EditorApplication.Exit(1); return; }

            var avatar = Object.FindAnyObjectByType<AvatarActor>();
            if (avatar != null) avatar.SitAt(avatar.SeatId);

            var brain = camera.GetComponent<Unity.Cinemachine.CinemachineBrain>();
            if (brain != null) brain.enabled = false;

            var table = AnchorTable.Load();
            var seat = table != null ? table.Seat(AvatarActor.DefaultSeatId) : null;
            if (seat == null) { Debug.LogError("[skin] no seat " + AvatarActor.DefaultSeatId); EditorApplication.Exit(1); return; }

            // 比交付的 avatar-seated.png 更近：那张要看坐深和腿，这张只看脸和上身的颜色。
            var facing = Quaternion.Euler(0f, seat.facingY, 0f);
            var forward = facing * Vector3.forward;
            var right = facing * Vector3.right;
            var seatPoint = new Vector3(seat.x, seat.y, seat.z);
            var eye = seatPoint + forward * 1.55f + right * 0.55f + Vector3.up * 1.15f;
            var lookAt = seatPoint + Vector3.up * 0.62f;
            camera.fieldOfView = 34f;
            camera.transform.position = eye;
            camera.transform.rotation = Quaternion.LookRotation(lookAt - eye, Vector3.up);

            var shotDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Shots"));
            Directory.CreateDirectory(shotDir);
            var target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = target;

            var lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var moon = lights.FirstOrDefault(l => l.name == "moon");
            var lamp = lights.FirstOrDefault(l => l.name == "bedside-lamp");
            var glows = lights.Where(l => l.name.StartsWith("ceiling-glow")).ToArray();
            var fill = lights.FirstOrDefault(l => l.name == "fill");
            var baseMoon = moon != null ? moon.color : Color.white;
            var baseLamp = lamp != null ? lamp.color : Color.white;
            var baseGlow = glows.Length > 0 ? glows[0].color : Color.white;
            var baseFill = fill != null ? fill.color : Color.white;
            var lampPower = lamp != null ? lamp.intensity : 1f;

            var variants = new (string name, Color filter, float wbTemp, float wbTint, Color moonC, Color lampC, float lampScale, Color glowC, Color fillC)[]
            {
                ("skin-1-baseline", new Color(1.00f, 0.96f, 0.93f), 0f, 0f, baseMoon, baseLamp, 1.0f, baseGlow, baseFill),
                ("skin-2-neutral", Color.white, -6f, 2f, baseMoon, baseLamp, 1.0f, baseGlow, baseFill),
                ("skin-3-cool", Color.white, -12f, 3f, new Color(1.00f, 0.97f, 0.95f), new Color(1.00f, 0.88f, 0.80f), 0.75f, new Color(1.00f, 0.96f, 0.92f), baseFill),
                ("skin-4-pale", new Color(0.98f, 1.00f, 1.02f), -16f, 4f, new Color(1.00f, 0.98f, 0.97f), new Color(1.00f, 0.90f, 0.84f), 0.60f, new Color(1.00f, 0.97f, 0.94f), new Color(0.74f, 0.76f, 0.82f)),
            };

            // 第一次 Camera.Render() 在这个工程里会拿错材质颜色（MToon 首帧的 keyword/属性块还没生效，
            // CliBody 早就记过同一现象：第一张绿、第二张才对）。先渲一张丢弃，再开始真正的变体。
            RefreshSkin(avatar != null ? avatar.gameObject : null);
            camera.Render();

            foreach (var v in variants)
            {
                if (moon != null) moon.color = v.moonC;
                if (lamp != null) { lamp.color = v.lampC; lamp.intensity = lampPower * v.lampScale; }
                foreach (var glow in glows) glow.color = v.glowC;
                if (fill != null) fill.color = v.fillC;
                RoomLook.Apply(filter: v.filter, whiteBalanceTemperature: v.wbTemp, whiteBalanceTint: v.wbTint);
                RefreshSkin(avatar != null ? avatar.gameObject : null);
                camera.Render();
                var file = Path.Combine(shotDir, v.name + ".png");
                WriteShot(target, file);

                // 同一档再拍一张脸的特写：肤色与眼睛只在这张上判得出来，半身图里脸只有几十像素。
                // 用头骨定位，不要用座椅点外推：坐姿的头位置随骨架走，外推几厘米就偏到窗外去了。
                var headCenter = avatar != null && avatar.Rig != null ? ActionGeometry.HerHeadCenter(avatar.Rig) : seatPoint + Vector3.up * 1.15f;
                var facing2 = avatar != null && avatar.Rig != null ? ActionGeometry.HerFacing(avatar.Rig) : forward;
                if (facing2.sqrMagnitude < 0.01f) facing2 = forward;
                var side = Vector3.Cross(Vector3.up, facing2).normalized;
                var faceEye = headCenter + facing2 * 0.62f + side * 0.10f + Vector3.up * 0.02f;
                var faceLook = headCenter + Vector3.up * 0.015f;
                camera.fieldOfView = 30f;
                camera.transform.position = faceEye;
                camera.transform.rotation = Quaternion.LookRotation(faceLook - faceEye, Vector3.up);
                camera.Render();
                var faceFile = Path.Combine(shotDir, v.name + "-face.png");
                WriteShot(target, faceFile);

                camera.fieldOfView = 34f;
                camera.transform.position = eye;
                camera.transform.rotation = Quaternion.LookRotation(lookAt - eye, Vector3.up);

                Debug.Log(string.Format("[skin] {0} filter=({1:F2},{2:F2},{3:F2}) wb={4}/{5} lamp={6:F2}x -> {7}",
                    v.name, v.filter.r, v.filter.g, v.filter.b, v.wbTemp, v.wbTint, v.lampScale, file));
            }

            camera.targetTexture = null;
            target.Release();
            Object.DestroyImmediate(target);
            EditorApplication.Exit(0);
        }

        /// <summary>
        /// "Why does her face look dirty" A/B: same face camera, one variable per shot.
        ///   unity.ps1 run -- -executeMethod Mas3D.Editor.CliPlayer.SkinDiag
        /// 五个变量分别单独关掉/改掉：玩家投影、灯投影、SSAO、MToon 明暗分界、shade 色亮度。
        /// </summary>
        public static void SkinDiag()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid()) { Debug.LogError("[skin] cannot open " + ScenePath); EditorApplication.Exit(1); return; }
            var boot = Object.FindAnyObjectByType<RoomBootstrap>();
            if (boot == null) { Debug.LogError("[skin] no RoomBootstrap"); EditorApplication.Exit(1); return; }
            boot.Boot();

            var camera = boot.OutputCamera;
            var avatar = Object.FindAnyObjectByType<AvatarActor>();
            if (camera == null || avatar == null || avatar.Rig == null) { Debug.LogError("[skin] camera or avatar missing"); EditorApplication.Exit(1); return; }
            avatar.SitAt(avatar.SeatId);

            var brain = camera.GetComponent<Unity.Cinemachine.CinemachineBrain>();
            if (brain != null) brain.enabled = false;

            var headCenter = ActionGeometry.HerHeadCenter(avatar.Rig);
            var facing = ActionGeometry.HerFacing(avatar.Rig);
            var side = Vector3.Cross(Vector3.up, facing).normalized;
            var eye = headCenter + facing * 0.62f + side * 0.10f + Vector3.up * 0.02f;
            var look = headCenter + Vector3.up * 0.015f;
            camera.fieldOfView = 30f;
            camera.transform.position = eye;
            camera.transform.rotation = Quaternion.LookRotation(look - eye, Vector3.up);

            var shotDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Shots"));
            Directory.CreateDirectory(shotDir);
            var target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = target;

            var lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var player = boot.Player != null ? boot.Player.transform.root.gameObject : null;
            var playerRenderers = player != null ? player.GetComponentsInChildren<Renderer>(true) : new Renderer[0];
            var ssao = FindSsao();

            var herRenderers = avatar.GetComponentsInChildren<Renderer>(true);
            var configs = new (string name, bool hidePlayer, bool shadowsOff, bool ssaoOff, bool herNoShadow, float shift, float toony, float shadeLift)[]
            {
                ("diag-1-baseline", false, false, false, false, AvatarShading.Shift, AvatarShading.Toony, 1.00f),
                ("diag-2-no-player", true, false, false, false, AvatarShading.Shift, AvatarShading.Toony, 1.00f),
                ("diag-3-no-shadows", false, true, false, false, AvatarShading.Shift, AvatarShading.Toony, 1.00f),
                ("diag-4-soft-mtoon", false, false, false, false, 0.10f, 0.50f, 1.35f),
                ("diag-5-soft-no-ao", false, false, true, false, 0.10f, 0.50f, 1.35f),
                // 只让她自己不收投影，房间那套阴影原样留着：脸干净、房间还有立体感。
                ("diag-6-her-noshadow", false, false, false, true, AvatarShading.Shift, AvatarShading.Toony, 1.00f),
                ("diag-7-noshadow-soft", false, false, false, true, 0.02f, 0.60f, 1.15f),
                ("diag-8-noshadow-soft-nolampwarm", false, false, false, true, 0.02f, 0.60f, 1.15f),
            };

            // 首帧材质色错乱（CliBody 记过的同一现象）：先渲一张丢弃。
            camera.Render();

            foreach (var v in configs)
            {
                foreach (var r in playerRenderers) r.enabled = !v.hidePlayer;
                foreach (var l in lights) l.shadows = v.shadowsOff ? LightShadows.None : l.shadows;
                foreach (var r in herRenderers) r.receiveShadows = !v.herNoShadow;
                if (ssao != null) { ssao.SetActive(!v.ssaoOff); ssao.Create(); }
                RewriteShading(avatar.gameObject, v.shift, v.toony, v.shadeLift);
                RefreshSkin(avatar.gameObject);
                camera.Render();
                var file = Path.Combine(shotDir, v.name + ".png");
                WriteShot(target, file);
                Debug.Log(string.Format("[skin] {0} hidePlayer={1} shadowsOff={2} ssaoOff={3} shift={4:F2} toony={5:F2} shadeLift={6:F2} -> {7}",
                    v.name, v.hidePlayer, v.shadowsOff, v.ssaoOff, v.shift, v.toony, v.shadeLift, file));
            }

            if (ssao != null) { ssao.SetActive(true); ssao.Create(); }
            foreach (var r in playerRenderers) r.enabled = true;
            camera.targetTexture = null;
            target.Release();
            Object.DestroyImmediate(target);
            EditorApplication.Exit(0);
        }

        /// <summary>场景里那支 SSAO（URP 17 的 ScreenSpaceAmbientOcclusion），找不到就返回 null。</summary>
        static ScreenSpaceAmbientOcclusion FindSsao()
        {
            // UniversalRenderPipelineAsset.scriptableRendererData 是 internal，编辑器里直接从资产读。
            var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/Mas3D-URP-Renderer.asset");
            if (data == null) return null;
            foreach (var feature in data.rendererFeatures)
            {
                var ssao = feature as ScreenSpaceAmbientOcclusion;
                if (ssao != null) return ssao;
            }
            return null;
        }

        /// <summary>把 MToon 的 shift/toony 与 shade 色亮度写进场景里的材质副本（诊断用，可变参数）。</summary>
        static void RewriteShading(GameObject avatar, float shift, float toony, float shadeLift)
        {
            var shiftId = Shader.PropertyToID("_ShadingShiftFactor");
            var toonyId = Shader.PropertyToID("_ShadingToonyFactor");
            var shadeId = Shader.PropertyToID("_ShadeColor");
            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null || !material.HasProperty(shiftId)) continue;
                    material.SetFloat(shiftId, shift);
                    if (material.HasProperty(toonyId)) material.SetFloat(toonyId, toony);
                    if (material.HasProperty(shadeId))
                    {
                        var shade = material.GetColor(shadeId);
                        material.SetColor(shadeId, new Color(Mathf.Min(1f, shade.r * shadeLift), Mathf.Min(1f, shade.g * shadeLift), Mathf.Min(1f, shade.b * shadeLift), shade.a));
                    }
                    material.DisableKeyword(AvatarShading.ParameterMapKeyword);
                }
            }
        }

        /// <summary>
        /// 光色定档（skin-4 那套）之后，把"脸干净"的做法并排出来：只改她自己的材质与收影，房间不动。
        ///   unity.ps1 run -- -executeMethod Mas3D.Editor.CliPlayer.SkinCombo
        /// </summary>
        public static void SkinCombo()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid()) { Debug.LogError("[skin] cannot open " + ScenePath); EditorApplication.Exit(1); return; }
            var boot = Object.FindAnyObjectByType<RoomBootstrap>();
            if (boot == null) { Debug.LogError("[skin] no RoomBootstrap"); EditorApplication.Exit(1); return; }
            boot.Boot();

            var camera = boot.OutputCamera;
            var avatar = Object.FindAnyObjectByType<AvatarActor>();
            if (camera == null || avatar == null || avatar.Rig == null) { Debug.LogError("[skin] camera or avatar missing"); EditorApplication.Exit(1); return; }
            avatar.SitAt(avatar.SeatId);

            var brain = camera.GetComponent<Unity.Cinemachine.CinemachineBrain>();
            if (brain != null) brain.enabled = false;

            // 定档的那套光（skin-4-pale）：滤镜微冷、WB −16/4、台灯 0.6、月光与吊顶光近白。
            var lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var lamp = lights.FirstOrDefault(l => l.name == "bedside-lamp");
            if (lamp != null) lamp.color = new Color(1.00f, 0.90f, 0.84f);
            foreach (var l in lights.Where(l => l.name == "moon")) l.color = new Color(1.00f, 0.98f, 0.97f);
            foreach (var l in lights.Where(l => l.name.StartsWith("ceiling-glow"))) l.color = new Color(1.00f, 0.97f, 0.94f);
            foreach (var l in lights.Where(l => l.name == "fill")) l.color = new Color(0.74f, 0.76f, 0.82f);
            if (lamp != null) lamp.intensity *= 0.60f;
            RoomLook.Apply(filter: new Color(0.98f, 1.00f, 1.02f), whiteBalanceTemperature: -16f, whiteBalanceTint: 4f);

            var head = ActionGeometry.HerHeadCenter(avatar.Rig);
            var facing = ActionGeometry.HerFacing(avatar.Rig);
            var side = Vector3.Cross(Vector3.up, facing).normalized;
            var faceEye = head + facing * 0.62f + side * 0.10f + Vector3.up * 0.02f;
            var faceLook = head + Vector3.up * 0.015f;

            var shotDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Shots"));
            Directory.CreateDirectory(shotDir);
            var target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = target;

            var herRenderers = avatar.GetComponentsInChildren<Renderer>(true);
            var skin = SkinMaterials(avatar.gameObject);
            Debug.Log("[skin] skin materials: " + string.Join(", ", skin.Select(m => m.name)));

            var configs = new (string name, bool noShadow, float shift, float toony, float shadeLift, float tintWarm, float tintCool)[]
            {
                ("combo-1-lightonly", false, AvatarShading.Shift, AvatarShading.Toony, 1.00f, 1f, 1f),
                ("combo-2-noshadow", true, AvatarShading.Shift, AvatarShading.Toony, 1.00f, 1f, 1f),
                ("combo-3-noshadow-lift", true, 0.02f, 0.55f, 1.55f, 1f, 1f),
                ("combo-4-lift-cool", true, 0.02f, 0.45f, 1.65f, 0.99f, 1.03f),
            };

            camera.Render();
            foreach (var v in configs)
            {
                foreach (var r in herRenderers) r.receiveShadows = !v.noShadow;
                RewriteShading(avatar.gameObject, v.shift, v.toony, v.shadeLift);
                foreach (var m in skin)
                {
                    var tint = new Color(v.tintWarm, (v.tintWarm + v.tintCool) * 0.5f, v.tintCool, 1f);
                    if (m.HasProperty("_Color")) m.SetColor("_Color", Color.Lerp(m.GetColor("_Color"), tint, 0.5f));
                    if (m.HasProperty("_ShadeColor")) m.SetColor("_ShadeColor", m.GetColor("_ShadeColor") * new Color(1.02f, 1.00f, 0.99f, 1f));
                    m.DisableKeyword(AvatarShading.ParameterMapKeyword);
                }
                RefreshSkin(avatar.gameObject);

                camera.fieldOfView = 30f;
                camera.transform.position = faceEye;
                camera.transform.rotation = Quaternion.LookRotation(faceLook - faceEye, Vector3.up);
                camera.Render();
                WriteShot(target, Path.Combine(shotDir, v.name + "-face.png"));

                camera.fieldOfView = 40f;
                camera.transform.position = head + facing * 1.5f + side * 0.30f + Vector3.down * 0.15f;
                camera.transform.rotation = Quaternion.LookRotation(head + Vector3.down * 0.05f - camera.transform.position, Vector3.up);
                camera.Render();
                WriteShot(target, Path.Combine(shotDir, v.name + ".png"));
                Debug.Log("[skin] " + v.name + " noShadow=" + v.noShadow + " shift=" + v.shift.ToString("F2") + " toony=" + v.toony.ToString("F2") + " lift=" + v.shadeLift.ToString("F2"));
            }

            camera.targetTexture = null;
            target.Release();
            Object.DestroyImmediate(target);
            EditorApplication.Exit(0);
        }

        /// <summary>她身上被判定为皮肤的那几件材质（名字或贴图名里带 skin/face/body 的）。</summary>
        static List<Material> SkinMaterials(GameObject avatar)
        {
            var result = new List<Material>();
            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null || result.Contains(material)) continue;
                    var name = material.name.ToLowerInvariant();
                    if (name.Contains("skin") || name.Contains("face") || name.Contains("body"))
                    {
                        result.Add(material);
                        continue;
                    }
                    var map = material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
                    if (map != null && map.name.ToLowerInvariant().Contains("skin")) result.Add(material);
                }
            }
            return result;
        }

        /// <summary>
        /// 脸"干净"的三种做法，全部在定档光（skin-4）下：把脸从 MToon 的两段色阶改成单段 lit。
        ///   unity.ps1 run -- -executeMethod Mas3D.Editor.CliPlayer.SkinFlat
        /// 依据：HAST-SEC 那套看起来干净，是因为脸只有一段颜色 + 没有投影；我们现在的"脏"是
        /// 烘焙在脸贴图里的暗带 × 投影 × 两段色阶叠出来的。
        /// </summary>
        public static void SkinFlat()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid()) { Debug.LogError("[skin] cannot open " + ScenePath); EditorApplication.Exit(1); return; }
            var boot = Object.FindAnyObjectByType<RoomBootstrap>();
            if (boot == null) { Debug.LogError("[skin] no RoomBootstrap"); EditorApplication.Exit(1); return; }
            boot.Boot();

            var camera = boot.OutputCamera;
            var avatar = Object.FindAnyObjectByType<AvatarActor>();
            if (camera == null || avatar == null || avatar.Rig == null) { Debug.LogError("[skin] camera or avatar missing"); EditorApplication.Exit(1); return; }
            avatar.SitAt(avatar.SeatId);

            var brain = camera.GetComponent<Unity.Cinemachine.CinemachineBrain>();
            if (brain != null) brain.enabled = false;

            var lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var lamp = lights.FirstOrDefault(l => l.name == "bedside-lamp");
            if (lamp != null) { lamp.color = new Color(1.00f, 0.90f, 0.84f); lamp.intensity *= 0.60f; }
            foreach (var l in lights.Where(l => l.name == "moon")) l.color = new Color(1.00f, 0.98f, 0.97f);
            foreach (var l in lights.Where(l => l.name.StartsWith("ceiling-glow"))) l.color = new Color(1.00f, 0.97f, 0.94f);
            foreach (var l in lights.Where(l => l.name == "fill")) l.color = new Color(0.74f, 0.76f, 0.82f);
            RoomLook.Apply(filter: new Color(0.98f, 1.00f, 1.02f), whiteBalanceTemperature: -16f, whiteBalanceTint: 4f);

            var head = ActionGeometry.HerHeadCenter(avatar.Rig);
            var facing = ActionGeometry.HerFacing(avatar.Rig);
            var side = Vector3.Cross(Vector3.up, facing).normalized;
            var faceEye = head + facing * 0.62f + side * 0.10f + Vector3.up * 0.02f;
            var faceLook = head + Vector3.up * 0.015f;

            var shotDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Shots"));
            Directory.CreateDirectory(shotDir);
            var target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = target;

            var herRenderers = avatar.GetComponentsInChildren<Renderer>(true);
            var shiftId = Shader.PropertyToID("_ShadingShiftFactor");
            var toonyId = Shader.PropertyToID("_ShadingToonyFactor");
            var shadeId = Shader.PropertyToID("_ShadeColor");

            // toony 的语义：1 = 两段色的交界被压成 0 宽 → 全走 litColor（单段）；0 = 最宽的渐变带。
            // 上一版把 toony 设成 0，等于把暗带拉到最宽，所以三档看起来一模一样。
            var configs = new (string name, float faceToony, float bodyToony, float bodyShadeLift)[]
            {
                ("flatA-face-single", 1.00f, 0.90f, 0.00f),
                ("flatB-face-single-body-soft", 1.00f, 0.92f, 0.35f),
                ("flatC-face-single-body-softer", 1.00f, 0.96f, 0.55f),
            };

            camera.Render();
            foreach (var v in configs)
            {
                foreach (var r in herRenderers) r.receiveShadows = false;
                foreach (var renderer in herRenderers)
                {
                    foreach (var m in renderer.sharedMaterials)
                    {
                        if (m == null || !m.HasProperty(shiftId)) continue;
                        var isFace = m.name.IndexOf("Face", System.StringComparison.OrdinalIgnoreCase) >= 0;
                        m.SetFloat(shiftId, 0f);
                        if (m.HasProperty(toonyId)) m.SetFloat(toonyId, isFace ? v.faceToony : v.bodyToony);
                        if (!isFace && m.HasProperty(shadeId) && v.bodyShadeLift > 0f)
                        {
                            var lit = m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white;
                            m.SetColor(shadeId, Color.Lerp(m.GetColor(shadeId), lit, v.bodyShadeLift));
                        }
                        m.DisableKeyword(AvatarShading.ParameterMapKeyword);
                    }
                }
                RefreshSkin(avatar.gameObject);

                camera.fieldOfView = 30f;
                camera.transform.position = faceEye;
                camera.transform.rotation = Quaternion.LookRotation(faceLook - faceEye, Vector3.up);
                camera.Render();
                WriteShot(target, Path.Combine(shotDir, v.name + "-face.png"));

                camera.fieldOfView = 40f;
                camera.transform.position = head + facing * 1.5f + side * 0.30f + Vector3.down * 0.15f;
                camera.transform.rotation = Quaternion.LookRotation(head + Vector3.down * 0.05f - camera.transform.position, Vector3.up);
                camera.Render();
                WriteShot(target, Path.Combine(shotDir, v.name + ".png"));
                Debug.Log("[skin] " + v.name + " faceToony=" + v.faceToony + " bodyToony=" + v.bodyToony + " shadeLift=" + v.bodyShadeLift);
            }

            camera.targetTexture = null;
            target.Release();
            Object.DestroyImmediate(target);
            EditorApplication.Exit(0);
        }

        /// <summary>看她身上那些材质到底挂的是哪个 shader、哪些明暗相关属性真的存在。</summary>
        public static void SkinShaders()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Local/avatars/Monkia.vrm");
            if (prefab == null) { Debug.LogError("[skin] cannot load vrm"); EditorApplication.Exit(1); return; }
            var instance = Object.Instantiate(prefab);
            var done = new HashSet<string>();
            foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var m in renderer.sharedMaterials)
                {
                    if (m == null || done.Contains(m.name)) continue;
                    done.Add(m.name);
                    var shader = m.shader;
                    var shad = string.Join(",", Enumerable.Range(0, shader.GetPropertyCount())
                        .Select(i => shader.GetPropertyName(i))
                        .Where(n => n.IndexOf("Shad", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    n.IndexOf("Toon", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    n.IndexOf("Color", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        .Take(14));
                    Debug.Log("[skin] " + m.name + " shader=" + shader.name + " | " + shad);
                    if (m.HasProperty("_ShadingToonyFactor")) Debug.Log("[skin]   toony=" + m.GetFloat("_ShadingToonyFactor") + " shift=" + m.GetFloat("_ShadingShiftFactor") + " shade=" + m.GetColor("_ShadeColor"));
                    if (m.HasProperty("_ShadeToony")) Debug.Log("[skin]   v4 toony=" + m.GetFloat("_ShadeToony") + " shift=" + m.GetFloat("_ShadingShiftFactor") + " shade=" + m.GetColor("_ShadeColor"));
                }
            }
            Object.DestroyImmediate(instance);
            EditorApplication.Exit(0);
        }

        /// <summary>决定性探针：直接把她脸材质涂成一个荧光色，看画面到底变不变。</summary>
        public static void SkinProbe()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid()) { Debug.LogError("[skin] no scene"); EditorApplication.Exit(1); return; }
            var boot = Object.FindAnyObjectByType<RoomBootstrap>();
            boot.Boot();
            var camera = boot.OutputCamera;
            var avatar = Object.FindAnyObjectByType<AvatarActor>();
            avatar.SitAt(avatar.SeatId);
            var brain = camera.GetComponent<Unity.Cinemachine.CinemachineBrain>();
            if (brain != null) brain.enabled = false;

            var head = ActionGeometry.HerHeadCenter(avatar.Rig);
            var facing = ActionGeometry.HerFacing(avatar.Rig);
            var side = Vector3.Cross(Vector3.up, facing).normalized;
            var shotDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Shots"));
            var target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = target;
            camera.fieldOfView = 30f;
            camera.transform.position = head + facing * 0.62f + side * 0.10f + Vector3.up * 0.02f;
            camera.transform.rotation = Quaternion.LookRotation(head + Vector3.up * 0.015f - camera.transform.position, Vector3.up);
            camera.Render();

            var hits = 0;
            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
            {
                var mats = renderer.sharedMaterials;
                for (var i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null || !m.HasProperty("_ShadingToonyFactor")) continue;
                    Debug.Log("[skin] probe sees " + m.name + " on " + renderer.name + " toony=" + m.GetFloat("_ShadingToonyFactor") + " shift=" + m.GetFloat("_ShadingShiftFactor"));
                    if (m.name.IndexOf("Face_00_SKIN", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        m.SetColor("_Color", new Color(0f, 1f, 0.2f, 1f));
                        m.SetFloat("_ShadingToonyFactor", 1f);
                        m.SetFloat("_ShadingShiftFactor", 1f);
                        Debug.Log("[skin] probe painted " + m.name + " -> "
                            + m.GetColor("_Color") + " toony=" + m.GetFloat("_ShadingToonyFactor")
                            + " shift=" + m.GetFloat("_ShadingShiftFactor"));
                        hits++;
                    }
                }
            }
            Debug.Log("[skin] probe hits=" + hits);
            RefreshSkin(avatar.gameObject);
            camera.Render();
            WriteShot(target, Path.Combine(shotDir, "probe-face.png"));
            camera.targetTexture = null;
            target.Release();
            Object.DestroyImmediate(target);
            EditorApplication.Exit(0);
        }

        /// <summary>
        /// 四个单变量，锁定脸上那道暗带到底是谁画的：SSAO / 投影 / 头发贴脸 / 全部关掉。
        ///   unity.ps1 run -- -executeMethod Mas3D.Editor.CliPlayer.FaceDiag2
        /// </summary>
        public static void FaceDiag2()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid()) { Debug.LogError("[face] no scene"); EditorApplication.Exit(1); return; }
            var boot = Object.FindAnyObjectByType<RoomBootstrap>();
            boot.Boot();
            var camera = boot.OutputCamera;
            var avatar = Object.FindAnyObjectByType<AvatarActor>();
            avatar.SitAt(avatar.SeatId);
            var brain = camera.GetComponent<Unity.Cinemachine.CinemachineBrain>();
            if (brain != null) brain.enabled = false;

            var head = ActionGeometry.HerHeadCenter(avatar.Rig);
            var facing = ActionGeometry.HerFacing(avatar.Rig);
            var side = Vector3.Cross(Vector3.up, facing).normalized;
            var shotDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Shots"));
            var target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = target;
            camera.fieldOfView = 30f;
            camera.transform.position = head + facing * 0.62f + side * 0.10f + Vector3.up * 0.02f;
            camera.transform.rotation = Quaternion.LookRotation(head + Vector3.up * 0.015f - camera.transform.position, Vector3.up);
            camera.Render();

            var ssao = FindSsao();
            var lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var savedShadows = lights.ToDictionary(l => l, l => l.shadows);
            var herRenderers = avatar.GetComponentsInChildren<Renderer>(true);
            var savedReceive = herRenderers.ToDictionary(r => r, r => r.receiveShadows);

            void Shoot(string name)
            {
                RefreshSkin(avatar.gameObject);
                camera.Render();
                WriteShot(target, Path.Combine(shotDir, name + "-face.png"));
                Debug.Log("[face] wrote " + name);
            }

            // 1 基线
            Shoot("fd2-1-base");

            // 2 关 SSAO
            if (ssao != null) ssao.SetActive(false);
            Shoot("fd2-2-nossao");
            if (ssao != null) ssao.SetActive(true);

            // 3 关全部灯投影
            foreach (var l in lights) l.shadows = LightShadows.None;
            Shoot("fd2-3-noshadow");
            foreach (var kv in savedShadows) if (kv.Key != null) kv.Key.shadows = kv.Value;

            // 4 她本人不收投影
            foreach (var r in herRenderers) r.receiveShadows = false;
            Shoot("fd2-4-noreceive");
            foreach (var kv in savedReceive) if (kv.Key != null) kv.Key.receiveShadows = kv.Value;

            // 5 关 SSAO + 关投影 + 不收影
            if (ssao != null) ssao.SetActive(false);
            foreach (var l in lights) l.shadows = LightShadows.None;
            foreach (var r in herRenderers) r.receiveShadows = false;
            Shoot("fd2-5-none");

            camera.targetTexture = null;
            target.Release();
            Object.DestroyImmediate(target);
            EditorApplication.Exit(0);
        }

        /// <summary>
        /// SSAO 是脸上那道灰带的真凶（关掉就干净）。这里扫三档强度/半径，各出一张脸 + 一张房间，
        /// 用来找"脸干净但墙角还有体积感"的折中点。
        ///   unity.ps1 run -- -executeMethod Mas3D.Editor.CliPlayer.SsaoTune
        /// </summary>
        public static void SsaoTune()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid()) { Debug.LogError("[ssao] no scene"); EditorApplication.Exit(1); return; }
            var boot = Object.FindAnyObjectByType<RoomBootstrap>();
            boot.Boot();
            var camera = boot.OutputCamera;
            var avatar = Object.FindAnyObjectByType<AvatarActor>();
            avatar.SitAt(avatar.SeatId);
            var brain = camera.GetComponent<Unity.Cinemachine.CinemachineBrain>();
            if (brain != null) brain.enabled = false;

            var feature = FindSsao();
            if (feature == null) { Debug.LogError("[ssao] no SSAO feature"); EditorApplication.Exit(1); return; }
            var so = new SerializedObject(feature);
            var intensity = so.FindProperty("m_Settings.Intensity");
            var radius = so.FindProperty("m_Settings.Radius");
            var falloff = so.FindProperty("m_Settings.Falloff");
            Debug.Log("[ssao] start intensity=" + intensity.floatValue + " radius=" + radius.floatValue + " falloff=" + falloff.floatValue);

            var head = ActionGeometry.HerHeadCenter(avatar.Rig);
            var facing = ActionGeometry.HerFacing(avatar.Rig);
            var side = Vector3.Cross(Vector3.up, facing).normalized;
            var shotDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Shots"));
            var target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = target;
            camera.Render();

            var configs = new (string name, float intensity, float radius, float falloff)[]
            {
                ("ssao-A-soft", 0.30f, 0.09f, 60f),
                ("ssao-B-softer", 0.18f, 0.07f, 45f),
                ("ssao-C-off", 0f, 0.07f, 45f),
            };

            foreach (var v in configs)
            {
                so.Update();
                intensity.floatValue = v.intensity;
                radius.floatValue = v.radius;
                falloff.floatValue = v.falloff;
                so.ApplyModifiedPropertiesWithoutUndo();
                feature.SetActive(v.intensity > 0f);

                camera.fieldOfView = 30f;
                camera.transform.position = head + facing * 0.62f + side * 0.10f + Vector3.up * 0.02f;
                camera.transform.rotation = Quaternion.LookRotation(head + Vector3.up * 0.015f - camera.transform.position, Vector3.up);
                camera.Render();
                WriteShot(target, Path.Combine(shotDir, v.name + "-face.png"));

                camera.fieldOfView = 62f;
                camera.transform.position = new Vector3(0f, 1.55f, -2.9f);
                camera.transform.rotation = Quaternion.LookRotation(new Vector3(0f, 1.10f, 0f) - camera.transform.position, Vector3.up);
                camera.Render();
                WriteShot(target, Path.Combine(shotDir, v.name + "-room.png"));
                Debug.Log("[ssao] " + v.name + " intensity=" + v.intensity + " radius=" + v.radius + " falloff=" + v.falloff);
            }

            camera.targetTexture = null;
            target.Release();
            Object.DestroyImmediate(target);
            EditorApplication.Exit(0);
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