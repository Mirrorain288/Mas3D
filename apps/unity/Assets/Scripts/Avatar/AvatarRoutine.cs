using System;
using System.Collections.Generic;
using Mas3D.Actions;
using Mas3D.Conversation;
using Mas3D.Room;
using Mas3D.UI;
using UnityEngine;

namespace Mas3D.Avatar
{
    /// <summary>
    /// Low-frequency, serene idle zone rotation in flat mode: when Monika has stayed idle without
    /// walking, dialogue, typing focus, or pair actions for <see cref="IdleSeconds"/>, she gently
    /// moves to another non-bathroom living zone. Any dialogue or interaction pauses and resets it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AvatarRoutine : MonoBehaviour
    {
        public const float DefaultIdleSeconds = 60f;

        static readonly string[] PreferredTargets = { "A4", "A2", "A6", "A5", "S1" };

        public float IdleSeconds = DefaultIdleSeconds;
        public bool Enabled = true;
        public bool DriveInUpdate = true;

        public AvatarWalker Walker;
        public ConversationController Conversation;
        public DialoguePanel Panel;
        public ActionDirector Actions;

        public Func<TurnPhase> PhaseSource;
        public Func<bool> TypingOverride;
        public Func<bool> ActionBusyOverride;

        public float Timer { get; private set; }
        public string LastPickedTarget { get; private set; }
        public int TransitionsTriggered { get; private set; }

        int _cursor = -1;

        void Update()
        {
            if (!DriveInUpdate) return;
            Tick(Mathf.Min(Time.deltaTime, AvatarWalker.MaxUpdateStep));
        }

        public void Tick(float deltaSeconds)
        {
            if (!Enabled)
            {
                Timer = 0f;
                return;
            }
            if (Walker == null || !(deltaSeconds > 0f)) return;

            var phase = PhaseSource != null
                ? PhaseSource()
                : (Conversation != null ? Conversation.Phase : TurnPhase.Idle);
            var mouthBusy = phase == TurnPhase.Sending || phase == TurnPhase.Thinking || phase == TurnPhase.Answered;
            var typing = TypingOverride != null
                ? TypingOverride()
                : (Panel != null && Panel.IsInputFocused);
            var acting = ActionBusyOverride != null
                ? ActionBusyOverride()
                : (Actions != null && !string.IsNullOrEmpty(Actions.Current));

            if (Walker.Walking || mouthBusy || typing || acting)
            {
                Timer = 0f;
                return;
            }

            Timer += deltaSeconds;
            if (Timer < IdleSeconds) return;

            Timer = 0f;
            var next = PickNextTarget();
            if (string.IsNullOrEmpty(next)) return;

            if (Walker.GoTo(next))
            {
                LastPickedTarget = next;
                TransitionsTriggered++;
            }
        }

        string PickNextTarget()
        {
            var table = AnchorTable.Load();
            if (table == null) return null;

            var candidates = CollectCandidates(table);
            if (candidates.Count == 0) return null;

            var currentId = Walker.At;
            var currentNode = !string.IsNullOrEmpty(currentId) ? EdgeTable.Node(currentId) : null;
            var currentRoom = currentNode != null ? currentNode.Room : null;

            // Pass 1: prefer moving into a different non-bathroom room first for serene variety.
            var picked = SelectFromCandidates(candidates, currentId, currentRoom, requireDifferentRoom: true);
            // Pass 2: fall back to any other non-bathroom zone when staying in one room.
            if (picked < 0) picked = SelectFromCandidates(candidates, currentId, currentRoom, requireDifferentRoom: false);
            if (picked < 0) return null;

            _cursor = picked;
            return candidates[picked].id;
        }

        int SelectFromCandidates(
            List<(string id, string room)> candidates,
            string currentId,
            string currentRoom,
            bool requireDifferentRoom)
        {
            var count = candidates.Count;
            for (var offset = 1; offset <= count; offset++)
            {
                var idx = (_cursor + offset) % count;
                var candidate = candidates[idx];
                if (string.Equals(candidate.id, currentId, StringComparison.Ordinal)) continue;
                if (requireDifferentRoom && !string.IsNullOrEmpty(currentRoom) &&
                    string.Equals(candidate.room, currentRoom, StringComparison.OrdinalIgnoreCase)) continue;
                return idx;
            }
            return -1;
        }

        static List<(string id, string room)> CollectCandidates(AnchorTable table)
        {
            var list = new List<(string id, string room)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void TryAdd(string id, string room)
            {
                if (string.IsNullOrEmpty(id) || seen.Contains(id)) return;
                if (IsBathroom(room)) return;
                var node = EdgeTable.Node(id);
                if (node == null || IsBathroom(node.Room)) return;
                var resolvedRoom = !string.IsNullOrEmpty(room) ? room : node.Room;
                seen.Add(id);
                list.Add((id, resolvedRoom));
            }

            foreach (var id in PreferredTargets)
            {
                var anchor = table.Anchor(id);
                if (anchor != null) { TryAdd(anchor.id, anchor.room); continue; }
                var seat = table.Seat(id);
                if (seat != null && (seat.intimate || seat.id == AvatarActor.DefaultSeatId))
                    TryAdd(seat.id, seat.room);
            }

            if (list.Count < 2 && table.anchors != null)
            {
                foreach (var anchor in table.anchors)
                {
                    if (anchor != null) TryAdd(anchor.id, anchor.room);
                }
            }

            return list;
        }

        static bool IsBathroom(string room) =>
            !string.IsNullOrEmpty(room) && string.Equals(room, "bathroom", StringComparison.OrdinalIgnoreCase);
    }
}
