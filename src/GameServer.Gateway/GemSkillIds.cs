/// <summary>
/// The skill ids of one character's socketed gems. The client binds skill bar keys by skill id, so each gem keeps its id
/// while it stays socketed, and other gems coming or going never change it: a gem that stays in its socket keeps its id,
/// a gem moved to another socket keeps it too (matched by its skill), and a new gem takes the lowest free id.
/// </summary>
sealed class GemSkillIds
{
    /// <summary>A socketed gem: the item it is in, its socket, its GrantedEffects row.</summary>
    public readonly record struct Gem(uint Host, int Socket, int Effect);

    Dictionary<(uint Host, int Socket), (ushort Id, int Effect)> _byPlace = [];
    readonly Lock _lock = new();

    /// <summary>The ids of <paramref name="gems"/>, in their order, from <paramref name="pool"/> (the ids gem skills may
    /// use, in the order to hand them out); null for a gem when the pool has no free id left.</summary>
    public IReadOnlyList<ushort?> Assign(IReadOnlyList<Gem> gems, IReadOnlyList<ushort> pool)
    {
        lock (_lock)
        {
            var ids = new ushort?[gems.Count];
            var used = new HashSet<ushort>();
            // A gem in the same socket with the same skill keeps its id.
            for (int i = 0; i < gems.Count; i++)
                if (_byPlace.TryGetValue((gems[i].Host, gems[i].Socket), out var had) && had.Effect == gems[i].Effect && used.Add(had.Id))
                    ids[i] = had.Id;
            // A gem moved to another socket keeps the id of the same skill's gem that left its place.
            var left = _byPlace.Where(p => !used.Contains(p.Value.Id)).Select(p => p.Value).ToList();
            for (int i = 0; i < gems.Count; i++)
                if (ids[i] is null && left.FindIndex(l => l.Effect == gems[i].Effect) is var at and >= 0)
                {
                    ids[i] = left[at].Id;
                    used.Add(left[at].Id);
                    left.RemoveAt(at);
                }
            // A new gem takes the lowest free id.
            for (int i = 0; i < gems.Count; i++)
                if (ids[i] is null && pool.FirstOrDefault(id => !used.Contains(id)) is var free and not 0)
                {
                    ids[i] = free;
                    used.Add(free);
                }
            _byPlace = [];
            for (int i = 0; i < gems.Count; i++)
                if (ids[i] is ushort id) _byPlace[(gems[i].Host, gems[i].Socket)] = (id, gems[i].Effect);
            return ids;
        }
    }
}
