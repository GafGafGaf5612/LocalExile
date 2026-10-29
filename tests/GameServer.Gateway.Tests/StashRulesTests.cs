using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

// What the special stash tabs hold (StashRules): a map tab only maps, a currency tab each currency in its own slot; items
// a tab can't show come out of it at load.
public class StashRulesTests : IDisposable
{
    internal const uint WandType = 0xd9b1a12f, ScrollType = 0x5c10aa01, MapType = 0x3a9b0001, OrbType = 0x1234abcd;
    internal const int Currency = 7, Map = 10;   // the starter tabs' indexes of those types (after the four normal tabs)
    readonly string _folder = TestSaves.Catalog(new TestSaves.Type(WandType, "Wand", 1, 3), new TestSaves.Type(ScrollType, "Scroll", StackSize: 40),
        new TestSaves.Type(MapType, "Map"), new TestSaves.Type(OrbType, "Orb", StackSize: 20));
    readonly string _account = $"Rules{Guid.NewGuid():N}"[..16];

    public StashRulesTests() => TestSaves.Ensure();

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    /// <summary>Scrolls have the currency affinity and slot 1 (a currency tab slot holds 50 of them), maps the map
    /// affinity; orbs and wands neither. The currency tab's crafting slot takes anything; two spare slots (the first two of
    /// Generic1–14) take its overflow.</summary>
    internal static StashRules Rules() => StashRules.For(
        new() { [ScrollType] = [3], [MapType] = [5] },
        new() { [3] = [0, 1, 7, 10, 3], [5] = [0, 1, 7, 10, 5] },
        new() { [StashRules.Currency] = new() { [ScrollType] = 1 } },
        new() { [ScrollType] = 50 },
        new() { [StashRules.Currency] = [CraftingSlot] },
        new() { [StashRules.Currency] = [SpareSlot1, SpareSlot2] });

    internal const int CraftingSlot = 28;   // CurrencyStashTabLayout CraftingSlot
    internal const int SpareSlot1 = 30, SpareSlot2 = 31;   // Generic1, Generic2

    internal static PlayerInventories.Shape? Shapes(Item i) => i.TypeHash switch
    {
        ScrollType => new(1, 1, "StackableCurrency", 40),
        OrbType => new(1, 1, "StackableCurrency", 20),
        MapType => new(1, 1, "Map"),
        _ => new(1, 3, "Wand"),
    };

    internal static Item Wand() => new(WandType, ["Base"], 0x40a1);
    internal static Item Scrolls(int n) => new(ScrollType, ["Base", "Stack"], 0x2222) { StackSize = (ushort)n };
    internal static Item MapItem() => new(MapType, ["Base"], 0x3333);

    AccountStash Open(StashRules? rules = null) => AccountStash.For(_account, new ItemCatalog(_folder) { Stash = rules ?? Rules() });

    [Fact]
    public void The_map_tab_takes_nothing_until_its_layout_is_known()
    {
        // The client lists a map tab's maps by tier, not at grid cells: anything saved there would not be shown.
        var stash = Open();

        Assert.Null(stash.Place(Map, 0, 0, new InventoryItem(1, 0, 0, Wand()), Shapes, out string why));
        Assert.Contains("doesn't hold", why);
        Assert.Null(stash.Place(Map, 0, 0, new InventoryItem(2, 0, 0, MapItem()), Shapes, out _));
        Assert.True(stash.Holds(0, MapType));   // a normal tab holds maps, and anything else
        Assert.False(Open(new StashRules()).Holds(Map, MapType));   // even without the tables
    }

    [Fact]
    public void Currency_dropped_anywhere_on_the_currency_tab_goes_into_its_slot()
    {
        var stash = Open();

        var placed = stash.Place(Currency, 54, 0, new InventoryItem(1, 0, 0, Scrolls(10)), Shapes, out _)!.Value.Placed;

        Assert.Equal((1, 0), (placed.X, placed.Y));
        Assert.Null(stash.Place(Currency, 3, 0, new InventoryItem(2, 0, 0, new Item(OrbType, ["Base"], 0x1)), Shapes, out string why));   // no slot for it
        Assert.Contains("doesn't hold", why);
    }

