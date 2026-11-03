using UniVRM10;
using UnityEngine;

namespace Mas3D.Avatar
{
    /// <summary>
    /// Task 1 of docs/superpowers/plans/2026-11-03-unity-avatar-in-scene.md: the one thing that
    /// knows how to talk to the imported VRM. Bones, expressions and bounds — nothing about the
    /// room, the seat or the conversation.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AvatarRig : MonoBehaviour
    {
        public const string DefaultPrefabPath = "Assets/_Local/avatars/Monkia.vrm";

        /// <summary>Whether the missing-VRM warning has already been logged for this object.</summary>
        bool _warned;

        public Vrm10Instance Instance { get; private set; }
        public Animator Animator { get; private set; }
        public Transform Root { get; private set; }

        /// <summary>Instantiate the imported prefab and attach a rig to it.</summary>
        public static AvatarRig Spawn(GameObject prefab, string name = "Monkia")
        {
            if (prefab == null) throw new System.ArgumentNullException(nameof(prefab));

            var instance = Object.Instantiate(prefab);
            instance.name = name;

            // The VRM prefab may someday carry its own rig; reuse it instead of stacking two.
            var rig = instance.GetComponent<AvatarRig>();
            if (rig == null) rig = instance.AddComponent<AvatarRig>();
            rig.Attach();
            return rig;
        }

        /// <summary>
        /// A rig added in the Inspector or baked into a prefab never goes through Spawn:
        /// bind to whatever VRM already lives under this object. Spawn calls Attach again
        /// after AddComponent has run Awake, so Attach must stay idempotent.
        /// </summary>
        void Awake()
        {
            EnsureBound();
        }

        /// <summary>
        /// Bind to the VRM now instead of waiting for Awake. Edit-mode callers (the scene builder,
        /// the capture step) never get an Awake, so a rig that came back from a saved scene would
        /// otherwise answer every bone lookup with null.
        /// </summary>
        public void EnsureBound()
        {
            if (Instance == null) Attach();
        }

        /// <summary>Bind to the VRM runtime already living under this GameObject.</summary>
        void Attach()
        {
            var self = gameObject;
            Instance = self.GetComponentInChildren<Vrm10Instance>(true);

            if (Instance != null)
            {
                Animator = Instance.GetComponent<Animator>();
                Root = Instance.transform;
                _warned = false;
            }
            else
            {
                // A non-VRM prefab would otherwise look bound and silently do nothing. EnsureBound
                // re-attaches on every call while Instance is null, so a failed lookup would repeat
                // this warning once per caller; say it once and let the state speak for itself.
                if (!_warned)
                {
                    Debug.LogWarning(
                        "AvatarRig on '" + self.name + "' found no Vrm10Instance: bones, expressions and bounds stay empty.",
                        self);
                    _warned = true;
                }
                Animator = self.GetComponentInChildren<Animator>(true);
                Root = self.transform;
            }

            if (Animator == null) return;

            // The humanoid skeleton is driven by us and by UniVRM; a state machine would fight both.
            // The VRM root stays where it is — reparenting breaks SpringBone and the Animator offsets.
            Animator.applyRootMotion = false;
            Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            Animator.enabled = false;
        }

        /// <summary>Humanoid bone lookup; returns null instead of throwing when the bone is absent.</summary>
        public Transform Bone(HumanBodyBones bone)
        {
            // Animator.GetBoneTransform logs an error for the sentinel value.
            if (bone == HumanBodyBones.LastBone) return null;

            var humanoid = Instance != null ? Instance.Humanoid : null;
            if (humanoid != null)
            {
                var boneTransform = humanoid.GetBoneTransform(bone);
                if (boneTransform != null) return boneTransform;
            }

            return Animator != null ? Animator.GetBoneTransform(bone) : null;
        }

        /// <summary>Null until the VRM runtime exists (a prefab asset never builds one).</summary>
        Vrm10RuntimeExpression Expression
        {
            get
            {
                if (Instance == null) return null;
                var runtime = Instance.Runtime;
                return runtime != null ? runtime.Expression : null;
            }
        }

        /// <summary>
        /// Whether this model carries the expression. SetWeight silently ignores unknown keys,
        /// so callers need this guard before expecting a weight to stick.
        /// </summary>
        public bool HasExpression(ExpressionKey key)
        {
            var expression = Expression;
            return expression != null && expression.GetWeights().ContainsKey(key);
        }

        public float ExpressionWeight(ExpressionKey key)
        {
            var expression = Expression;
            return expression != null ? expression.GetWeight(key) : 0f;
        }

        /// <summary>Write an expression weight; an unknown key is a no-op, never an exception.</summary>
        public void SetExpression(ExpressionKey key, float weight)
        {
            var expression = Expression;
            if (expression == null) return;
            if (!expression.GetWeights().ContainsKey(key)) return;
            expression.SetWeight(key, Mathf.Clamp01(weight));
        }

        /// <summary>Union of every renderer bounds, measured on whatever pose the model is in.</summary>
        public Bounds Measure()
        {
            var origin = Root != null ? Root.position : transform.position;
            var bounds = new Bounds(origin, Vector3.zero);
            var found = false;

            foreach (var renderer in GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                if (!found)
                {
                    bounds = renderer.bounds;
                    found = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return bounds;
        }
    }
}
