using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class BenchBalanceTests
{
    [Fact]
    public void Display_uses_the_current_personal_stash_balance_without_counting_inventory_twice()
    {
        TestSaves.Ensure();
        var (catalog, _, bench, types) = BenchCraftingTests.Setup.Value;
        string name = "Balance" + Guid.NewGuid().ToString("N");
        var items = PlayerInventories.For(name, catalog);
        var stash = AccountStash.For(name + "Account", catalog);
        var recipe = bench.All.First(r => r.Mod is not null && r.Classes.Contains(25) && r.Costs.Count == 1 && r.Costs[0].Amount >= 6);
        var (hash, cost) = recipe.Costs.Single();
        Item Currency(int n) => ItemCatalog.Create(catalog.ByHash(hash)!) with { StackSize = (ushort)n };
        PlayerInventories.Shape? Shape(Item i) => catalog.ByHash(i.TypeHash) is { } t ? new(t.Width, t.Height, t.Class, t.MaxStack) : null;
        stash.AddStacked(0, Currency(cost - 2), Shape, out _);
        stash.AddStacked(1, Currency(5), Shape, out _);
        items.Add(PlayerInventories.MainInventory, 0, 0, Currency(2));
        items.Add(PlayerInventories.CraftingBench, 0, 0, ItemCatalog.Create(types.Single(t => t.Class == "Helmet")));
        long Held(IReadOnlyList<InventoryItem> snapshot) => snapshot.Where(i => i.Item.TypeHash == hash).Sum(i => (long)i.Item.StackSize);
        var before = BenchBalance.Snapshot(stash, catalog, bench);
        Assert.Equal(cost + 3, Held(before));
        Assert.Equal(cost + 5, Held(before) + items.Count(PlayerInventories.MainInventory, hash));
        Assert.NotNull(ItemMoves.CraftAtBench(items, stash, bench, recipe.Row, false, new Random(1)));
        Assert.Equal(5, Held(BenchBalance.Snapshot(stash, catalog, bench)));
        Assert.Equal(0, items.Count(PlayerInventories.MainInventory, hash));
        Assert.Null(PlayerInventories.Row(InstanceBench.BalanceInventoryId));
        Assert.Null(stash.TabOf(InstanceBench.BalanceInventoryId));
        Assert.All(before, view => Assert.DoesNotContain(view.Id, items.Items(PlayerInventories.MainInventory).Select(i => i.Id)));
    }

    [Fact]
    public void Large_totals_do_not_overflow_the_wire_stack_size_and_empty_balances_remove_the_old_view()
    {
        var catalog = BenchCraftingTests.Setup.Value.Catalog;
        uint hash = catalog.Find(CurrencyEffects.ChaosOrb)!.Hash;
        var snapshot = BenchBalance.DisplayStacks(catalog, [(hash, 150000)]);
        Assert.Equal(150000L, snapshot.Sum(i => (long)i.Item.StackSize));
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(snapshot.Count, snapshot.Select(i => i.Id).Distinct().Count());
        Assert.Empty(BenchBalance.DisplayStacks(catalog, [(hash, 0)]));
        // Ordinary stash kind 0x11 is excluded by the native bench filter; balance kind 0x12 is accepted.
        Assert.Equal(0x12, InstanceBench.BalanceSlot.Kind);
        Assert.Equal(26, InstanceBench.BalanceSlot.Row);
        Assert.Equal(0, InstanceBench.BalanceSlot.Flags);
    }
}
