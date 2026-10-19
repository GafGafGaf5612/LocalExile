using System.Collections.Concurrent;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// A character's items, by Inventories.dat row (the Gateway registers inventory id = row + 1 for every row, 05 §2). Kept
/// in memory per character name and saved after every change (<see cref="CharacterSaves"/>, data/saves/), so they follow
/// the player through area changes and Gateway restarts.
/// </summary>
sealed class PlayerInventories
{
    public const byte MainInventory = 0, Weapon1 = 2, Offhand1 = 3, Flask = 11, Cursor = InstanceInventories.CursorRow;

    private static readonly ConcurrentDictionary<string, PlayerInventories> ByCharacter = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<byte, List<InventoryItem>> _items = [];
    private readonly Dictionary<byte, uint> _revisions = [];
    private uint _nextItemId = 1;
    private readonly Lock _lock = new();
    private string? _owner;   // the character whose save this is; null while the start kit is being built

    /// <summary>Writes the character's save (every inventory, with item ids and cells).</summary>
    void Persist()
    {
        if (_owner is null) return;
        Dictionary<byte, List<CharacterSaves.SavedPlacement>> items;
        lock (_lock)
            items = _items.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key,
                kv => kv.Value.Select(i => new CharacterSaves.SavedPlacement(i.Id, i.X, i.Y, CharacterSaves.ToSaved(i.Item))).ToList());
        // The rest of the save (skill bar, experience, passives) stays as it is.
        CharacterSaves.SetInventories(_owner, items);
    }

    public static uint InventoryId(byte row) => (uint)row + 1;
    public static byte? Row(uint inventoryId) => inventoryId >= 1 && inventoryId <= InstanceInventories.PlayerDefaults.Length ? (byte)(inventoryId - 1) : null;

    public IReadOnlyList<InventoryItem> Items(byte row)
    {
        lock (_lock) return _items.TryGetValue(row, out var l) ? [.. l] : [];
    }

    public InventoryItem Add(byte row, byte x, byte y, Item item)
    {
        InventoryItem placed;
        lock (_lock)
        {
            placed = new InventoryItem(_nextItemId++, x, y, item);
            (_items.TryGetValue(row, out var l) ? l : _items[row] = []).Add(placed);
        }
        Persist();
        return placed;
    }

    /// <summary>Puts an item (keeping its id) at a cell.</summary>
    public InventoryItem Put(byte row, InventoryItem item)
    {
        lock (_lock) (_items.TryGetValue(row, out var l) ? l : _items[row] = []).Add(item);
        Persist();
        return item;
    }

    /// <summary>What an item needs for placement: its grid size, ItemClasses Id, how many share a cell (1: no stack) and
    /// what a full stack becomes (null: it stays; 5 Scroll Fragments → a Scroll of Wisdom).</summary>
    public readonly record struct Shape(byte Width, byte Height, string Class, ushort MaxStack = 1, Func<Item>? FullStack = null);

    /// <summary>A stack that reached its max becomes its full-stack item (one of it, same id and cell).</summary>
    public static InventoryItem Completed(InventoryItem it, Shape shape) =>
        shape.FullStack is { } full && shape.MaxStack > 1 && it.Item.StackSize >= shape.MaxStack ? it with { Item = full() } : it;

    /// <summary>The held item went to <see cref="Row"/> as <see cref="Placed"/>; <see cref="Displaced"/> (the one it was
    /// dropped on) is now held. <see cref="Merged"/>: the held stack went onto a stack of its type instead, which is
    /// <see cref="Placed"/> now (same id, larger); <see cref="Rest"/> is what is still held (same id as <see cref="Held"/>),
    /// or null.</summary>
    public sealed record Placement(byte Row, InventoryItem Placed, InventoryItem Held, InventoryItem? Displaced,
        bool Merged = false, InventoryItem? Rest = null);

    /// <summary>
    /// Puts the item held on the cursor into inventory <paramref name="row"/> at (x, y). Equipment and other single-item
    /// inventories take it at (0, 0) when its class fits (<see cref="Accepts"/>); grids need it inside the grid. When exactly
    /// one item is in the way it is swapped onto the cursor, unless it is a stack of the held stack's type with room: then
    /// the held stack fills it and the rest stays held. Two or more refuse the move. Returns null with the reason.
    /// </summary>
    /// <remarks>With <paramref name="one"/> (Shift + click, 0x2a's first flag) only one of a held stack goes: into an
    /// empty cell as a new item, or onto a stack of its type with room; the rest stays held.</remarks>
    public Placement? Place(byte row, int x, int y, Func<Item, Shape?> shapeOf, out string why, bool one = false)
    {
        var result = PlaceCore(row, x, y, shapeOf, out why, one);
        if (result is not null) Persist();
        return result;
    }

    Placement? PlaceCore(byte row, int x, int y, Func<Item, Shape?> shapeOf, out string why, bool one = false)
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
            int amount = one ? 1 : held.Item.StackSize;
            if (inTheWay is [var under] && under.Item.TypeHash == held.Item.TypeHash && shape.MaxStack > 1 && under.Item.StackSize < shape.MaxStack)
            {
                int moved = Math.Min(amount, shape.MaxStack - under.Item.StackSize);
                var filled = Completed(under with { Item = under.Item with { StackSize = (ushort)(under.Item.StackSize + moved) } }, shape);
                items[items.IndexOf(under)] = filled;
                InventoryItem? rest = null;
                if (moved < held.Item.StackSize) cursor[0] = rest = held with { Item = held.Item with { StackSize = (ushort)(held.Item.StackSize - moved) } };
                else cursor.RemoveAt(0);
                return new(row, filled, held, null, Merged: true, rest);
            }
            if (amount < held.Item.StackSize)
            {
                // One of the stack into an empty cell, as a new item.
                if (inTheWay.Count > 0) { why = "only one of the stack, and something is in the way"; return null; }
                var single = new InventoryItem(_nextItemId++, (byte)x, (byte)y, held.Item with { StackSize = (ushort)amount });
                items.Add(single);
                var left = cursor[0] = held with { Item = held.Item with { StackSize = (ushort)(held.Item.StackSize - amount) } };
                return new(row, single, held, null, Rest: left);
            }
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

    /// <summary>
    /// The held item clicked on item <paramref name="itemId"/> in <paramref name="row"/> (C→S 0x4a): <see cref="Place"/> on
    /// that item's cell. A stack of the same type with room takes what fits (with <paramref name="one"/>, one) and the
    /// rest stays held; anything else is swapped (refused with <paramref name="one"/>). Null with the reason; nothing changes.
    /// </summary>
    public Placement? MergeOnto(byte row, uint itemId, Func<Item, Shape?> shapeOf, out string why, bool one = false)
    {
        Placement? result;
        lock (_lock)
        {
            why = "";
            if (!_items.TryGetValue(Cursor, out var cursor) || cursor.Count == 0) { why = "nothing held"; return null; }
            var held = cursor[0];
            if (ItemsIn(row).FirstOrDefault(i => i.Id == itemId) is not { } target) { why = "no such item"; return null; }
            result = PlaceCore(row, target.X, target.Y, shapeOf, out why, one && held.Item.StackSize > 1);
        }
        if (result is not null) Persist();
        return result;
    }

    /// <summary>After the main hand (Weapon1 / Weapon2) was emptied: a one-handed weapon in that side's off hand moves to
    /// the main hand. Returns it (same id, now in the main hand), or null.</summary>
    public InventoryItem? PromoteOffhand(byte mainRow, Func<Item, Shape?> shapeOf)
    {
        var result = PromoteOffhandCore(mainRow, shapeOf);
        if (result is not null) Persist();
        return result;
    }

    InventoryItem? PromoteOffhandCore(byte mainRow, Func<Item, Shape?> shapeOf)
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
        var result = SocketCore(row, itemId, socket, shapeOf, newId, out why);
        if (result is not null) Persist();
        return result;
    }

    (InventoryItem Item, InventoryItem HeldBefore, InventoryItem? HeldNow)? SocketCore(byte row, uint itemId, int socket,
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
        var result = UnsocketCore(row, itemId, socket, newId, out why);
        if (result is not null) Persist();
        return result;
    }

    (InventoryItem Item, InventoryItem Held)? UnsocketCore(byte row, uint itemId, int socket, Func<uint> newId, out string why)
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
        lock (_lock) return FreeCellCore(row, width, height, shapeOf);
    }

    (byte X, byte Y)? FreeCellCore(byte row, byte width, byte height, Func<Item, Shape?> shapeOf) =>
        FreeCellIn(row, _items.TryGetValue(row, out var l) ? l : [], width, height, shapeOf);

    static (byte X, byte Y)? FreeCellIn(byte row, List<InventoryItem> items, byte width, byte height, Func<Item, Shape?> shapeOf)
    {
        var slot = InstanceInventories.PlayerDefaults[row];
        for (int x = 0; x + width <= slot.Width; x++)
            for (int y = 0; y + height <= slot.Height; y++)
                if (!items.Any(i => shapeOf(i.Item) is { } s && i.X < x + width && x < i.X + s.Width && i.Y < y + height && y < i.Y + s.Height))
                    return ((byte)x, (byte)y);
        return null;
    }

    /// <summary>
    /// Splits <paramref name="amount"/> off the stack <paramref name="itemId"/> in <paramref name="row"/> onto the empty
    /// cursor, as a new item, in one save. Returns the stack as it is now (same id) and the held part, or null with the
    /// reason (something held, no such stack, an amount of 0 or the whole stack).
    /// </summary>
    public (InventoryItem Stack, InventoryItem Held)? Split(byte row, uint itemId, int amount, out string why)
    {
        var result = SplitCore(row, itemId, amount, out why);
        if (result is not null) Persist();
        return result;
    }

    (InventoryItem Stack, InventoryItem Held)? SplitCore(byte row, uint itemId, int amount, out string why)
    {
        lock (_lock)
        {
            why = "";
            if (row == Cursor) { why = "the cursor"; return null; }
            var cursor = ItemsIn(Cursor);
            if (cursor.Count > 0) { why = "something is held"; return null; }
            var items = ItemsIn(row);
            int at = items.FindIndex(i => i.Id == itemId);
            if (at < 0) { why = "no such item"; return null; }
            var stack = items[at];
            if (amount < 1 || amount >= stack.Item.StackSize) { why = $"cannot split {amount} off a stack of {stack.Item.StackSize}"; return null; }
            items[at] = stack with { Item = stack.Item with { StackSize = (ushort)(stack.Item.StackSize - amount) } };
            var held = new InventoryItem(_nextItemId++, 0, 0, stack.Item with { StackSize = (ushort)amount });
            cursor.Add(held);
            return (items[at], held);
        }
    }

    /// <summary>Sets the size of a stack (same id), or removes it at 0, in one save. Returns it as it is now (null when
    /// removed or not there).</summary>
    public InventoryItem? SetStackSize(byte row, uint itemId, int size)
    {
        InventoryItem? result = null;
        lock (_lock)
        {
            var items = ItemsIn(row);
            int at = items.FindIndex(i => i.Id == itemId);
            if (at < 0) return null;
            if (size <= 0) items.RemoveAt(at);
            else result = items[at] = items[at] with { Item = items[at].Item with { StackSize = (ushort)size } };
        }
        Persist();
        return result;
    }

    /// <summary>What <see cref="AddStacked"/> did: stacks that grew (same ids), new items, and how many did not fit.</summary>
    public sealed record Stacking(IReadOnlyList<InventoryItem> Changed, IReadOnlyList<InventoryItem> Added, int Left)
    {
        /// <summary>For the inventory's 0xbf: the grown stacks are removed and added again, the new items added.</summary>
        public IReadOnlyList<uint> RemovedIds => [.. Changed.Select(c => c.Id)];
        public IReadOnlyList<InventoryItem> AddedItems => [.. Changed, .. Added];
        public bool Any => Changed.Count + Added.Count > 0;
    }

    /// <summary>
    /// Puts <paramref name="item"/> (a stack of <see cref="Item.StackSize"/>) into the grid <paramref name="row"/>: first
    /// onto the stacks of its type that have room (fullest first), then as new stacks of at most its max size in free
    /// cells, in one save. What does not fit is <see cref="Stacking.Left"/>.
    /// </summary>
    public Stacking AddStacked(byte row, Item item, Func<Item, Shape?> shapeOf)
    {
        Stacking result;
        lock (_lock) result = StackInto(row, ItemsIn(row), item, shapeOf, ref _nextItemId);
        if (result.Any) Persist();
        return result;
    }

    /// <summary>Whether all of <paramref name="item"/> would fit into <paramref name="row"/> (<see cref="AddStacked"/>),
    /// changing nothing.</summary>
    public bool Fits(byte row, Item item, Func<Item, Shape?> shapeOf)
    {
        lock (_lock)
        {
            uint id = _nextItemId;
            return StackInto(row, [.. ItemsIn(row)], item, shapeOf, ref id).Left == 0;
        }
    }

    Stacking StackInto(byte row, List<InventoryItem> items, Item item, Func<Item, Shape?> shapeOf, ref uint nextId)
    {
        var shape = shapeOf(item) ?? new(1, 1, "");
        int max = Math.Max((int)shape.MaxStack, 1), left = item.StackSize;
        var changed = new List<InventoryItem>();
        var added = new List<InventoryItem>();
        if (max > 1)
            foreach (var s in items.Where(i => i.Item.TypeHash == item.TypeHash && i.Item.StackSize < max).OrderByDescending(i => i.Item.StackSize).ToList())
            {
                if (left == 0) break;
                int take = Math.Min(left, max - s.Item.StackSize);
                left -= take;
                var grown = Completed(s with { Item = s.Item with { StackSize = (ushort)(s.Item.StackSize + take) } }, shape);
                items[items.IndexOf(s)] = grown;
                changed.Add(grown);
            }
        while (left > 0 && FreeCellIn(row, items, shape.Width, shape.Height, shapeOf) is { } cell)
        {
            int take = Math.Min(left, max);
            left -= take;
            var placed = Completed(new InventoryItem(nextId++, cell.X, cell.Y, item with { StackSize = (ushort)take }), shape);
            items.Add(placed);
            added.Add(placed);
        }
        return new(changed, added, left);
    }

    /// <summary>A fresh id for an item that enters the inventories (picked up from the ground, out of a socket).</summary>
    public uint NewItemId()
    {
        lock (_lock) return _nextItemId++;
    }

    /// <summary>
    /// Picks an item up from inventory <paramref name="row"/> onto the empty cursor, keeping its id, in one step and one
    /// save, so a crash at any moment leaves the item in exactly one place. Null (nothing changes) when the cursor
    /// already holds something or the item is not in that row.
    /// </summary>
    public InventoryItem? PickUp(byte row, uint itemId)
    {
        var result = PickUpCore(row, itemId);
        if (result is not null) Persist();
        return result;
    }

    InventoryItem? PickUpCore(byte row, uint itemId)
    {
        lock (_lock)
        {
            if (row == Cursor || ItemsIn(Cursor).Count > 0 || RemoveCore(row, itemId) is not { } it) return null;
            var held = it with { X = 0, Y = 0 };
            ItemsIn(Cursor).Add(held);
            return held;
        }
    }

    /// <summary>Takes an item out of an inventory; null when it is not there.</summary>
    public InventoryItem? Remove(byte row, uint itemId)
    {
        var result = RemoveCore(row, itemId);
        if (result is not null) Persist();
        return result;
    }

    InventoryItem? RemoveCore(byte row, uint itemId)
    {
        lock (_lock)
        {
            if (!_items.TryGetValue(row, out var l) || l.FindIndex(i => i.Id == itemId) is var at && at < 0) return null;
            var it = l[at];
            l.RemoveAt(at);
            return it;
        }
    }

    /// <summary>What <see cref="Pay"/> changed in the row: emptied stacks (ids), and stacks left smaller (same ids).</summary>
    public sealed record Payment(IReadOnlyList<uint> Removed, IReadOnlyList<InventoryItem> Changed);

    /// <summary>
    /// Takes <paramref name="amount"/> of the item type <paramref name="typeHash"/> from the stacks in <paramref name="row"/>
    /// (smallest stacks first), in one step and one save. Null (nothing changes) when the row holds fewer.
    /// </summary>
    public Payment? Pay(byte row, uint typeHash, int amount)
    {
        Payment payment;
        lock (_lock)
        {
            var stacks = ItemsIn(row).Where(i => i.Item.TypeHash == typeHash).OrderBy(i => i.Item.StackSize).ToList();
            if (stacks.Sum(i => (int)i.Item.StackSize) < amount) return null;
            var removed = new List<uint>();
            var changed = new List<InventoryItem>();
            var list = ItemsIn(row);
            foreach (var s in stacks)
            {
                if (amount <= 0) break;
                int take = Math.Min(amount, s.Item.StackSize);
                amount -= take;
                int at = list.FindIndex(i => i.Id == s.Id);
                if (take == s.Item.StackSize) { list.RemoveAt(at); removed.Add(s.Id); }
                else { list[at] = s with { Item = s.Item with { StackSize = (ushort)(s.Item.StackSize - take) } }; changed.Add(list[at]); }
            }
            payment = new(removed, changed);
        }
        Persist();
        return payment;
    }

    /// <summary>The next revision number of an inventory's contents (sent with each S→C 0xbf).</summary>
    public uint NextRevision(byte row)
    {
        lock (_lock) return _revisions[row] = _revisions.GetValueOrDefault(row) + 1;
    }

    private static readonly ConcurrentDictionary<string, int> ClassRows = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The character's class (Characters.dat row), for its start kit: set when it connects, before its items
    /// are first used. Items kept in memory without a save file belonged to a deleted character of the same name (Auth
    /// sets the save aside, and every character's items are saved as soon as they exist), so they are dropped.</summary>
    public static void SetClass(string character, int characterRow)
    {
        ClassRows[character] = characterRow;
        if (CharacterSaves.Load(character) is null && ByCharacter.TryRemove(character, out _))
            Console.WriteLine($"[gateway] {character}: no save any more (deleted?), items start over");
    }

    /// <summary>The character's items, created with the class's start kit on first use.</summary>
    public static PlayerInventories For(string character, ItemCatalog catalog, string folder = "data") =>
        ByCharacter.GetOrAdd(character, name =>
        {
            var inv = FromSave(name, catalog) ?? StartKit(catalog, folder, ClassRows.GetValueOrDefault(name, 1));
            inv._owner = name;
            inv.Persist();
            return inv;
        });

    /// <summary>The character's saved items (ids and cells kept), or null without a save.</summary>
    static PlayerInventories? FromSave(string character, ItemCatalog catalog)
    {
        if (CharacterSaves.Load(character) is not { } save) return null;
        var inv = new PlayerInventories();
        foreach (var (row, items) in save.Inventories)
            foreach (var p in items)
                if (CharacterSaves.FromSaved(p.Item, catalog) is { } item)
                {
                    inv.ItemsIn(row).Add(new InventoryItem(p.Id, p.X, p.Y, item));
                    // Stash ids (AccountStash.FirstItemId up) are the account's; the character's own stay below them.
                    if (p.Id < AccountStash.FirstItemId) inv._nextItemId = Math.Max(inv._nextItemId, p.Id + 1);
                }
                else Console.WriteLine($"[gateway] {character}: saved item type 0x{p.Item.Type:x8} is unknown, dropped");
        return inv;
    }

    /// <summary>
    /// The class's start kit from its Characters.dat row (the Witch: row 1): StartWeapons[0] (Driftwood Wand) in Weapon1, the
    /// StartSkillGem and StartSupportGem (SkillGems rows → their BaseItemTypes: Fireball, Arcane Surge Support) in the main
    /// inventory, and a
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
        // A gem's socket colour: its largest attribute requirement (SkillGems Strength/Dexterity/IntelligenceRequirementPercent).
        SocketColour Colour(string column)
        {
            if (row.GetProperty(column) is not { ValueKind: JsonValueKind.Number } k) return SocketColour.Blue;
            var gem = g.RootElement[k.GetInt32()];
            int str = gem.GetProperty("StrengthRequirementPercent").GetInt32(), dex = gem.GetProperty("DexterityRequirementPercent").GetInt32(),
                @int = gem.GetProperty("IntelligenceRequirementPercent").GetInt32();
            return str >= dex && str >= @int ? SocketColour.Red : dex >= @int ? SocketColour.Green : SocketColour.Blue;
        }
        // The start weapon with two linked sockets in the start gems' colours (the Witch: blue, blue), empty, for its gems.
        if (row.GetProperty("StartWeapons").EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Number } w &&
            catalog.Row(w.GetInt32()) is { Supported: true } weapon)
            inv.Add(Weapon1, 0, 0, ItemCatalog.Create(weapon, [new(Colour("StartSkillGem")), new(Colour("StartSupportGem"))], [2]));
        Put(MainInventory, 0, 0, Gem("StartSkillGem"));
        Put(MainInventory, 1, 0, Gem("StartSupportGem"));
        Put(Flask, 0, 0, catalog.Find("Metadata/Items/Flasks/FlaskLife1"));
        Put(Flask, 1, 0, catalog.Find("Metadata/Items/Flasks/FlaskMana1"));
        return inv;
    }
}
