using GameServer.Core.Protocol.Instance;
using static GameServer.Gateway.Tests.StashRulesTests;

namespace GameServer.Gateway.Tests;

// /item_tab: every item type with a slot of its own on a slotted tab gets up to the asked amount more, each capped by
// what its slot holds (one of an item that doesn't stack), in one save.
public class ItemTabCommandTests : IDisposable
{
    const int Normal = 0;
    readonly string _folder = TestSaves.Catalog(new TestSaves.Type(WandType, "Wand", 1, 3), new TestSaves.Type(ScrollType, "Scroll", StackSize: 40),
        new TestSaves.Type(MapType, "Map"), new TestSaves.Type(OrbType, "Orb", StackSize: 20));
    readonly string _account = $"ItemTab{Guid.NewGuid():N}"[..16];

    public ItemTabCommandTests() => TestSaves.Ensure();

    public void Dispose()
    {
        AccountStash.Forget(_account);
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    // Scrolls: currency-tab limit 50; orbs: no item-specific limit (StashRules.SlotStack); a wand doesn't stack.
    // As StashRules.Load does, every slotted type has the tab's affinity, so a reload keeps it there.
    static StashRules Rules() => StashRules.For(
        new() { [ScrollType] = [3], [OrbType] = [3], [WandType] = [3] },
        new() { [3] = [0, 1, 7, 10, StashRules.Currency] },
        new() { [StashRules.Currency] = new() { [ScrollType] = 1, [OrbType] = 2, [WandType] = 3 } },
        new() { [ScrollType] = 50 });

    AccountStash Open() => AccountStash.For(_account, new ItemCatalog(_folder) { Stash = Rules() });

    static Item? Create(uint type) => type switch
    {
        ScrollType => Scrolls(1),
        OrbType => new Item(OrbType, ["Base", "Stack"], 0x1) { StackSize = 1 },
        WandType => Wand(),
        _ => null,
    };

    static int StackAt(AccountStash stash, int slot) =>
        stash.Inventory(Currency).Items.Where(i => i.X == slot && i.Y == 0).Sum(i => i.Item.StackSize);

    [Fact]
    public void Each_slot_gets_the_amount_up_to_what_it_holds_and_it_survives_a_reload()
    {
        var stash = Open();

        var first = stash.FillSlots(Currency, 30, Create, Shapes, out _)!;
        Assert.Equal(3, first.Added.Count);
        Assert.Equal((30, 30, 1), (StackAt(stash, 1), StackAt(stash, 2), StackAt(stash, 3)));

        var second = stash.FillSlots(Currency, 1000, Create, Shapes, out _)!;
        Assert.Equal(2, second.Changed.Count);
        Assert.Empty(second.Added);
        Assert.Equal(1, second.Full);   // the wand: one is all its slot takes
        Assert.Equal((50, 1030, 1), (StackAt(stash, 1), StackAt(stash, 2), StackAt(stash, 3)));
        Assert.Equal(first.Added.Single(i => i.X == 2).Id, second.Changed.Single(i => i.X == 2).Id);   // grown in place

        Assert.Equal(2, stash.FillSlots(Currency, 1000, Create, Shapes, out _)!.Full);
        AccountStash.Forget(_account);
        var reloaded = Open();
        Assert.Equal((50, 2030, 1), (StackAt(reloaded, 1), StackAt(reloaded, 2), StackAt(reloaded, 3)));
    }

    [Fact]
    public void Types_the_server_cannot_create_are_skipped()
    {
        var stash = Open();

        var fill = stash.FillSlots(Currency, 5, t => t == OrbType ? null : Create(t), Shapes, out _)!;

        Assert.Equal(1, fill.Skipped);
        Assert.Equal(0, StackAt(stash, 2));
        Assert.Equal(5, StackAt(stash, 1));
    }

    [Fact]
    public void A_slot_beyond_a_byte_cell_is_skipped_instead_of_wrapping()
    {
        var rules = StashRules.For(new() { [ScrollType] = [3], [OrbType] = [3] }, new() { [3] = [StashRules.Currency] },
            new() { [StashRules.Currency] = new() { [ScrollType] = 1, [OrbType] = 256 + 1 } });
        var stash = AccountStash.For(_account, new ItemCatalog(_folder) { Stash = rules });

        var fill = stash.FillSlots(Currency, 5, Create, Shapes, out _)!;

        Assert.Equal(1, fill.Skipped);
        Assert.Equal(5, StackAt(stash, 1));   // not 10: the orb did not wrap into slot 1
    }

    [Fact]
    public void Tabs_without_a_slot_per_type_are_refused_unchanged()
    {
        var stash = Open();

        Assert.Null(stash.FillSlots(Normal, 10, Create, Shapes, out string why));
        Assert.Contains("no slot for each item type", why);
        Assert.Null(stash.FillSlots(Map, 10, Create, Shapes, out why));
        Assert.Contains("doesn't show", why);
        Assert.Empty(stash.Inventory(Normal).Items);
    }

    [Fact]
    public void The_tab_is_found_by_its_name_or_its_only_type()
    {
        var stash = Open();

        Assert.Equal(Currency, Program.ItemTabOf(stash, "currency"));
        Assert.Equal(Currency, Program.ItemTabOf(stash, "CurrencyStash"));
        Assert.Equal(Map, Program.ItemTabOf(stash, "MAP"));
        Assert.Null(Program.ItemTabOf(stash, "Normal"));   // four NormalStash tabs: which one is ambiguous
        Assert.Null(Program.ItemTabOf(stash, "nothing like it"));
    }
}
