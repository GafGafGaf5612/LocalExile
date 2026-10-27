using GameServer.Core.Projectiles;

partial class Program
{
    // The deferred layer may replace this inventory; it does not install another production entry or simulator.
    static partial void RegisterExtendedProjectileSkillDefinitions(Dictionary<string, ProjectileSkillDefinition> definitions,
        ref bool requireExplicitProfiles);

    static partial void RegisterProjectileSkillDefinitions(Dictionary<string, ProjectileSkillDefinition> definitions,
        ref bool requireExplicitProfiles)
    {
        foreach (string effect in GatewayUpstreamProjectileProfiles.Effects)
            if (Skills.ProjectileFor(Skills.EffectRow(effect)) is { } projectile)
                definitions.Add(effect, UpstreamProjectileDefinition(effect, projectile.Id));
        RegisterExtendedProjectileSkillDefinitions(definitions, ref requireExplicitProfiles);
    }

    static partial void ConfigureCatalogProjectileDefinition(int effect, ref ProjectileSkillDefinition? definition)
    {
        if (definition is not null && Skills.EffectId(effect) is { } id)
            definition = UpstreamProjectileDefinition(id, definition.Metadata);
    }

    static ProjectileSkillDefinition UpstreamProjectileDefinition(string effect, string metadata) =>
        new(metadata, (get, speed) => GatewayUpstreamProjectileProfiles.Resolve(effect, metadata, get, speed,
            Skills.IsAttack(Skills.EffectRow(effect))))
        {
            SpecializeRelease = (context, use) =>
            {
                if (use.Profile.ContactArea is { } contact)
                {
                    int radius = SkillRadius(context.Conn, SupportedSkill(context.Conn, context.SkillId).Stats,
                        !Skills.IsAttack(context.Gem.Effect), context.Gem.Effect);
                    use = use with { Profile = use.Profile with { ContactArea = contact with { Radius = radius },
                        EndArea = use.Profile.EndArea is { } end ? end with { Radius = radius } : null } };
                }
                var shot = (ProjectileOffense)use.Payload!;
                if (GatewayUpstreamProjectileProfiles.IsKineticBlast(effect))
                    return use with { Payload = shot with { AreaVisual = KineticBlastImpactAo,
                        Presentation = new() { EffectLifetime = ProjectileRuntime.Time(.35) } } };
                if (!GatewayUpstreamProjectileProfiles.IsFireball(metadata)) return use;
                string ao = Art.AnimatedInPlaceOf(DefaultProjectileImpactAo, context.Variation);
                return use with { Payload = shot with { Presentation = new()
                {
                    EffectLifetime = ProjectileRuntime.Time(1.5),
                    // Upstream delays art only. Outbound contact area damage still commits before the return.
                    EventEffect = value => value.Kind == ProjectileEventKind.Area
                        && (!value.Projectile.Profile.Returns || value.Projectile.Profile.CannotReturn
                            || value.Projectile.Profile.TerminalAreaRequiresNoContact) ? ao
                        : value.Kind == ProjectileEventKind.End && value.Projectile.Phase > 0
                            && value.Reason is ProjectileEndReason.Returned or ProjectileEndReason.Enemy
                                or ProjectileEndReason.Distance or ProjectileEndReason.Lifetime ? ao : null
                } } };
            }
        };
}
