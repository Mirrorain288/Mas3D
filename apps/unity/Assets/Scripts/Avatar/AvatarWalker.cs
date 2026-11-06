using System;
using System.Collections.Generic;
using Mas3D.Actions;
using Mas3D.Conversation;
using Mas3D.Room;
using UnityEngine;

namespace Mas3D.Avatar
{
    /// <summary>
    /// Task 2 of docs/superpowers/plans/2026-11-05-unity-p7-voice-walking-body.md: she walks the
    /// hand written <see cref="EdgeTable"/> graph, straight segments only. No NavMesh and no A*: the
    /// room has seven places she may stand, so the shortest path is a table read, not a search.
    ///
    /// Three rules shape everything here:
    /// 1. the mouth wins over the legs — while the conversation is Thinking or Answered she neither
    ///    starts a walk nor picks a new target mid-walk, she only finishes the segment she is on;
    /// 2. the body follows the root — the capsule is re-fitted through <see cref="AvatarActor.FitBody"/>
    ///    whenever the pose changes, so there is one placement rule, not a walking copy of it;
    /// 3. the player owns his own motion — she stops when his capsule is in the way and never pushes.
    ///
    /// There is no walk clip in the VRM and no AnimatorController in the repo, so the root translates
    /// and the existing idle keeps breathing: the legs do not step. That is a known cosmetic gap,
    /// recorded in the task report, not something this class pretends to solve.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AvatarWalker : MonoBehaviour
    {
        /// <summary>Walking speed in m/s; the brief's 1.1.</summary>
        public const float Speed = 1.1f;

        /// <summary>Degrees per second. She turns while she walks, not after arriving.</summary>
        public const float TurnRate = 180f;

        /// <summary>Facing tolerance at the destination; the brief allows 5 degrees.</summary>
        public const float ArriveAngle = 5f;

        /// <summary>A single frame longer than this is treated as this long, so a hitch cannot teleport her.</summary>
        public const float MaxUpdateStep = 0.1f;

        /// <summary>How close a node has to be before she is believed to be standing on it.</summary>
        public const float NodeWindow = 0.5f;

        /// <summary>
        /// How close her root has to be to a seat's own xz before she is believed to be sitting on it.
        /// A seat sit puts the root exactly on the seat point, so five centimetres is already generous:
        /// anything further is a hand placed body that is seated on nothing this graph knows.
        /// </summary>
        public const float SeatWindow = 0.05f;

        /// <summary>Probe capsule radius: the player's own 0.30, so "in her way" means "would collide".</summary>
        public const float ProbeRadius = 0.30f;
        public const float ProbeBottom = 0.30f;
        public const float ProbeTop = 1.00f;

        /// <summary>The body being walked; falls back to the one on this object.</summary>
        public AvatarActor Actor;

        /// <summary>Who decides whether the mouth is busy; falls back to the one in the scene.</summary>
        public ConversationController Conversation;

        /// <summary>
        /// Phase injection point. Tests drive it by hand; the scene leaves it null and reads
        /// <see cref="Conversation"/> instead.
        /// </summary>
        public Func<TurnPhase> PhaseSource;

        /// <summary>
        /// Seat injection point, the same shape as <see cref="PhaseSource"/>: returns true when the
        /// seat took. Null in the scene, so seating runs through AvatarActor; a test sets it to make a
        /// seat refuse and prove that a failed sit withdraws the arrival instead of leaving the walker
        /// claiming a pose she is not in.
        /// </summary>
        public Func<string, bool> SeatAttempt;

        /// <summary>True in the scene; tests turn it off and call <see cref="Tick"/> themselves.</summary>
        public bool DriveInUpdate = true;

        /// <summary>True between <see cref="GoTo"/> and arriving (or stopping at a node).</summary>
        public bool Walking { get; private set; }

        /// <summary>True when the last walk ended because the player's body was in the way.</summary>
        public bool Blocked { get; private set; }

        /// <summary>True when the last walk actually reached its destination.</summary>
        public bool Arrived { get; private set; }

        /// <summary>The node she is standing on (or sitting at), as far as this walker knows.</summary>
        public string At { get; private set; }

        /// <summary>The node id of the walk in progress, or null.</summary>
        public string Target { get; private set; }

        /// <summary>How many graph edges of the current (or last) walk have been completed.</summary>
        public int Legs { get; private set; }

        /// <summary>Seconds of simulated walking since the last <see cref="GoTo"/>.</summary>
        public float Elapsed { get; private set; }

        /// <summary>Why the last <see cref="GoTo"/> was refused or the last walk stopped short.</summary>
        public string LastFaults { get; private set; }

        List<WalkNode> _route;
        int _index;
        readonly Collider[] _probe = new Collider[16];

        /// <summary>
        /// True after she stopped between two nodes (blocked mid-edge, or the body vanished): <see
        /// cref="At"/> is null and must stay null until <see cref="ResnapToNearestNode"/> says a node
        /// is close enough to believe. Guessing here would plan the next walk from a place she never
        /// stood, and the sweep test only proves node-to-node lines.
        /// </summary>
        bool _betweenNodes;

        /// <summary>Where the room puts her root. For the shipped avatar this is the actor itself.</summary>
        Transform Root
        {
            get
            {
                var actor = ResolveActor();
                if (actor != null && actor.Rig != null && actor.Rig.Root != null) return actor.Rig.Root;
                return transform;
            }
        }

        void Update()
        {
            if (!DriveInUpdate) return;
            Tick(Mathf.Min(Time.deltaTime, MaxUpdateStep));
        }

        void OnDisable()
        {
            // A disabled walker is not walking any more; leaving Walking true would make the next
            // GoTo answer "already walking" forever. Disabled mid-edge it is worse than that: the node
            // she last finished is not where the object comes back, so keeping it would let the next
            // GoTo plan a straight line the edge sweep never proved. Stop the way a blocked walk stops.
            if (Walking) StopQuietly("走位途中被禁用，位置未知，等 Resnap 重新认路");
            else Walking = false;

            _route = null;
            Target = null;
        }

        /// <summary>
        /// Start walking to <paramref name="id"/>. Returns false and changes nothing when she is
        /// already walking, when the mouth is busy (Thinking/Answered), when the node is unknown, or
        /// when the graph has no path. A refused call must leave pose, position and seat alone.
        /// </summary>
        public bool GoTo(string id)
        {
            var actor = ResolveActor();
            if (actor == null) return Refuse("场景里没有 AvatarActor");

            if (Walking) return Refuse("已经在走去 " + Target + " 的路上");

            var phase = EffectivePhase();
            if (phase == TurnPhase.Thinking || phase == TurnPhase.Answered)
            {
                return Refuse("对话进行中（" + phase + "），腿不能动");
            }

            var node = EdgeTable.Node(id);
            if (node == null) return Refuse("未知节点 " + id);

            // A mid-edge stop is not a place the graph knows. Only an explicit ResnapToNearestNode
            // hands her back; otherwise this would quietly start the route at the node she is not on.
            if (string.IsNullOrEmpty(At) && !_betweenNodes) RememberWhereSheIs(actor);
            if (string.IsNullOrEmpty(At)) return Refuse("不知道她此刻站在哪个节点，无法规划路线");

            var route = EdgeTable.Path(At, id);
            if (route == null) return Refuse(At + " 到 " + id + " 没有路");

            LastFaults = null;
            Target = id;
            Blocked = false;
            Elapsed = 0f;

            if (route.Count <= 1)
            {
                // Already there: only the facing (and the seat) can differ. A seat that will not take
                // is still a failure here - the same rollback as ArriveAt, so "arrived" means one
                // thing in both places instead of "arrived, unless it is a seat that failed".
                FaceDirectly(node.FacingY);
                Target = null;
                if (!TakeTheSeatIfNeeded(actor, node)) return ForgetTheNode();
                Arrived = true;
                OnNode(node.Id);
                return true;
            }

            if (actor.Seat != null && actor.Seat.Seated) StandUpAt(actor, At);

            _route = route;
            _index = 1;
            Legs = 0;
            Walking = true;
            Arrived = false;
            return true;
        }

        /// <summary>
        /// One simulated step. Movement is along the segment at <see cref="Speed"/>; the facing follows
        /// the segment at <see cref="TurnRate"/>, and on the last leg it is blended toward the
        /// destination facing early enough that arriving and facing happen in the same tick.
        /// Arrival snaps her to the node exactly rather than stopping inside a tolerance, so there is
        /// no arrival radius to keep in step with the 0.05 m the brief allows.
        /// </summary>
        public void Tick(float deltaSeconds)
        {
            if (!Walking) return;
            if (!(deltaSeconds > 0f)) return;

            var dt = Mathf.Min(deltaSeconds, MaxUpdateStep);
            var actor = ResolveActor();
            if (actor == null)
            {
                StopQuietly("没有 actor，走位中止");
                return;
            }

            Elapsed += dt;

            var root = Root;
            var node = _route[_index];
            var last = _index == _route.Count - 1;

            var here = root.position;
            var toNode = node.Feet - here;
            toNode.y = 0f;
            var remaining = toNode.magnitude;

            var desired = remaining > 1e-4f ? YawOf(toNode) : node.FacingY;
            if (last)
            {
                // She has to be facing the anchor when she stops, not after she stops. Turn only when
                // the time left on the segment is no longer enough to finish the turn while walking.
                var timeLeft = remaining / Speed;
                var turnNeeded = Mathf.Abs(Mathf.DeltaAngle(root.eulerAngles.y, node.FacingY)) / TurnRate;
                if (turnNeeded >= timeLeft) desired = node.FacingY;
            }

            TurnTowards(desired, dt);

            var step = Speed * dt;
            var next = remaining <= step ? node.Feet : here + toNode / remaining * step;
            next.y = node.Feet.y;

            if (ThePlayerIsInTheWay(next))
            {
                Blocked = true;
                StopQuietly("玩家挡在 " + node.Id + " 方向，她只能自己停下");
                return;
            }

            root.position = next;

            if (Flat(next, node.Feet) > 0.001f) return;

            if (!last)
            {
                // The mouth takes the next leg away: finish this segment, stop on this node.
                if (TheMouthIsBusy())
                {
                    StopAt(node, "说话中，停在 " + node.Id + "，不走下一段");
                    return;
                }

                _index++;
                OnNode(node.Id);
                Legs++;
                return;
            }

            if (YawError(node.FacingY) > ArriveAngle) return;

            ArriveAt(actor, node);
        }

        /// <summary>Stand at the node she is on, re-seat the idle baseline and re-fit the body.</summary>
        void StandUpAt(AvatarActor actor, string nodeId)
        {
            var node = EdgeTable.Node(nodeId);
            if (node == null) return;

            actor.Seat.Stand(node.Feet, node.FacingY);
            if (actor.Idle != null) actor.Idle.CaptureBaseline();
            actor.FitBody();
            Root.position = new Vector3(node.Feet.x, node.Feet.y, node.Feet.z);
        }

        void ArriveAt(AvatarActor actor, WalkNode node)
        {
            var root = Root;
            root.position = new Vector3(node.Feet.x, node.Feet.y, node.Feet.z);
            root.rotation = Quaternion.Euler(0f, node.FacingY, 0f);

            Walking = false;
            Target = null;
            Legs++;

            // She is on the node's floor spot either way, but a seat that will not take leaves her
            // standing where the graph says she is sitting. Saying "arrived on S2" then would plan the
            // way out from the seat's stand point, which is where a failed sit did not put her: the
            // place is real, the pose is not, so only the place is withdrawn.
            if (!TakeTheSeatIfNeeded(actor, node))
            {
                ForgetTheNode();
                return;
            }

            Arrived = true;
            OnNode(node.Id);
        }

        /// <summary>
        /// Keep her off the graph after an arrival that ended in the wrong pose: the next GoTo has to
        /// ask where she is (Resnap) instead of walking out of a node she is not standing on.
        /// </summary>
        bool ForgetTheNode()
        {
            Arrived = false;
            At = null;
            _betweenNodes = true;
            return false;
        }

        /// <summary>
        /// Sit where the node says, if the node is a seat at all. Returns false only when she was asked
        /// to sit and could not: the caller then has the place but not the pose, and that is not an
        /// arrival. <see cref="PhaseSource"/> is the pattern this follows - an injection point for
        /// tests, null in the scene.
        /// </summary>
        bool TakeTheSeatIfNeeded(AvatarActor actor, WalkNode node)
        {
            if (!node.IsSeat) return true;

            if (SeatAttempt != null)
            {
                if (SeatAttempt(node.SeatId)) return true;
                LastFaults = "座 " + node.SeatId + " 坐不下去（注入的座判定）";
                return false;
            }

            if (actor.Seat == null) return true;

            // A bed is a second pose, not a second kind of seat: on S2 the hips ride on the mattress
            // top (topY) with AvatarPose.BedSitting, and a chair sit would both bury her a centimetre
            // into the duvet and drop the bed's seated lean. ActionDirector.BedSeatId is the one place
            // that names the bed, so the walker reads it instead of keeping its own copy of "S2".
            if (string.Equals(node.SeatId, ActionDirector.BedSeatId, StringComparison.OrdinalIgnoreCase))
            {
                // SitOnBed re-bases the idle and re-fits the body itself; repeating those here would
                // capture the baseline twice and claim a pose that was never applied.
                if (actor.SitOnBed(node.SeatId)) return true;
                LastFaults = "床座 " + node.SeatId + " 坐不下去";
                return false;
            }

            var table = AnchorTable.Load();
            var seat = table != null ? table.Seat(node.SeatId) : null;
            if (seat == null)
            {
                LastFaults = "座 " + node.SeatId + " 不在 anchors.json 里，到了也没法坐下";
                return false;
            }

            actor.Seat.Sit(seat);
            if (actor.Idle != null) actor.Idle.CaptureBaseline();
            actor.FitBody();
            return true;
        }

        void StopAt(WalkNode node, string reason)
        {
            Walking = false;
            Arrived = false;
            Target = null;
            OnNode(node.Id);
            LastFaults = reason;
        }

        void StopQuietly(string reason)
        {
            Walking = false;
            Arrived = false;
            Target = null;
            // She stopped between two nodes, so the node she last passed is not where she stands.
            // Say so instead of naming a place she is not on: the next route would start there, and
            // the sweep test only ever proved node-to-node lines.
            At = null;
            _betweenNodes = true;
            LastFaults = reason;
        }

        /// <summary>Record a node she is really on; the only place <see cref="At"/> becomes non-null.</summary>
        void OnNode(string id)
        {
            At = id;
            _betweenNodes = false;
        }

        /// <summary>
        /// Re-read her place on the graph after a stop between nodes. Same 0.5 m window
        /// <see cref="RememberWhereSheIs"/> uses: a node that close is believed, anything further
        /// leaves <see cref="At"/> null. Returns whether she is back on the graph.
        /// </summary>
        public bool ResnapToNearestNode()
        {
            // Mid-walk her root is between two nodes by construction, and believing the nearest one
            // would turn a swept, walked line into a straight line nobody proved. Say no and touch
            // nothing: the walk keeps its node, its target and its feet until it stops.
            if (Walking) return false;

            var actor = ResolveActor();
            At = null;
            _betweenNodes = false;
            if (actor == null) return false;
            RememberWhereSheIs(actor);
            return !string.IsNullOrEmpty(At);
        }

        /// <summary>Where she is right now, in graph terms: the seat under her if seated, else the nearest node.</summary>
        void RememberWhereSheIs(AvatarActor actor)
        {
            if (!string.IsNullOrEmpty(At)) return;

            if (actor.Seat != null && actor.Seat.Seated)
            {
                // AvatarActor.SeatId is written once in Spawn. After ActionDirector sits her on the bed
                // with a direct SitOnBed it still says "S1", and planning from the bench teleports her
                // off the mattress. The seat whose own xz she is standing on is the only thing that
                // knows where the body really is; when she is on neither, say so instead of guessing.
                var under = SeatUnderneath();
                if (under != null)
                {
                    OnNode(under);
                    return;
                }

                _betweenNodes = true;
                return;
            }

            var root = Root.position;
            WalkNode nearest = null;
            var best = float.PositiveInfinity;
            foreach (var node in EdgeTable.Nodes)
            {
                var delta = node.Feet - root;
                delta.y = 0f;
                var distance = delta.magnitude;
                if (distance >= best) continue;
                best = distance;
                nearest = node;
            }

            // A walker dropped far from the graph (a hand placed prefab, a test) should say so rather
            // than walk in from a node it is nowhere near.
            if (nearest != null && best <= NodeWindow) OnNode(nearest.Id);
        }

        /// <summary>The seat id whose own xz her root is standing on, or null when she is on none.</summary>
        string SeatUnderneath()
        {
            var table = AnchorTable.Load();
            if (table == null) return null;

            var root = Root.position;
            string bestId = null;
            var best = float.PositiveInfinity;
            foreach (var node in EdgeTable.Nodes)
            {
                if (!node.IsSeat) continue;
                var seat = table.Seat(node.SeatId);
                if (seat == null) continue;

                var dx = seat.x - root.x;
                var dz = seat.z - root.z;
                var distance = Mathf.Sqrt(dx * dx + dz * dz);
                if (distance >= best) continue;
                best = distance;
                bestId = node.SeatId;
            }

            return best <= SeatWindow ? bestId : null;
        }

        bool TheMouthIsBusy()
        {
            var phase = EffectivePhase();
            return phase == TurnPhase.Thinking || phase == TurnPhase.Answered;
        }

        /// <summary>Which phase the conversation is in; injected first, then the scene controller.</summary>
        public TurnPhase EffectivePhase()
        {
            if (PhaseSource != null) return PhaseSource();

            var conversation = Conversation != null ? Conversation : FindAnyObjectByType<ConversationController>();
            return conversation != null ? conversation.Phase : TurnPhase.Idle;
        }

        bool ThePlayerIsInTheWay(Vector3 next)
        {
            var bottom = new Vector3(next.x, next.y + ProbeBottom, next.z);
            var top = new Vector3(next.x, next.y + ProbeTop, next.z);
            var count = Physics.OverlapCapsuleNonAlloc(bottom, top, ProbeRadius, _probe, ~0, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < count; i++)
            {
                var hit = _probe[i];
                if (hit is CharacterController) return true;
            }

            return false;
        }

        void TurnTowards(float desiredYaw, float dt)
        {
            var root = Root;
            var current = root.eulerAngles.y;
            var delta = Mathf.DeltaAngle(current, desiredYaw);
            var limit = TurnRate * dt;
            var applied = Mathf.Clamp(delta, -limit, limit);
            if (Mathf.Abs(applied) < 1e-4f) return;
            root.rotation = Quaternion.Euler(0f, current + applied, 0f);
        }

        void FaceDirectly(float facingY)
        {
            Root.rotation = Quaternion.Euler(0f, facingY, 0f);
        }

        float YawError(float expected)
        {
            return Mathf.Abs(Mathf.DeltaAngle(Root.eulerAngles.y, expected));
        }

        static float YawOf(Vector3 direction)
        {
            return Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
        }

        static float Flat(Vector3 a, Vector3 b)
        {
            var dx = a.x - b.x;
            var dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        AvatarActor ResolveActor()
        {
            if (Actor == null) Actor = GetComponent<AvatarActor>();
            return Actor;
        }

        bool Refuse(string reason)
        {
            LastFaults = reason;
            Debug.Log("[walk] 拒绝走位：" + reason, this);
            return false;
        }
    }
}
