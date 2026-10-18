using System.Collections.Concurrent;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// A character's items, by Inventories.dat row (the Gateway registers inventory id = row + 1 for every row, 05 §2). Kept
/// in memory per character name for the life of the Gateway, so the items follow the player through area changes; they
/// are not saved yet.
/// </summary>
sealed class PlayerInventories
{
    public const byte MainInventory = 0, Weapon1 = 2, Flask = 11, Cursor = InstanceInventories.CursorRow;

    private static readonly ConcurrentDictionary<string, PlayerInventories> ByCharacter = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<byte, List<InventoryItem>> _items = [];
    private readonly Dictionary<byte, uint> _revisions = [];
    private uint _nextItemId = 1;
    private readonly Lock _lock = new();

    public static uint InventoryId(byte row) => (uint)row + 1;
    public static byte? Row(uint inventoryId) => inventoryId >= 1 && inventoryId <= InstanceInventories.PlayerDefaults.Length ? (byte)(inventoryId - 1) : null;

    public IReadOnlyList<InventoryItem> Items(byte row)
    {
        lock (_lock) return _items.TryGetValue(row, out var l) ? [.. l] : [];
    }

    public InventoryItem Add(byte row, byte x, byte y, Item item)
    {
        lock (_lock)
        {
            var placed = new InventoryItem(_nextItemId++, x, y, item);
            (_items.TryGetValue(row, out var l) ? l : _items[row] = []).Add(placed);
            return placed;
        }
    }

    /// <summary>Puts an item (keeping its id) at a cell.</summary>
    public InventoryItem Put(byte row, InventoryItem item)
    {
        lock (_lock) (_items.TryGetValue(row, out var l) ? l : _items[row] = []).Add(item);
        return item;
    }

    /// <summary>What an item needs for placement: its grid size and ItemClasses Id.</summary>
    public readonly record struct Shape(byte Width, byte Height, string Class);

    /// <summary>The held item went to <see cref="Row"/> as <see cref="Placed"/>; <see cref="Displaced"/> (the one it was
    /// dropped on) is now held.</summary>
    public sealed record Placement(byte Row, InventoryItem Placed, InventoryItem Held, InventoryItem? Displaced);

    /// <summary>
    /// Puts the item held on the cursor into inventory <paramref name="row"/> at (x, y). Equipment and other single-item
    /// inventories take it at (0, 0) when its class fits (<see cref="Accepts"/>); grids need it inside the grid. When exactly
    /// one item is in the way it is swapped onto the cursor; two or more refuse the move. Returns null with the reason.
    /// </summary>
    public Placement? Place(byte row, int x, int y, Func<Item, Shape?> shapeOf, out string why)
    {
        lock (_lock)
        {
            why = "";
            if (row == Cursor || row >= InstanceInventories.PlayerDefaults.Length) { why = "not an inventory to place into"; return null; }
            if (!_items.TryGetValue(Cursor, out var cursor) || cursor.Count == 0) { why = "nothing held"; return null; }
            var held = cursor[0];
            if (shapeOf(held.Item) is not { } shape) { why = "unknown item type"; return null; }
            var slot = InstanceInventories.PlayerDefaults[row];
            List<InventoryItem> inTheWay;
            // A one-handed weapon put into an off hand while that side's main hand is empty goes to the main hand.
            if ((row == 3 || row == 15) && OneHand.Contains(shape.Class) && (!_items.TryGetValue((byte)(row - 1), out var main) || main.Count == 0))
                row = (byte)(row - 1);
            if (slot.SingleItem)
            {
                slot = InstanceInventories.PlayerDefaults[row];
                if (!Accepts(row, shape.Class)) { why = $"{shape.Class} does not go in row {row}"; return null; }
                (x, y) = (0, 0);
                inTheWay = [.. ItemsIn(row)];
            }
            else
            {
                if (x < 0 || y < 0 || x + shape.Width > slot.Width || y + shape.Height > slot.Height) { why = "outside the grid"; return null; }
                if (row == Flask && !Accepts(row, shape.Class)) { why = $"{shape.Class} does not go in the flask slots"; return null; }
                inTheWay = [.. ItemsIn(row).Where(i => shapeOf(i.Item) is { } s && i.X < x + shape.Width && x < i.X + s.Width && i.Y < y + shape.Height && y < i.Y + s.Height)];
            }
            if (inTheWay.Count > 1) { why = $"{inTheWay.Count} items in the way"; return null; }
            var items = ItemsIn(row);
            cursor.RemoveAt(0);
            InventoryItem? displaced = null;
            if (inTheWay.Count == 1)
            {
                items.Remove(inTheWay[0]);
                displaced = inTheWay[0] with { X = 0, Y = 0 };
                cursor.Add(displaced);
            }
            var placed = held with { X = (byte)x, Y = (byte)y };
            items.Add(placed);
            return new(row, placed, held, displaced);
        }
    }

