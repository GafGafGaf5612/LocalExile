namespace GameServer.Core.World;

/// <summary>
/// The live area instances of the official-client servers (Auth hands out tickets for them, Gateway serves them).
/// Rules (as on the official servers):
/// <list type="bullet">
/// <item>A town has at most one live instance; entering a town always joins it.</item>
/// <item>Other areas can have any number of instances. A plain area transition click joins the character's previous live instance of
/// that area, else creates one; the instance chooser (Ctrl+click on an area transition) lists them and can create a new one.</item>
/// <item>An instance is kept for <see cref="Lifetime"/> (default 6 minutes, within the observed 5–7) after its last player
/// leaves, then it is gone; a town is recreated on the next entry.</item>
/// </list>
/// Thread-safe. Time comes from <see cref="TimeProvider"/> so tests can move it.
/// </summary>
public sealed class InstanceRegistry(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _lock = new();
    private readonly Dictionary<ulong, AreaInstanceState> _instances = [];
    private readonly Dictionary<(string Character, ushort Area), ulong> _last = [];
    private ulong _nextId = 1;

    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(6);

    /// <summary>Areas that are towns (WorldAreas.IsTown), by HASH16. Defaults to the towns known so far.</summary>
    public IReadOnlySet<ushort> Towns { get; init; } = new HashSet<ushort> { 0x235c /* 1_1_town Lioneye's Watch */ };

    public bool IsTown(ushort area) => Towns.Contains(area);

    /// <summary>Seed for a new non-town instance. Default: random. The server cannot generate layouts itself yet, so the
    /// dev servers pin it (one dumped layout per area); towns always use 1.</summary>
    public Func<ushort, uint>? SeedFor { get; init; }

    /// <summary>The instance a character enters for <paramref name="area"/> without choosing: the town's single instance,
    /// else the character's previous live instance of that area, else a new one.</summary>
    public AreaInstanceState Enter(ushort area, string character)
    {
        lock (_lock)
        {
            Prune();
            if (IsTown(area)) return Join(TownLocked(area), character);
            if (_last.TryGetValue((character, area), out ulong id) && _instances.TryGetValue(id, out var previous))
                return Join(previous, character);
            return Join(CreateLocked(area), character);
        }
    }

    /// <summary>Joins a specific live instance (chooser), or null if it expired. Towns ignore the id (there is only one).</summary>
    public AreaInstanceState? EnterExisting(ulong id, string character)
    {
        lock (_lock)
        {
            Prune();
            if (!_instances.TryGetValue(id, out var inst)) return null;
            return Join(inst, character);
        }
    }

    /// <summary>Creates and joins a new instance (chooser "new instance"). For a town this joins the existing one.
    /// <paramref name="seed"/> ≠ 0 is the new instance's seed instead of <see cref="SeedFor"/>'s (dev: capture tours).</summary>
    public AreaInstanceState EnterNew(ushort area, string character, uint seed = 0)
    {
        lock (_lock)
        {
            Prune();
            return Join(IsTown(area) ? TownLocked(area) : CreateLocked(area, seed), character);
        }
    }

    public void Leave(ulong id, string character)
    {
        lock (_lock)
        {
            if (!_instances.TryGetValue(id, out var inst)) return;
            inst.Players.Remove(character);
            if (inst.Players.Count == 0) inst.EmptySince = _clock.GetUtcNow();
        }
    }

    /// <summary>Every player leaves every instance, which starts the expiry of the ones they were in: a Gateway that
    /// restarted lost its connections without sending "leave". Returns how many players were removed.</summary>
    public int LeaveAll()
    {
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            int removed = 0;
            foreach (var inst in _instances.Values.Where(i => i.Players.Count > 0))
            {
                removed += inst.Players.Count;
                inst.Players.Clear();
                inst.EmptySince = now;
            }
            return removed;
        }
    }

    /// <summary>Live instances of an area, newest first.</summary>
    public IReadOnlyList<AreaInstanceState> List(ushort area)
    {
        lock (_lock)
        {
            Prune();
            return [.. _instances.Values.Where(i => i.AreaHash == area).OrderByDescending(i => i.Created)];
        }
    }

    /// <summary>How long until an instance expires: the full <see cref="Lifetime"/> while someone is in it.</summary>
    public TimeSpan TimeLeft(AreaInstanceState inst)
    {
        lock (_lock)
        {
            if (inst.EmptySince is not { } t) return Lifetime;
            var left = Lifetime - (_clock.GetUtcNow() - t);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    public AreaInstanceState? Get(ulong id)
    {
        lock (_lock) return _instances.GetValueOrDefault(id);
    }

    private AreaInstanceState TownLocked(ushort area) =>
        _instances.Values.FirstOrDefault(i => i.AreaHash == area) ?? CreateLocked(area);

    private AreaInstanceState CreateLocked(ushort area, uint requested = 0)
    {
        bool town = IsTown(area);
        // Towns use seed 1 (their layout is fixed anyway); other areas the requested seed, else SeedFor's, else random.
        uint seed = town ? 1u : requested != 0 ? requested : SeedFor?.Invoke(area) ?? (uint)Random.Shared.Next(1, int.MaxValue);
        var inst = new AreaInstanceState(_nextId++, area, town, seed, _clock.GetUtcNow());
        _instances[inst.Id] = inst;
        return inst;
    }

    private AreaInstanceState Join(AreaInstanceState inst, string character)
    {
        // A character is in one instance at a time.
        foreach (var other in _instances.Values)
            if (other != inst && other.Players.Remove(character) && other.Players.Count == 0) other.EmptySince = _clock.GetUtcNow();
        inst.Players.Add(character);
        inst.EmptySince = null;
        _last[(character, inst.AreaHash)] = inst.Id;
        return inst;
    }

    private void Prune()
    {
        var now = _clock.GetUtcNow();
        foreach (var inst in _instances.Values.Where(i => i.EmptySince is { } t && now - t >= Lifetime).ToList())
        {
            _instances.Remove(inst.Id);
            foreach (var key in _last.Where(kv => kv.Value == inst.Id).Select(kv => kv.Key).ToList()) _last.Remove(key);
        }
    }
}

public sealed class AreaInstanceState(ulong id, ushort areaHash, bool isTown, uint seed, DateTimeOffset created)
{
    public ulong Id { get; } = id;
    public ushort AreaHash { get; } = areaHash;
    public bool IsTown { get; } = isTown;
    public uint Seed { get; } = seed;
    public DateTimeOffset Created { get; } = created;
    /// <summary>When the last player left (expiry starts), or null while someone is in it.</summary>
    public DateTimeOffset? EmptySince { get; internal set; }
    public HashSet<string> Players { get; } = [];
}
