using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class CurrencyStashStackTests : IDisposable
{
    const uint Lifeforce = 0x12340001, Scroll = 0x12340002;
    const int Tab = StashRulesTests.Currency;
    readonly string _folder = TestSaves.Catalog(new(Lifeforce, "Lifeforce", StackSize: 50000),
        new(Scroll, "Scroll", StackSize: 40));
    readonly string _account = "currency-" + Guid.NewGuid().ToString("N");
    readonly ItemCatalog _catalog;

    public CurrencyStashStackTests()
    {
        TestSaves.Ensure();
        void Write(string name, object rows) => File.WriteAllText(Path.Combine(_folder, "tables", name + ".json"),
            JsonSerializer.Serialize(rows));
        foreach (string name in new[] { "StashTabAffinityByBaseItemType", "StashtabAffinityByItemClassCategory",
            "ItemClasses", "EssenceStashTabLayout", "FragmentStashTabLayout", "DelveStashTabLayout",
            "BlightStashTabLayout", "DeliriumStashTabLayout", "DivinationCardStashTabLayout" }) Write(name, Array.Empty<object>());
        Write("StashTabAffinities", new[] { new { Id = 3, __col2 = new[] { 3, 0 } } });
        // The layout Id need not match the base Id: StoredItem is the authoritative row reference.
        Write("CurrencyStashTabLayout", new[] {
            new { Id = "HarvestSlot", StoredItem = 0, FirstSlotIndex = 99 },
            new { Id = "ScrollSlot", StoredItem = 1, FirstSlotIndex = 1 } });
        Write("CurrencyItems", new[] {
            new { BaseItemTypesKey = 0, StackSize = 50000, CurrencyTab_StackSize = 50000, FullStack_BaseItemTypesKey = (int?)null },
            new { BaseItemTypesKey = 1, StackSize = 40, CurrencyTab_StackSize = 10000, FullStack_BaseItemTypesKey = (int?)null } });
        _catalog = new ItemCatalog(_folder);
    }

    PlayerInventories.Shape? Shape(Item item) => _catalog.ByHash(item.TypeHash) is { } t
        ? new(t.Width, t.Height, t.Class, t.MaxStack) : null;
    AccountStash Open() => AccountStash.For(_account, _catalog);

    [Fact]
    public void Currency_limits_come_from_the_table_and_grid_tabs_keep_the_item_limit()
    {
        Assert.Equal<ushort?>(50000, _catalog.Stash.MaxStack(StashRules.Currency, Lifeforce));
        Assert.Equal<ushort?>(10000, _catalog.Stash.MaxStack(StashRules.Currency, Scroll));
        Assert.Equal<ushort?>(5000, _catalog.Stash.MaxStack(StashRules.Essence, Scroll));
        Assert.Null(_catalog.Stash.MaxStack(0, Lifeforce));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lifeforce_merges_past_5000_stops_at_50000_and_survives_reload(bool quickMove)
    {
        var stash = Open();
        var item = ItemCatalog.Create(_catalog.ByHash(Lifeforce)!);
        var first = stash.AddStacked(Tab, item with { StackSize = 5000 }, Shape, out _)!;
        Assert.Equal(0, first.Left);
        var held = item with { StackSize = 45001 };
        if (quickMove)
        {
            var moved = stash.AddStacked(Tab, held, Shape, out _)!;
            Assert.Equal(1, moved.Left);
            Assert.Empty(moved.Added);
            Assert.Equal(50000, Assert.Single(moved.Changed).Item.StackSize);
        }
        else
        {
            var moved = stash.Merge(Tab, 99, 0, new(1, 0, 0, held), Shape);
            Assert.NotNull(moved);
            Assert.Equal(45000, moved.Value.Moved);
            Assert.Equal(50000, moved.Value.Stack.Item.StackSize);
            Assert.Null(stash.Merge(Tab, 99, 0, new(2, 0, 0, item), Shape));
        }
        AccountStash.Forget(_account);
        var saved = Assert.Single(Open().Inventory(Tab).Items);
        Assert.Equal(99, saved.X);
        Assert.Equal(50000, saved.Item.StackSize);
    }

    public void Dispose()
    {
        AccountStash.Forget(_account);
        Directory.Delete(_folder, recursive: true);
    }
}