    /// <summary>After the main hand (Weapon1 / Weapon2) was emptied: a one-handed weapon in that side's off hand moves to
    /// the main hand. Returns it (same id, now in the main hand), or null.</summary>
    public InventoryItem? PromoteOffhand(byte mainRow, Func<Item, Shape?> shapeOf)
    {
        lock (_lock)
        {
            if (mainRow is not (2 or 14) || ItemsIn(mainRow).Count > 0) return null;
            var off = ItemsIn((byte)(mainRow + 1));
            if (off.Count == 0 || shapeOf(off[0].Item) is not { } s || !OneHand.Contains(s.Class)) return null;
            var moved = off[0] with { X = 0, Y = 0 };
            off.RemoveAt(0);
            ItemsIn(mainRow).Add(moved);
            return moved;
        }
    }

    List<InventoryItem> ItemsIn(byte row) => _items.TryGetValue(row, out var l) ? l : _items[row] = [];

    /// <summary>
    /// Puts the held gem into socket <paramref name="socket"/> of item <paramref name="itemId"/> in <paramref name="row"/>.
    /// A gem already in the socket goes to the cursor. Returns the item as it is now and the gem now held (or null), or
    /// null with the reason.
    /// </summary>
    public (InventoryItem Item, InventoryItem HeldBefore, InventoryItem? HeldNow)? Socket(byte row, uint itemId, int socket,
        Func<Item, Shape?> shapeOf, Func<uint> newId, out string why)
    {
        lock (_lock)
        {
            why = "";
            if (!_items.TryGetValue(Cursor, out var cursor) || cursor.Count == 0) { why = "nothing held"; return null; }
            var held = cursor[0];
            if (shapeOf(held.Item)?.Class is not ("Active Skill Gem" or "Support Skill Gem")) { why = "the held item is not a gem"; return null; }
            var items = ItemsIn(row);
            int at = items.FindIndex(i => i.Id == itemId);
            if (at < 0) { why = "no such item"; return null; }
            var item = items[at];
            if (socket < 0 || socket >= item.Item.Sockets.Count) { why = "no such socket"; return null; }
            var sockets = item.Item.Sockets.ToList();
            var old = sockets[socket].Gem;
            sockets[socket] = sockets[socket] with { Gem = held.Item };
            items[at] = item with { Item = item.Item with { Sockets = sockets } };
            cursor.RemoveAt(0);
            InventoryItem? now = old is null ? null : new InventoryItem(newId(), 0, 0, old);
            if (now is not null) cursor.Add(now);
            return (items[at], held, now);
        }
    }

