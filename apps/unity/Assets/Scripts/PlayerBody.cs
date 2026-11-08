using System.Collections.Generic;
using Mas3D.Avatar;
using UnityEngine;

namespace Mas3D
{
    /// <summary>
    /// P7 Task 3: the player stops being an empty capsule. This owns exactly one thing — a VRM model
    /// that follows the player's transform — and deliberately owns no capsule, no collider and no
    /// movement: <see cref="PlayerMotor"/> stays the only authority on where the player is.
    ///
    /// Three rules the tests (PlayerBodyTests) pin down, and the reasons behind them:
    ///  * The model root is a child of the player transform, not of the head or the camera. A body
    ///    parented to the head follows the eye instead of the feet and lags the capsule on every
    ///    step; parented to the camera it would also inherit the orbit and spin with the mouse.
    ///  * Not one collider survives the spawn. The body is scenery drawn inside the capsule; a second
    ///    collider would make CharacterController.Move collide with the player's own limbs and shove
    ///    him around from the inside.
    ///  * The model is an imported asset under Assets/_Local, so it can only be loaded by path in the
    ///    editor. <see cref="Prefab"/> exists so a build can bake it into the scene instead.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerBody : MonoBehaviour
    {
        /// <summary>The male VRM next to her; imported, never generated at runtime.</summary>
        public const string BodyPrefabPath = "Assets/_Local/avatars/Me.vrm";

        public const string BodyName = "Me";

        /// <summary>
        /// The boom length under which the head gets out of the camera. The rig treats 1.2 m and closer
        /// as first person (PlayerCameraRig.FirstPersonAtOrBelow, camera 5 cm behind the eyes), and sits
        /// inside the skull a whole step before that — so the threshold has to be *slacker* than the
        /// rig's line, otherwise the exact 1.2 m frame keeps the back of the head across the screen.
        /// 1.25 leaves a 5 cm margin; the pair is pinned by CameraStabilityTests.
        /// </summary>
        public const float NearDistance = 1.25f;

        /// <summary>
        /// Standing height the model is scaled to. It is the capsule height (PlayerMotor.CapsuleHeight)
        /// rather than "whatever the artist exported": this VRM is authored at 2.03 m, and a body that
        /// pokes 0.33 m out of the top of the capsule reads as a giant next to her.
        /// </summary>
        public const float TargetHeight = 1.70f;

        /// <summary>Fallback head rule for a model whose renderers are unnamed: the top third.</summary>
        const float HeadShareOfHeight = 0.62f;

        [Tooltip("Optional: the body prefab baked into the scene. Empty in the editor, where the VRM is loaded by path.")]
        public GameObject Prefab;

        [Tooltip("Optional: the body instance baked into the scene by CliPlayer. Adopted, never re-instantiated.")]
        public GameObject Baked;

        public PlayerMotor Motor { get; private set; }
        public PlayerCameraRig Rig { get; private set; }
        public AvatarRig Avatar { get; private set; }

        /// <summary>True while the head is hidden because the boom is against the player.</summary>
        public bool HeadHidden { get; private set; }

        public bool Spawned { get { return Avatar != null; } }

        public int HeadRendererCount { get { return _headRenderers.Count; } }

        public IReadOnlyList<Renderer> HeadRenderers { get { return _headRenderers; } }

        /// <summary>Whether this machine can load the body at all; false in a build without a baked prefab.</summary>
        public static bool PrefabAvailable
        {
            // Only the editor can answer this: a build has no AssetDatabase, and the baked Prefab field
            // belongs to an instance nobody has spawned yet.
            get { return LoadPrefab() != null; }
        }

        readonly List<Renderer> _headRenderers = new List<Renderer>();

        Transform _head;
        float _feetOffset;
        bool _visibilityApplied;
        bool _headLogged;

        /// <summary>
        /// Hand the body the player it belongs to. Idempotent: RoomBootstrap may re-wire a scene that
        /// already carries a body, and every call must leave the same state behind.
        /// </summary>
        public void Attach(PlayerMotor motor, Transform head, PlayerCameraRig rig)
        {
            Motor = motor;
            _head = head != null ? head : (motor != null ? motor.Head : null);
            Rig = rig;
        }

        /// <summary>
        /// Instantiate the VRM under the player. Returns false when there is nothing to spawn (not
        /// attached yet, or the asset is missing) — the player then simply stays the capsule he was,
        /// which is what -nobody 1 and every capture without a body rely on.
        /// </summary>
        public bool Spawn()
        {
            if (Avatar != null) return true;

            if (Motor == null)
            {
                Debug.LogWarning("[body] 还没 Attach 就 Spawn：先把它交给 PlayerMotor，否则身体挂不到玩家身上。");
                return false;
            }

            AvatarRig rig = null;
            if (Baked != null)
            {
                // Baked body: the scene already carries the model, so adopting it is the only thing left to do.
                // SetActive first: an inactive object's Awake has not run, so the rig would stay unbound.
                Baked.SetActive(true);
                rig = Baked.GetComponent<AvatarRig>();
                if (rig == null) rig = Baked.AddComponent<AvatarRig>();
                rig.EnsureBound();
            }
            else
            {
                var prefab = Prefab != null ? Prefab : LoadPrefab();
                if (prefab == null)
                {
                    Debug.LogError("[body] 找不到 " + BodyPrefabPath + "：玩家只能继续当胶囊。"
                        + "打包时要先把模型烘进场景（Baked 字段）。");
                    return false;
                }

                rig = AvatarRig.Spawn(prefab, BodyName);
            }

            Avatar = rig;
            var root = Avatar.transform;
            root.SetParent(transform, false);
            root.localPosition = Vector3.zero;
            root.localRotation = Quaternion.identity;

            Finish(root);
            return true;
        }

        /// <summary>Shared tail of both spawn paths: strip, scale, stand, collect, place, log.</summary>
        void Finish(Transform root)
        {
            var springColliders = Avatar.GetComponentsInChildren<Collider>(true).Length;
            StripColliders(Avatar.gameObject);
            FitToHeight();
            AvatarPose.ArmsDown(Avatar);
            CollectHeadRenderers();
            _visibilityApplied = false;
            ApplyNow();

            Debug.Log(string.Format("[body] spawned '{0}' source={1} renderers={2} head={3} height={4:F3} scale={5:F3} feetOffset={6:F3} strippedColliders={7}",
                BodyName, Baked != null ? "baked" : "prefab", Avatar.GetComponentsInChildren<Renderer>(true).Length,
                _headRenderers.Count, Avatar.Measure().size.y, root.localScale.x, _feetOffset, springColliders));
        }

        /// <summary>
        /// Put the model where the stance says it is. The eye is the one number both the standing rig
        /// and every stance agree on (SetPosture writes Head.localPosition.y), so the body sinks by
        /// exactly the amount the eye sank — no second copy of the posture table lives here.
        /// </summary>
        public void ApplyNow()
        {
            if (Avatar == null) return;

            var root = Avatar.transform;
            var eye = _head != null ? _head.localPosition.y : PlayerMotor.Metrics(Motor != null ? Motor.Posture : PlayerPosture.Standing).eye;
            var standing = PlayerMotor.Metrics(PlayerPosture.Standing).eye;
            root.localPosition = new Vector3(0f, _feetOffset + (eye - standing), 0f);

            UpdateHeadVisibility();
        }

        void LateUpdate()
        {
            ApplyNow();
        }

        void OnDisable()
        {
            // A disabled body must not leave the camera staring into a hidden head.
            if (_visibilityApplied) ShowHead(true);
        }

        /// <summary>Scale the model to the capsule height and put its soles on the player's feet.</summary>
        void FitToHeight()
        {
            var root = Avatar.transform;
            var bounds = Avatar.Measure();
            var height = bounds.size.y;
            if (height > 0.05f) root.localScale = Vector3.one * (TargetHeight / height);

            bounds = Avatar.Measure();
            _feetOffset = transform.position.y - bounds.min.y;
            root.localPosition = new Vector3(0f, _feetOffset, 0f);
        }

        void CollectHeadRenderers()
        {
            _headRenderers.Clear();
            var root = Avatar.transform;
            var height = Avatar.Measure().size.y;
            var headLine = transform.position.y + _feetOffset + height * HeadShareOfHeight;

            foreach (var renderer in Avatar.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;

                var name = renderer.name.ToLowerInvariant();
                var byName = name.Contains("face") || name.Contains("hair");
                var byHeight = renderer.bounds.min.y >= headLine;
                if (!byName && !byHeight) continue;

                _headRenderers.Add(renderer);
                if (!_headLogged)
                {
                    _headLogged = true;
                    Debug.Log("[body] head mesh='" + renderer.name + "' rule=" + (byName ? "name" : "height")
                        + " minY=" + renderer.bounds.min.y.ToString("F3") + " line=" + headLine.ToString("F3"));
                }
            }
        }

        void UpdateHeadVisibility()
        {
            if (Rig == null || _headRenderers.Count == 0) return;

            var hide = Rig.Distance < NearDistance;
            if (_visibilityApplied && hide == HeadHidden) return;

            HeadHidden = hide;
            ShowHead(!hide);
        }

        void ShowHead(bool visible)
        {
            foreach (var renderer in _headRenderers)
                if (renderer != null) renderer.enabled = visible;

            _visibilityApplied = true;
            HeadHidden = !visible;
            Debug.Log("[body] head " + (visible ? "shown" : "hidden") + " (" + _headRenderers.Count
                + " renderers, boom " + (Rig != null ? Rig.Distance : 0f).ToString("F2") + " m)");
        }

        void StripColliders(GameObject root)
        {
            // DestroyImmediate, not Destroy: the whole point is that the collider is gone before anyone
            // measures the hierarchy in the same frame (PlayerBodyTests counts them right after Spawn).
            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
            {
                if (collider == null) continue;
                DestroyImmediate(collider);
            }
        }

        static GameObject LoadPrefab()
        {
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(BodyPrefabPath);
#else
            // A built player has no AssetDatabase: the body has to arrive through Prefab.
            return null;
#endif
        }
    }
}
