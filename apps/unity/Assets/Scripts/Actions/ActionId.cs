namespace Mas3D.Actions
{
    /// <summary>
    /// Task 3 of docs/superpowers/plans/2026-11-04-unity-actions-p4.md: the five actions the
    /// performance layer knows. The strings are the ids the player keys and the tests use, so they
    /// are spelled once here and never rebuilt from an enum name.
    /// </summary>
    public static class ActionId
    {
        public const string Hands = "hands";
        public const string PatHer = "pat-her";
        public const string PatMe = "pat-me";
        public const string Kiss = "kiss";
        public const string SitTogether = "sit-together";

        /// <summary>Every id, in the order the actions shipped; the entry point refuses anything else.</summary>
        public static readonly string[] All = { Hands, PatHer, PatMe, Kiss, SitTogether };
    }
}
