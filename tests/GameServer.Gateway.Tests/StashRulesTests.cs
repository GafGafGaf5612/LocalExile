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
    /// affinity; orbs and wands neither.</summary>
    internal static StashRules Rules() => StashRules.For(
        new() { [ScrollType] = [3], [MapType] = [5] },
        new() { [3] = [0, 1, 7, 10, 3], [5] = [0, 1, 7, 10, 5] },
        new() { [StashRules.Currency] = new() { [ScrollType] = 1 } },
        new() { [ScrollType] = 50 });

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
        Assert.Equal(StashRules.SlotStack, rules.MaxStack(StashRules.Currency, scroll.Hash));
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
