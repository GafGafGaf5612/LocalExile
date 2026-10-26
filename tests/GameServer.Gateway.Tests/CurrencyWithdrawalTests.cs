using GameServer.Core.Protocol.Instance;
using static GameServer.Gateway.Tests.StashRulesTests;

namespace GameServer.Gateway.Tests;

[Collection(TestSaves.FailWriteCollection)]
public class CurrencyWithdrawalTests : IDisposable
{
    readonly string _folder = TestSaves.Catalog(new TestSaves.Type(ScrollType, "Scroll", StackSize: 40),
        new TestSaves.Type(MapType, "Map"));
    readonly string _key = $"Withdraw{Guid.NewGuid():N}"[..24];
    const byte Main = PlayerInventories.MainInventory;

    public CurrencyWithdrawalTests() => TestSaves.Ensure();

    public void Dispose()
    {
        Saves.FailWrite = null;
        Directory.Delete(_folder, recursive: true);
    }

    ItemCatalog Catalog() => new(_folder)
    {
        Stash = StashRules.For(new() { [ScrollType] = [3] }, new() { [3] = [StashRules.Currency] },
            new() { [StashRules.Currency] = new() { [ScrollType] = 1 } }, new() { [ScrollType] = 5000 }),
    };

    (PlayerInventories Items, AccountStash Stash) Open() =>
        (PlayerInventories.For(_key, Catalog()), AccountStash.For(_key, Catalog()));

    (PlayerInventories Items, AccountStash Stash) Reload()
    {
        PlayerInventories.Forget(_key);
        AccountStash.Forget(_key);
        return Open();
    }

    static InventoryItem Deposit(AccountStash stash, int amount) =>
        stash.Place(Currency, 1, 0, new InventoryItem(1, 0, 0, Scrolls(amount)), Shapes, out _)!.Value.Placed;

    static void FillExceptFirst(PlayerInventories items)
    {
        for (byte x = 0; x < 12; x++)
            for (byte y = 0; y < 5; y++)
                if (x != 0 || y != 0) items.Add(Main, x, y, MapItem());
    }

    [Theory]
    [InlineData(25, 25, 0)]
    [InlineData(40, 40, 0)]
    [InlineData(200, 40, 160)]
    [InlineData(5000, 40, 4960)]
    public void A_left_click_withdraws_at_most_one_inventory_stack(int source, int expected, int remaining)
    {
        var (items, stash) = Open();
        var inTab = Deposit(stash, source);

        var moved = ItemMoves.StashToInventory(stash, items, Currency, inTab.Id, Shapes)!;

        Assert.Equal(expected, moved.InventoryAdded.Single().Item.StackSize);
        Assert.Equal(remaining, moved.LeftInTab?.Item.StackSize ?? 0);
        var (again, stashAgain) = Reload();
        Assert.Equal(expected, again.Items(Main).Single().Item.StackSize);
        Assert.Equal(remaining, stashAgain.Inventory(Currency).Items.Sum(i => i.Item.StackSize));
        if (remaining > 0) Assert.Equal(inTab.Id, stashAgain.Inventory(Currency).Items.Single().Id);
    }

    [Fact]
    public void Topping_up_an_existing_stack_counts_towards_the_one_stack_withdrawal()
    {
        var (items, stash) = Open();
        var own = items.Add(Main, 0, 0, Scrolls(30));
        var inTab = Deposit(stash, 200);

        var moved = ItemMoves.StashToInventory(stash, items, Currency, inTab.Id, Shapes)!;

        Assert.Equal(40, moved.InventoryChanged.Single(i => i.Id == own.Id).Item.StackSize);
        Assert.Equal(30, moved.InventoryAdded.Single().Item.StackSize);
        Assert.Equal(160, moved.LeftInTab!.Item.StackSize);
        var (again, stashAgain) = Reload();
        Assert.Equal([30, 40], again.Items(Main).Select(i => (int)i.Item.StackSize).Order());
        Assert.Equal(160, stashAgain.Inventory(Currency).Items.Single().Item.StackSize);
    }

    [Theory]
    [InlineData(0, 40, 160)]
    [InlineData(30, 40, 190)]
    [InlineData(40, 40, 200)]
    public void Limited_inventory_capacity_leaves_unmoved_currency_in_the_stash(int existing, int final, int remaining)
    {
        var (items, stash) = Open();
        FillExceptFirst(items);
        if (existing > 0) items.Add(Main, 0, 0, Scrolls(existing));
        var inTab = Deposit(stash, 200);

        var moved = ItemMoves.StashToInventory(stash, items, Currency, inTab.Id, Shapes);

        if (existing == 40) Assert.Null(moved);
        else Assert.Equal(remaining, moved!.LeftInTab!.Item.StackSize);
        var (again, stashAgain) = Reload();
        Assert.Equal(final, again.Items(Main).Single(i => i.Item.TypeHash == ScrollType).Item.StackSize);
        Assert.Equal(remaining, stashAgain.Inventory(Currency).Items.Single().Item.StackSize);
    }

    [Fact]
    public void A_failed_stash_write_rolls_back_the_inventory_withdrawal()
    {
        var (items, stash) = Open();
        var inTab = Deposit(stash, 200);
        int writes = 0;
        // Inventory persists first; fail the following stash write, after the inventory has changed.
        Saves.FailWrite = key => key.Equals(_key, StringComparison.OrdinalIgnoreCase) && ++writes == 2;

        Assert.ThrowsAny<IOException>(() => ItemMoves.StashToInventory(stash, items, Currency, inTab.Id, Shapes));
        Saves.FailWrite = null;

        var (again, stashAgain) = Reload();
        Assert.Empty(again.Items(Main));
        Assert.Equal(200, stashAgain.Inventory(Currency).Items.Single().Item.StackSize);
    }

    [Theory]
    [InlineData(200, 200, 5, 0)]
    [InlineData(5000, 2400, 60, 2600)]
    public void A_right_click_withdraws_as_much_of_the_slot_as_fits(int source, int expected, int stacks, int remaining)
    {
        var (items, stash) = Open();
        var inTab = Deposit(stash, source);

        var moved = ItemMoves.StashToInventory(stash, items, Currency, inTab.Id, Shapes, all: true)!;

        Assert.Equal(stacks, moved.InventoryAdded.Count);
        Assert.Equal(expected, moved.InventoryAdded.Sum(i => i.Item.StackSize));
        Assert.Equal(remaining, moved.LeftInTab?.Item.StackSize ?? 0);
        var (again, stashAgain) = Reload();
        Assert.Equal(expected, again.Items(Main).Sum(i => i.Item.StackSize));
        Assert.Equal(remaining, stashAgain.Inventory(Currency).Items.Sum(i => i.Item.StackSize));
    }
}
