using GameServer.Core.Projectiles;

partial class Program
{
    // The host has no dependency on a concrete provider. Coverage supplies the optional implementation.
    internal sealed record ProjectileSkillDefinition(string Metadata,
        Func<Func<string, int>, double, ProjectileProfile> Resolve)
    {
        public bool RequiresTerrain { get; init; }
        public bool CancelWindupOnNewAction { get; init; }
        public Func<ClientState, string?>? Unavailable { get; init; }
        public Func<ProjectileReleaseContext, ProjectileUse, ProjectileUse>? SpecializeRelease { get; init; }
        public Func<ProjectileEntity, ProjectilePresentation>? Presentation { get; init; }
    }

    internal sealed record ProjectileReleaseContext(ClientState Conn, ProjectileSession Session, ushort SkillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) Gem,
        int Variation, Func<string, int> Get, Func<string, int> Player, Func<string, int> Own);

    internal sealed record ProjectilePresentation
    {
        public string? FlightVisual { get; init; }
        public uint? FlightClip { get; init; }
        public uint? SecondFormClip { get; init; }
        public Func<ProjectileEvent, string?>? EventEffect { get; init; }
    }

    static partial void RegisterProjectileSkillDefinitions(Dictionary<string, ProjectileSkillDefinition> definitions,
        ref bool requireExplicitProfiles);

    static readonly (IReadOnlyDictionary<string, ProjectileSkillDefinition> Definitions, bool RequireExplicitProfiles) ProjectileDefinitions
        = BuildProjectileDefinitions();

    static (IReadOnlyDictionary<string, ProjectileSkillDefinition>, bool) BuildProjectileDefinitions()
    {
        var definitions = new Dictionary<string, ProjectileSkillDefinition>(StringComparer.Ordinal);
        bool requireExplicit = false;
        RegisterProjectileSkillDefinitions(definitions, ref requireExplicit);
        return (definitions, requireExplicit);
    }

    internal static ProjectileSkillDefinition? ProjectileDefinition(string effect) =>
        ProjectileDefinitions.Definitions.GetValueOrDefault(effect);

    // Catalog metadata permits only the basic approximation. Providers can require explicit recipes;
    // that is an admission policy, never a choice of simulator or production entry point.
    internal static ProjectileSkillDefinition? CatalogProjectileDefinition(int effect)
    {
        if (!Skills.IsProjectile(effect) || Skills.ProjectileFor(effect) is not { } projectile) return null;
        return new(projectile.Id, (get, speed) =>
            GatewayProjectileDefaults.Resolve(projectile.Id, get, speed, Skills.IsAttack(effect)));
    }

    static ProjectileSkillDefinition? ResolveProjectileDefinition(int effect) =>
        ProjectileDefinition(Skills.EffectId(effect) ?? "")
        ?? (ProjectileDefinitions.RequireExplicitProfiles ? null : CatalogProjectileDefinition(effect));

    static ProjectilePresentation PresentationFor(ProjectileEntity p) =>
        (p.Use.Payload as ProjectileOffense)?.Presentation
        ?? (p.Use.Payload is ProjectileOffense shot && Skills.EffectId(shot.Effect) is { } effect
            ? ProjectileDefinition(effect)?.Presentation?.Invoke(p) : null)
        ?? new();
}
