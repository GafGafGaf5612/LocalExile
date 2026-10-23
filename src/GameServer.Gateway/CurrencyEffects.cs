using GameServer.Core.Protocol.Instance;

/// <summary>
/// Currency used on an item (C→S 0x71, Agents/docs/protocol/13-items.md §4b): what each handled orb does to the target.
/// </summary>
static class CurrencyEffects
{
    public const string ChromaticOrb = "Metadata/Items/Currency/CurrencyRerollSocketColours";
    public const string JewellersOrb = "Metadata/Items/Currency/CurrencyRerollSocketNumbers";
    public const string OrbOfFusing = "Metadata/Items/Currency/CurrencyRerollSocketLinks";
    public const string OrbOfAlchemy = "Metadata/Items/Currency/CurrencyUpgradeToRare";
    public const string ChaosOrb = "Metadata/Items/Currency/CurrencyRerollRare";
    public const string DivineOrb = "Metadata/Items/Currency/CurrencyModValues";
    public const string BlessedOrb = "Metadata/Items/Currency/CurrencyRerollImplicit";
    public const string OrbOfScouring = "Metadata/Items/Currency/CurrencyConvertToNormal";
    public const string OrbOfTransmutation = "Metadata/Items/Currency/CurrencyUpgradeToMagic";
    public const string OrbOfAlteration = "Metadata/Items/Currency/CurrencyRerollMagic";
    public const string OrbOfAugmentation = "Metadata/Items/Currency/CurrencyAddModToMagic";
    public const string RegalOrb = "Metadata/Items/Currency/CurrencyUpgradeMagicToRare";
    public const string ExaltedOrb = "Metadata/Items/Currency/CurrencyAddModToRare";
    public const string OrbOfAnnulment = "Metadata/Items/Currency/CurrencyRemoveMod";

    /// <summary>Orb of Annulment: one random explicit affix leaves a magic or rare item; implicits, the other affixes,
    /// rarity and name stay. Refused for anything else, or an item without explicit affixes.</summary>
    public static Item? Annulment(Item item, Random random)
    {
        var fractured = item.FracturedIndexes();
        var explicits = item.Mods.Select((m, i) => (m, i)).Where(p => p.m.Section == ModSection.Explicit && !fractured.Contains(p.i)).Select(p => p.i).ToList();
        if (item.Rarity is not (ItemRarity.Magic or ItemRarity.Rare) || explicits.Count == 0) return null;
        int gone = explicits[random.Next(explicits.Count)];
        return item with { Mods = [.. item.Mods.Where((_, i) => i != gone)] };
    }

    /// <summary>Exalted Orb: a rare item keeps its affixes and name and gets one more (at most three prefixes and three
    /// suffixes). Refused for anything else, or when the item is full.</summary>
    public static Item? Exalted(Item item, ItemRolls rolls, Random random) =>
        item.Rarity != ItemRarity.Rare || !item.Components.Contains("Mods") ? null : rolls.AddAffix(item, 3, random);

    /// <summary>Regal Orb: a magic item becomes rare with its affixes kept, one more affix (at most three of each kind)
    /// and a rare name. Refused for anything else, or when no affix fits.</summary>
    public static Item? Regal(Item item, ItemRolls rolls, Random random) =>
        item.Rarity != ItemRarity.Magic || !item.Components.Contains("Mods") || rolls.AddAffix(item, 3, random) is not { } added ? null
            : added with { Rarity = ItemRarity.Rare, Identified = true, NameWords = rolls.RareName(item, random) };

    /// <summary>Orb of Augmentation: a magic item with a free prefix or suffix (at most one of each) gets one more affix;
    /// its affixes and rarity stay. Anything else is refused.</summary>
    public static Item? Augmentation(Item item, ItemRolls rolls, Random random) =>
        item.Rarity != ItemRarity.Magic || !item.Components.Contains("Mods") ? null : rolls.AddAffix(item, 1, random);

    /// <summary>Orb of Alteration (as the official one): a magic item with mods gets new affixes, 1-2 with at most one
    /// prefix and one suffix, each tier drawn by weight. Anything else is refused.</summary>
    public static Item? Alteration(Item item, ItemRolls rolls, Random random) =>
        item.Rarity != ItemRarity.Magic || !item.Components.Contains("Mods") ? null
            : rolls.Roll(item, ItemRarity.Magic, random) with { Identified = true };

    /// <summary>Orb of Transmutation: a normal item with mods becomes an identified magic one with 1-2 random affixes
    /// (<see cref="ItemRolls.Roll"/>); anything else is refused.</summary>
    public static Item? Transmutation(Item item, ItemRolls rolls, Random random) =>
        item.Rarity != ItemRarity.Normal || !item.Components.Contains("Mods") ? null
            : rolls.Roll(item, ItemRarity.Magic, random) with { Identified = true };

