using GameServer.Core.Protocol.Instance;

/// <summary>
/// Item changes that touch more than one save (the character's items and the account's stash) or one save more than
/// once (a currency item used up and the item it changed). Each runs in one transaction (<see cref="Saves"/>): it is
/// saved whole or not at all, so an item is never lost between its two places, nor counted in both. Checks come before
/// the first change; the handlers in Program.cs send the client what changed afterwards.
/// </summary>
static class ItemMoves
{
    const byte Cursor = PlayerInventories.Cursor, Main = PlayerInventories.MainInventory;

    /// <summary>An item out of a stash tab onto the empty cursor (C→S 0x28), keeping its id. Null (nothing changes) when
    /// something is held or the item is not in the tab.</summary>
    public static InventoryItem? StashToCursor(AccountStash stash, PlayerInventories items, int tab, uint itemId) => Saves.Atomically(() =>
    {
        if (items.Items(Cursor).Count > 0 || stash.Take(tab, itemId) is not { } taken) return null;
        stash.SetLastTab(tab);   // the player works in this tab: it is on screen
        return items.Put(Cursor, taken with { X = 0, Y = 0 });
    });

    /// <summary>An item out of a stash tab into the main inventory's first free cell (C→S 0xe3), keeping its id. Null
    /// (nothing changes) when it is not there, its type is unknown or the inventory has no room.</summary>
    public static InventoryItem? StashToInventory(AccountStash stash, PlayerInventories items, int tab, uint itemId,
        Func<Item, PlayerInventories.Shape?> shapeOf) => Saves.Atomically(() =>
    {
        if (tab < 0 || tab >= stash.Count || stash.Inventory(tab).Items.FirstOrDefault(i => i.Id == itemId) is not { } inTab
            || shapeOf(inTab.Item) is not { } shape || items.FreeCell(Main, shape.Width, shape.Height, shapeOf) is not { } cell
            || stash.Take(tab, itemId) is not { } taken) return null;
        stash.SetLastTab(tab);
        return items.Put(Main, taken with { X = cell.X, Y = cell.Y });
    });

    /// <summary>An item out of whichever inventory holds it into a stash tab's first free cell (C→S 0xe0), with a stash id.
    /// Returns the row it left and the item as placed, or null with the reason (nothing changes).</summary>
    public static (byte From, InventoryItem Removed, InventoryItem Placed)? InventoryToStash(AccountStash stash, PlayerInventories items, int tab,
        uint itemId, Func<Item, PlayerInventories.Shape?> shapeOf, out string why)
    {
        string reason = "";
        var result = Saves.Atomically<(byte, InventoryItem, InventoryItem)?>(() =>
        {
            if (tab < 0 || tab >= stash.Count) { reason = "no such tab"; return null; }
            if (items.RowOf(itemId) is not byte from || items.Items(from).FirstOrDefault(i => i.Id == itemId) is not { } item)
            { reason = "the item is not in the inventories"; return null; }
            if (shapeOf(item.Item) is not { } shape) { reason = "unknown item type"; return null; }
            if (stash.FreeCell(tab, shape.Width, shape.Height, shapeOf) is not { } cell) { reason = "the tab is full"; return null; }
            var removed = items.Remove(from, itemId)!;
            if (stash.Place(tab, cell.X, cell.Y, removed, shapeOf, out reason) is not var (placed, _))
            {
                items.Put(from, removed);   // in the same transaction: nothing changes
                return null;
            }
            stash.SetLastTab(tab);
            return (from, removed, placed);
        });
        why = reason;
        return result;
    }

    /// <summary>What <see cref="HeldToStash"/> did. <see cref="Merged"/>: the held stack went onto <see cref="InTab"/>
    /// (same id, larger). <see cref="One"/>: one of the held stack went into an empty cell as <see cref="InTab"/>.
    /// Otherwise the held item is <see cref="InTab"/> and <see cref="Displaced"/> (the one it was dropped on) is held now.
    /// <see cref="Held"/> is what the cursor holds after (null: nothing).</summary>
    public sealed record StashPlacement(InventoryItem InTab, InventoryItem HeldBefore, InventoryItem? Held, InventoryItem? Displaced,
        bool Merged, bool One);

