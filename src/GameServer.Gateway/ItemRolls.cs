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
    /// rarity, only get the rarity. <paramref name="shape"/> overrides the affix count and the most of each kind.
    /// Fractured mods stay, first, and count towards the affixes.</summary>
    public Item Roll(Item item, ItemRarity rarity, Random rng, (int Count, int MaxEach)? shape = null,
        ModCatalog.Mod? guaranteed = null, int? maxModLevel = null, bool respectMetamods = false) =>
        RollCore(item, rarity, rng, shape, guaranteed, maxModLevel, respectMetamods, null, null)!;

    /// <summary>A Harvest reforge that must include one modifier with this Tags.dat tag (fire, life, attack �?�). Null when
    /// the base has no room for one at this item level.</summary>
    public Item? RollRequiredTag(Item item, ItemRarity rarity, Random rng, string tag) =>
        rarity == ItemRarity.Rare ? RollCore(item, rarity, rng, null, null, null, false, tag, null) : null;

    /// <summary>A Harvest "more/less likely to receive the same modifier types" reforge. <paramref name="percent"/> is the
    /// client parameter (900 = ten times the chance of a tag the item already has, -90 = a tenth); a local, documented
    /// weighting of the ordinary pool, not an official probability.</summary>
    public Item? RollAffinity(Item item, ItemRarity rarity, Random rng, int percent) =>
        rarity == ItemRarity.Rare ? RollCore(item, rarity, rng, null, null, null, false, null, percent) : null;

    Item? RollCore(Item item, ItemRarity rarity, Random rng, (int Count, int MaxEach)? shape,
        ModCatalog.Mod? guaranteed, int? maxModLevel, bool respectMetamods, string? requiredTag, int? affinityPercent)
    {
        var fractured = item.FracturedIndexes();
        var rules = new MetacraftRules(item, catalog.Mods);
        var kept = item.Mods.Where((m, i) => m.Section != ModSection.Explicit || fractured.Contains(i) || respectMetamods && rules.Protected(i)).ToList();
        var result = item with { Rarity = rarity, Mods = kept, NameWords = [] };
        if (catalog.ByHash(item.TypeHash) is not { } t || !t.Components.Contains("Mods") || rarity is not (ItemRarity.Magic or ItemRarity.Rare))
            return result;
        var (count, maxEach) = shape ?? (rarity == ItemRarity.Magic
            ? (rng.Next(1, 3), 1)
            : (rng.Next(12) switch { < 7 => 4, < 11 => 5, _ => 6 }, 3));
        var generation = new MetacraftRules(item with { Mods = kept }, catalog.Mods);
        var pool = Pool(t, Math.Min(item.ItemLevel, maxModLevel ?? item.ItemLevel))
            .Where(p => !respectMetamods || generation.Allows(p.Mod)).ToList();
        var chosen = kept.Where(m => m.Section == ModSection.Explicit).Select(m => catalog.Mods.ByHash(m.Hash)).OfType<ModCatalog.Mod>().ToList();
        int keptCount = chosen.Count;
        if (guaranteed is not null) chosen.Add(guaranteed);
        bool Fits(ModCatalog.Mod mod, int max) => chosen.Count(c => c.GenerationType == mod.GenerationType) < max &&
            !chosen.Any(c => c.Hash == mod.Hash || c.Families.Intersect(mod.Families).Any());
        if (requiredTag is not null)
        {
            var required = pool.Where(p => p.Mod.Tags.Contains(requiredTag) && Fits(p.Mod, maxEach)).ToList();
            if (required.Count == 0) return null;
            chosen.Add(Pick(required, rng));
        }
        // The affinity recipes weight the rest of the pool by whether it shares a tag with the item's current affixes.
        var affinityTags = affinityPercent is null ? null
            : kept.Where(m => m.Section == ModSection.Explicit).Select(m => catalog.Mods.ByHash(m.Hash)).OfType<ModCatalog.Mod>()
                .SelectMany(m => m.Tags).ToHashSet();
        while (chosen.Count < count)
        {
            var allowed = pool.Where(p => Fits(p.Mod, maxEach)).ToList();
            if (affinityPercent is { } percent && affinityTags is { Count: > 0 })
                allowed = [.. allowed.Select(p => (p.Mod, Weight: affinityTags.Overlaps(p.Mod.Tags)
                    ? Math.Max(1, p.Weight * (100 + percent) / 100) : p.Weight))];
            if (allowed.Count == 0 || allowed.All(p => p.Weight <= 0)) break;
            chosen.Add(Pick(allowed, rng));
        }
        // Prefixes first, as the tooltip lists them.
        var explicits = chosen.Skip(keptCount).OrderBy(m => m.GenerationType).Select(m => m.Roll(ModSection.Explicit, rng));
        return result with { Mods = [.. kept, .. explicits], NameWords = rarity == ItemRarity.Rare ? RareName(t, rng) : [] };
    }

    /// <summary>Divine Orb: every explicit mod keeps its place and tier and rerolls only its values (a new tier would
    /// change the mod, and the client reorders the tooltip by mod). Null when the item has no explicit mods.</summary>
    public Item? Reforge(Item item, Random rng)
    {
        var rules = new MetacraftRules(item, catalog.Mods);
        bool Mutable(int i) => item.Mods[i].Section == ModSection.Explicit && !rules.Protected(i) && catalog.Mods.ByHash(item.Mods[i].Hash) is not null;
        if (!Enumerable.Range(0, item.Mods.Count).Any(Mutable)) return null;
        return item with { Mods = [.. item.Mods.Select((m, i) => Mutable(i) ? catalog.Mods.ByHash(m.Hash)!.Roll(ModSection.Explicit, rng) : m)] };
    }

    public Item? Scour(Item item)
    {
        if (item.Rarity is not (ItemRarity.Magic or ItemRarity.Rare)) return null;
        var rules = new MetacraftRules(item, catalog.Mods);
        var kept = item.Mods.Where((m, i) => m.Section != ModSection.Explicit || rules.Protected(i)).ToArray();
        int count = kept.Count(m => m.Section == ModSection.Explicit);
        var rarity = count == 0 ? ItemRarity.Normal : count == 1 ? ItemRarity.Magic : ItemRarity.Rare;
        if (kept.Length == item.Mods.Count && rarity == item.Rarity) return null;
        return item with { Mods = kept, Rarity = rarity, Identified = true, NameWords = rarity == ItemRarity.Rare ? item.NameWords : [] };
    }

    public Item? Annul(Item item, Random rng)
    {
        if (item.Rarity is not (ItemRarity.Magic or ItemRarity.Rare)) return null;
        var rules = new MetacraftRules(item, catalog.Mods);
        var candidates = Enumerable.Range(0, item.Mods.Count).Where(i => item.Mods[i].Section == ModSection.Explicit && !rules.Protected(i)
            && (catalog.Mods.ByHash(item.Mods[i].Hash) is not { } mod || rules.Allows(mod))).ToArray();
        if (candidates.Length == 0) return null;
        int removed = candidates[rng.Next(candidates.Length)];
        return item with { Mods = [.. item.Mods.Where((_, i) => i != removed)] };
    }

    /// <summary>Blessed Orb: every implicit mod keeps its place and tier and rerolls only its values within the tier, as
    /// <see cref="Reforge"/> does for explicits; explicit, enchant and other mods stay. Null when the item has no implicit
    /// mod (nothing is spent).</summary>
    public Item? Bless(Item item, Random rng) => !item.Mods.Any(m => m.Section == ModSection.Implicit) ? null
        : item with { Mods = [.. item.Mods.Select(m => m.Section == ModSection.Implicit && catalog.Mods.ByHash(m.Hash) is { } mod
            ? mod.Roll(ModSection.Implicit, rng) : m)] };

    /// <summary>One more explicit affix, drawn by weight from the base's pool at its item level: no family already on the
    /// item and at most <paramref name="maxEach"/> prefixes / suffixes. The old mods stay; null when nothing fits.</summary>
    public Item? AddAffix(Item item, int maxEach, Random rng, bool respectMetamods = true) =>
        AddAffixCore(item, maxEach, rng, respectMetamods, requiredTag: null);

    /// <summary>A Harvest augment's added modifier: one with this Tags.dat tag, as <see cref="AddAffix"/> otherwise.</summary>
    public Item? AddTaggedAffix(Item item, int maxEach, Random rng, string tag, bool respectMetamods = true) =>
        AddAffixCore(item, maxEach, rng, respectMetamods, tag);

    Item? AddAffixCore(Item item, int maxEach, Random rng, bool respectMetamods, string? requiredTag)
    {
        if (catalog.ByHash(item.TypeHash) is not { } t) return null;
        var have = item.Mods.Where(m => m.Section == ModSection.Explicit).Select(m => catalog.Mods.ByHash(m.Hash)).OfType<ModCatalog.Mod>().ToList();
        var rules = new MetacraftRules(item, catalog.Mods);
        var allowed = Pool(t, item.ItemLevel).Where(p => (!respectMetamods || rules.Allows(p.Mod)) && have.Count(h => h.GenerationType == p.Mod.GenerationType) < maxEach &&
            !have.Any(h => h.Families.Intersect(p.Mod.Families).Any()) && (requiredTag is null || p.Mod.Tags.Contains(requiredTag))).ToList();
        if (allowed.Count == 0) return null;
        var added = Pick(allowed, rng);
        // Prefixes before suffixes, as Roll orders them.
        var mods = item.Mods.ToList();
        int at = added.GenerationType == Prefix
            ? mods.FindLastIndex(m => m.Section == ModSection.Explicit && catalog.Mods.ByHash(m.Hash)?.GenerationType == Prefix) + 1
            : mods.Count;
        if (at == 0) at = mods.FindIndex(m => m.Section == ModSection.Explicit) is int e and >= 0 ? e : mods.Count;
        // Never before the fractured mods, which stay the first explicits.
        if (item.FracturedIndexes() is { Count: > 0 } f) at = Math.Max(at, f.Max() + 1);
        mods.Insert(at, added.Roll(ModSection.Explicit, rng));
        return item with { Mods = mods };
    }

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

    /// <summary>Vaal Orb on a rare item: <paramref name="count"/> random explicit affixes leave and as many new ones come
    /// (<see cref="AddAffix"/>, at most three of each kind); the others stay.</summary>
    public Item ReplaceAffixes(Item item, int count, Random rng)
    {
        var fractured = item.FracturedIndexes();
        var explicits = item.Mods.Select((m, i) => (m, i)).Where(p => p.m.Section == ModSection.Explicit && !fractured.Contains(p.i)).Select(p => p.i)
            .OrderBy(_ => rng.Next()).Take(count).ToHashSet();
        var result = item with { Mods = [.. item.Mods.Where((_, i) => !explicits.Contains(i))] };
        for (int n = 0; n < explicits.Count && AddAffix(result, 3, rng, respectMetamods: false) is { } added; n++) result = added;
        return result;
    }

    /// <summary>A rare name for this item's base; empty when the base is unknown.</summary>
    public IReadOnlyList<uint> RareName(Item item, Random rng) => catalog.ByHash(item.TypeHash) is { } t ? RareName(t, rng) : [];

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
