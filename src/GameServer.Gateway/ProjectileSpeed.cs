using System.Globalization;

/// <summary>Ordinary additive projectile speed, resolved once when the projectile leaves the caster.</summary>
static class ProjectileSpeed
{
    // A positive floor keeps -100% reductions and tiny test overrides from producing infinite flight delays.
    // This is a server guard, not a measured minimum speed of the official game.
    public const float MinimumCellsPerSecond = 0.01f;

    /// <summary>
    /// The player's virtual projectile_speed_+% already includes base_projectile_speed_+% (PanelStats).
    /// Add only the skill's raw base input, separately: overlaying its dictionary would lose player bonuses.
    /// More/less and conditional modifiers are outside this calculation. A finite positive override is in cells/s.
    /// </summary>
    public static float CellsPerSecond(int worldUnitsPerSecond, Func<string, int> playerFinal,
        Func<string, int> skillRaw, string? testOverride = null)
    {
        if (float.TryParse(testOverride, NumberStyles.Float, CultureInfo.InvariantCulture, out float forced)
            && float.IsFinite(forced) && forced > 0)
            return Math.Max(MinimumCellsPerSecond, forced);

        long increased = (long)playerFinal("projectile_speed_+%") + skillRaw("base_projectile_speed_+%");
        double speed = Math.Max(0, worldUnitsPerSecond) / (250.0 / 23.0) * Math.Max(0, 1 + increased / 100.0);
        return (float)Math.Max(MinimumCellsPerSecond, speed);
    }
}
