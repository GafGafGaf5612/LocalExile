using GameServer.Core.Projectiles;

partial class Program
{
    static partial void RegisterProjectileSkillDefinitions(Dictionary<string, ProjectileSkillDefinition> definitions,
        ref bool requireExplicitProfiles)
    {
        foreach (string effect in GatewayProjectileSkillProfiles.Effects)
            if (Skills.ProjectileFor(Skills.EffectRow(effect)) is { } projectile)
                definitions.Add(effect, SkillProjectileDefinition(effect, projectile.Id));
    }

    static partial void ConfigureCatalogProjectileDefinition(int effect, ref ProjectileSkillDefinition? definition)
    {
        if (definition is not null && Skills.EffectId(effect) is { } id)
            definition = SkillProjectileDefinition(id, definition.Metadata);
    }

    static ProjectileSkillDefinition SkillProjectileDefinition(string effect, string metadata) =>
        new(metadata, (get, speed) => GatewayProjectileSkillProfiles.Resolve(effect, metadata, get, speed,
            Skills.IsAttack(Skills.EffectRow(effect))))
        {
            SpecializeRelease = (context, use) =>
            {
                // Player base increased area is already included in the panel total. Lay the skill's own
                // supported stats over the area-specific player snapshot, then scale each base radius once.
                var areaStats = MergeSkillStats(AreaPlayerStats(context.Conn), SupportedSkill(context.Conn, context.SkillId).Stats);
                int Get(string key) => Stats.Key(key) is uint id ? areaStats.GetValueOrDefault(id) : 0;
                use = use with { Profile = GatewayProjectileSkillProfiles.ScaleAreas(use.Profile, effect, Get,
                    !Skills.IsAttack(context.Gem.Effect)) };
                var shot = (ProjectileOffense)use.Payload!;
                if (GatewayProjectileSkillProfiles.IsKineticBlast(effect))
                    return use with { Payload = shot with { AreaVisual = KineticBlastImpactAo,
                        Presentation = new() { EffectLifetime = ProjectileRuntime.Time(.35) } } };
                if (!GatewayProjectileSkillProfiles.IsFireball(metadata)) return use;
                string ao = Art.AnimatedInPlaceOf(DefaultProjectileImpactAo, context.Variation);
                return use with { Payload = shot with { Presentation = new()
                {
                    EffectLifetime = ProjectileRuntime.Time(1.5),
                    EventEffect = value => value.Kind == ProjectileEventKind.Area ? ao : null
                } } };
            }
        };
}
