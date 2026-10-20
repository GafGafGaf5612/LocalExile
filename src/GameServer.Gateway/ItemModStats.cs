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

    /// <summary>A worn item with what it needs: strength, dexterity, intelligence and character level.</summary>
    public sealed record Worn(uint Id, Item Item, int Strength, int Dexterity, int Intelligence, int Level);

    /// <summary>
    /// The worn items that are inactive (their requirements are not met), as in the game: an item stays equipped but its
    /// mods and socketed gems do nothing. An item is checked against the character's own attributes
    /// (<paramref name="baseAttributes"/>: class and passives) plus those of every <i>other</i> active item, never its
    /// own, and the character's level. Every item starts active and items are switched off until nothing changes, so a
    /// lost item can cascade, and two items can still qualify each other.
    /// </summary>
    public static HashSet<uint> Inactive(IReadOnlyList<Worn> worn, (int Str, int Dex, int Int) baseAttributes, int level,
        Func<Item, (int Str, int Dex, int Int)> attributesOf)
    {
        var inactive = new HashSet<uint>();
        var own = worn.ToDictionary(w => w.Id, w => attributesOf(w.Item));
        for (bool changed = true; changed;)
        {
            changed = false;
            var active = worn.Where(w => !inactive.Contains(w.Id)).ToList();
            var (str, dex, intel) = (baseAttributes.Str + active.Sum(w => own[w.Id].Str), baseAttributes.Dex + active.Sum(w => own[w.Id].Dex),
                baseAttributes.Int + active.Sum(w => own[w.Id].Int));
            foreach (var w in active)
            {
                var (s, d, i) = own[w.Id];
                if (level >= w.Level && str - s >= w.Strength && dex - d >= w.Dexterity && intel - i >= w.Intelligence) continue;
                inactive.Add(w.Id);
                changed = true;
                break;   // the totals changed: check the rest again against the new ones
            }
        }
        return inactive;
    }

    /// <summary>The attributes an item's global mods give (additional_strength, all attributes, the pairs …).</summary>
    public static (int Str, int Dex, int Int) Attributes(Item item, ModCatalog mods)
    {
        var stats = Sum([item], mods);
        return CharacterStats.Attributes(name => stats.GetValueOrDefault(name));
    }

    /// <summary>An item's level requirement: the higher of its base's drop level and 80% of its highest mod level.</summary>
    public static int LevelRequirement(Item item, int dropLevel, ModCatalog mods) =>
        Math.Max(dropLevel, item.Mods.Select(m => mods.ByHash(m.Hash)?.Level ?? 0).DefaultIfEmpty(0).Max() * 8 / 10);

    /// <summary>Two stat dictionaries added together (passives + items).</summary>
    public static Dictionary<uint, int> Plus(Dictionary<uint, int> a, Dictionary<uint, int> b)
    {
        var sum = new Dictionary<uint, int>(a);
        foreach (var (k, v) in b) sum[k] = sum.GetValueOrDefault(k) + v;
        return sum;
    }
}
