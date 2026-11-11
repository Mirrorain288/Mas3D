using UnityEngine;

namespace Mas3D.Actions
{
    /// <summary>
    /// Task 1 of docs/superpowers/plans/2026-11-08-action-phases.md: the spec's phase lengths and the
    /// curve that turns a phase plus its progress into an IK weight. Pure numbers, so the curve can be
    /// asserted without a rig, a player or a director.
    /// </summary>
    public static class PerformanceTiming
    {
        /// <summary>Seconds the performance spends walking in and reaching, or 0 for an unknown id.</summary>
        public static float Enter(string id)
        {
            switch (id)
            {
                case ActionId.Hands: return 2.6f;
                case ActionId.PatHer: return 0.9f;
                case ActionId.PatMe: return 0.9f;
                case ActionId.Kiss: return 1.4f;
                case ActionId.SitTogether: return 2.2f;
                default: return 0f;
            }
        }

        /// <summary>Seconds the performance spends letting go and walking back, or 0 for an unknown id.</summary>
        public static float Leave(string id)
        {
            switch (id)
            {
                case ActionId.Hands: return 1.6f;
                case ActionId.PatHer: return 0.7f;
                case ActionId.PatMe: return 0.7f;
                case ActionId.Kiss: return 1.0f;
                case ActionId.SitTogether: return 1.4f;
                default: return 0f;
            }
        }

        /// <summary>
        /// How much of the bones the performance may claim, 0 to 1, given the phase and the fraction
        /// of it already played. An entry stays flat until 70 percent - the walk in has to finish
        /// before the arms start to reach - and a leave is back to zero a third of the way out.
        /// </summary>
        public static float IkWeight(ActionPhase phase, float u)
        {
            switch (phase)
            {
                case ActionPhase.Enter:
                    return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((u - 0.7f) / 0.3f));
                case ActionPhase.Hold:
                    return 1f;
                case ActionPhase.Leave:
                    return 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(u / 0.3f));
                default:
                    return 0f;
            }
        }
    }
}
