using GameServer.Core.Protocol.Instance;
using static GameServer.Gateway.Tests.StashRulesTests;

namespace GameServer.Gateway.Tests;

// Ctrl + click: currency into its affinity tab's slot, items out of the stash onto the inventory's stacks, buying from a
// shop; each one transaction, and with a write made to fail, nothing moves.
[Collection(TestSaves.FailWriteCollection)]
public class CtrlClickTests : IDisposable
{
    readonly string _folder = TestSaves.Catalog(new TestSaves.Type(WandType, "Wand", 1, 3), new TestSaves.Type(ScrollType, "Scroll", StackSize: 40),
        new TestSaves.Type(MapType, "Map"), new TestSaves.Type(OrbType, "Orb", StackSize: 20));
    readonly string _character = $"Ctrl{Guid.NewGuid():N}"[..20];
    readonly string _account = $"Ctrl{Guid.NewGuid():N}"[..16];
    const byte Main = PlayerInventories.MainInventory;

    public CtrlClickTests() => TestSaves.Ensure();

    public void Dispose()
    {
        Saves.FailWrite = null;
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    ItemCatalog Catalog() => new(_folder) { Stash = Rules() };
    PlayerInventories Items() => PlayerInventories.For(_character, Catalog());
    AccountStash Stash() => AccountStash.For(_account, Catalog());
    (PlayerInventories Items, AccountStash Stash) Reload()
    {
        PlayerInventories.Forget(_character);
        AccountStash.Forget(_account);
        return (Items(), Stash());
    }

    [Fact]
    public void Currency_goes_into_its_slot_of_the_currency_tab_onto_what_is_there()
    {
        var (items, stash) = (Items(), Stash());
        stash.Place(Currency, 1, 0, new InventoryItem(1, 0, 0, Scrolls(5)), Shapes, out _);
        var scrolls = items.Add(Main, 3, 2, Scrolls(12));

        var moved = ItemMoves.InventoryToStash(stash, items, Currency, scrolls.Id, Shapes, out _)!;

        Assert.Equal(17, moved.TabChanged.Single().Item.StackSize);
        Assert.Null(moved.LeftInInventory);
        var (again, stashAgain) = Reload();
        Assert.Empty(again.Items(Main));
        Assert.Equal([(1, 0, 17)], stashAgain.Inventory(Currency).Items.Select(i => ((int)i.X, (int)i.Y, (int)i.Item.StackSize)));
    }

    [Fact]
    public void What_a_full_slot_cannot_take_stays_in_the_inventory()
    {
        var (items, stash) = (Items(), Stash());
        stash.Place(Currency, 1, 0, new InventoryItem(1, 0, 0, Scrolls(40)), Shapes, out _);
        var scrolls = items.Add(Main, 3, 2, Scrolls(30));

        var moved = ItemMoves.InventoryToStash(stash, items, Currency, scrolls.Id, Shapes, out _)!;

        Assert.Equal(20, moved.LeftInInventory!.Item.StackSize);   // the slot holds 50
        var (again, stashAgain) = Reload();
        Assert.Equal(20, again.Items(Main).Single().Item.StackSize);
        Assert.Equal(50, stashAgain.Inventory(Currency).Items.Single().Item.StackSize);
        Assert.Null(ItemMoves.InventoryToStash(stashAgain, again, Currency, scrolls.Id, Shapes, out string why));   // full now
        Assert.Equal("the tab is full", why);
    }

    [Fact]
    public void Shift_placing_one_on_a_full_currency_slot_changes_nothing()
    {
        var (items, stash) = (Items(), Stash());
        stash.Place(Currency, 1, 0, new InventoryItem(1, 0, 0, Scrolls(40)), Shapes, out _);
        stash.Merge(Currency, 1, 0, new InventoryItem(2, 0, 0, Scrolls(10)), Shapes);   // the slot's 50: full
        items.Put(PlayerInventories.Cursor, new InventoryItem(items.NewItemId(), 0, 0, Scrolls(7)));

        // Dropped anywhere on the tab, one of the held stack: its slot is full, so nothing moves (nothing is swapped out).
        Assert.Null(ItemMoves.HeldToStash(stash, items, Currency, 30, 0, one: true, Shapes, out _));

        var (again, stashAgain) = Reload();
        Assert.Equal(50, stashAgain.Inventory(Currency).Items.Single().Item.StackSize);
        Assert.Equal(7, again.Items(PlayerInventories.Cursor).Single().Item.StackSize);
    }

    [Fact]
    public void A_tab_that_does_not_hold_the_item_changes_nothing()
    {
        var (items, stash) = (Items(), Stash());
        var wand = items.Add(Main, 0, 0, Wand());

        Assert.Null(ItemMoves.InventoryToStash(stash, items, Map, wand.Id, Shapes, out string why));
        Assert.Contains("doesn't hold", why);
        Assert.Single(items.Items(Main));
        Assert.Empty(stash.Inventory(Map).Items);
    }

    [Fact]
    public void A_stack_taken_from_the_stash_tops_up_the_inventorys_stack_first()
    {
        var (items, stash) = (Items(), Stash());
        var own = items.Add(Main, 0, 0, Scrolls(30));
        var inTab = stash.Place(0, 0, 0, new InventoryItem(1, 0, 0, Scrolls(25)), Shapes, out _)!.Value.Placed;

        var moved = ItemMoves.StashToInventory(stash, items, 0, inTab.Id, Shapes)!;

        Assert.Equal(40, moved.InventoryChanged.Single(i => i.Id == own.Id).Item.StackSize);
        Assert.Equal(15, moved.InventoryAdded.Single().Item.StackSize);
        var (again, stashAgain) = Reload();
        Assert.Equal([15, 40], again.Items(Main).Select(i => (int)i.Item.StackSize).Order());
        Assert.Empty(stashAgain.Inventory(0).Items);
    }

    [Fact]
    public void Taking_a_stack_when_only_part_of_it_fits_leaves_the_rest_in_the_tab()
    {
        var (items, stash) = (Items(), Stash());
        // A full inventory (12 x 5) but for one scroll stack with room for 10.
        for (byte x = 0; x < 12; x++)
            for (byte y = 0; y < 5; y++) items.Add(Main, x, y, x == 0 && y == 0 ? Scrolls(30) : Scrolls(40) with { TypeHash = MapType, StackSize = 1 });
        var inTab = stash.Place(0, 0, 0, new InventoryItem(1, 0, 0, Scrolls(25)), Shapes, out _)!.Value.Placed;

        var moved = ItemMoves.StashToInventory(stash, items, 0, inTab.Id, Shapes)!;

        Assert.Equal(15, moved.LeftInTab!.Item.StackSize);
        var (again, stashAgain) = Reload();
        Assert.Equal(40, again.Items(Main).Single(i => i.Item.TypeHash == ScrollType).Item.StackSize);
        Assert.Equal(15, stashAgain.Inventory(0).Items.Single().Item.StackSize);
    }

    [Fact]
    public void A_ctrl_click_to_the_stash_whose_save_fails_moves_nothing()
    {
        var (items, stash) = (Items(), Stash());
        var scrolls = items.Add(Main, 3, 2, Scrolls(12));
        int n = 0;
        Saves.FailWrite = k => k.Equals(_character, StringComparison.OrdinalIgnoreCase) && ++n >= 1;   // the stash's write goes, the character's fails

        Assert.ThrowsAny<IOException>(() => ItemMoves.InventoryToStash(stash, items, Currency, scrolls.Id, Shapes, out _));
        Saves.FailWrite = null;

        var (again, stashAgain) = Reload();
        Assert.Equal(12, again.Items(Main).Single().Item.StackSize);
        Assert.Empty(stashAgain.Inventory(Currency).Items);
    }

    [Fact]
    public void Ctrl_click_buy_pays_and_stacks_the_item_together()
    {
        var items = Items();
        items.Add(Main, 0, 0, new Item(OrbType, ["Base", "Stack"], 0x1) { StackSize = 10 });
        items.Add(Main, 1, 0, Scrolls(30));

        var bought = ItemMoves.BuyToInventory(items, null, Scrolls(5), (OrbType, 3), Shapes, out _);

        Assert.NotNull(bought);
        var (again, _) = Reload();
        Assert.Equal(7, again.Items(Main).Single(i => i.Item.TypeHash == OrbType).Item.StackSize);
        Assert.Equal(35, again.Items(Main).Single(i => i.Item.TypeHash == ScrollType).Item.StackSize);
    }

    [Fact]
    public void A_price_is_paid_from_the_inventory_first_and_the_rest_from_the_stash()
    {
        var (items, stash) = (Items(), Stash());
        items.Add(Main, 0, 0, Scrolls(1));
        stash.Place(Currency, 1, 0, new InventoryItem(1, 0, 0, Scrolls(5)), Shapes, out _);

        var bought = ItemMoves.BuyToInventory(items, stash, Wand(), (ScrollType, 3), Shapes, out _);

        Assert.Equal(3, bought!.Value.Paid!.Stash!.Tabs[Currency].Changed.Single().Item.StackSize);   // 1 from the inventory, 2 of the stash's 5
        var (again, stashAgain) = Reload();
        Assert.Equal([WandType], again.Items(Main).Select(i => i.Item.TypeHash));   // the scroll in the inventory went
        Assert.Equal(3, stashAgain.Inventory(Currency).Items.Single().Item.StackSize);
        // A click (0x88) pays the same way; with too little in both, nothing is taken.
        Assert.NotNull(ItemMoves.Buy(again, stashAgain, Main, new InventoryItem(again.NewItemId(), 4, 0, Wand()), (ScrollType, 3)));
        Assert.Null(ItemMoves.Buy(again, stashAgain, Main, new InventoryItem(again.NewItemId(), 6, 0, Wand()), (ScrollType, 1)));
        Assert.Empty(Reload().Stash.Inventory(Currency).Items);
    }

    [Fact]
    public void A_click_on_a_currency_slot_picks_up_the_whole_stack()
    {
        // The client sends a plain click on a special tab's item as a split of the whole stack (0x7c, live 2026-10-20).
        var (items, stash) = (Items(), Stash());
        var inSlot = stash.Place(Currency, 1, 0, new InventoryItem(1, 0, 0, Scrolls(16)), Shapes, out _)!.Value.Placed;

        var (left, held) = ItemMoves.SplitFromStash(stash, items, Currency, inSlot.Id, 16)!.Value;

        Assert.Null(left);
        Assert.Equal((inSlot.Id, 16), (held.Id, (int)held.Item.StackSize));
        var (again, stashAgain) = Reload();
        Assert.Empty(stashAgain.Inventory(Currency).Items);
        Assert.Equal(inSlot.Id, again.Items(PlayerInventories.Cursor).Single().Id);
    }

    [Fact]
    public void Ctrl_click_buy_without_room_or_money_takes_nothing()
    {
        var items = Items();
        items.Add(Main, 0, 0, new Item(OrbType, ["Base", "Stack"], 0x1) { StackSize = 2 });

        Assert.Null(ItemMoves.BuyToInventory(items, null, Wand(), (OrbType, 3), Shapes, out string why));
        Assert.Equal("not enough to pay", why);
        for (byte x = 1; x < 12; x++) items.Add(Main, x, 0, Wand());
        for (byte y = 3; y < 5; y++)
            for (byte x = 0; x < 12; x++) items.Add(Main, x, y, MapItem());
        Assert.Null(ItemMoves.BuyToInventory(items, null, Wand(), null, Shapes, out why));
        Assert.Equal("no room in the inventory", why);
        Assert.Equal(2, Reload().Items.Items(Main).Single(i => i.Item.TypeHash == OrbType).Item.StackSize);
    }

    [Fact]
    public void A_ctrl_click_buy_whose_item_cannot_be_saved_takes_no_price()
    {
        var items = Items();
        items.Add(Main, 0, 0, new Item(OrbType, ["Base", "Stack"], 0x1) { StackSize = 10 });
        int n = 0;
        Saves.FailWrite = k => k.Equals(_character, StringComparison.OrdinalIgnoreCase) && ++n >= 2;   // the payment's write goes, the item's fails

        Assert.ThrowsAny<IOException>(() => ItemMoves.BuyToInventory(items, null, Wand(), (OrbType, 3), Shapes, out _));
        Saves.FailWrite = null;

        Assert.Equal(10, Reload().Items.Items(Main).Single().Item.StackSize);
    }
}
