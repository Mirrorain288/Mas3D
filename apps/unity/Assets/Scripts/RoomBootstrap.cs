using Mas3D.Actions;
using Mas3D.Avatar;
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

        // The imported floor sits at y = 0.04; spawning exactly on it leaves the capsule
        // penetrating, and a penetrating CharacterController never reports itself grounded.
        public const float SpawnLift = 0.10f;

        public int ColliderCount { get; private set; }
        public AnchorTable Anchors { get; private set; }

        void Start() => Boot();

        public void Boot()
        {
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

            Debug.Log($"[room] colliders={ColliderCount} spawn={(anchor != null ? anchor.id : "none")} at={(anchor != null ? anchor.x.ToString("F2") + "," + anchor.z.ToString("F2") : "-")}");
        }
    }
}