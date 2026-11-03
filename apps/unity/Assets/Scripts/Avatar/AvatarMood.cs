using Mas3D.Conversation;
using UniVRM10;
using UnityEngine;

namespace Mas3D.Avatar
{
    /// <summary>
    /// Task 4 of docs/superpowers/plans/2026-11-03-unity-avatar-in-scene.md: turns the conversation
    /// phase into three expression weights — Relaxed, Happy, Sad — and touches nothing else. Blink
    /// belongs to <see cref="AvatarIdle"/>; the mouth shapes belong to P3 lip-sync. Every write goes
    /// through <see cref="AvatarRig.HasExpression"/> because SetWeight silently ignores unknown keys.
    ///
    /// The state is only "which temporary expression is showing, at what weight, for how many seconds
    /// are left". <see cref="Apply"/> is idempotent: applying the phase that is already showing does
    /// not restart its clock, so <see cref="Update"/> can poll cheaply.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AvatarMood : MonoBehaviour
    {
        /// <summary>The rig this mood writes to; falls back to the rig on the same GameObject.</summary>
        public AvatarRig Rig;

        /// <summary>
        /// Polled once per Update. Nullable: the room may have no conversation object yet, and a
        /// mood with no controller simply never changes (Tick still drains whatever is showing).
        /// </summary>
        public ConversationController Controller;

        /// <summary>Relaxed weight while the model is thinking.</summary>
        public float ThinkingRelaxed = 0.20f;

        /// <summary>Relaxed weight at rest (Idle / Sending) and after a temporary expression ends.</summary>
        public float IdleRelaxed = 0.35f;

        /// <summary>How wide the smile is while an answer is fresh.</summary>
        public float HappyWeight = 0.65f;

        /// <summary>How long the smile holds before settling back to Relaxed.</summary>
        public float HappySeconds = 2.5f;

        /// <summary>How heavy the frown is after a failed turn.</summary>
        public float SadWeight = 0.50f;

        /// <summary>How long the frown holds before settling back to Relaxed.</summary>
        public float SadSeconds = 3.0f;

        /// <summary>The Happy/Sad expression on screen, if any. Only one can be active.</summary>
        ExpressionKey? _temporary;

        /// <summary>Seconds left before <see cref="_temporary"/> clears. Only meaningful with a key.</summary>
        float _remaining;

        TurnPhase _phase;
        bool _phaseKnown;

        void Awake()
        {
            if (Rig == null) Rig = GetComponent<AvatarRig>();
        }

        void Update()
        {
            Poll(Time.deltaTime);
        }

        /// <summary>
        /// One frame of the mood: react to a phase change, then advance the clock. Public so a test
        /// can drive the same path with the component disabled; <see cref="Update"/> stays private.
        /// </summary>
        public void Poll(float dt)
        {
            // BindConversation(null) never calls Apply, so a mood with no controller would otherwise
            // rest on the model's neutral pose while a wired one rests on IdleRelaxed. Apply once.
            if (Controller == null && !_phaseKnown) Apply(TurnPhase.Idle);

            // ConversationController has no Phase event on purpose (plan: not changing it for a
            // facial expression), so poll and only react on an actual transition.
            if (Controller != null)
            {
                var phase = Controller.Phase;
                if (!_phaseKnown || phase != _phase) Apply(phase);
            }

            Tick(dt);
        }

        /// <summary>
        /// Translate one phase into weights. Any Happy/Sad left from the previous turn is cleared
        /// first, so a failure never keeps a smile underneath it. Idempotent: the same phase twice
        /// leaves both the weights and the remaining seconds untouched.
        /// </summary>
        public void Apply(TurnPhase phase)
        {
            EnsureRig();
            if (_phaseKnown && phase == _phase) return;

            _phase = phase;
            _phaseKnown = true;
            ClearTemporary();

            switch (phase)
            {
                case TurnPhase.Thinking:
                    WriteRelaxed(ThinkingRelaxed);
                    break;

                case TurnPhase.Answered:
                    WriteRelaxed(IdleRelaxed);
                    Begin(ExpressionKey.Happy, HappyWeight, HappySeconds);
                    break;

                case TurnPhase.Failed:
                    WriteRelaxed(IdleRelaxed);
                    Begin(ExpressionKey.Sad, SadWeight, SadSeconds);
                    break;

                default:                       // Idle and Sending: at rest, nothing temporary
                    WriteRelaxed(IdleRelaxed);
                    break;
            }
        }

        /// <summary>
        /// Advance the clock only. Steps the remaining time first, and when it reaches zero in this
        /// call, clears the temporary expression and settles back on <see cref="IdleRelaxed"/> — read
        /// at the moment of expiry, not the value the phase started with. A tick longer than the
        /// remaining time therefore expires in that same call, never one tick late.
        /// </summary>
        public void Tick(float dt)
        {
            if (dt < 0f) dt = 0f;
            if (!_temporary.HasValue) return;

            _remaining -= dt;
            if (_remaining > 0f) return;

            var expired = _temporary.Value;
            _temporary = null;
            _remaining = 0f;

            Write(expired, 0f);
            WriteRelaxed(IdleRelaxed);
        }

        void Begin(ExpressionKey key, float weight, float seconds)
        {
            if (seconds <= 0f) return;
            Write(key, weight);
            _temporary = key;
            _remaining = seconds;
        }

        /// <summary>Drop whatever Happy/Sad is showing; the next state decides what replaces it.</summary>
        void ClearTemporary()
        {
            if (!_temporary.HasValue) return;
            Write(_temporary.Value, 0f);
            _temporary = null;
            _remaining = 0f;
        }

        void WriteRelaxed(float weight)
        {
            Write(ExpressionKey.Relaxed, weight);
        }

        /// <summary>The only expression write in this class: unknown keys and a missing rig are no-ops.</summary>
        void Write(ExpressionKey key, float weight)
        {
            if (Rig == null) return;
            if (!Rig.HasExpression(key)) return;
            Rig.SetExpression(key, Mathf.Clamp01(weight));
        }

        void EnsureRig()
        {
            if (Rig == null) Rig = GetComponent<AvatarRig>();
        }
    }
}