    [Fact]
    public void The_currency_tab_crafting_slot_takes_any_item_and_keeps_it_after_a_reload()
    {
        var stash = Open();

        var placed = stash.Place(Currency, CraftingSlot, 0, new InventoryItem(1, 0, 0, Wand()), Shapes, out _)!.Value.Placed;

        Assert.Equal((CraftingSlot, 0), ((int)placed.X, (int)placed.Y));
        // A second item dropped there swaps with the first (it goes onto the cursor), as in any slot.
        var (_, displaced) = stash.Place(Currency, CraftingSlot, 0, new InventoryItem(2, 0, 0, Wand()), Shapes, out _)!.Value;
        Assert.Equal(placed.Id, displaced!.Id);
        // The load-time repair leaves it there (it would move an item a special tab can't show).
        AccountStash.Forget(_account);
        Assert.Equal((CraftingSlot, 0), Open().Inventory(Currency).Items.Select(i => ((int)i.X, (int)i.Y)).Single());
    }

    [Fact]
    public void What_its_own_slot_has_no_room_for_goes_into_the_spare_slots_in_order()
    {
        var stash = Open();
        stash.Place(Currency, 1, 0, new InventoryItem(1, 0, 0, Scrolls(40)), Shapes, out _);   // the own slot holds 50

        var stacked = stash.AddStacked(Currency, Scrolls(70), Shapes, out _)!;   // 10 + 50 + 10

        Assert.Equal(0, stacked.Left);
        Assert.Equal([(SpareSlot1, 50), (SpareSlot2, 10)], stacked.Added.Select(i => ((int)i.X, (int)i.Item.StackSize)));
        // Next time the spare slot with room is topped up before anything new.
        var again = stash.AddStacked(Currency, Scrolls(5), Shapes, out _)!;
        Assert.Equal((SpareSlot2, 15), ((int)again.Changed.Single().X, (int)again.Changed.Single().Item.StackSize));
        Assert.Equal(3, Open().Inventory(Currency).Items.Count);
    }

    [Fact]
    public void When_every_spare_slot_is_full_the_rest_stays_out()
    {
        var stash = Open();

        var stacked = stash.AddStacked(Currency, Scrolls(170), Shapes, out _)!;   // own 50 + two spare 50 each

        Assert.Equal(20, stacked.Left);
        Assert.Equal(3, stacked.Added.Count);
    }

    [Fact]
    public void Currency_dropped_on_a_spare_slot_stays_there_and_other_items_are_refused()
    {
        var stash = Open();

        var placed = stash.Place(Currency, SpareSlot2, 0, new InventoryItem(1, 0, 0, Scrolls(10)), Shapes, out _)!.Value.Placed;

        Assert.Equal((SpareSlot2, 0), ((int)placed.X, (int)placed.Y));
        Assert.Null(stash.Place(Currency, SpareSlot1, 0, new InventoryItem(2, 0, 0, Wand()), Shapes, out string why));   // not a currency
        Assert.Contains("doesn't hold", why);
    }

    [Fact]
    public void Currency_in_the_crafting_slot_stacks_only_to_its_own_size()
    {
        var stash = Open();
        stash.Place(Currency, CraftingSlot, 0, new InventoryItem(1, 0, 0, Scrolls(30)), Shapes, out _);

        var (stack, moved) = stash.Merge(Currency, CraftingSlot, 0, new InventoryItem(2, 0, 0, Scrolls(30)), Shapes)!.Value;

        Assert.Equal((10, 40), (moved, (int)stack.Item.StackSize));   // the scroll's own 40, not the slot's 50
        Assert.Equal((CraftingSlot, 0), ((int)stack.X, (int)stack.Y));
    }

