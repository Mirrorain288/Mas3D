using System;
using UnityEngine;

namespace Mas3D.Room
{
    public readonly struct InteriorDoorSpec
    {
        public InteriorDoorSpec(string pivotName, string leafMesh, string handleMesh, float swingAngle)
        {
            PivotName = pivotName;
            LeafMesh = leafMesh;
            HandleMesh = handleMesh;
            SwingAngle = swingAngle;
        }

        public string PivotName { get; }
        public string LeafMesh { get; }
        public string HandleMesh { get; }
        public float SwingAngle { get; }
    }

    /// <summary>
    /// Hinged interior door controller supporting smooth open/close rotation around local +Y,
    /// temporary collider suppression during swing, and deferred collider restoration while
    /// overlapping a player bounding box.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InteractiveDoor : MonoBehaviour
    {
        public const float ClosedAngle = 0f;
        public const float OpenAngle = -90f;
        public const float DefaultSwingDuration = 0.35f;

        public static readonly InteriorDoorSpec[] InteriorSpecs =
        {
            new InteriorDoorSpec("Door_BathBedroom", "mesh_341", "mesh_340", OpenAngle),
            new InteriorDoorSpec("Door_BedroomLiving", "mesh_238", "mesh_237", OpenAngle),
            new InteriorDoorSpec("Door_LivingKitchen", "mesh_438", "mesh_297", OpenAngle),
        };

        public string DoorId = string.Empty;
        public float SwingAngle = OpenAngle;
        public float SwingDuration = DefaultSwingDuration;

        public Collider PlayerCollider { get; set; }
        public bool IsOpen { get; private set; }
        public bool IsMoving { get; private set; }
        public bool WaitingClearance { get; private set; }
        public float CurrentAngle { get; private set; }
        public float TargetAngle => IsOpen ? SwingAngle : ClosedAngle;
        public Vector3 InteractionCenter => GetWorldBounds().center;

        /// <summary>Live world-space center of this door's handle mesh, falling back to InteractionCenter.</summary>
        public Vector3 HandleWorldPosition()
        {
            EnsureHandleCached();
            if (_handleRenderer != null && _handleRenderer.enabled) return _handleRenderer.bounds.center;
            if (_handleTransform != null) return _handleTransform.position;
            return InteractionCenter;
        }

        string ResolveHandleMeshName()
        {
            for (var i = 0; i < InteriorSpecs.Length; i++)
            {
                if (string.Equals(InteriorSpecs[i].PivotName, DoorId, StringComparison.Ordinal) ||
                    string.Equals(InteriorSpecs[i].PivotName, gameObject.name, StringComparison.Ordinal))
                    return InteriorSpecs[i].HandleMesh;
            }
            return null;
        }

        void EnsureHandleCached()
        {
            if (_handleSearched) return;
            _handleSearched = true;
            var handleName = ResolveHandleMeshName();
            if (string.IsNullOrEmpty(handleName)) return;

            var renderers = GetComponentsInChildren<Renderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                if (string.Equals(renderers[i].gameObject.name, handleName, StringComparison.Ordinal))
                {
                    _handleRenderer = renderers[i];
                    _handleTransform = renderers[i].transform;
                    return;
                }
            }

            var transforms = GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < transforms.Length; i++)
            {
                if (string.Equals(transforms[i].name, handleName, StringComparison.Ordinal))
                {
                    _handleTransform = transforms[i];
                    return;
                }
            }
        }

        public bool AreCollidersEnabled
        {
            get
            {
                EnsureCollidersCached();
                return _collidersEnabled && AllCollidersEnabled();
            }
        }

        Collider[] _colliders = Array.Empty<Collider>();
        bool _collidersCached;
        bool _collidersEnabled = true;
        bool _hasBaseRotation;
        Quaternion _baseLocalRotation = Quaternion.identity;
        float _progress;
        bool _handleSearched;
        Renderer _handleRenderer;
        Transform _handleTransform;

        public static bool IsInteriorDoorMesh(string meshName)
        {
            if (string.IsNullOrEmpty(meshName)) return false;
            for (var i = 0; i < InteriorSpecs.Length; i++)
            {
                if (string.Equals(InteriorSpecs[i].LeafMesh, meshName, StringComparison.Ordinal) ||
                    string.Equals(InteriorSpecs[i].HandleMesh, meshName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        public static InteractiveDoor[] FindActive(GameObject root)
        {
            var candidates = root != null
                ? root.GetComponentsInChildren<InteractiveDoor>(false)
                : FindObjectsByType<InteractiveDoor>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            var count = 0;
            for (var i = 0; i < candidates.Length; i++)
            {
                var d = candidates[i];
                if (d != null && d.isActiveAndEnabled) count++;
            }

            if (count == 0) return Array.Empty<InteractiveDoor>();
            var active = new InteractiveDoor[count];
            var idx = 0;
            for (var i = 0; i < candidates.Length; i++)
            {
                var d = candidates[i];
                if (d != null && d.isActiveAndEnabled)
                {
                    active[idx++] = d;
                }
            }
            return active;
        }

        void Awake()
        {
            EnsureBaseRotation();
            RefreshColliders();
            ApplyAngle(ClosedAngle);
            _collidersEnabled = AllCollidersEnabled();
        }

        void Update()
        {
            if (IsMoving || !_collidersEnabled)
            {
                Tick(Time.deltaTime);
            }
        }

        public void RefreshColliders()
        {
            _colliders = GetComponentsInChildren<Collider>(true);
            _collidersCached = true;
            if (_collidersEnabled && !IsMoving && !WaitingClearance)
            {
                SetCollidersEnabled(true);
            }
        }

        public void Toggle()
        {
            SetOpen(!IsOpen, false);
        }

        public void SetOpen(bool open, bool immediate = false)
        {
            EnsureBaseRotation();
            EnsureCollidersCached();
            IsOpen = open;
            var target = open ? 1f : 0f;

            if (immediate)
            {
                _progress = target;
                IsMoving = false;
                WaitingClearance = false;
                ApplyAngle(TargetAngle);
                SetCollidersEnabled(true);
                return;
            }

            IsMoving = !Mathf.Approximately(_progress, target);
            if (IsMoving)
            {
                WaitingClearance = false;
                SetCollidersEnabled(false);
            }
        }

        public void Tick(float deltaTime, Bounds? playerBounds = null)
        {
            EnsureBaseRotation();
            EnsureCollidersCached();

            var target = IsOpen ? 1f : 0f;
            var dt = Mathf.Max(0f, deltaTime);

            if (!Mathf.Approximately(_progress, target) && dt > 0f)
            {
                var duration = Mathf.Max(0.01f, SwingDuration);
                _progress = Mathf.MoveTowards(_progress, target, dt / duration);
                ApplyAngle(Mathf.Lerp(ClosedAngle, SwingAngle, Mathf.SmoothStep(0f, 1f, _progress)));
            }

            if (Mathf.Approximately(_progress, target))
            {
                _progress = target;
                ApplyAngle(TargetAngle);
                IsMoving = false;
            }
            else
            {
                IsMoving = true;
            }

            if (IsMoving)
            {
                WaitingClearance = false;
                SetCollidersEnabled(false);
                return;
            }

            if (!_collidersEnabled)
            {
                if (TryGetOverlapBounds(playerBounds, out var bounds) && GetWorldBounds().Intersects(bounds))
                {
                    WaitingClearance = true;
                    return;
                }

                WaitingClearance = false;
                SetCollidersEnabled(true);
            }
        }

        public Bounds GetWorldBounds()
        {
            var has = false;
            var bounds = default(Bounds);
            var renderers = GetComponentsInChildren<Renderer>(false);
            for (var i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null || !r.enabled) continue;
                if (!has) { bounds = r.bounds; has = true; }
                else bounds.Encapsulate(r.bounds);
            }
            if (has) return bounds;

            EnsureCollidersCached();
            for (var i = 0; i < _colliders.Length; i++)
            {
                var col = _colliders[i];
                if (col == null) continue;
                var b = MeasureColliderWorldBounds(col);
                if (!has) { bounds = b; has = true; }
                else bounds.Encapsulate(b);
            }
            return has ? bounds : new Bounds(transform.position, Vector3.zero);
        }

        static Bounds MeasureColliderWorldBounds(Collider col)
        {
            if (col is BoxCollider box)
            {
                var center = box.transform.TransformPoint(box.center);
                var lossy = box.transform.lossyScale;
                var size = new Vector3(
                    Mathf.Abs(box.size.x * lossy.x),
                    Mathf.Abs(box.size.y * lossy.y),
                    Mathf.Abs(box.size.z * lossy.z));
                var right = box.transform.right * (size.x * 0.5f);
                var up = box.transform.up * (size.y * 0.5f);
                var forward = box.transform.forward * (size.z * 0.5f);
                var ext = new Vector3(
                    Mathf.Abs(right.x) + Mathf.Abs(up.x) + Mathf.Abs(forward.x),
                    Mathf.Abs(right.y) + Mathf.Abs(up.y) + Mathf.Abs(forward.y),
                    Mathf.Abs(right.z) + Mathf.Abs(up.z) + Mathf.Abs(forward.z));
                return new Bounds(center, ext * 2f);
            }

            var wasEnabled = col.enabled;
            col.enabled = true;
            var b = col.bounds;
            col.enabled = wasEnabled;
            return b;
        }

        bool TryGetOverlapBounds(Bounds? explicitBounds, out Bounds bounds)
        {
            if (explicitBounds.HasValue)
            {
                bounds = explicitBounds.Value;
                return true;
            }
            if (PlayerCollider != null && PlayerCollider.enabled)
            {
                bounds = PlayerCollider.bounds;
                return true;
            }
            bounds = default;
            return false;
        }

        void EnsureBaseRotation()
        {
            if (_hasBaseRotation) return;
            _baseLocalRotation = transform.localRotation;
            _hasBaseRotation = true;
        }

        void EnsureCollidersCached()
        {
            if (_collidersCached) return;
            RefreshColliders();
        }

        void ApplyAngle(float angle)
        {
            CurrentAngle = angle;
            transform.localRotation = _baseLocalRotation * Quaternion.Euler(0f, angle, 0f);
        }

        void SetCollidersEnabled(bool enabled)
        {
            _collidersEnabled = enabled;
            for (var i = 0; i < _colliders.Length; i++)
            {
                if (_colliders[i] != null)
                {
                    _colliders[i].enabled = enabled;
                }
            }
        }

        bool AllCollidersEnabled()
        {
            for (var i = 0; i < _colliders.Length; i++)
            {
                if (_colliders[i] != null && !_colliders[i].enabled) return false;
            }
            return true;
        }
    }
}
