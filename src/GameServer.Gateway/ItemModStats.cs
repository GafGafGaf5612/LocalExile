using GameServer.Core.Protocol.Instance;

/// <summary>
/// What equipped items' mods do (Agents/docs/protocol/13-items.md §2c). Every mod list counts (implicit, explicit,
/// enchant, scourge, hidden), identified or not, as in the game. A stat that Stats.dat marks IsLocal changes the item it
/// is on (<see cref="Local"/>: a weapon's critical chance, attack speed, accuracy …); every other stat is the
/// character's and is added to the passives' stats (<see cref="Global"/>), so it reaches the pools and the stats the
/// client shows the same way.
/// </summary>
static class ItemModStats
{
    /// <summary>The equipment rows whose items count: body armour, the first weapon set, helmet, amulet, rings, gloves,
    /// boots and belt (Inventories.dat rows 1-10). The second weapon set (14, 15) and the flasks (11) do not.</summary>
    public static readonly byte[] EquipmentRows = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

    /// <summary>The summed values of the stats on these items' mods, by Stats.dat Id: global ones, or with
    /// <paramref name="local"/> the item's own.</summary>
    public static Dictionary<string, int> Sum(IEnumerable<Item> items, ModCatalog mods, bool local = false)
    {
        var sum = new Dictionary<string, int>();
        foreach (var mod in items.SelectMany(i => i.Mods))
        {
            if (mods.ByHash(mod.Hash) is not { } row) continue;
            for (int s = 0; s < row.Stats.Count && s < mod.Values.Count; s++)
                if (row.Stats[s].Local == local)
                    sum[row.Stats[s].Id] = sum.GetValueOrDefault(row.Stats[s].Id) + mod.Values[s];
        }
        return sum;
    }

    /// <summary>The character's stats from these equipped items, by client stat key (Stats.dat row + 1).</summary>
    public static Dictionary<uint, int> Global(IEnumerable<Item> equipped, ModCatalog mods, StatCatalog stats)
    {
        var result = new Dictionary<uint, int>();
        foreach (var (id, value) in Sum(equipped, mods))
            if (stats.Key(id) is uint key) result[key] = result.GetValueOrDefault(key) + value;
        return result;
    }

    /// <summary>One item's local stats by Stats.dat Id (local_critical_strike_chance_+% …).</summary>
    public static Dictionary<string, int> Local(Item item, ModCatalog mods) => Sum([item], mods, local: true);

    /// <summary>Two stat dictionaries added together (passives + items).</summary>
    public static Dictionary<uint, int> Plus(Dictionary<uint, int> a, Dictionary<uint, int> b)
    {
        var sum = new Dictionary<uint, int>(a);
        foreach (var (k, v) in b) sum[k] = sum.GetValueOrDefault(k) + v;
        return sum;
    }
}
