/// <summary>
/// Returning projectiles (poewiki "Return"): return after a collision with a target, with terrain, or at maximum
/// distance; the projectile then flies back toward the caster and continues 15 m past it, and can return only once.
/// The stats are the game's own: <c>skill_projectiles_cannot_return</c> forbids it, <c>projectiles_return</c> (any
/// projectile) and <c>attack_projectiles_return</c> (attacks only) always return, and
/// <c>projectile_return_%_chance</c> rolls. Some skills are exceptions (Spark and the other projectiles that move by
/// their own rules, rain/tornado secondary projectiles); Freezing Pulse only returns from a collision.
/// Game_PROJECTILE_RETURN=1/0 forces the decision for a live check.
/// </summary>
static class ProjectileReturn
{
    /// <summary>Skills whose projectiles cannot return (the wiki's exceptions this server can fire).</summary>
    static readonly HashSet<string> CannotReturn =
    [
        "Spark", "SparkAltX", "SparkAltY",
        "RainOfArrows", "BlastRain",
        "SplittingSteel", "VenomGyre", "WinterOrb", "ForbiddenRite", "Cremation",
        "RollingMagma", "ToxicRain", "PoisonousConcoction",
    ];

    /// <summary>Projectiles that need a collision (target or terrain) to return; at maximum distance they do not.</summary>
    static readonly HashSet<string> NeedsCollision = ["FreezingPulse"];

    public static bool CannotReturnFromDistance(string? effectId) => effectId is not null && NeedsCollision.Contains(effectId);

    /// <summary>Distance from a cell to a segment, for a returning projectile's hit sweep (grid cells).</summary>
    public static double DistanceToSegment((int X, int Y) point, (int X, int Y) a, (int X, int Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 0) return Math.Sqrt(Math.Pow(point.X - a.X, 2) + Math.Pow(point.Y - a.Y, 2));
        double t = Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared, 0, 1);
        double x = a.X + dx * t, y = a.Y + dy * t;
        return Math.Sqrt(Math.Pow(point.X - x, 2) + Math.Pow(point.Y - y, 2));
    }

    /// <summary>The "while Returning" more multiplier: the support's less Damage and the game's returning-projectile
    /// final multipliers, combined multiplicatively (all are percentages, e.g. -70 = ×0.3).</summary>
    public static double DamageMore(Func<string, int> stat)
    {
        double more = 1;
        foreach (string name in new[]
                 {
                     "support_return_returning_projectiles_damage_+%_final",
                     "active_skill_returning_projectile_damage_+%_final",
                     "damage_+%_final_from_returning_projectile",
                 })
            more *= Math.Max(0, 1 + stat(name) / 100.0);
        return more;
    }

    public static bool ShouldReturn(string? effectId, bool attack, Func<string, int> stat, int roll, string? forced = null)
    {
        if (effectId is not null && CannotReturn.Contains(effectId)) return false;
        if (forced is "1") return true;
        if (forced is "0") return false;
        if (stat("skill_projectiles_cannot_return") != 0) return false;
        if (stat("projectiles_return") != 0) return true;
        if (attack && stat("attack_projectiles_return") != 0) return true;
        int chance = Math.Clamp(stat("projectile_return_%_chance"), 0, 100);
        return chance > 0 && roll < chance;
    }
}
