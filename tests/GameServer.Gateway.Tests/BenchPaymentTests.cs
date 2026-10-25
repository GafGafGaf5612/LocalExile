using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

[Collection(TestSaves.FailWriteCollection)]
public class BenchPaymentTests : IDisposable
{
    readonly string name = "Bench" + Guid.NewGuid().ToString("N");
    const byte Main = PlayerInventories.MainInventory, Slot = PlayerInventories.CraftingBench;
    static ItemCatalog Catalog => BenchCraftingTests.Setup.Value.Catalog;
    static BenchCrafting Bench => BenchCraftingTests.Setup.Value.Bench;
    PlayerInventories Items => PlayerInventories.For(name, Catalog);
    AccountStash Stash => AccountStash.For(name + "Account", Catalog);
    public BenchPaymentTests() => TestSaves.Ensure();
    public void Dispose() => Saves.FailWrite = null;
    static PlayerInventories.Shape? Shape(Item i) => Catalog.ByHash(i.TypeHash) is { } t ? new(t.Width, t.Height, t.Class, t.MaxStack) : null;
    static Item Currency(uint hash, int count) => ItemCatalog.Create(Catalog.ByHash(hash)!) with { StackSize = (ushort)count };
    void Deposit(uint hash, int count, int tab = 0) => Assert.Equal(0, Stash.AddStacked(tab, Currency(hash, count), Shape, out _)!.Left);
    static BenchCrafting.Recipe Recipe => Bench.All.First(r => r.Mod is not null && r.Classes.Contains(25) && r.Costs.Count == 1 && r.Costs[0].Amount >= 6);
    InventoryItem Target(string itemClass = "Helmet") => Items.Add(Slot, 0, 0,
        ItemCatalog.Create(BenchCraftingTests.Setup.Value.Types.Single(t => t.Class == itemClass)) with { ItemLevel = 100 });

    [Fact]
    public void Split_inventory_and_multiple_stash_tabs_pay_exactly_the_price_and_persist_the_craft()
    {
        var target = Target();
        var recipe = Recipe;
        var (currency, amount) = recipe.Costs.Single();
        Items.Add(Main, 0, 0, Currency(currency, 2));
        Deposit(currency, 2);
        Deposit(currency, amount + 5, 1);
        Assert.NotNull(ItemMoves.CraftAtBench(Items, Stash, Bench, recipe.Row, false, new Random(1)));
        Assert.Equal(0, Items.Count(Main, currency));
        Assert.Equal(9, Stash.CountOf(currency));
        var saved = CharacterSaves.Load(name)!.Inventories[Slot].Single(i => i.Id == target.Id);
        Assert.Single(saved.Item.Mods!, m => m.Id == recipe.Mod!.Id);
    }

    [Fact]
    public void Missing_one_of_multiple_currencies_changes_neither_the_item_nor_any_stack()
    {
        var target = Target("UtilityFlask");
        var recipe = Bench.All.First(r => r.Enchant is not null && r.Costs.Count == 2);
        Deposit(recipe.Costs[0].Currency, recipe.Costs[0].Amount);
        Deposit(recipe.Costs[1].Currency, recipe.Costs[1].Amount - 1);
        Assert.Null(ItemMoves.CraftAtBench(Items, Stash, Bench, recipe.Row, false, new Random(1)));
        Assert.Equal(recipe.Costs[0].Amount, Stash.CountOf(recipe.Costs[0].Currency));
        Assert.Equal(recipe.Costs[1].Amount - 1, Stash.CountOf(recipe.Costs[1].Currency));
        Assert.Equal(target.Item, Items.Items(Slot).Single().Item);
        Deposit(recipe.Costs[1].Currency, 1);
        Assert.NotNull(ItemMoves.CraftAtBench(Items, Stash, Bench, recipe.Row, false, new Random(1)));
        Assert.Equal(0, Stash.CountOf(recipe.Costs[0].Currency));
        Assert.Equal(0, Stash.CountOf(recipe.Costs[1].Currency));
    }

    [Fact]
    public void A_failed_character_save_rolls_back_stash_payment()
    {
        var target = Target();
        var recipe = Recipe;
        var (currency, amount) = recipe.Costs.Single();
        Deposit(currency, amount);
        Saves.FailWrite = key => key.Equals(name, StringComparison.OrdinalIgnoreCase);
        Assert.ThrowsAny<IOException>(() => ItemMoves.CraftAtBench(Items, Stash, Bench, recipe.Row, false, new Random(2)));
        Saves.FailWrite = null;
        Assert.Equal(amount, Stash.CountOf(currency));
        Assert.Equal(target.Item.Mods, Items.Items(Slot).Single().Item.Mods);
    }

    [Fact]
    public void Replacement_requires_confirmation_and_additional_scouring_payment()
    {
        var target = Target();
        var recipe = Recipe;
        var original = Bench.Apply(recipe.Row, target.Item, false, new Random(1))!.Item;
        Items.Change(Slot, target.Id, _ => original);
        var (currency, amount) = recipe.Costs.Single();
        Deposit(currency, amount);
        Assert.Null(ItemMoves.CraftAtBench(Items, Stash, Bench, recipe.Row, false, new Random(2)));
        Assert.Null(ItemMoves.CraftAtBench(Items, Stash, Bench, recipe.Row, true, new Random(2)));
        Assert.Equal(amount, Stash.CountOf(currency));
        uint scour = Catalog.Find(CurrencyEffects.OrbOfScouring)!.Hash;
        Deposit(scour, 1);
        Assert.NotNull(ItemMoves.CraftAtBench(Items, Stash, Bench, recipe.Row, true, new Random(2)));
        Assert.Equal(0, Stash.CountOf(currency));
        Assert.Equal(0, Stash.CountOf(scour));
        Assert.Single(Items.Items(Slot).Single().Item.Mods, Bench.IsCrafted);
    }
}