    /// <summary>Orb of Scouring: a magic, rare or unique item becomes normal; its explicit mods and name go, the base
    /// (implicit, enchant …) mods stay. On this server it also sets an armour piece's, weapon's or flask's quality back to 0. Refused when
    /// there is nothing to remove.</summary>
    public static Item? Scouring(Item item)
    {
        bool armourQuality = (item.Components.Contains("Armour") || item.Components.Contains("Weapon") || item.Components.Contains("Flask")) && item.Quality > 0;
        if (item.Rarity == ItemRarity.Normal && !armourQuality) return null;
        // Fractured mods stay; an item that keeps one stays magic (a normal item shows no explicit mods).
        var fractured = item.FracturedIndexes();
        return item with { Rarity = fractured.Count > 0 ? ItemRarity.Magic : ItemRarity.Normal, Identified = true, NameWords = [],
            Mods = [.. item.Mods.Where((m, i) => m.Section != ModSection.Explicit || fractured.Contains(i))],
            Quality = armourQuality ? (byte)0 : item.Quality };
    }

    public const string ArmourersScrap = "Metadata/Items/Currency/CurrencyArmourQuality";
    public const int MaximumQuality = 20;

    /// <summary>Armourer's Scrap: +1% quality on an armour piece (any rarity), up to 20%; the client derives the higher
    /// armour / evasion / energy shield from the quality. Refused for other items or at 20%.</summary>
    public static Item? ArmourerScrap(Item item) =>
        !item.Components.Contains("Armour") || !item.Components.Contains("Quality") || item.Quality >= MaximumQuality ? null
            : item with { Quality = (byte)(item.Quality + 1) };

    public const string VaalOrb = "Metadata/Items/Currency/CurrencyCorrupt";

    /// <summary>Vaal Orb (this server's rule): a weapon, armour piece or jewellery (ring, amulet, belt) that is not
    /// corrupted or unique becomes corrupted, always. A normal or magic item becomes rare with 4-6 new affixes; a rare one
    /// keeps its name and has 1 (20%), 2 (20%), 3 (20%), 4 (20%) or 5 (10%) affixes replaced, or all of them rerolled
    /// (10%, 4-6 affixes, new name).</summary>
    public static Item? Vaal(Item item, ItemCatalog.BaseType type, ItemRolls rolls, Random random)
    {
        bool target = item.Components.Contains("Weapon") || item.Components.Contains("Armour") || type.Class is "Ring" or "Amulet" or "Belt";
        if (!target || item.Corrupted || item.Rarity == ItemRarity.Unique || !item.Components.Contains("Mods")) return null;
        int roll = random.Next(100);
        Item result = item.Rarity != ItemRarity.Rare || roll >= 90 ? rolls.Roll(item, ItemRarity.Rare, random)
            : rolls.ReplaceAffixes(item, roll switch { < 20 => 1, < 40 => 2, < 60 => 3, < 80 => 4, _ => 5 }, random);
        return result with { Corrupted = true, Identified = true };
    }

    public const string GlassblowersBauble = "Metadata/Items/Currency/CurrencyFlaskQuality";

    /// <summary>Glassblower's Bauble: +1% quality on a flask (any rarity), up to 20%. Refused for other items or at 20%.</summary>
    public static Item? Glassblower(Item item) =>
        !item.Components.Contains("Flask") || !item.Components.Contains("Quality") || item.Quality >= MaximumQuality ? null
            : item with { Quality = (byte)(item.Quality + 1) };

    public const string FracturingOrb = "Metadata/Items/Currency/CurrencyFractureRare";

    /// <summary>Fracturing Orb (as the official one): a rare item with at least four explicit mods, not corrupted and not
    /// fractured yet, has one random explicit mod fractured: it moves to the front of the explicits and from then on no
    /// currency removes or changes it (Scouring, Chaos, Annulment, Divine, Vaal …).</summary>
    public static Item? Fracture(Item item, Random random)
    {
        var explicits = item.Mods.Select((m, i) => (m, i)).Where(p => p.m.Section == ModSection.Explicit).ToList();
        if (item.Rarity != ItemRarity.Rare || item.Corrupted || item.FracturedCount > 0 || explicits.Count < 4) return null;
        var (mod, index) = explicits[random.Next(explicits.Count)];
        var mods = item.Mods.Where((_, i) => i != index).ToList();
        mods.Insert(explicits[0].i, mod);
        return item with { Mods = mods, FracturedCount = 1 };
    }

