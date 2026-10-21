using GameServer.Core.Protocol.Instance;

/// <summary>
/// Currency used on an item (C→S 0x71, Agents/docs/protocol/13-items.md §4b): what each handled orb does to the target.
/// </summary>
static class CurrencyEffects
{
    public const string ChromaticOrb = "Metadata/Items/Currency/CurrencyRerollSocketColours";
    public const string JewellersOrb = "Metadata/Items/Currency/CurrencyRerollSocketNumbers";

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
