using GameServer.Core.Projectiles;

/// <summary>Executable upstream 79cc033 behavior, without later researched skill recipes.</summary>
static class GatewayUpstreamProjectileProfiles
{
    public static IReadOnlyList<string> Effects { get; } = Array.AsReadOnly(new[]
        { "Fireball", "KineticBlast", "KineticBlastAltX", "KineticBlastAltY" });

    static readonly HashSet<string> CannotReturn = new(StringComparer.Ordinal)
    {
        "Spark", "SparkAltX", "SparkAltY", "RainOfArrows", "BlastRain", "SplittingSteel", "VenomGyre",
        "WinterOrb", "ForbiddenRite", "Cremation", "RollingMagma", "ToxicRain", "PoisonousConcoction"
    };
    static readonly IReadOnlySet<ProjectileEndReason> EmptyFlightEnds =
        new HashSet<ProjectileEndReason> { ProjectileEndReason.Distance, ProjectileEndReason.Lifetime };

    public static bool IsKineticBlast(string effect) => effect is "KineticBlast" or "KineticBlastAltX" or "KineticBlastAltY";
    public static bool IsFireball(string metadata) => metadata.Contains("Fireball", StringComparison.Ordinal);

    public static ProjectileProfile Resolve(string effect, string metadata, Func<string, int> get, double speed, bool attack)
    {
        var p = GatewayProjectileDefaults.Resolve(metadata, get, speed, attack);
        // Upstream sums these four inputs and caps its ordinary fan at twenty projectiles.
        int pierce = Math.Max(0, get("projectile_number_of_targets_to_pierce")
            + get("projectile_base_number_of_targets_to_pierce")
            + get("arrow_number_of_targets_to_pierce") + get("arrow_base_number_of_targets_to_pierce"));
        bool unlimited = get("always_pierce") != 0 || get("arrows_always_pierce") != 0;
        int count = Math.Clamp(1 + get("number_of_additional_projectiles"), 1, 20);
        double gap = get("projectile_spread_radius") is > 0 and <= 90 and var value ? value : 10;
        p = p with
        {
            Count = count, AimSpreadDistance = 0, SpreadRadians = gap * Math.PI / 180 * (count - 1), CircularSpread = false,
            Pierce = unlimited || pierce > 0 ? -1 : 0,
            OutboundHitLimit = !unlimited && pierce > 0 ? 1 + pierce : null,
            TerminalAreaRequiresNoContact = unlimited || pierce > 0,
            Lifetime = long.MaxValue / 4,
            CannotReturn = p.CannotReturn || CannotReturn.Contains(effect),
            ReturnOnDistance = effect != "FreezingPulse",
            ReturnOvershoot = 150, IgnoreTerrainOnReturn = true, ExcludeTurnaroundTarget = true, SkipDeniedReturnContacts = true,
            ReturnHitLimit = unlimited || get("returning_projectiles_always_pierce") != 0 ? null : 1 + pierce,
            ReturnSpeedMultiplier = Math.Max(1d, p.Speed * (1 + get("returned_projectile_speed_+%") / 100d)) / p.Speed,
            ReturnHitRule = new(ProjectileHitScope.Emission),
            SkipDeniedContacts = !IsKineticBlast(effect)
        };
        if (!IsKineticBlast(effect) && !IsFireball(metadata)) return p;
        int radius = SkillArea.Radius(SkillArea.BaseRadius(effect, get), get, spell: !attack);
        var area = new ProjectileAreaRecipe(IsKineticBlast(effect) ? "upstream-kinetic-blast" : "upstream-fireball", radius)
            { ExcludeContactTarget = true };
        // The old KB code applies direct damage despite a contrary comment. Both skills exclude that victim from area.
        return p with
        {
            DirectDamage = true, ContactArea = area, ContactAreaOnReturn = IsKineticBlast(effect),
            EndArea = IsKineticBlast(effect) ? area : null,
            EndAreaReasons = IsKineticBlast(effect) ? EmptyFlightEnds : null,
            HitRule = new(IsKineticBlast(effect) ? ProjectileHitScope.Projectile : ProjectileHitScope.Emission)
        };
    }
}