    public const string MirrorOfKalandra = "Metadata/Items/Currency/CurrencyDuplicate";

    /// <summary>Mirror of Kalandra (as the official one): a mirrored copy of an item that is neither corrupted nor
    /// mirrored, with every property kept (mods and their values, fractured mods, quality, sockets, links, socketed gems,
    /// name). The original stays as it was. Null when the item cannot be mirrored.</summary>
    public static Item? Mirror(Item item) =>
        item.Corrupted || item.Mirrored || !item.Components.Contains("Mods") ? null : item with { Mirrored = true };

    public const string GemcuttersPrism = "Metadata/Items/Currency/CurrencyGemQuality";

    /// <summary>Gemcutter's Prism: +1% quality on a gem (active or support) that is not corrupted, up to 20%. Refused for
    /// other items, corrupted gems or at 20%.</summary>
    public static Item? Gemcutter(Item item) =>
        !item.Components.Contains("SkillGem") || !item.Components.Contains("Quality") || item.Corrupted || item.Quality >= MaximumQuality ? null
            : item with { Quality = (byte)(item.Quality + 1) };

    public const string OrbOfChance = "Metadata/Items/Currency/CurrencyUpgradeRandomly";

    /// <summary>Orb of Chance (this server's rule, not the official one): a normal weapon or armour piece becomes rare
    /// (30%: 4-6 affixes and a rare name) or magic (2-4 affixes, at most two of each kind), identified. Refused for
    /// anything else.</summary>
    public static Item? Chance(Item item, ItemRolls rolls, Random random) =>
        item.Rarity != ItemRarity.Normal || !item.Components.Contains("Mods") || !(item.Components.Contains("Weapon") || item.Components.Contains("Armour")) ? null
        : (random.Next(100) < 30 ? rolls.Roll(item, ItemRarity.Rare, random)
            : rolls.Roll(item, ItemRarity.Magic, random, (random.Next(2, 5), 2))) with { Identified = true };

    public const string BlacksmithsWhetstone = "Metadata/Items/Currency/CurrencyWeaponQuality";

    /// <summary>Blacksmith's Whetstone: +1% quality on a weapon (any rarity), up to 20%; the client derives the higher
    /// physical damage from the quality. Refused for other items or at 20%.</summary>
    public static Item? Whetstone(Item item) =>
        !item.Components.Contains("Weapon") || !item.Components.Contains("Quality") || item.Quality >= MaximumQuality ? null
            : item with { Quality = (byte)(item.Quality + 1) };

    /// <summary>Orb of Alchemy: a normal item with mods becomes an identified rare one with 4-6 new random affixes and
    /// a rare name (<see cref="ItemRolls.Roll"/>); anything else is refused.</summary>
    public static Item? Alchemy(Item item, ItemRolls rolls, Random random) =>
        item.Rarity != ItemRarity.Normal || !item.Components.Contains("Mods") ? null
            : rolls.Roll(item, ItemRarity.Rare, random) with { Identified = true };

    /// <summary>Chaos Orb: a rare item with mods gets all its explicit affixes rerolled (4-6 new ones, new tiers and
    /// values) and a new rare name; implicits stay. Anything else is refused.</summary>
    public static Item? Chaos(Item item, ItemRolls rolls, Random random) =>
        item.Rarity != ItemRarity.Rare || !item.Components.Contains("Mods") ? null
            : rolls.Roll(item, ItemRarity.Rare, random) with { Identified = true };

    /// <summary>Change only links; socket colours, gems and all other properties stay intact.</summary>
    public static Item? Fusing(Item item, ItemCatalog.BaseType type, Random random)
    {
        int count = item.Sockets.Count;
        if (count < 2 || count > 6 || count > SocketCapacity(item, type)) return null;
        IReadOnlyList<byte> old = item.Links ?? [.. Enumerable.Repeat((byte)1, count)];
        // Small fully linked items can be unlinked; protect completed five- and six-socket items.
        if (count >= 5 && old.Count == 1 && old[0] == count) return null;

        // At most 32 possible partitions. Weight by the largest linked group, then divide that weight among
        // its layouts. These are initial server weights, not recovered official probabilities.
        double[] weights = [0, 500, 3500, 4500, 1400, 90, 10];
        var layouts = new List<byte[]>();
        int[] sizes = new int[7];
        for (int mask = 0; mask < 1 << (count - 1); mask++)
        {
            var groups = new List<byte> { 1 };
            for (int edge = 0; edge < count - 1; edge++)
                if ((mask & (1 << edge)) != 0) groups[^1]++;
                else groups.Add(1);
            var layout = groups.ToArray();
            layouts.Add(layout);
            sizes[layout.Max()]++;
        }
        var choices = layouts.Where(g => !g.SequenceEqual(old)).Select(g =>
            (Links: g, Weight: weights[g.Max()] / sizes[g.Max()] * Math.Pow(1 + item.Quality / 100.0, g.Max() - 1))).ToArray();
        double roll = random.NextDouble() * choices.Sum(c => c.Weight);
        foreach (var choice in choices)
        {
            roll -= choice.Weight;
            if (roll < 0) return item with { Links = choice.Links };
        }
        return item with { Links = choices[^1].Links };
    }

