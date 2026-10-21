using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// Item changes that touch the character's items and the stash (or one save twice): each is one transaction. The failure
// tests make one of its writes fail, as a full disk would, then load both saves again as a restarted Gateway would: every
// item must be in exactly one place, as it was before the change.
[Collection(TestSaves.FailWriteCollection)]
public class ItemMovesTests : IDisposable
{
    const uint WandType = 0xd9b1a12f, OrbType = 0x1234abcd;
    readonly string _folder = TestSaves.Catalog(new(WandType, "Wand", 1, 3), new(OrbType, "Orb", StackSize: 40));
    readonly string _character = $"Mover{Guid.NewGuid():N}"[..20];
    readonly string _account = $"Acc{Guid.NewGuid():N}"[..16];
    const byte Main = PlayerInventories.MainInventory, Cursor = PlayerInventories.Cursor;

    public ItemMovesTests() => TestSaves.Ensure();

    public void Dispose()
    {
        Saves.FailWrite = null;
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    ItemCatalog Catalog() => new(_folder);
    PlayerInventories Items() => PlayerInventories.For(_character, Catalog());
    AccountStash Stash() => AccountStash.For(_account, Catalog());

    // A restarted Gateway: both loaded again from the database.
    (PlayerInventories Items, AccountStash Stash) Reload()
    {
        PlayerInventories.Forget(_character);
        AccountStash.Forget(_account);
        return (Items(), Stash());
    }

    static Item Wand() => new(WandType, ["Base"], 0x40a1);
    static Item Orbs(int n) => new(OrbType, ["Base", "Stack"], 0x1111) { StackSize = (ushort)n };
    static PlayerInventories.Shape? Shapes(Item i) => i.TypeHash == OrbType ? new(1, 1, "StackableCurrency", 40) : new(1, 3, "Wand");

    // Writes of the save under this key fail (from the nth on, counting from 1).
    void FailWrites(string key, int from = 1)
    {
        int n = 0;
        Saves.FailWrite = k => k.Equals(key, StringComparison.OrdinalIgnoreCase) && ++n >= from;
    }

    static List<uint> Ids(IEnumerable<InventoryItem> items) => [.. items.Select(i => i.Id).Order()];

    InventoryItem InStash(AccountStash stash, Item item, int tab = 0, int x = 0, int y = 0) =>
        stash.Place(tab, x, y, new InventoryItem(1, 0, 0, item), Shapes, out _)!.Value.Placed;

    [Fact]
    public void Without_a_transaction_a_failed_second_write_loses_the_item()
    {
        // The control: the same failure with the two writes on their own (how moves worked before) loses the item, so the
        // failure tests below would see a loss.
        var (items, stash) = (Items(), Stash());
        var wand = InStash(stash, Wand());
        FailWrites(_character);

        stash.Take(0, wand.Id);
        Assert.ThrowsAny<IOException>(() => items.Put(Cursor, wand));
        Saves.FailWrite = null;

        var (again, stashAgain) = Reload();
        Assert.Empty(stashAgain.Inventory(0).Items);
        Assert.Empty(again.Items(Cursor));
    }

    [Fact]
    public void Stash_to_cursor_saves_both_sides()
    {
        var (items, stash) = (Items(), Stash());
        var wand = InStash(stash, Wand(), tab: 2);

        var held = ItemMoves.StashToCursor(stash, items, 2, wand.Id);

        Assert.Equal(wand.Id, held!.Id);
        var (again, stashAgain) = Reload();
        Assert.Empty(stashAgain.Inventory(2).Items);
        Assert.Equal([wand.Id], Ids(again.Items(Cursor)));
        Assert.Equal(2, stashAgain.LastTab);
    }

    [Fact]
    public void Stash_to_cursor_keeps_the_item_in_the_stash_when_the_character_cannot_be_saved()
    {
        var (items, stash) = (Items(), Stash());
        var wand = InStash(stash, Wand());
        FailWrites(_character);

        Assert.ThrowsAny<IOException>(() => ItemMoves.StashToCursor(stash, items, 0, wand.Id));
        Saves.FailWrite = null;

        var (again, stashAgain) = Reload();
        Assert.Equal([wand.Id], Ids(stashAgain.Inventory(0).Items));
        Assert.Empty(again.Items(Cursor));
    }

    [Fact]
    public void Stash_to_inventory_keeps_the_item_in_the_stash_when_the_character_cannot_be_saved()
    {
        var (items, stash) = (Items(), Stash());
        var wand = InStash(stash, Wand());
        FailWrites(_character);

        Assert.ThrowsAny<IOException>(() => ItemMoves.StashToInventory(stash, items, 0, wand.Id, Shapes));
        Saves.FailWrite = null;

        var (again, stashAgain) = Reload();
        Assert.Equal([wand.Id], Ids(stashAgain.Inventory(0).Items));
        Assert.Empty(again.Items(Main));
    }

    [Fact]
    public void Inventory_to_stash_saves_both_sides_with_a_stash_id()
    {
        var (items, stash) = (Items(), Stash());
        var wand = items.Add(Main, 0, 0, Wand());

        var moved = ItemMoves.InventoryToStash(stash, items, 1, wand.Id, Shapes, out _);

        Assert.True(moved!.TabAdded.Single().Id >= AccountStash.FirstItemId);
        var (again, stashAgain) = Reload();
        Assert.Empty(again.Items(Main));
        Assert.Equal([moved.TabAdded.Single().Id], Ids(stashAgain.Inventory(1).Items));
    }

    [Fact]
    public void Inventory_to_stash_keeps_the_item_in_the_inventory_when_the_stash_cannot_be_saved()
    {
        var (items, stash) = (Items(), Stash());
        var wand = items.Add(Main, 0, 0, Wand());
        FailWrites(_account);

        Assert.ThrowsAny<IOException>(() => ItemMoves.InventoryToStash(stash, items, 1, wand.Id, Shapes, out _));
        Saves.FailWrite = null;

        var (again, stashAgain) = Reload();
        Assert.Equal([wand.Id], Ids(again.Items(Main)));
        Assert.Empty(stashAgain.Inventory(1).Items);
    }

    [Fact]
    public void A_swap_with_a_stash_item_keeps_both_when_the_character_cannot_be_saved()
    {
        var (items, stash) = (Items(), Stash());
        var inTab = InStash(stash, Wand());
        var held = items.Put(Cursor, new InventoryItem(items.NewItemId(), 0, 0, Wand()));
        FailWrites(_character);

        Assert.ThrowsAny<IOException>(() => ItemMoves.HeldToStash(stash, items, 0, 0, 0, one: false, Shapes, out _));
        Saves.FailWrite = null;

        var (again, stashAgain) = Reload();
        Assert.Equal([inTab.Id], Ids(stashAgain.Inventory(0).Items));
        Assert.Equal([held.Id], Ids(again.Items(Cursor)));
    }

    [Fact]
    public void A_swap_with_a_stash_item_saves_both_sides()
    {
        var (items, stash) = (Items(), Stash());
        var inTab = InStash(stash, Wand());
        var held = items.Put(Cursor, new InventoryItem(items.NewItemId(), 0, 0, Wand()));

        var done = ItemMoves.HeldToStash(stash, items, 0, 0, 0, one: false, Shapes, out _)!;

        Assert.Equal(inTab.Id, done.Displaced!.Id);
        var (again, stashAgain) = Reload();
        Assert.Equal([done.InTab.Id], Ids(stashAgain.Inventory(0).Items));
        Assert.Equal([inTab.Id], Ids(again.Items(Cursor)));
        Assert.NotEqual(held.Id, inTab.Id);
    }

    [Fact]
    public void A_held_stack_merged_into_the_stash_keeps_its_count_when_the_character_cannot_be_saved()
    {
        var (items, stash) = (Items(), Stash());
        var under = InStash(stash, Orbs(30));
        items.Put(Cursor, new InventoryItem(items.NewItemId(), 0, 0, Orbs(15)));
        FailWrites(_character);

        Assert.ThrowsAny<IOException>(() => ItemMoves.HeldToStash(stash, items, 0, 0, 0, one: false, Shapes, out _));
        Saves.FailWrite = null;

        var (again, stashAgain) = Reload();
        Assert.Equal(30, stashAgain.Inventory(0).Items.Single(i => i.Id == under.Id).Item.StackSize);
        Assert.Equal(15, again.Items(Cursor).Single().Item.StackSize);
    }

    [Fact]
    public void Splitting_a_stash_stack_keeps_it_whole_when_the_character_cannot_be_saved()
    {
        var (items, stash) = (Items(), Stash());
        var stack = InStash(stash, Orbs(20));
        FailWrites(_character);

        Assert.ThrowsAny<IOException>(() => ItemMoves.SplitFromStash(stash, items, 0, stack.Id, 5, Shapes));
        Saves.FailWrite = null;

        var (again, stashAgain) = Reload();
        Assert.Equal(20, stashAgain.Inventory(0).Items.Single().Item.StackSize);
        Assert.Empty(again.Items(Cursor));
    }

    [Fact]
    public void A_purchase_pays_and_gives_the_item_together()
    {
        var items = Items();
        items.Add(Main, 0, 0, Orbs(10));
        var wand = new InventoryItem(items.NewItemId(), 4, 0, Wand());

        var bought = ItemMoves.Buy(items, null, Main, wand, (OrbType, 3));

        Assert.NotNull(bought);
        var (again, _) = Reload();
        Assert.Equal(7, again.Items(Main).Single(i => i.Item.TypeHash == OrbType).Item.StackSize);
        Assert.Contains(again.Items(Main), i => i.Id == wand.Id);
        // Not enough: nothing changes.
        Assert.Null(ItemMoves.Buy(again, null, Main, new InventoryItem(again.NewItemId(), 6, 0, Wand()), (OrbType, 8)));
        Assert.Equal(2, Reload().Items.Items(Main).Count);
    }

    [Fact]
    public void A_purchase_whose_item_cannot_be_saved_takes_no_price()
    {
        var items = Items();
        items.Add(Main, 0, 0, Orbs(10));
        FailWrites(_character, from: 2);   // the payment's write goes through, the item's fails

        Assert.ThrowsAny<IOException>(() => ItemMoves.Buy(items, null, Main, new InventoryItem(items.NewItemId(), 4, 0, Wand()), (OrbType, 3)));
        Saves.FailWrite = null;

        var (again, _) = Reload();
        Assert.Equal(10, again.Items(Main).Single().Item.StackSize);
    }

    [Fact]
    public void A_currency_item_used_on_an_item_is_spent_only_with_its_effect()
    {
        var items = Items();
        var scrolls = items.Add(Main, 0, 0, Orbs(3));
        var wand = items.Add(Main, 2, 0, Wand() with { Identified = false });
        FailWrites(_character, from: 2);   // one scroll gone, the identification not saved

        Assert.ThrowsAny<IOException>(() => ItemMoves.UseOn(items, Main, scrolls.Id, Main, wand.Id, i => i with { Identified = true }));
        Saves.FailWrite = null;
        var (again, _) = Reload();
        Assert.Equal(3, again.Items(Main).Single(i => i.Id == scrolls.Id).Item.StackSize);
        Assert.False(again.Items(Main).Single(i => i.Id == wand.Id).Item.Identified);

        Assert.NotNull(ItemMoves.UseOn(again, Main, scrolls.Id, Main, wand.Id, i => i with { Identified = true }));
        var (after, _) = Reload();
        Assert.Equal(2, after.Items(Main).Single(i => i.Id == scrolls.Id).Item.StackSize);
        Assert.True(after.Items(Main).Single(i => i.Id == wand.Id).Item.Identified);
    }

    [Fact]
    public void An_orb_of_regret_is_spent_only_with_its_refund_point()
    {
        var items = Items();
        var orb = items.Add(Main, 0, 0, Orbs(2));
        FailWrites(_character, from: 2);

        Assert.ThrowsAny<IOException>(() => ItemMoves.UseForRefundPoint(items, _character, Main, orb.Id, 1));
        Saves.FailWrite = null;
        Assert.Equal(2, Reload().Items.Items(Main).Single().Item.StackSize);
        Assert.Null(CharacterSaves.Load(_character)!.RefundPoints);

        Assert.NotNull(ItemMoves.UseForRefundPoint(Items(), _character, Main, orb.Id, 1));
        Assert.Equal(1, Reload().Items.Items(Main).Single().Item.StackSize);
        Assert.Equal((ushort)1, CharacterSaves.Load(_character)!.RefundPoints);
    }

    [Fact]
    public void After_a_failed_save_the_old_copy_refuses_changes_and_the_next_use_loads_the_saved_items()
    {
        var items = Items();
        var wand = items.Add(Main, 0, 0, Wand());
        FailWrites(_character);
        Assert.ThrowsAny<IOException>(() => items.Remove(Main, wand.Id));
        Saves.FailWrite = null;

        Assert.Throws<InvalidOperationException>(() => items.Add(Main, 4, 0, Wand()));
        var fresh = Items();
        Assert.NotSame(items, fresh);
        Assert.Equal([wand.Id], Ids(fresh.Items(Main)));
    }
}
