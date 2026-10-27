namespace GameServer.Core.Projectiles;

public readonly record struct ProjectileActorKey(uint Id, int Generation = 0);

public readonly record struct ProjectileVector(double X, double Y)
{
    public static ProjectileVector operator +(ProjectileVector a, ProjectileVector b) => new(a.X + b.X, a.Y + b.Y);
    public static ProjectileVector operator -(ProjectileVector a, ProjectileVector b) => new(a.X - b.X, a.Y - b.Y);
    public static ProjectileVector operator *(ProjectileVector a, double value) => new(a.X * value, a.Y * value);
    public double Length => Math.Sqrt(X * X + Y * Y);
    public double Dot(ProjectileVector b) => X * b.X + Y * b.Y;
    public ProjectileVector Unit => Length > 0 ? this * (1 / Length) : new(1, 0);
    public ProjectileVector Rotate(double angle) => new(X * Math.Cos(angle) - Y * Math.Sin(angle),
        X * Math.Sin(angle) + Y * Math.Cos(angle));
    public override string ToString() => $"({X}, {Y})";
}

public sealed record ProjectileActor(ProjectileActorKey Key, int Team, Func<long, ProjectileVector> Position, double Radius);
public readonly record struct TerrainContact(double Fraction, ProjectileVector Normal);
public enum ProjectileHitResult { Miss, Blocked, Hit }
public enum ProjectileHitScope { Emission, Projectile }
public enum ProjectileRepeat { Once, Interval, Window }
public sealed record ProjectileHitRule(ProjectileHitScope Scope = ProjectileHitScope.Emission,
    ProjectileRepeat Repeat = ProjectileRepeat.Once, long Period = 0);
public enum ProjectileEndReason { Enemy, Terrain, Distance, Lifetime, Landing, Consumed, Teardown, Fork, Split, Returned }
public enum ProjectileEventKind { Spawn, Redirect, End, Area, Hit }
public sealed record ProjectileAreaRecipe(string Stage, double Radius, int Count = 1, double Offset = 0,
    double Multiplier = 1, ProjectileHitScope Scope = ProjectileHitScope.Projectile);
public sealed record ProjectilePulseRecipe(string Stage, double Radius, long Interval);
public sealed record ProjectileEmissionRecipe(ProjectileProfile Child, bool Radial = false, long Interval = 0,
    bool RandomDirections = false);
public sealed record ProjectileFormRecipe(double Distance, double SpeedMultiplier, int Pierce,
    double DamageMultiplier = 1);
public sealed record ProjectileSplitFormRecipe(double SpeedMultiplier, int Pierce);
public sealed record ProjectileTargetedRecipe(string Stage, int Count, double Radius);

/// <summary>Resolved stage parameters. Counts and restrictions are resolved before a use is released.</summary>
public sealed record ProjectileProfile(string Id)
{
    public int Count { get; init; } = 1;
    public double SpreadRadians { get; init; } = Math.PI / 6;
    /// <summary>Sample each launch direction within the spread instead of placing an evenly spaced fan.</summary>
    public bool RandomSpread { get; init; }
    /// <summary>Zero keeps a fixed fan; positive values widen it as aim approaches the emitter.
    /// The host adopts/calibrates the reference distance, independently of projectile count.</summary>
    public double AimSpreadDistance { get; init; }
    public double Speed { get; init; } = 100;
    public double Range { get; init; } = 120;
    public double Radius { get; init; } = 4;
    public long Lifetime { get; init; } = 1200000;
    public long SequentialDelay { get; init; }
    public bool DirectDamage { get; init; } = true;
    public int Pierce { get; init; }
    public int Fork { get; init; }
    public int Chain { get; init; }
    public int Split { get; init; }
    public double ChainRange { get; init; } = 60;
    public bool Returns { get; init; }
    public bool CannotPierce { get; init; }
    public bool CannotFork { get; init; }
    public bool CannotChain { get; init; }
    public bool CannotSplit { get; init; }
    public bool CannotReturn { get; init; }
    public int TerrainBounces { get; init; }
    public ProjectileHitRule HitRule { get; init; } = new();
    public ProjectileAreaRecipe? EndArea { get; init; }
    public ProjectileAreaRecipe? ContactArea { get; init; }
    public ProjectileEmissionRecipe? EndEmission { get; init; }
    public ProjectileEmissionRecipe? TravelEmission { get; init; }
    public ProjectilePulseRecipe? Pulse { get; init; }
    public bool Lobbed { get; init; }
    public int GroundChains { get; init; }
    public double GroundBounceDistance { get; init; } = 20;
    public ProjectileCountConsumer CountConsumer { get; init; }
    public ProjectileFormRecipe? FormChange { get; init; }
    public ProjectileSplitFormRecipe? SplitForm { get; init; }
    public bool SplitOnTerrain { get; init; }
    /// <summary>Fill this skill's split count even when there are fewer available target actors.</summary>
    public bool SplitWithoutTargets { get; init; }
    public ProjectileTargetedRecipe? ContactTargets { get; init; }
    public long WanderInterval { get; init; }
}

