using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// One class: AccountStash.Folder is static.
public class AccountStashTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), $"stashes-{Guid.NewGuid():N}");
    readonly string _account = $"Acc{Guid.NewGuid():N}"[..16];

    public AccountStashTests() => AccountStash.Folder = _folder;

    public void Dispose()
    {
        AccountStash.Reset();
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    AccountStash Open() => AccountStash.For(_account, new ItemCatalog(_folder));

    static Item Wand() => new(0xd9b1a12f, ["Base"], 0x40a1);

    // Every test item is 1 × 3, like a wand.
    static PlayerInventories.Shape? Shape(Item _) => new(1, 3, "Wand");

    [Fact]
    public void A_new_account_gets_the_starter_tabs_of_several_types()
    {
        var tabs = Open().Records(null);

        Assert.True(tabs.Count >= 5);
        Assert.Equal(Enumerable.Range(0, tabs.Count).Select(i => (ushort)i), tabs.Select(t => t.Index));
        Assert.Equal(Enumerable.Range(0, tabs.Count).Select(i => AccountStash.InventoryId(i)), tabs.Select(t => t.Inventory));
        Assert.Contains(tabs, t => t.Type == 0);   // NormalStash
        Assert.Contains(tabs, t => t.Type == 1);   // PremiumStash
        Assert.Contains(tabs, t => t.Type == 7);   // QuadStash
        Assert.Contains(tabs, t => t.Type == 3);   // CurrencyStash
        Assert.All(tabs, t => Assert.False(t.Loaded));
    }

    [Fact]
    public void Only_the_last_tab_carries_the_select_affinity()
    {
        var stash = Open();
        stash.SetLastTab(2);

        var tabs = stash.Records(0);

        Assert.Equal([2], tabs.Where(t => t.Affinities != 0).Select(t => (int)t.Index));
        Assert.Equal(1u, tabs[2].Affinities);
        Assert.All(stash.Records(null), t => Assert.Equal(0u, t.Affinities));
    }

    [Fact]
    public void A_placed_item_gets_a_stash_id_and_comes_back_with_it()
    {
        var stash = Open();
        var held = new InventoryItem(7, 0, 0, Wand());

        var done = stash.Place(1, 3, 2, held, Shape, out _);

        Assert.NotNull(done);
        var (placed, displaced) = done.Value;
        Assert.Null(displaced);
        Assert.True(placed.Id >= AccountStash.FirstItemId);
        Assert.Equal((3, 2), (placed.X, placed.Y));
        Assert.Equal(placed.Id, stash.Take(1, placed.Id)?.Id);
        Assert.Empty(stash.Inventory(1).Items);
    }

    [Fact]
    public void Dropping_on_one_item_swaps_it_and_on_two_refuses()
    {
        var stash = Open();
        var a = stash.Place(0, 0, 0, new InventoryItem(1, 0, 0, Wand()), Shape, out _)!.Value.Placed;
        var b = stash.Place(0, 1, 0, new InventoryItem(2, 0, 0, Wand()), Shape, out _)!.Value.Placed;

        Assert.Null(stash.Place(0, 0, 0, new InventoryItem(3, 0, 0, new Item(0x1234, ["Base"], 0x40a1)), _ => new PlayerInventories.Shape(2, 2, "Helmet"), out string why));
        Assert.Contains("in the way", why);
        var swap = stash.Place(0, 0, 1, new InventoryItem(4, 0, 0, Wand()), Shape, out _);
        Assert.Equal(a.Id, swap!.Value.Displaced!.Id);
        Assert.Equal(2, stash.Inventory(0).Items.Count);
        Assert.Contains(stash.Inventory(0).Items, i => i.Id == b.Id);
    }

    [Fact]
    public void Outside_the_grid_is_refused()
    {
        var stash = Open();
        Assert.Null(stash.Place(0, 11, 10, new InventoryItem(1, 0, 0, Wand()), Shape, out string why));
        Assert.Equal("outside the grid", why);
    }

    [Fact]
    public void Tabs_items_and_the_last_tab_survive_a_restart_without_reusing_ids()
    {
        var stash = Open();
        var placed = stash.Place(4, 5, 5, new InventoryItem(9, 0, 0, Wand()), Shape, out _)!.Value.Placed;
        stash.Take(4, placed.Id);
        var kept = stash.Place(4, 6, 6, new InventoryItem(10, 0, 0, Wand()), Shape, out _)!.Value.Placed;
        stash.SetLastTab(4);
        AccountStash.Reset();

        // The file holds the item where it lay (this catalog has no item tables, so loading would drop its type).
        var saved = System.Text.Json.JsonSerializer.Deserialize<AccountStash.Save>(
            File.ReadAllText(Directory.GetFiles(_folder).Single()))!;
        var again = Open();

        Assert.Equal([(kept.Id, (byte)6, (byte)6)], saved.Tabs[4].Items.Select(p => (p.Id, p.X, p.Y)));
        Assert.Equal(4, again.LastTab);
        // The id taken out earlier is not handed out again.
        Assert.True(again.NewItemId() > kept.Id);
    }

    [Fact]
    public void Stash_inventory_ids_map_back_to_their_tab()
    {
        var stash = Open();
        Assert.Equal(3, stash.TabOf(AccountStash.InventoryId(3)));
        Assert.Null(stash.TabOf(1));
        Assert.Null(stash.TabOf(AccountStash.InventoryId(stash.Count)));
    }

    [Fact]
    public void Gold_is_the_accounts_and_survives_a_restart()
    {
        var stash = Open();
        Assert.Equal(0u, stash.Gold);
        Assert.Equal(500u, stash.AddGold(500));
        Assert.Equal(0u, stash.AddGold(-1000));   // never below 0
        stash.AddGold(1200);
        AccountStash.Reset();

        Assert.Equal(1200u, Open().Gold);
    }

    [Fact]
    public void Spending_gold_takes_nothing_when_there_is_not_enough()
    {
        var stash = Open();
        stash.AddGold(300);

        Assert.Null(stash.TrySpendGold(301));
        Assert.Equal(300u, stash.Gold);
        Assert.Equal(100u, stash.TrySpendGold(200));
        Assert.Equal(100u, stash.Gold);
    }
}
