using System;
using System.Collections.Generic;
using Mas3D.Actions;
using Mas3D.UI;
using UnityEngine;

namespace Mas3D.Room
{
    /// <summary>
    /// Handles player interaction with explicit <see cref="InteractiveDoor"/> targets via
    /// <see cref="InteractKey"/> (<c>F</c>) or non-UI left click, gated by typing/busy/keyboard state.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DoorInteractor : MonoBehaviour
    {
        public const KeyCode InteractKey = KeyCode.F;
        public const float ReachDistance = 2.6f;
        public const float MinLookDot = 0.2f;
        public const string PromptOpen = "按 F 开门";
        public const string PromptClose = "按 F 关门";

        public PlayerMotor Player;
        public PlayerCameraRig CameraRig;
        public ActionDirector Actions;
        public DialoguePanel Panel;
        public Camera CameraOverride;

        public bool CanTakeKeys = true;
        public bool KeyboardInput = true;
        public Func<bool> IsTypingOverride;
        public Func<bool> IsBusyOverride;

        public InteractiveDoor CurrentTarget { get; private set; }
        public string CurrentPrompt { get; private set; } = string.Empty;

        public bool IsTyping =>
            (Panel != null && Panel.IsInputFocused) ||
            (IsTypingOverride != null && IsTypingOverride());

        public bool IsBusy =>
            (Actions != null && !string.IsNullOrEmpty(Actions.Current)) ||
            (IsBusyOverride != null && IsBusyOverride());

        public bool CanInteractNow =>
            CanTakeKeys &&
            KeyboardInput &&
            (Player == null || (Player.KeyboardInput && Player.WantsKeyboard)) &&
            !IsTyping &&
            !IsBusy;

        readonly List<InteractiveDoor> _doors = new List<InteractiveDoor>();
        string _lastSentPrompt = string.Empty;

        public static string FormatPrompt(InteractiveDoor door)
        {
            if (door == null) return string.Empty;
            return door.IsOpen ? PromptClose : PromptOpen;
        }

        public void Bind(
            PlayerMotor player,
            PlayerCameraRig cameraRig,
            ActionDirector actions,
            DialoguePanel panel,
            IEnumerable<InteractiveDoor> doors)
        {
            Player = player;
            CameraRig = cameraRig;
            Actions = actions;
            Panel = panel;
            SetDoors(doors);
            RefreshTargetFromView();
        }

        public void SetDoors(IEnumerable<InteractiveDoor> doors)
        {
            _doors.Clear();
            var playerCol = Player != null ? Player.GetComponent<Collider>() : null;
            if (doors == null) return;

            foreach (var door in doors)
            {
                if (door == null || _doors.Contains(door)) continue;
                if (playerCol != null) door.PlayerCollider = playerCol;
                _doors.Add(door);
            }
        }

        void Update()
        {
            GetPlayerView(out var origin, out var lookForward);
            RefreshTarget(origin, lookForward);

            if (Input.GetKeyDown(InteractKey))
            {
                PressInteract(origin, lookForward);
                return;
            }

            if (Input.GetMouseButtonDown(0))
            {
                TryClickAtScreenPoint(Input.mousePosition);
            }
        }

        void OnDisable()
        {
            CurrentTarget = null;
            CurrentPrompt = string.Empty;
            PushPrompt(string.Empty);
        }

        public InteractiveDoor RefreshTargetFromView()
        {
            GetPlayerView(out var origin, out var lookForward);
            return RefreshTarget(origin, lookForward);
        }

        public InteractiveDoor RefreshTarget(Vector3 origin, Vector3 lookForward)
        {
            if (!CanInteractNow)
            {
                CurrentTarget = null;
                CurrentPrompt = string.Empty;
                PushPrompt(CurrentPrompt);
                return null;
            }

            CurrentTarget = SelectBestDoor(origin, lookForward);
            CurrentPrompt = FormatPrompt(CurrentTarget);
            PushPrompt(CurrentPrompt);
            return CurrentTarget;
        }

        public bool PressInteract()
        {
            GetPlayerView(out var origin, out var lookForward);
            return PressInteract(origin, lookForward);
        }

        public bool PressInteract(Vector3 origin, Vector3 lookForward)
        {
            if (!CanInteractNow)
            {
                RefreshTarget(origin, lookForward);
                return false;
            }

            var target = RefreshTarget(origin, lookForward);
            if (target == null) return false;

            target.Toggle();
            CurrentPrompt = FormatPrompt(target);
            PushPrompt(CurrentPrompt);
            return true;
        }

        public bool TryClickAtScreenPoint(Vector2 screenPoint)
        {
            if (!CanInteractNow) return false;
            if (Panel != null && Panel.ContainsScreenPoint(screenPoint)) return false;

            var cam = CameraOverride != null ? CameraOverride : (CameraRig != null ? CameraRig.Output : null);
            if (cam == null) return false;

            var ray = cam.ScreenPointToRay(screenPoint);
            var hits = Physics.RaycastAll(ray, 16f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            GetPlayerView(out var origin, out var lookForward);

            var maxRayDist = 16f;
            for (var i = 0; i < hits.Length; i++)
            {
                var col = hits[i].collider;
                if (col == null || IsPlayerCollider(col)) continue;

                var hitDoor = col.GetComponentInParent<InteractiveDoor>();
                if (hitDoor != null && hitDoor.isActiveAndEnabled && _doors.Contains(hitDoor))
                {
                    if (Vector3.Distance(origin, hitDoor.InteractionCenter) > ReachDistance + 1e-4f) return false;
                    hitDoor.Toggle();
                    RefreshTarget(origin, lookForward);
                    return true;
                }

                maxRayDist = hits[i].distance;
                break;
            }

            // Fallback while a door is mid-swing (colliders suppressed): pick nearest visible moving door bounds.
            InteractiveDoor movingHit = null;
            var bestDist = maxRayDist;
            for (var i = 0; i < _doors.Count; i++)
            {
                var door = _doors[i];
                if (door == null || !door.isActiveAndEnabled || !door.IsMoving) continue;
                if (Vector3.Distance(origin, door.InteractionCenter) > ReachDistance + 1e-4f) continue;

                if (door.GetWorldBounds().IntersectRay(ray, out var boundDist) && boundDist < bestDist)
                {
                    bestDist = boundDist;
                    movingHit = door;
                }
            }

            if (movingHit == null) return false;
            movingHit.Toggle();
            RefreshTarget(origin, lookForward);
            return true;
        }

        InteractiveDoor SelectBestDoor(Vector3 origin, Vector3 lookForward)
        {
            var lookFlat = new Vector3(lookForward.x, 0f, lookForward.z);
            var hasLook = lookFlat.sqrMagnitude > 1e-6f;
            if (hasLook) lookFlat.Normalize();

            InteractiveDoor best = null;
            var bestScore = float.NegativeInfinity;

            for (var i = 0; i < _doors.Count; i++)
            {
                var door = _doors[i];
                if (door == null || !door.isActiveAndEnabled) continue;

                var delta = door.InteractionCenter - origin;
                var dist = delta.magnitude;
                if (dist > ReachDistance + 1e-4f) continue;

                var dot = 1f;
                if (hasLook)
                {
                    var flatDelta = new Vector3(delta.x, 0f, delta.z);
                    if (flatDelta.sqrMagnitude > 1e-6f)
                    {
                        dot = Vector3.Dot(lookFlat, flatDelta.normalized);
                    }
                }

                if (dot < MinLookDot) continue;

                var score = dot * 2.0f + (1f - dist / ReachDistance);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = door;
                }
            }

            return best;
        }

        void GetPlayerView(out Vector3 origin, out Vector3 lookForward)
        {
            origin = Player != null ? Player.transform.position + Vector3.up * 1.0f : transform.position;
            lookForward = GetLookForward();
        }

        Vector3 GetLookForward()
        {
            if (CameraOverride != null) return CameraOverride.transform.forward;
            if (CameraRig != null && CameraRig.Output != null) return CameraRig.Output.transform.forward;
            if (CameraRig != null) return Quaternion.Euler(0f, CameraRig.IntentYaw, 0f) * Vector3.forward;
            if (Player != null) return Player.transform.forward;
            return transform.forward;
        }

        bool IsPlayerCollider(Collider col)
        {
            if (Player == null || col == null) return false;
            return col.transform == Player.transform || col.transform.IsChildOf(Player.transform);
        }

        void PushPrompt(string prompt)
        {
            var value = prompt ?? string.Empty;
            if (string.Equals(_lastSentPrompt, value, StringComparison.Ordinal)) return;
            _lastSentPrompt = value;
            if (Panel != null)
            {
                Panel.ShowDoorPrompt(value);
            }
        }
    }
}
