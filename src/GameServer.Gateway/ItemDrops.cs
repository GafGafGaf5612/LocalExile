using GameServer.Core.Protocol.Instance;

/// <summary>
/// What a dying monster drops: a first, basic loot table of our own (the game's drop pools, item rarity and mods come
/// later; Agents/docs/protocol/15-progression.md §3). How many items: normal 1 at 35%, magic 1–2 (one sure), rare 2–4,
/// unique 4–6. Each item is currency (45%: the common orbs and scrolls, weighted), a life or mana flask (10%) or a piece
/// of equipment (45%: any weapon, armour or jewellery base whose DropLevel is at most the monster's level), normal, at the
/// monster's level as its item level.
/// </summary>
sealed class ItemDrops(ItemCatalog catalog)
{
    // (BaseItemTypes Id, weight): Scroll of Wisdom and Portal Scroll most, then shards' worth of basic orbs.
    static readonly (string Id, int Weight)[] Currency =
    [
        ("Metadata/Items/Currency/CurrencyIdentification", 400), ("Metadata/Items/Currency/CurrencyPortal", 200),
        ("Metadata/Items/Currency/CurrencyArmourQuality", 80), ("Metadata/Items/Currency/CurrencyWeaponQuality", 60),
        ("Metadata/Items/Currency/CurrencyUpgradeToMagic", 100), ("Metadata/Items/Currency/CurrencyAddModToMagic", 60),
        ("Metadata/Items/Currency/CurrencyRerollMagic", 40), ("Metadata/Items/Currency/CurrencyFlaskQuality", 30),
        ("Metadata/Items/Currency/CurrencyRerollSocketColours", 25), ("Metadata/Items/Currency/CurrencyRerollSocketNumbers", 10),
        ("Metadata/Items/Currency/CurrencyRerollSocketLinks", 5), ("Metadata/Items/Currency/CurrencyUpgradeToRare", 8),
        ("Metadata/Items/Currency/CurrencyUpgradeRandomly", 8), ("Metadata/Items/Currency/CurrencyPassiveRefund", 6),
        ("Metadata/Items/Currency/CurrencyRerollRare", 3),
    ];

    // Equipment classes that drop (ItemClasses Id).
    static readonly HashSet<string> EquipmentClasses =
    [
        "One Hand Sword", "Thrusting One Hand Sword", "One Hand Axe", "One Hand Mace", "Sceptre", "Dagger", "Rune Dagger", "Claw", "Wand",
        "Two Hand Sword", "Two Hand Axe", "Two Hand Mace", "Staff", "Warstaff", "Bow",
        "Body Armour", "Helmet", "Gloves", "Boots", "Shield", "Quiver", "Ring", "Amulet", "Belt",
    ];

    // Bases that exist in the tables but never drop (event, league and test items).
    static readonly string[] NotDropped = ["Royale", "Demigod", "Talisman", "Breach", "Synthesis", "Fishing", "Test", "Unarmed", "Atlas",
        "Expedition", "Heist", "Relic", "Basilisk", "Crusader", "Eyrie", "Elder", "Shaper", "Necropolis", "Sanctum", "Ultimatum", "Legion"];

    private readonly Lazy<(List<(ItemCatalog.BaseType Type, int Weight)> Currency, List<(ItemCatalog.BaseType Type, int DropLevel)> Equipment,
        List<(ItemCatalog.BaseType Type, int DropLevel)> Flasks)> _pools = new(() => Build(catalog));

    public sealed record Drop(Item Item, ItemCatalog.BaseType Type);

    /// <summary>The items a monster of this level and rarity drops (possibly none).</summary>
    public IReadOnlyList<Drop> Roll(int monsterLevel, MonsterRarity rarity, Random rng)
    {
        int count = rarity switch
        {
            MonsterRarity.Magic => rng.Next(1, 3),
            MonsterRarity.Rare => rng.Next(2, 5),
            MonsterRarity.Unique => rng.Next(4, 7),
            _ => rng.NextDouble() < 0.35 ? 1 : 0,
        };
        var drops = new List<Drop>();
        for (int i = 0; i < count; i++)
            if (RollOne(monsterLevel, rng) is { } d) drops.Add(d);
        return drops;
    }

    Drop? RollOne(int level, Random rng)
    {
        var (currency, equipment, flasks) = _pools.Value;
        double kind = rng.NextDouble();
        ItemCatalog.BaseType? type = null;
        if (kind < 0.45 && currency.Count > 0)
        {
            int total = currency.Sum(c => c.Weight), pick = rng.Next(total);
            foreach (var (t, w) in currency) { if (pick < w) { type = t; break; } pick -= w; }
        }
        else if (kind < 0.55 && flasks.Where(f => f.DropLevel <= level).ToList() is { Count: > 0 } f)
            type = f[rng.Next(f.Count)].Type;
        else if (equipment.Where(e => e.DropLevel <= level).ToList() is { Count: > 0 } e)
            type = e[rng.Next(e.Count)].Type;
        if (type is null) return null;
        var (sockets, links) = catalog.DefaultSockets(type);
        return new Drop(ItemCatalog.Create(type, sockets, links) with { ItemLevel = (byte)Math.Clamp(level, 1, 100) }, type);
    }

    private static (List<(ItemCatalog.BaseType, int)>, List<(ItemCatalog.BaseType, int)>, List<(ItemCatalog.BaseType, int)>) Build(ItemCatalog catalog)
    {
        var currency = new List<(ItemCatalog.BaseType, int)>();
        foreach (var (id, weight) in Currency)
            if (catalog.Find(id) is { Supported: true } t && t.Id == id) currency.Add((t, weight));
        var equipment = new List<(ItemCatalog.BaseType, int)>();
        var flasks = new List<(ItemCatalog.BaseType, int)>();
        for (int row = 0; catalog.Row(row) is { } t; row++)
        {
            if (!t.Supported || t.DropLevel < 1 || string.IsNullOrEmpty(t.Name) || NotDropped.Any(n => t.Id.Contains(n))) continue;
            if (EquipmentClasses.Contains(t.Class)) equipment.Add((t, t.DropLevel));
            else if (t.Class is "LifeFlask" or "ManaFlask") flasks.Add((t, t.DropLevel));
        }
        return (currency, equipment, flasks);
    }
}