    /// <summary>Takes the gem out of socket <paramref name="socket"/> onto the (empty) cursor. Returns the item as it is
    /// now and the gem held, or null with the reason.</summary>
    public (InventoryItem Item, InventoryItem Held)? Unsocket(byte row, uint itemId, int socket, Func<uint> newId, out string why)
    {
        lock (_lock)
        {
            why = "";
            var cursor = ItemsIn(Cursor);
            if (cursor.Count > 0) { why = "something is held"; return null; }
            var items = ItemsIn(row);
            int at = items.FindIndex(i => i.Id == itemId);
            if (at < 0) { why = "no such item"; return null; }
            var item = items[at];
            if (socket < 0 || socket >= item.Item.Sockets.Count || item.Item.Sockets[socket].Gem is not { } gem) { why = "no gem in that socket"; return null; }
            var sockets = item.Item.Sockets.ToList();
            sockets[socket] = sockets[socket] with { Gem = null };
            items[at] = item with { Item = item.Item with { Sockets = sockets } };
            var held = new InventoryItem(newId(), 0, 0, gem);
            cursor.Add(held);
            return (items[at], held);
        }
    }

    /// <summary>The gems socketed in the items of the visible equipment rows, in row and socket order.</summary>
    public IReadOnlyList<Item> SocketedGems()
    {
        lock (_lock)
            return [.. VisibleSlots.Keys.Order().SelectMany(r => _items.TryGetValue(r, out var l) ? l : [])
                .SelectMany(i => i.Item.Sockets).Select(s => s.Gem).OfType<Item>()];
    }

    static readonly string[] OneHand = ["Claw", "Dagger", "Wand", "One Hand Sword", "Thrusting One Hand Sword", "One Hand Axe", "One Hand Mace", "Sceptre", "Rune Dagger"];
    static readonly string[] TwoHand = ["Bow", "Staff", "Two Hand Sword", "Two Hand Axe", "Two Hand Mace", "Warstaff", "FishingRod"];

    /// <summary>Whether an item class goes into an equipment row (Inventories.dat rows 1-11, 14, 15, 102). Other single-item
    /// rows (crafting benches …) take anything.</summary>
    public static bool Accepts(byte row, string itemClass) => row switch
    {
        1 => itemClass == "Body Armour",
        2 or 14 => OneHand.Contains(itemClass) || TwoHand.Contains(itemClass),
        3 or 15 => OneHand.Contains(itemClass) || itemClass is "Shield" or "Quiver",
        4 => itemClass == "Helmet",
        5 => itemClass == "Amulet",
        6 or 7 or 102 => itemClass == "Ring",
        8 => itemClass == "Gloves",
        9 => itemClass == "Boots",
        10 => itemClass == "Belt",
        Flask => itemClass is "LifeFlask" or "ManaFlask" or "HybridFlask" or "UtilityFlask" or "UtilityFlaskCritical",
        _ => true,
    };

    /// <summary>Inventories.dat column 7 by row: the slot the player's model shows an equipped item in (17 = none).</summary>
    static readonly Dictionary<byte, byte> VisibleSlots = new()
    {
        [1] = 2, [2] = 0, [3] = 1, [4] = 4, [5] = 11, [6] = 8, [7] = 9, [8] = 5, [9] = 6, [10] = 10,
    };

    public static bool ShowsOnModel(byte row) => VisibleSlots.ContainsKey(row);

    /// <summary>What the player's model wears: every visible equipment slot (Weapon1, Offhand1, armour …), with its item
    /// or empty, so an unequipped item disappears from the model.</summary>
    public IReadOnlyList<VisibleItem> Equipped(Func<Item, VisibleItem?> look)
    {
        lock (_lock)
            return [.. VisibleSlots.OrderBy(e => e.Value).Select(e =>
                _items.TryGetValue(e.Key, out var l) && l.Count > 0 && look(l[0].Item) is { } v ? v with { Slot = e.Value } : VisibleItem.Empty(e.Value))];
    }

