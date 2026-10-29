using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class DelveStashTests
{
    static ItemCatalog Catalog => BenchCraftingTests.Setup.Value.Catalog;
    static PlayerInventories.Shape? Shape(Item item) => Catalog.ByHash(item.TypeHash) is { } t
        ? new(t.Width, t.Height, t.Class, t.MaxStack) : null;

    [Theory]
    [InlineData("Upgrade", 1)] [InlineData("Upgrade", 2)] [InlineData("Upgrade", 3)] [InlineData("Upgrade", 4)]
    [InlineData("Reroll", 1)] [InlineData("Reroll", 2)] [InlineData("Reroll", 3)] [InlineData("Reroll", 4)]
    public void Empty_legacy_resonators_migrate_to_stackable_with_exact_unlinked_delve_sockets(string family, int count)
    {
        string legacyId = $"Metadata/Items/Delve/DelveSocketableCurrency{family}{count}";
        var legacy = Enumerable.Range(294, 8).Select(Catalog.Row).Single(t => t?.Id == legacyId)!;
        var old = new CharacterSaves.SavedItem(legacy.Hash,
            Sockets: [new(SocketColour.White), new(SocketColour.White)], Links: [2]);
        var item = CharacterSaves.FromSaved(old, Catalog);
        var type = Catalog.ByHash(item.TypeHash)!;
        Assert.Contains("DelveStackableSocketable", type.Id);
        Assert.True(type.Stackable);
        Assert.Equal(type.Hash, Catalog.Find(legacyId)!.Hash);
        Assert.Equal(count, item.Sockets.Count);
        Assert.All(item.Sockets, socket => Assert.Equal(SocketColour.Delve, socket.Colour));
        Assert.Null(item.Links);
        Assert.Equal(count, ItemCatalog.Create(type).Sockets.Count);
        Assert.Equal(count, Catalog.DefaultSockets(type).Sockets.Count);
        Assert.Equal(item.TypeHash, CharacterSaves.FromSaved(CharacterSaves.ToSaved(item), Catalog).TypeHash);
    }

    [Fact]
    public void Filled_legacy_resonator_keeps_its_type_and_embedded_fossil()
    {
        var legacy = Catalog.Row(298)!;
        var fossil = Catalog.Row(269)!;
        var saved = new CharacterSaves.SavedItem(legacy.Hash,
            Sockets: [new(SocketColour.Delve, CharacterSaves.ToSaved(ItemCatalog.Create(fossil)))]);
        var item = CharacterSaves.FromSaved(saved, Catalog);
        Assert.Equal(legacy.Hash, item.TypeHash);
        Assert.Equal(fossil.Hash, Assert.Single(item.Sockets).Gem!.TypeHash);
    }

    [Fact]
    public void Every_delve_layout_item_stacks_to_5000_in_its_own_valid_cell_and_survives_reload()
    {
        TestSaves.Ensure();
        string account = "delve-" + Guid.NewGuid().ToString("N");
        var stash = AccountStash.For(account, Catalog);
        const int tab = 13;
        Assert.Equal("DelveStash", stash.TypeOf(tab));
        Assert.Equal((41, 4), ((int)stash.Inventory(tab).Slot.Width, (int)stash.Inventory(tab).Slot.Height));
        using var layout = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(BenchCraftingTests.Data(), "tables/DelveStashTabLayout.json")));
        foreach (var row in layout.RootElement.EnumerateArray().Where(r => r.GetProperty("StoredItem").ValueKind == JsonValueKind.Number))
        {
            var type = Catalog.Row(row.GetProperty("StoredItem").GetInt32())!;
            int slot = row.GetProperty("FirstSlotIndex").GetInt32();
            var item = ItemCatalog.Create(type);
            var first = stash.AddStacked(tab, item with { StackSize = 4990 }, Shape, out string why);
            Assert.NotNull(first);
            Assert.Equal("", why);
            Assert.Equal(0, first.Left);
            Assert.Equal(slot, Assert.Single(first.Added).X);
            var merge = stash.Merge(tab, slot, 0, new(1, 0, 0, item with { StackSize = 10 }), Shape);
            Assert.NotNull(merge);
            Assert.Equal(10, merge.Value.Moved);
            Assert.Equal(5000, merge.Value.Stack.Item.StackSize);
        }
        var before = stash.Inventory(tab).Items;
        Assert.Equal(before.Count, before.Select(i => i.X).Distinct().Count());
        Assert.All(before, i => Assert.True(i.X < 41 && i.Y == 0));
        AccountStash.Forget(account);
        var after = AccountStash.For(account, Catalog).Inventory(tab).Items;
        Assert.Equal(before.Count, after.Count);
        Assert.All(after, i => Assert.Equal(5000, i.Item.StackSize));
    }

    [Theory]
    [InlineData(37)]
    [InlineData(39)]
    public void Delve_crafting_cells_accept_weapons_swap_and_survive_reload(int slot)
    {
        TestSaves.Ensure();
        string account = "delve-gear-" + Guid.NewGuid().ToString("N");
        var stash = AccountStash.For(account, Catalog);
        const int tab = 13;
        var weapon = Catalog.Find("Driftwood Wand")!;
        Assert.NotNull(weapon);
        var item = ItemCatalog.Create(weapon);
        var placed = stash.Place(tab, slot, 0, new(1, 0, 0, item), Shape, out string why);
        Assert.NotNull(placed);
        Assert.Equal("", why);
        Assert.Equal(slot, placed.Value.Placed.X);
        var swap = stash.Place(tab, slot, 0, new(2, 0, 0, item), Shape, out _);
        Assert.NotNull(swap);
        Assert.Equal(placed.Value.Placed.Id, swap.Value.Displaced!.Id);
        AccountStash.Forget(account);
        var restored = Assert.Single(AccountStash.For(account, Catalog).Inventory(tab).Items);
        Assert.Equal(slot, restored.X);
        Assert.Equal(item.TypeHash, restored.Item.TypeHash);
    }

    [Theory]
    [InlineData("HarvestSeedRed", 50000)]
    [InlineData("HarvestSeedGreen", 50000)]
    [InlineData("HarvestSeedBlue", 50000)]
    [InlineData("HarvestSeedBoss", 10000)]
    [InlineData("HarvestSeedFaridun", 10000)]
    public void Harvest_currency_uses_its_table_limit_preserves_overflow_and_survives_reload(string id, int limit)
    {
        TestSaves.Ensure();
        string account = "lifeforce-" + Guid.NewGuid().ToString("N");
        var stash = AccountStash.For(account, Catalog);
        const int tab = 7;
        var type = Catalog.Find("Metadata/Items/Currency/" + id)!;
        var item = ItemCatalog.Create(type);
        var initial = stash.AddStacked(tab, item with { StackSize = 50000 }, Shape, out _)!;
        Assert.Equal(0, initial.Left);
        var grown = stash.AddStacked(tab, item with { StackSize = 20000 }, Shape, out _)!;
        Assert.Equal(0, grown.Left);
        var before = stash.Inventory(tab).Items;
        Assert.Equal(70000, before.Sum(i => (int)i.Item.StackSize));
        Assert.Equal(limit, before.Single(i => i.X == Catalog.Stash.SlotOf(StashRules.Currency, type.Hash)).Item.StackSize);
        Assert.All(before, i => Assert.InRange((int)i.Item.StackSize, 1, limit));
        AccountStash.Forget(account);
        var after = AccountStash.For(account, Catalog).Inventory(tab).Items;
        Assert.Equal(before.Select(i => (i.Id, i.X, i.Y, i.Item.StackSize)),
            after.Select(i => (i.Id, i.X, i.Y, i.Item.StackSize)));
    }
}
