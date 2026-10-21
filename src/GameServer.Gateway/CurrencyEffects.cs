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