    /// <summary>A free cell for an item of this size in a grid inventory, row by row; null when it is full.</summary>
    public (byte X, byte Y)? FreeCell(byte row, byte width, byte height, Func<Item, Shape?> shapeOf)
    {
        lock (_lock)
        {
            var slot = InstanceInventories.PlayerDefaults[row];
            var items = _items.TryGetValue(row, out var l) ? l : [];
            for (int x = 0; x + width <= slot.Width; x++)
                for (int y = 0; y + height <= slot.Height; y++)
                    if (!items.Any(i => shapeOf(i.Item) is { } s && i.X < x + width && x < i.X + s.Width && i.Y < y + height && y < i.Y + s.Height))
                        return ((byte)x, (byte)y);
            return null;
        }
    }

    /// <summary>A fresh id for an item that enters the inventories (picked up from the ground, out of a socket).</summary>
    public uint NewItemId()
    {
        lock (_lock) return _nextItemId++;
    }

    /// <summary>Takes an item out of an inventory; null when it is not there.</summary>
    public InventoryItem? Remove(byte row, uint itemId)
    {
        lock (_lock)
        {
            if (!_items.TryGetValue(row, out var l) || l.FindIndex(i => i.Id == itemId) is var at && at < 0) return null;
            var it = l[at];
            l.RemoveAt(at);
            return it;
        }
    }

    /// <summary>The next revision number of an inventory's contents (sent with each S→C 0xbf).</summary>
    public uint NextRevision(byte row)
    {
        lock (_lock) return _revisions[row] = _revisions.GetValueOrDefault(row) + 1;
    }

    /// <summary>The character's items, created with the class's start kit on first use.</summary>
    public static PlayerInventories For(string character, ItemCatalog catalog, string folder = "data") =>
        ByCharacter.GetOrAdd(character, _ => StartKit(catalog, folder));

    /// <summary>
    /// The Witch's start kit from Characters.dat (row 1): StartWeapons[0] (Driftwood Wand) in Weapon1, the StartSkillGem and
    /// StartSupportGem (SkillGems rows → their BaseItemTypes: Fireball, Arcane Surge Support) in the main inventory, and a
    /// Small Life Flask and Small Mana Flask in the flask slots. Missing tables or unsupported types are skipped;
    /// Game_START_ITEMS=0 starts empty.
    /// </summary>
    static PlayerInventories StartKit(ItemCatalog catalog, string folder, int characterRow = 1)
    {
        var inv = new PlayerInventories();
        if (Environment.GetEnvironmentVariable("Game_START_ITEMS") == "0") return inv;
        string chars = Path.Combine(folder, "tables", "Characters.json"), gems = Path.Combine(folder, "tables", "SkillGems.json");
        if (catalog.Error is not null || !File.Exists(chars) || !File.Exists(gems)) return inv;
        using var c = JsonDocument.Parse(File.ReadAllBytes(chars));
        using var g = JsonDocument.Parse(File.ReadAllBytes(gems));
        var row = c.RootElement[characterRow];
        void Put(byte inventory, byte x, byte y, ItemCatalog.BaseType? t)
        {
            if (t is { Supported: true }) inv.Add(inventory, x, y, ItemCatalog.Create(t));
        }
        ItemCatalog.BaseType? Gem(string column) =>
            row.GetProperty(column) is { ValueKind: JsonValueKind.Number } k
                ? catalog.Row(g.RootElement[k.GetInt32()].GetProperty("BaseItemTypesKey").GetInt32()) : null;
        // The start weapon with two linked sockets in the class's colour (the Witch: blue), empty, for its gems.
        if (row.GetProperty("StartWeapons").EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Number } w &&
            catalog.Row(w.GetInt32()) is { Supported: true } weapon)
            inv.Add(Weapon1, 0, 0, ItemCatalog.Create(weapon, [new(SocketColour.Blue), new(SocketColour.Blue)], [2]));
        Put(MainInventory, 0, 0, Gem("StartSkillGem"));
        Put(MainInventory, 1, 0, Gem("StartSupportGem"));
        Put(Flask, 0, 0, catalog.Find("Metadata/Items/Flasks/FlaskLife1"));
        Put(Flask, 1, 0, catalog.Find("Metadata/Items/Flasks/FlaskMana1"));
        return inv;
    }
}
