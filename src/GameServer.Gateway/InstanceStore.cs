using GameServer.Core.Protocol.Instance;

/// <summary>
/// What the Gateway keeps per live area instance across connections: its ground items (issue #5) and monsters (#12). An instance is known by
/// Auth's id and creation time (Auth's ids restart at 1 when it restarts). Kept in memory, so it survives client crashes,
/// logouts and disconnects; it is wiped, like Auth's instance, once no connection has been in it for the lifetime
/// (<c>InstanceRegistry.Lifetime</c>). Agents/docs/protocol/13-items.md §5.
/// </summary>
sealed class InstanceStore(TimeProvider clock, TimeSpan lifetime)
{
    readonly Dictionary<(ulong Id, long Created), InstanceState> _instances = [];

    /// <summary>A connection enters the instance: its state, a fresh one for a new or expired instance.</summary>
    public InstanceState Enter(ulong id, long created)
    {
        lock (_instances)
        {
            var now = clock.GetUtcNow();
            foreach (var (key, _) in _instances.Where(e => e.Value.EmptySince is { } t && now - t >= lifetime).ToList())
                _instances.Remove(key);
            if (!_instances.TryGetValue((id, created), out var state)) _instances[(id, created)] = state = new InstanceState();
            state.Connections++;
            state.EmptySince = null;
            return state;
        }
    }

    /// <summary>A connection left the instance; the lifetime starts when the last one leaves.</summary>
    public void Leave(InstanceState state)
    {
        lock (_instances)
            if (--state.Connections == 0) state.EmptySince = clock.GetUtcNow();
    }
}

sealed class InstanceState
{
    public InstanceGround Ground { get; } = new();
    /// <summary>The living monsters as the last connection left them (issue #12), or null before the first connection
    /// placed the area's packs.</summary>
    public volatile List<SavedMonster>? Monsters;
    internal int Connections;
    internal DateTimeOffset? EmptySince;
}

/// <summary>An instance's items on the ground. Their object ids come from their own range, so they stay the same for every
/// connection and never meet the ids a connection gives its monsters and effects (0x3000 up).</summary>
sealed class InstanceGround
{
    public const uint FirstItemId = 0x100000;
    readonly Dictionary<uint, WorldItemObject> _items = [];
    uint _nextId = FirstItemId;

    public uint Add(WorldItemObject item)
    {
        lock (_items)
        {
            uint id = _nextId++;
            _items[id] = item;
            return id;
        }
    }

    public WorldItemObject? Get(uint id)
    {
        lock (_items) return _items.GetValueOrDefault(id);
    }

    /// <summary>Removes the item: only one caller gets it, so a pickup can never duplicate it.</summary>
    public WorldItemObject? Take(uint id)
    {
        lock (_items) return _items.Remove(id, out var item) ? item : null;
    }

    public IReadOnlyList<(uint Id, WorldItemObject Item)> All()
    {
        lock (_items) return [.. _items.Select(e => (e.Key, e.Value))];
    }
}

/// <summary>A living monster of an instance, as it was when the last connection left: re-created there with this life on
/// the next entry, idle until the player comes in range. Dead monsters are not kept, so they stay dead.</summary>
// shortcut: a /spawn_mob name is not kept (packs have none), add it when named monsters matter.
sealed record SavedMonster(int Variety, byte Level, MonsterRarity Rarity, bool Hostile, int X, int Y, uint Life)
{
    public static List<SavedMonster> Save(IEnumerable<SpawnedMonster> monsters, double now) =>
        [.. monsters.Where(m => !m.Dead && m.Variety is not null).Select(m =>
        {
            var (x, y) = m.Walk.Position(now);
            return new SavedMonster(m.Variety!.Row, m.Level, m.Rarity, m.Hostile, x, y, m.Life);
        })];
}
