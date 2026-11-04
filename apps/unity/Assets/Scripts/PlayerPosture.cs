namespace Mas3D
{
    /// <summary>
    /// The stances the player can hold. Every number a stance implies lives in
    /// <see cref="PlayerMotor.Metrics"/>, so the eye and the capsule can never disagree.
    /// </summary>
    public enum PlayerPosture
    {
        Standing,
        Crouching,
        Sitting,
        Kneeling,
    }
}
