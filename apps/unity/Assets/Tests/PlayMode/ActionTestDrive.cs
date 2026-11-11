using NUnit.Framework;
using UnityEngine;
using Mas3D.Actions;

namespace Mas3D.Tests
{
    /// <summary>
    /// Steps an <see cref="ActionDirector"/> through its phases without a frame loop, so a test says
    /// "wait for the hold" instead of counting frames against a nominal length. Every helper drives
    /// with the fixed step the action fixtures already use and fails loudly when a phase never comes.
    /// </summary>
    public static class ActionTestDrive
    {
        /// <summary>One frame at 60 Hz, the step every action test drives with.</summary>
        public const float Step = 1f / 60f;

        /// <summary>Ticks until the action reports <see cref="ActionPhase.Hold"/>.</summary>
        public static void ToHold(ActionDirector d, float maxSeconds = 6f)
        {
            if (d.Phase == ActionPhase.Hold) return;

            var steps = Mathf.CeilToInt(maxSeconds / Step);
            for (var i = 0; i < steps; i++)
            {
                d.Tick(Step);
                if (d.Phase == ActionPhase.Hold) return;
            }

            Assert.Fail("the entry did not reach the hold within " + maxSeconds + " s (phase "
                + d.Phase + ", action " + d.Current + ", " + d.LastFaults + ")");
        }

        /// <summary>Ticks until the leave has ended and the director reports <see cref="ActionPhase.None"/>.</summary>
        public static void ToLeaveEnd(ActionDirector d, float maxSeconds = 6f)
        {
            var steps = Mathf.CeilToInt(maxSeconds / Step);
            for (var i = 0; i < steps; i++)
            {
                d.Tick(Step);
                if (d.Phase == ActionPhase.None) return;
            }

            Assert.Fail("the leave did not finish within " + maxSeconds + " s (phase " + d.Phase
                + ", action " + d.Current + ")");
        }

        /// <summary>Ticks for <paramref name="seconds"/> of action time.</summary>
        public static void Hold(ActionDirector d, float seconds)
        {
            var steps = Mathf.RoundToInt(seconds / Step);
            if (steps < 1) steps = 1;   // a sub-frame request still means "advance one frame"
            for (var i = 0; i < steps; i++) d.Tick(Step);
        }
    }
}
