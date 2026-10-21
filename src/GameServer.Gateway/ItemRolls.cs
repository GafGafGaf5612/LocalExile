using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// Magic and rare items (Agents/docs/protocol/13-items.md §2b). Affixes come from Mods.dat: the base's ModDomain,
/// GenerationType 1 (prefix) or 2 (suffix), Level at most the item level, not essence-only, a weight above 0 for the
/// item's tags (<see cref="ModCatalog.Mod.WeightFor"/>) and no family already on the item. Each affix is drawn by weight
/// from every mod still allowed, so prefixes and suffixes compete. Magic: 1-2 affixes, at most one of each. Rare: 4-6,
/// at most three of each, and a name from Words.dat word lists 1 (prefix) and 2 (suffix), drawn by the same tag weights.
/// </summary>
sealed class ItemRolls(ItemCatalog catalog, string folder = "data")
{
    public const int Prefix = 1, Suffix = 2;

    /// <summary>A Words.dat row: the key the client looks it up by (HASH32), its text and spawn weights.</summary>
    public sealed record Word(uint Key, string Text, int List, IReadOnlyList<(string Tag, int Weight)> SpawnWeights);

    private readonly Lazy<List<Word>> _words = new(() => LoadWords(folder));
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int Row, int Level), IReadOnlyList<(ModCatalog.Mod, int)>> _pools = new();

    /// <summary>The affixes that can roll on this base at this item level.</summary>
    public IReadOnlyList<(ModCatalog.Mod Mod, int Weight)> Pool(ItemCatalog.BaseType t, int itemLevel) =>
        _pools.GetOrAdd((t.Row, itemLevel), _ => [.. catalog.Mods.All
            .Where(m => m.Domain == t.ModDomain && m.GenerationType is Prefix or Suffix && m.Level <= itemLevel && !m.EssenceOnly)
            .Select(m => (m, m.WeightFor(t.Tags))).Where(p => p.Item2 > 0)]);

    /// <summary>The item with this rarity and freshly rolled affixes (its implicits and other lists kept, earlier
    /// explicits replaced); a rare item also gets a name. Types without a Mods component, or normal / unique
    /// rarity, only get the rarity.</summary>
    public Item Roll(Item item, ItemRarity rarity, Random rng)
    {
        var kept = item.Mods.Where(m => m.Section != ModSection.Explicit).ToList();
        var result = item with { Rarity = rarity, Mods = kept, NameWords = [] };
        if (catalog.ByHash(item.TypeHash) is not { } t || !t.Components.Contains("Mods") || rarity is not (ItemRarity.Magic or ItemRarity.Rare))
            return result;
        var (count, maxEach) = rarity == ItemRarity.Magic
            ? (rng.Next(1, 3), 1)
            : (rng.Next(12) switch { < 7 => 4, < 11 => 5, _ => 6 }, 3);
        var pool = Pool(t, item.ItemLevel).ToList();
        var chosen = new List<ModCatalog.Mod>();
        while (chosen.Count < count)
        {
            var allowed = pool.Where(p => chosen.Count(c => c.GenerationType == p.Mod.GenerationType) < maxEach &&
                !chosen.Any(c => c.Families.Intersect(p.Mod.Families).Any())).ToList();
            if (allowed.Count == 0) break;
            chosen.Add(Pick(allowed, rng));
        }
        // Prefixes first, as the tooltip lists them.
        var explicits = chosen.OrderBy(m => m.GenerationType).Select(m => m.Roll(ModSection.Explicit, rng));
        return result with { Mods = [.. kept, .. explicits], NameWords = rarity == ItemRarity.Rare ? RareName(t, rng) : [] };
    }

    /// <summary>Divine Orb: every explicit mod keeps its place and tier and rerolls only its values (a new tier would
    /// change the mod, and the client reorders the tooltip by mod). Null when the item has no explicit mods.</summary>
    public Item? Reforge(Item item, Random rng) => !item.Mods.Any(m => m.Section == ModSection.Explicit) ? null
        : item with { Mods = [.. item.Mods.Select(m => m.Section == ModSection.Explicit && catalog.Mods.ByHash(m.Hash) is { } mod
            ? mod.Roll(ModSection.Explicit, rng) : m)] };

    /// <summary>Two Words.dat keys (prefix from list 1, suffix from list 2) for the base's tags; empty without Words.dat.</summary>
    public IReadOnlyList<uint> RareName(ItemCatalog.BaseType t, Random rng)
    {
        uint? Draw(int list)
        {
            var words = _words.Value.Where(w => w.List == list)
                .Select(w => (w, w.SpawnWeights.FirstOrDefault(s => t.Tags.Contains(s.Tag)).Weight)).Where(p => p.Weight > 0).ToList();
            return words.Count == 0 ? null : Pick(words, rng).Key;
        }
        return Draw(1) is uint a && Draw(2) is uint b ? [a, b] : [];
    }

    /// <summary>"Dire Bane" from the keys (for logs and /mods).</summary>
    public string NameOf(IReadOnlyList<uint> keys) => string.Concat(keys.Select(k => _words.Value.FirstOrDefault(w => w.Key == k)?.Text ?? $"[{k:x8}]"));

    static T Pick<T>(IReadOnlyList<(T Item, int Weight)> choices, Random rng)
    {
        long pick = rng.NextInt64(choices.Sum(c => (long)c.Weight));
        foreach (var (item, weight) in choices)
        {
            if (pick < weight) return item;
            pick -= weight;
        }
        return choices[^1].Item;
    }

    static List<Word> LoadWords(string folder)
    {
        string path = Path.Combine(folder, "tables", "Words.json"), tagsPath = Path.Combine(folder, "tables", "Tags.json");
        if (!File.Exists(path) || !File.Exists(tagsPath)) return [];
        List<string> tags;
        using (var t = JsonDocument.Parse(File.ReadAllBytes(tagsPath)))
            tags = [.. t.RootElement.EnumerateArray().Select(r => r.GetProperty("Id").GetString() ?? "")];
        using var d = JsonDocument.Parse(File.ReadAllBytes(path));
        return [.. d.RootElement.EnumerateArray().Select(r => new Word(r.GetProperty("HASH32").GetUInt32(), r.GetProperty("Text").GetString() ?? "",
            r.GetProperty("Wordlist").GetInt32(),
            [.. r.GetProperty("SpawnWeight_Tags").EnumerateArray().Zip(r.GetProperty("SpawnWeight_Values").EnumerateArray(),
                (k, v) => (k.GetInt32() < tags.Count ? tags[k.GetInt32()] : "", v.GetInt32()))]))];
    }
}
