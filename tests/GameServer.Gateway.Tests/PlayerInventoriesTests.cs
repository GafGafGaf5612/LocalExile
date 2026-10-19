using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// One class: CharacterSaves.Folder is static, so these tests must not run in parallel with other save tests.
public class PlayerInventoriesTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), $"saves-{Guid.NewGuid():N}");
    readonly string _character = $"Picker{Guid.NewGuid():N}"[..20];

    public PlayerInventoriesTests() => CharacterSaves.Folder = _folder;

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    // No item tables in this folder: the character starts with no kit, and items are kept by their type hash.
    PlayerInventories NewInventories() => PlayerInventories.For(_character, new ItemCatalog(_folder));

    static Item Wand() => new(0xd9b1a12f, ["Base"], 0x40a1);

    [Fact]
    public void Picking_up_moves_the_item_to_the_cursor_with_its_id_in_one_save()
    {
        var items = NewInventories();
        var placed = items.Add(PlayerInventories.MainInventory, 3, 1, Wand());

        var held = items.PickUp(PlayerInventories.MainInventory, placed.Id);

        Assert.NotNull(held);
        Assert.Equal(placed.Id, held.Id);
        Assert.Empty(items.Items(PlayerInventories.MainInventory));
        Assert.Equal([placed.Id], items.Items(PlayerInventories.Cursor).Select(i => i.Id));
        // What a crash right now would leave on disk: the item once, on the cursor.
        var saved = CharacterSaves.Load(_character)!.Inventories;
        Assert.False(saved.ContainsKey(PlayerInventories.MainInventory));
        Assert.Equal([placed.Id], saved[PlayerInventories.Cursor].Select(p => p.Id));
    }

    [Fact]
    public void Picking_up_while_holding_an_item_changes_nothing()
    {
        var items = NewInventories();
        var first = items.Add(PlayerInventories.MainInventory, 0, 0, Wand());
        var second = items.Add(PlayerInventories.MainInventory, 2, 0, Wand());
        items.PickUp(PlayerInventories.MainInventory, first.Id);

        Assert.Null(items.PickUp(PlayerInventories.MainInventory, second.Id));
        Assert.Equal([second.Id], items.Items(PlayerInventories.MainInventory).Select(i => i.Id));
        Assert.Equal([first.Id], items.Items(PlayerInventories.Cursor).Select(i => i.Id));
        Assert.Equal([second.Id], CharacterSaves.Load(_character)!.Inventories[PlayerInventories.MainInventory].Select(p => p.Id));
    }

    [Fact]
    public void Picking_up_an_item_that_is_not_there_changes_nothing()
    {
        var items = NewInventories();
        var placed = items.Add(PlayerInventories.MainInventory, 0, 0, Wand());

        Assert.Null(items.PickUp(PlayerInventories.MainInventory, placed.Id + 100));
        Assert.Null(items.PickUp(PlayerInventories.Flask, placed.Id));
        Assert.Equal([placed.Id], items.Items(PlayerInventories.MainInventory).Select(i => i.Id));
        Assert.Empty(items.Items(PlayerInventories.Cursor));
    }

    // Orbs of Transmutation: 1×1, 40 to a stack.
    const uint Orb = 0x1234abcd;
    static Item Orbs(int n) => new(Orb, ["Base", "Usable", "Stack"], 0x1111) { StackSize = (ushort)n };
    static PlayerInventories.Shape? Shapes(Item i) => i.TypeHash == Orb ? new(1, 1, "StackableCurrency", 40) : new(1, 3, "Wand");

    InventoryItem Hold(PlayerInventories items, Item item)
    {
        var placed = items.Add(PlayerInventories.MainInventory, 11, 4, item);
        return items.PickUp(PlayerInventories.MainInventory, placed.Id)!;
    }

    [Fact]
    public void A_held_stack_fills_a_stack_of_its_type_and_the_rest_stays_held()
    {
        var items = NewInventories();
        var under = items.Add(PlayerInventories.MainInventory, 0, 0, Orbs(30));
        var held = Hold(items, Orbs(15));

        var done = items.Place(PlayerInventories.MainInventory, 0, 0, Shapes, out _);

        Assert.NotNull(done);
        Assert.True(done.Merged);
        Assert.Null(done.Displaced);
        Assert.Equal((under.Id, (ushort)40), (done.Placed.Id, done.Placed.Item.StackSize));
        Assert.Equal((held.Id, (ushort)5), (done.Rest!.Id, done.Rest.Item.StackSize));
        Assert.Equal([5], items.Items(PlayerInventories.Cursor).Select(i => (int)i.Item.StackSize));
        Assert.Equal([40], CharacterSaves.Load(_character)!.Inventories[PlayerInventories.MainInventory].Select(p => (int)p.Item.StackSize));
    }

    [Fact]
    public void A_held_stack_that_fits_entirely_leaves_the_cursor_empty()
    {
        var items = NewInventories();
        items.Add(PlayerInventories.MainInventory, 0, 0, Orbs(10));
        Hold(items, Orbs(15));

        var done = items.Place(PlayerInventories.MainInventory, 0, 0, Shapes, out _);

        Assert.True(done!.Merged);
        Assert.Null(done.Rest);
        Assert.Empty(items.Items(PlayerInventories.Cursor));
        Assert.Equal([25], items.Items(PlayerInventories.MainInventory).Select(i => (int)i.Item.StackSize));
    }

    [Fact]
    public void A_full_stack_or_another_type_is_swapped()
    {
        var items = NewInventories();
        var full = items.Add(PlayerInventories.MainInventory, 0, 0, Orbs(40));
        var held = Hold(items, Orbs(3));

        var done = items.Place(PlayerInventories.MainInventory, 0, 0, Shapes, out _);

        Assert.False(done!.Merged);
        Assert.Equal(full.Id, done.Displaced!.Id);
        Assert.Equal(held.Id, done.Placed.Id);
    }

    [Fact]
    public void Splitting_holds_a_new_stack_and_shrinks_the_old_one()
    {
        var items = NewInventories();
        var stack = items.Add(PlayerInventories.MainInventory, 2, 1, Orbs(20));

        var done = items.Split(PlayerInventories.MainInventory, stack.Id, 7, out _);

        Assert.NotNull(done);
        var (left, held) = done.Value;
        Assert.Equal((stack.Id, (ushort)13), (left.Id, left.Item.StackSize));
        Assert.NotEqual(stack.Id, held.Id);
        Assert.Equal(7, held.Item.StackSize);
        Assert.Equal([held.Id], items.Items(PlayerInventories.Cursor).Select(i => i.Id));
        var saved = CharacterSaves.Load(_character)!.Inventories;
        Assert.Equal([13], saved[PlayerInventories.MainInventory].Select(p => (int)p.Item.StackSize));
        Assert.Equal([7], saved[PlayerInventories.Cursor].Select(p => (int)p.Item.StackSize));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(21)]
    public void Splitting_nothing_or_the_whole_stack_is_refused(int amount)
    {
        var items = NewInventories();
        var stack = items.Add(PlayerInventories.MainInventory, 0, 0, Orbs(20));

        Assert.Null(items.Split(PlayerInventories.MainInventory, stack.Id, amount, out _));
        Assert.Equal([20], items.Items(PlayerInventories.MainInventory).Select(i => (int)i.Item.StackSize));
        Assert.Empty(items.Items(PlayerInventories.Cursor));
    }

    [Fact]
    public void Splitting_while_holding_an_item_is_refused()
    {
        var items = NewInventories();
        var stack = items.Add(PlayerInventories.MainInventory, 0, 0, Orbs(20));
        Hold(items, Wand());

        Assert.Null(items.Split(PlayerInventories.MainInventory, stack.Id, 5, out _));
    }

    [Fact]
    public void Added_stacks_fill_the_fullest_stack_first_then_free_cells()
    {
        var items = NewInventories();
        var small = items.Add(PlayerInventories.MainInventory, 0, 0, Orbs(5));
        var big = items.Add(PlayerInventories.MainInventory, 1, 0, Orbs(35));

        var done = items.AddStacked(PlayerInventories.MainInventory, Orbs(90), Shapes);

        Assert.Equal(0, done.Left);
        Assert.Equal([(big.Id, 40), (small.Id, 40)], done.Changed.Select(c => (c.Id, (int)c.Item.StackSize)));
        Assert.Equal([40, 10], done.Added.Select(a => (int)a.Item.StackSize));
        Assert.Equal(90 + 40, items.Items(PlayerInventories.MainInventory).Sum(i => i.Item.StackSize));
        Assert.Equal([big.Id, small.Id], done.RemovedIds);
    }

    [Fact]
    public void What_does_not_fit_is_left_and_fits_says_so_first()
    {
        var items = NewInventories();
        // Fill the 12×5 main inventory with full stacks except one cell holding 30.
        for (byte x = 0; x < 12; x++)
            for (byte y = 0; y < 5; y++)
                items.Add(PlayerInventories.MainInventory, x, y, Orbs(x == 0 && y == 0 ? 30 : 40));

        Assert.True(items.Fits(PlayerInventories.MainInventory, Orbs(10), Shapes));
        Assert.False(items.Fits(PlayerInventories.MainInventory, Orbs(11), Shapes));
        Assert.Equal(30, items.Items(PlayerInventories.MainInventory)[0].Item.StackSize);   // Fits changed nothing

        var done = items.AddStacked(PlayerInventories.MainInventory, Orbs(15), Shapes);
        Assert.Equal(5, done.Left);
        Assert.Empty(done.Added);
        Assert.Equal(40, items.Items(PlayerInventories.MainInventory)[0].Item.StackSize);
    }
}
