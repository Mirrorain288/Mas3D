using System;
using Mas3D.Actions;
using Mas3D.Avatar;
using Mas3D.Boot;
using Mas3D.Conversation;
using Mas3D.Net;
using Mas3D.UI;
using Mas3D.Voice;
using UnityEngine;

namespace Mas3D
{
    /// <summary>
    /// Turns the imported room into a playable one at startup: colliders on every mesh, the player
    /// placed on a verified anchor, and the camera rig wired to that player.
    /// </summary>
    public class RoomBootstrap : MonoBehaviour
    {
        public GameObject Room;
        public PlayerMotor Player;
        public PlayerCameraRig Rig;
        public Camera OutputCamera;
        public string SpawnAnchor = "A2";
        public bool AttachColliders = true;

        /// <summary>Her, when the scene carries her. Wired here so no other script has to know
        /// which object she ended up on.</summary>
        public AvatarActor Actor;

        /// <summary>The conversation that drives her face. Left null in a dialogue-less scene, in
        /// which case Boot falls back to whatever controller it can find.</summary>
        public ConversationController Dialogue;

        /// <summary>按住 V 说话的语音层：它要的正是同一块面板，所以在这里接线，别处不必知道。</summary>
        public VoiceInput Voice;

        /// <summary>
        /// The performance state machine, wired to her, the player and the camera rig. Boot creates it
        /// when the scene carries none, so the shipped scene needs no extra object and every caller of
        /// Boot (CliPlayer included) gets a working director. Boot only wires it: which posture an
        /// action needs, and where the player stands, stays <see cref="ActionDirector.TryEnterAction"/>'s
        /// decision — entering an action must not be forced on the player at spawn.
        /// </summary>
        public ActionDirector Actions;

        /// <summary>
        /// Her legs. Boot creates it on her own object (the walker moves the actor root, so it must live
        /// there) and never starts a walk: who she goes to and when is the conversation's call, not the
        /// spawn's. An AvatarWalker already placed by hand wins, the same rule Actions follows.
        /// </summary>
        public AvatarWalker Walker;

        /// <summary>
        /// 键盘模式（E 进输入框、Esc 退出并锁走路键盘）。和 Actions 一样由 Boot 建、Boot 接线：
        /// 场景里不需要预先摆一个组件，而每个调用 Boot 的入口（CliPlayer 也算）都自动拿到。
        /// </summary>
        public InputModes Modes;

        // The imported floor sits at y = 0.04; spawning exactly on it leaves the capsule
        // penetrating, and a penetrating CharacterController never reports itself grounded.
        public const float SpawnLift = 0.10f;

        public int ColliderCount { get; private set; }
        public AnchorTable Anchors { get; private set; }

        /// <summary>Boot 解析出来的启动参数。Task 3 的自检模式靠它拿 SelfTestPath；空命令行时是默认值。</summary>
        public BootArgs StartupArgs { get; private set; }

        void Start() => Boot();

