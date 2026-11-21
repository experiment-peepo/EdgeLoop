namespace EdgeLoop.Classes
{
    /// <summary>Compile-time switches for features that exist in code but aren't ready to be shown.</summary>
    public static class FeatureFlags
    {
        /// <summary>
        /// In-player censoring needs more work before it is visible. While false, its settings are
        /// hidden and it never runs, even if an older settings.json has EnableLocalCensor=true.
        /// The detector, its tests and the saved settings stay in place for when this is turned on.
        /// </summary>
        public const bool CensorFeatureVisible = false;

        /// <summary>Whether censoring should run with the given settings.</summary>
        public static bool IsCensorEnabled(UserSettings settings)
            => CensorFeatureVisible && settings?.EnableLocalCensor == true;
    }
}