public sealed record ProjectileUse(ProjectileProfile Profile, ProjectileActorKey Owner, ProjectileActorKey Emitter,
    int Team, ProjectileVector Origin, ProjectileVector Aim)
{
    public ProjectileActorKey? ReturnAnchor { get; init; }
    public object? Payload { get; init; }
    /// <summary>Optional initial-projectile snapshots; continuation children inherit their parent's selected payload.</summary>
    public IReadOnlyList<object?>? ProjectilePayloads { get; init; }
    public int Seed { get; init; } = 1;
}

public sealed class ProjectileEntity
{
    internal ProjectileEntity(long id, long useId, long emissionId, long parent, ProjectileUse use,
        ProjectileVector position, ProjectileVector direction, long at)
    {
        Id = id; UseId = useId; EmissionId = emissionId; ParentId = parent; Use = use;
        Position = position; Direction = direction.Unit; CreatedAt = at;
        Remaining = use.Profile.Range; Pierce = use.Profile.Pierce;
        Fork = use.Profile.Fork; Chain = use.Profile.Chain; Split = use.Profile.Split;
        TerrainBounces = use.Profile.TerrainBounces;
        GroundChains = use.Profile.GroundChains;
        int seed = unchecked(use.Seed * 397 ^ (int)id);
        Random = new(seed);
        RandomSource = new(seed ^ 0x2d53a1b7);
        RewardRandomSource = new(seed ^ 0x5e418ac3);
        ReactionRandomSource = new(seed ^ 0x1a736d49);
        Speed = use.Profile.Speed;
        ExpiresAt = at + use.Profile.Lifetime;
    }
    public long Id { get; }
    public long UseId { get; }
    public long EmissionId { get; }
    public long ParentId { get; }
    public ProjectileUse Use { get; }
    public ProjectileProfile Profile => Use.Profile;
    public ProjectileVector Position { get; internal set; }
    public ProjectileVector Direction { get; internal set; }
    public long CreatedAt { get; }
    public long ExpiresAt { get; internal set; }
    public int Phase { get; internal set; }
    public double Remaining { get; internal set; }
    public double Distance { get; internal set; }
    public double Speed { get; internal set; }
    public int Form { get; internal set; }
    public int GroundBounces { get; internal set; }
    public double DamageMultiplier { get; internal set; } = 1;
    internal int Pierce, Fork, Chain, Split, TerrainBounces;
    internal int GroundChains;
    internal long ChildEmission;
    internal int EmissionNumber;
    internal readonly Random Random;
    public Random RandomSource { get; }
    public Random RewardRandomSource { get; }
    public Random ReactionRandomSource { get; }
    internal readonly HashSet<ProjectileActorKey> Contacts = [];
    internal readonly HashSet<long> Interactions = [];
}

public sealed record ProjectileDamage(ProjectileEntity Projectile, ProjectileActorKey Target, long At, string Stage,
    ProjectileVector Position, double Multiplier = 1)
{
    public int Phase { get; } = Projectile.Phase;
}

public sealed record ProjectileEvent(ProjectileEventKind Kind, ProjectileEntity Projectile, long At,
    ProjectileVector Position, ProjectileEndReason? Reason = null, double Radius = 0, string? Stage = null);

/// <summary>All methods run synchronously under the host's combat lock. Hit commits gameplay before returning.
/// Publish records presentation intents; it must not perform network IO or delay simulation.</summary>
public interface IProjectileWorld
{
    IReadOnlyList<ProjectileActor> Actors(long from, long until);
    TerrainContact? TraceTerrain(ProjectileVector from, ProjectileVector to);
    ProjectileHitResult Hit(ProjectileDamage damage);
    void Publish(ProjectileEvent value);
}