        public void Boot()
        {
            // 命令行的东西必须在别的组件读配置之前落下：DialogueBridge 的执行顺序是 100，它的 Start
            // 晚于这里，所以 -bridge / -token 改的就是它随后要用的那份 BridgeSettings。
            StartupArgs = BootArgs.Parse(Environment.GetCommandLineArgs());
            ApplyStartupArgs(StartupArgs);

            if (Room != null && AttachColliders) ColliderCount = RoomColliders.Attach(Room);
            Anchors = AnchorTable.Load();

            if (Player == null) Player = FindAnyObjectByType<PlayerMotor>();
            if (Player == null) return;
            Player.EnsureReady();
            RoomLook.Apply();

            var anchor = Anchors != null ? Anchors.Anchor(SpawnAnchor) : null;
            if (anchor != null)
            {
                var feet = anchor.Feet(SpawnLift);
                Player.Teleport(feet, anchor.facingY);
                if (Player.Head != null) Player.Head.localRotation = Quaternion.identity;
            }

            if (Rig == null) Rig = Player.GetComponent<PlayerCameraRig>();
            if (Rig != null)
            {
                Rig.Build(Player.Head, OutputCamera != null ? OutputCamera : Camera.main);
                if (anchor != null) Rig.SetOrbit(anchor.facingY, 14f);
            }

            // 玩家身体接在相机之后：它要跟的是玩家的 transform 和眼睛高度，而"镜头贴到脸上时把头藏起来"
            // 那条线读的是 Rig.Distance —— Rig 这一行上面才建好。挂在玩家身上（不是 Boot 这个对象上），
            // 因为模型的父级就是这个 transform；-nobody 1 时不建，玩家回到那颗空胶囊，截图与回归都能照旧。
            if (!StartupArgs.NoBody)
            {
                var body = Player.GetComponent<PlayerBody>();
                if (body == null) body = Player.gameObject.AddComponent<PlayerBody>();
                body.Attach(Player, Player.Head, Rig);
                body.Spawn();
            }

            // She is wired last: her gaze target is the player's head, which only exists after
            // EnsureReady, and her mood has to see the same controller the panel submits to.
            if (Actor != null)
            {
                Actor.BindGaze(Player.Head);
                var conversation = Dialogue != null ? Dialogue : FindAnyObjectByType<ConversationController>();
                if (conversation != null) Actor.BindConversation(conversation);
            }

            // 语音层也接在同一块面板上：没有面板就没地方落状态词和识别结果。
            // 令牌只读桥接自己写盘的那一份；读不到就警告一次照常起，按住 V 时由 VoiceInput
            // 自己把失败落到面板上，不能让没起桥接的机器连房间都进不去。
            if (Voice != null && Dialogue != null)
            {
                var token = DialogueBridge.ReadTokenFromDisk();
                if (string.IsNullOrEmpty(token))
                    Debug.LogWarning("[voice] 没有令牌（" + BridgeSettings.TokenFilePath + "）：ASR 会被桥接拒绝，其余照常");
                // Dialogue 是驱动器不是面板；它驱动的那块面板才是语音要落字的地方。
                var panel = Dialogue.Panel != null ? Dialogue.Panel : FindAnyObjectByType<DialoguePanel>();
                if (panel != null) Voice.Bind(panel, new BridgeSettings { Token = token });
            }

            // The director is wired last because it needs all three: her rig, the player's head and the
            // camera orbit Rig.Build set up above. Creating it here (rather than in the scene) keeps the
            // saved scene free of a component nobody has to place by hand; an Actions field already
            // filled in wins, so a hand-wired director is not replaced.
            if (Actions == null)
            {
                var go = new GameObject("Actions");

                // Parented, not loose: a runtime object joins whatever scene is active, and Main is
                // loaded additively in tests. Loose, the director outlives the scene that gave it a
                // player and keeps ticking against a destroyed one — MissingReferenceException every
                // frame, which is what turned one unload into 97 red cases.
                go.transform.SetParent(transform, false);
                Actions = go.AddComponent<ActionDirector>();
            }
            Actions.Actor = Actor;
            Actions.Player = Player;
            Actions.Camera = Rig;

            // 走位器挂在她身上而不是 Boot 上：它每 tick 挪的就是这个 transform，挂远了会多一层父子换算。
            // 这里只给对象，不给目标 —— 她走不走、走去哪，是对话的决定，不是开场的决定。
            if (Actor != null)
            {
                if (Walker == null) Walker = Actor.GetComponent<AvatarWalker>();
                if (Walker == null) Walker = Actor.gameObject.AddComponent<AvatarWalker>();
                Walker.Actor = Actor;
                Walker.Conversation = Dialogue != null ? Dialogue : FindAnyObjectByType<ConversationController>();
            }

            // 播报挂在会话语义那一层（同一 GameObject）：reply 一到就要用面板落字幕、用桥接的地址和令牌
            // 去请求语音。这里只交接对象，不读令牌 —— DialogueBridge 的执行顺序是 100、Boot 是默认 0，
            // 此刻令牌还没从磁盘写进 Settings；播报持有同一块 Settings，等第一句回复时再读，
            // 读不到还有 ReadTokenFromDisk() 兜底（见 ReplySpeaker.ResolveToken）。
            if (Dialogue != null)
            {
                var speaker = Dialogue.GetComponent<ReplySpeaker>();
                if (speaker == null) speaker = Dialogue.gameObject.AddComponent<ReplySpeaker>();
                speaker.Panel = Dialogue.Panel != null ? Dialogue.Panel : FindAnyObjectByType<DialoguePanel>();
                var settings = FindAnyObjectByType<DialogueBridge>();
                speaker.Settings = settings != null && settings.Settings != null ? settings.Settings : new BridgeSettings();
                Dialogue.Speaker = speaker;
            }

            // 键盘模式挂在 Boot 的子物体上，理由和 Actions 一样：运行时 new 出来的对象必须跟着场景走，
            // 否则 Main 被卸载后它还在 Update 里摸已经销毁的面板（MissingReferenceException）。
            // 面板优先取对话控制器手上那块；场景里没有对话时退回全局找 —— Bind(null) 是合法的，
            // 那种场景只剩 E/Esc 的空转，走路照常。
            var dialoguePanel = Dialogue != null && Dialogue.Panel != null ? Dialogue.Panel : FindAnyObjectByType<DialoguePanel>();
            if (Modes == null)
            {
                var modesGo = new GameObject("Input");
                modesGo.transform.SetParent(transform, false);
                Modes = modesGo.AddComponent<InputModes>();
            }
            Modes.Bind(Player, dialoguePanel);   // Bind 同时把 Panel/Modes 交给玩家：焦点在框里、Esc 锁死时不走路

            Debug.Log($"[room] colliders={ColliderCount} spawn={(anchor != null ? anchor.id : "none")} at={(anchor != null ? anchor.x.ToString("F2") + "," + anchor.z.ToString("F2") : "-")}");

            // 自检只挂在 -selftest 上：它是玩家包的自动验收入口，编辑器里（命令行不带该参数）连组件都不创建，
            // 场景与既有测试不受影响。放最后是因为它一挂上就要用上面接好的桥接、面板和导演。
            if (!string.IsNullOrEmpty(StartupArgs.SelfTestPath) && GetComponent<PlayerSelfTest>() == null)
                gameObject.AddComponent<PlayerSelfTest>();
        }

