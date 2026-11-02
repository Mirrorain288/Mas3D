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

            Debug.Log($"[room] colliders={ColliderCount} spawn={(anchor != null ? anchor.id : "none")} at={(anchor != null ? anchor.x.ToString("F2") + "," + anchor.z.ToString("F2") : "-")}");
        }
    }
}