    /// <summary>
    /// The held item into a stash tab at (x, y) (C→S 0x2a): onto a stack of its type with room (with <paramref name="one"/>,
    /// one of it), else with <paramref name="one"/> one of the held stack into an empty cell, else the whole item, swapping
    /// the one item it was dropped on onto the cursor. Null with the reason (nothing changes).
    /// </summary>
    public static StashPlacement? HeldToStash(AccountStash stash, PlayerInventories items, int tab, int x, int y, bool one,
        Func<Item, PlayerInventories.Shape?> shapeOf, out string why)
    {
        string reason = "";
        var result = Saves.Atomically(() =>
        {
            if (items.Items(Cursor) is not [var held, ..]) { reason = "nothing held"; return null; }
            one &= held.Item.StackSize > 1;
            if (stash.Merge(tab, x, y, held, shapeOf, one ? 1 : int.MaxValue) is var (stack, moved))
                return new StashPlacement(stack, held, items.SetStackSize(Cursor, held.Id, held.Item.StackSize - moved), null, Merged: true, One: one);
            if (one)
            {
                if (stash.Inventory(tab).Items.Any(i => shapeOf(i.Item) is { } s && i.X <= x && x < i.X + s.Width && i.Y <= y && y < i.Y + s.Height))
                { reason = "something in the way"; return null; }
                var single = new InventoryItem(items.NewItemId(), 0, 0, held.Item with { StackSize = 1 });
                if (stash.Place(tab, x, y, single, shapeOf, out reason) is not var (put, _)) return null;
                return new StashPlacement(put, held, items.SetStackSize(Cursor, held.Id, held.Item.StackSize - 1), null, Merged: false, One: true);
            }
            if (stash.Place(tab, x, y, held, shapeOf, out reason) is not var (placed, displaced)) return null;
            stash.SetLastTab(tab);
            items.Remove(Cursor, held.Id);
            if (displaced is not null) items.Put(Cursor, displaced);
            return new StashPlacement(placed, held, displaced, displaced, Merged: false, One: false);
        });
        why = reason;
        return result;
    }

    /// <summary>Splits <paramref name="amount"/> off a stash tab's stack onto the empty cursor (C→S 0x7c), as a new
    /// character item. Returns the tab's stack as it is now and the held part, or null (nothing changes).</summary>
    public static (InventoryItem Left, InventoryItem Part)? SplitFromStash(AccountStash stash, PlayerInventories items, int tab, uint itemId, int amount) =>
        Saves.Atomically<(InventoryItem, InventoryItem)?>(() =>
        {
            if (items.Items(Cursor).Count > 0 || tab < 0 || tab >= stash.Count
                || stash.Inventory(tab).Items.FirstOrDefault(i => i.Id == itemId) is not { } source
                || stash.Shrink(tab, itemId, amount) is not { } left) return null;
            var part = items.Put(Cursor, new InventoryItem(items.NewItemId(), 0, 0, source.Item with { StackSize = (ushort)amount }));
            return (left, part);
        });

    /// <summary>
    /// A purchase (C→S 0x88): the price (<paramref name="price"/>: currency type and amount, or null when free) is taken
    /// from the main inventory's stacks and the item goes to <paramref name="row"/> at its cell, together. Null when the
    /// price can't be paid (nothing changes).
    /// </summary>
    public static (PlayerInventories.Payment? Paid, InventoryItem Bought)? Buy(PlayerInventories items, byte row, InventoryItem item,
        (uint Currency, int Amount)? price) =>
        Saves.Atomically<(PlayerInventories.Payment?, InventoryItem)?>(() =>
        {
            PlayerInventories.Payment? paid = null;
            if (price is var (currency, amount) && (paid = items.Pay(Main, currency, amount)) is null) return null;
            return (paid, items.Put(row, item));
        });

    /// <summary>An Orb of Regret used (C→S 0x58): one leaves its stack and the character has <paramref name="refundPoints"/>
    /// refund points, together. Null when the item is gone (nothing changes).</summary>
    public static PlayerInventories.Consumption? UseForRefundPoint(PlayerInventories items, string character, byte row, uint itemId, ushort refundPoints) =>
        Saves.Atomically(() =>
        {
            if (items.ConsumeOne(row, itemId) is not { } taken) return null;
            CharacterSaves.SetRefundPoints(character, refundPoints);
            return taken;
        });

    /// <summary>A currency item used on an item (C→S 0x71): one leaves its stack and the target becomes
    /// <paramref name="change"/>(target), together. Null when either is gone (nothing changes).</summary>
    public static (PlayerInventories.Consumption Used, InventoryItem Changed)? UseOn(PlayerInventories items, byte usedRow, uint usedId,
        byte targetRow, uint targetId, Func<Item, Item> change) =>
        Saves.Atomically<(PlayerInventories.Consumption, InventoryItem)?>(() =>
        {
            if (!items.Items(targetRow).Any(i => i.Id == targetId) || items.ConsumeOne(usedRow, usedId) is not { } used) return null;
            return (used, items.Change(targetRow, targetId, change)!);
        });
}