        /// <summary>
        /// 把 -bridge / -token 落到场景里那块 BridgeSettings 上，再打一行"这局用的是哪个桥接"。
        /// BridgeSettings 只有 Url（没有 Host/Port 字段），所以地址按 ws://host:port/mas3d/ws 拼回去；
        /// 没给 -bridge 就原样不动，只在日志里报 Url 的端点。
        /// </summary>
        void ApplyStartupArgs(BootArgs boot)
        {
            var bridge = FindAnyObjectByType<DialogueBridge>();
            var settings = bridge != null && bridge.Settings != null ? bridge.Settings : new BridgeSettings();

            // 写 Url/Token 的逻辑在 BootArgs.ApplyTo 里（纯函数，EditMode 直接测得到）；这里只管找对象、打日志。
            BootArgs.ApplyTo(settings, boot);

            string host;
            int port;
            if (boot.HasBridge)
            {
                host = boot.Host;
                port = boot.Port;
            }
            else
            {
                BootArgs.SplitUrl(settings.Url, out host, out port);
            }

            Debug.Log("[boot] bridge=" + host + ":" + port
                + " token=" + (!string.IsNullOrEmpty(boot.Token) ? "set" : "disk")
                + " selftest=" + (!string.IsNullOrEmpty(boot.SelfTestPath) ? boot.SelfTestPath : "none"));
        }
    }
}