    [Fact]
    public void A_currency_slot_holds_more_than_a_stack()
    {
        var stash = Open();
        stash.Place(Currency, 1, 0, new InventoryItem(1, 0, 0, Scrolls(40)), Shapes, out _);

        var (stack, moved) = stash.Merge(Currency, 77, 0, new InventoryItem(2, 0, 0, Scrolls(30)), Shapes)!.Value;

        Assert.Equal(10, moved);   // up to the slot's 50, not the item's 40 … and then the slot is full
        Assert.Equal(50, stack.Item.StackSize);
        Assert.Null(stash.Merge(Currency, 1, 0, new InventoryItem(3, 0, 0, Scrolls(1)), Shapes));
    }

    [Fact]
    public void Items_a_tab_cannot_show_come_out_of_it_at_load()
    {
        // As the live test left them: a wand in the map tab, and a scroll outside its slot in the currency tab.
        Open();
        AccountStash.Forget(_account);
        Saves.Db.PutStashTab(_account, Map, JsonSerializer.Serialize(new List<CharacterSaves.SavedPlacement>
            { new(0x40000005, 0, 0, CharacterSaves.ToSaved(Wand())), new(0x40000006, 0, 3, CharacterSaves.ToSaved(MapItem())) }, CharacterSaves.Opts));
        Saves.Db.PutStashTab(_account, Currency, JsonSerializer.Serialize(new List<CharacterSaves.SavedPlacement>
            { new(0x40000007, 54, 0, CharacterSaves.ToSaved(Scrolls(12))) }, CharacterSaves.Opts));

        var stash = Open();

        Assert.Empty(stash.Inventory(Map).Items);                                     // the map tab shows nothing yet
        Assert.Contains(stash.Inventory(0).Items, i => i.Id == 0x40000005);           // the wand, in the first normal tab
        Assert.Contains(stash.Inventory(0).Items, i => i.Id == 0x40000006);           // the map too
        Assert.Equal([(0x40000007u, (byte)1, (byte)0)], stash.Inventory(Currency).Items.Select(i => (i.Id, i.X, i.Y)));
        // Saved so: a restart finds them there.
        AccountStash.Forget(_account);
        var again = Open();
        Assert.Contains(again.Inventory(0).Items, i => i.Id == 0x40000005);
        Assert.Equal((byte)1, again.Inventory(Currency).Items.Single().X);
    }

    [Fact]
    public void Without_the_tables_every_tab_holds_anything_and_nothing_moves()
    {
        var stash = Open(new StashRules());

        Assert.NotNull(stash.Place(Currency, 0, 0, new InventoryItem(1, 0, 0, Wand()), Shapes, out _));
        AccountStash.Forget(_account);
        Assert.Single(Open(new StashRules()).Inventory(Currency).Items);
    }

    [Fact]
    public void Affinities_set_in_the_tab_settings_are_kept()
    {
        var stash = Open();

        Assert.Equal(1u << 3, stash.SetAffinities(Currency, [(3, true)]));
        Assert.Equal(0u, stash.SetAffinities(Currency, [(3, false)]));
        stash.SetAffinities(1, [(5, true), (0, true)]);   // affinity 0 is never stored (the panel's own selection bit)
        AccountStash.Forget(_account);
        Assert.Equal(1u << 5, Open().Records(null)[1].Affinities);
    }

    [Fact]
    public void The_real_tables_put_a_scroll_of_wisdom_in_slot_1_and_keep_it_out_of_the_map_tab()
    {
        if (FindData() is not { } data) return;   // without the extracted game data there is nothing to check
        var catalog = new ItemCatalog(data);
        var scroll = catalog.Find("Metadata/Items/Currency/CurrencyIdentification")!;
        var rules = catalog.Stash;

        Assert.True(rules.Loaded);
        Assert.Equal(1, rules.SlotOf(StashRules.Currency, scroll.Hash));
        Assert.Equal<ushort?>(10000, rules.MaxStack(StashRules.Currency, scroll.Hash));
        Assert.False(rules.Holds(StashRules.Map, scroll.Hash));
        Assert.True(rules.Holds(0, scroll.Hash));
    }

    static string? FindData()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data", "tables", "StashTabAffinities.json"))) return Path.Combine(dir.FullName, "data");
        return null;
    }
}
