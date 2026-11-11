namespace Mas3D.Actions
{
    /// <summary>
    /// Task 1 of docs/superpowers/plans/2026-11-08-action-phases.md: which leg of a performance the
    /// clock is on. An action enters, holds and leaves; only the hold may credit contact, and only
    /// the enter and the leave have a nominal length.
    /// </summary>
    public enum ActionPhase
    {
        /// <summary>Idle: no performance, no clock, no weight.</summary>
        None = 0,

        /// <summary>Reaching into the action; the performance may claim the bones only at the end of it.</summary>
        Enter = 1,

        /// <summary>The contact itself, for as long as it lasts.</summary>
        Hold = 2,

        /// <summary>Letting go; the performance's claim fades out over the first third of it.</summary>
        Leave = 3,
    }
}