    // Initial local rules: 1, 2, 3, 4 or 6 sockets (five deliberately excluded).
    // Weights and link chances are approximations, not recovered client/server constants.
    public static int MaximumSockets(Item item, ItemCatalog.BaseType type)
    {
        int levelLimit = item.ItemLevel >= 50 ? 6 : item.ItemLevel >= 25 ? 4 : item.ItemLevel >= 2 ? 3 : 2;
        return Math.Min(SocketCapacity(item, type), levelLimit);
    }

    public static int SocketCapacity(Item item, ItemCatalog.BaseType type)
    {
        if (!item.Components.Contains("Sockets") || !type.Components.Contains("Sockets")) return 0;
        int limit = type.Class switch
        {
            "Body Armour" or "Bow" or "Staff" or "Warstaff" or "Two Hand Sword" or "Two Hand Axe" or "Two Hand Mace" => 6,
            "Helmet" or "Gloves" or "Boots" => 4,
            "Shield" or "Claw" or "Dagger" or "Rune Dagger" or "Wand" or "One Hand Sword" or
                "Thrusting One Hand Sword" or "One Hand Axe" or "One Hand Mace" or "Sceptre" => 3,
            _ => 0,
        };
        return Math.Min(limit, type.Width * type.Height);
    }

    /// <summary>Reforge empty sockets, colours and link groups. Refused targets spend no currency.</summary>
    public static Item? Jewellers(Item item, ItemCatalog.BaseType type, (int Str, int Dex, int Int) requirements, Random random)
    {
        int max = MaximumSockets(item, type);
        if (max == 0 || item.Sockets.Count >= SocketCapacity(item, type) || item.Sockets.Any(s => s.Gem is not null)) return null;
        (int Count, int Weight)[] choices = [(1, 50), (2, 120), (3, 100), (4, 30), (6, 1)];
        var allowed = choices.Where(c => c.Count <= max && c.Count != item.Sockets.Count).ToArray();
        if (allowed.Length == 0) return null;
        int roll = random.Next(allowed.Sum(c => c.Weight));
        int count = allowed[0].Count;
        foreach (var choice in allowed)
        {
            if (roll < choice.Weight) { count = choice.Count; break; }
            roll -= choice.Weight;
        }
        int r = requirements.Str + ColourBase, g = requirements.Dex + ColourBase, b = requirements.Int + ColourBase;
        var sockets = new List<ItemSocket>();
        var links = new List<byte> { 1 };
        for (int i = 0; i < count; i++)
        {
            int colour = random.Next(r + g + b);
            sockets.Add(new(colour < r ? SocketColour.Red : colour < r + g ? SocketColour.Green : SocketColour.Blue));
            if (i == 0) continue;
            if (random.Next(2) == 0) links[^1]++;
            else links.Add(1);
        }
        return item with { Sockets = sockets, Links = links };
    }

    // shortcut: the real game's colour weights are not known; each colour weighs its attribute requirement + 12.
    const int ColourBase = 12;

    /// <summary>
    /// Chromatic Orb: every socket gets a new colour, red / green / blue weighted by the base's strength / dexterity /
    /// intelligence requirement + 12 (never white). Gems and links stay. The result differs from the old colours (rerolled
    /// up to 50 times; a single socket always can). Null for an item without sockets.
    /// </summary>
    public static Item? Chromatic(Item item, (int Str, int Dex, int Int) requirements, Random random)
    {
        if (item.Sockets.Count == 0) return null;
        int r = requirements.Str + ColourBase, g = requirements.Dex + ColourBase, b = requirements.Int + ColourBase;
        SocketColour Roll()
        {
            int x = random.Next(r + g + b);
            return x < r ? SocketColour.Red : x < r + g ? SocketColour.Green : SocketColour.Blue;
        }
        IReadOnlyList<ItemSocket> sockets = item.Sockets;
        for (int tries = 0; tries < 50; tries++)
        {
            sockets = [.. item.Sockets.Select(s => s with { Colour = Roll() })];
            if (!sockets.Select(s => s.Colour).SequenceEqual(item.Sockets.Select(s => s.Colour))) break;
        }
        return item with { Sockets = sockets };
    }
}
