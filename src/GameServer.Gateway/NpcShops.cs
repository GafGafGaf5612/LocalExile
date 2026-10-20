using GameServer.Core.Protocol.Instance;

/// <summary>
/// NPC shops (Agents/docs/protocol/17-npcs.md §5). The client files name the shops (NPCShopSets → NPCShop) but not what
/// they sell: the stock is the server's. Ours is a fixed Act 1 assortment per NPC. A shop tab is an inventory in the
/// client's second inventory manager (S→C 0xc0 manager 1, instance +0xca10), opened with S→C 0x8c.
/// </summary>
static class NpcShops
{
    /// <summary>A shop tab: 12 × 12 cells, the size of the vendor window's grid. Inventories.dat row 83
    /// (CraftPredictionScratch, kind 0x0f) only gives the client a generic grid type.</summary>
    public static readonly InstanceInventories.Slot Tab = new(83, 0x0f, 12, 12);

    /// <summary>A sell offer grid (the player's and the NPC's side of the sell window): the main inventory's row and size,
    /// so every item fits (the client checks a drop against the row, 0x141d628f0; row 83 refused everything).</summary>
    public static readonly InstanceInventories.Slot Offer = new(0, 0x00, 12, 5);

    /// <summary>Inventory manager of NPC inventories (0x14216aa30: 0 player, 1 instance +0xca10, 2 +0xcb90).</summary>
    public const byte Manager = 1;

    /// <summary>Shop tab inventory ids, above the player's 1..117.</summary>
    public const uint FirstTabId = 0x400;

    static readonly Dictionary<string, (string Name, int Count)[]> Stock = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Metadata/NPC/Act1/Tarkleigh"] =
        [
            ("Rusted Sword", 1), ("Rusted Hatchet", 1), ("Driftwood Club", 1), ("Glass Shank", 1), ("Crude Bow", 1),
            ("Driftwood Wand", 1), ("Driftwood Sceptre", 1), ("Splintered Tower Shield", 1), ("Plate Vest", 1),
            ("Shabby Jerkin", 1), ("Simple Robe", 1), ("Iron Hat", 1), ("Iron Gauntlets", 1), ("Iron Greaves", 1),
        ],
        ["Metadata/NPC/Act1/Nessa"] =
        [
            ("Small Life Flask", 3), ("Small Mana Flask", 3), ("Medium Life Flask", 1), ("Scroll of Wisdom", 5),
            ("Portal Scroll", 5), ("Orb of Transmutation", 1), ("Fireball", 1), ("Frostblink", 1),
            // Maps for the hideout's map device (21-hideouts.md §5).
            ("Beach Map", 10),
        ],
        ["Metadata/NPC/Act1/Bestel"] =
        [
            ("Iron Ring", 2), ("Paua Amulet", 1), ("Chain Belt", 1), ("Rustic Sash", 1), ("Rusted Sword", 1), ("Iron Hat", 1),
        ],
    };

    /// <summary>What everything costs for now: our own prices, in Scrolls of Wisdom.</summary>
    public const string Currency = "Scroll of Wisdom";
    public const int PriceInCurrency = 1;

    /// <summary>What the NPC gives for any sold item, for now: one Scroll Fragment.</summary>
    public const string SellReward = "Scroll Fragment";

    /// <summary>The NPC's stock laid out in one tab (first free cell, column by column), with item ids from
    /// <paramref name="nextId"/>. Unknown or unwritable item types are skipped and named in <paramref name="notes"/>.</summary>
    public static List<InventoryItem> Fill(string npcId, ItemCatalog items, Func<uint> nextId, List<string> notes)
    {
        var currency = items.Find(Currency);
        if (currency is null) notes.Add($"no item type {Currency}: the stock has no prices");
        var placed = new List<(InventoryItem Item, byte W, byte H)>();
        foreach (var (name, count) in Stock.GetValueOrDefault(npcId) ?? [])
        {
            if (items.Find(name) is not { Supported: true } type) { notes.Add($"no writable item type {name}"); continue; }
            var (sockets, links) = items.DefaultSockets(type);
            for (int i = 0; i < count; i++)
            {
                if (FreeCell(placed, type.Width, type.Height) is not { } cell) { notes.Add($"no room for {name}"); break; }
                var item = ItemCatalog.Create(type, sockets, links);
                if (currency is not null) item = item with { Price = [(currency.Hash, (uint)PriceInCurrency)] };
                placed.Add((new InventoryItem(nextId(), cell.X, cell.Y, item), type.Width, type.Height));
            }
        }
        return [.. placed.Select(p => p.Item)];
    }

    static (byte X, byte Y)? FreeCell(List<(InventoryItem Item, byte W, byte H)> placed, byte width, byte height)
    {
        for (int x = 0; x + width <= Tab.Width; x++)
            for (int y = 0; y + height <= Tab.Height; y++)
                if (!placed.Any(p => p.Item.X < x + width && x < p.Item.X + p.W && p.Item.Y < y + height && y < p.Item.Y + p.H))
                    return ((byte)x, (byte)y);
        return null;
    }
}
