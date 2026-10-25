using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class MetacraftTests
{
    [Fact]
    public void Both_locks_prevent_removal_and_value_changes_and_fractures_survive_scour()
    {
        var (catalog, rolls, bench, types) = BenchCraftingTests.Setup.Value;
        var type = types.First(t => t.Class == "One Hand Sword");
        var rng = new Random(14);
        var locks = new[] { "item_generation_cannot_change_prefixes", "item_generation_cannot_change_suffixes" }
            .Select(stat => bench.All.First(r => r.Mod?.Stats.Any(s => s.Id == stat) == true).Mod!.Roll(ModSection.Explicit, rng)).ToArray();
        var item = rolls.Roll(ItemCatalog.Create(type) with { ItemLevel = 100 }, ItemRarity.Rare, rng, (4, 3));
        item = item with { Mods = [.. item.Mods, .. locks] };
        Assert.Null(CurrencyEffects.Scouring(item, rolls));
        Assert.Null(CurrencyEffects.Annulment(item, rolls, rng));
        Assert.Null(rolls.Reforge(item, rng));
        var fractured = item with { Mods = [item.Mods.First(m => m.Section == ModSection.Explicit)], FracturedCount = 1 };
        var scoured = CurrencyEffects.Scouring(fractured, rolls)!;
        Assert.Equal(ItemRarity.Magic, scoured.Rarity);
        Assert.Equal(fractured.Mods, scoured.Mods);
        Assert.Equal(1, scoured.FracturedCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Scour_preserves_locked_side_and_removes_protecting_craft(bool prefixes)
    {
        var (catalog, rolls, bench, types) = BenchCraftingTests.Setup.Value;
        var type = types.First(t => t.Class == "One Hand Sword");
        string stat = prefixes ? "item_generation_cannot_change_prefixes" : "item_generation_cannot_change_suffixes";
        var craft = bench.All.First(r => r.Mod?.Stats.Any(s => s.Id == stat) == true).Mod!;
        var rng = new Random(51);
        var item = rolls.Roll(ItemCatalog.Create(type) with { ItemLevel = 100, Quality = 20 }, ItemRarity.Rare, rng, (6, 3));
        int side = prefixes ? ItemRolls.Prefix : ItemRolls.Suffix;
        var kept = item.Mods.Where(m => catalog.Mods.ByHash(m.Hash)!.GenerationType == side).ToArray();
        item = item with { Mods = [.. kept, craft.Roll(ModSection.Explicit, rng)] };
        var after = CurrencyEffects.Scouring(item, rolls)!;
        Assert.Equal(kept, after.Mods);
        Assert.Equal(ItemRarity.Rare, after.Rarity);
        Assert.Equal(20, after.Quality);
        Assert.Same(item.Sockets, after.Sockets);
        for (int i = 0; i < 30; i++)
        {
            var annul = CurrencyEffects.Annulment(item, rolls, rng)!;
            Assert.Equal(kept, annul.Mods);
            var divine = rolls.Reforge(item, rng)!;
            Assert.Equal(kept, divine.Mods.Take(kept.Length));
            var chaos = CurrencyEffects.Chaos(item, rolls, rng)!;
            Assert.All(kept, m => Assert.Contains(m, chaos.Mods));
        }
    }

    [Theory]
    [InlineData("attack")]
    [InlineData("caster")]
    public void Cannot_roll_tags_protect_annul_and_filter_exalt(string tag)
    {
        var (catalog, rolls, bench, types) = BenchCraftingTests.Setup.Value;
        var type = types.First(t => t.Class == "One Hand Sword");
        string stat = $"item_generation_cannot_roll_{tag}_affixes";
        var craft = bench.All.First(r => r.Mod?.Stats.Any(s => s.Id == stat) == true).Mod!;
        var tagged = catalog.Mods.All.First(m => m.Domain == 1 && m.GenerationType == 1 && m.Tags.Contains(tag));
        var rng = new Random(67);
        var fixedMod = tagged.Roll(ModSection.Explicit, rng);
        var item = ItemCatalog.Create(type) with { ItemLevel = 100, Rarity = ItemRarity.Rare,
            Mods = [fixedMod, craft.Roll(ModSection.Explicit, rng)] };
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(fixedMod, Assert.Single(CurrencyEffects.Annulment(item, rolls, rng)!.Mods));
            var exalt = CurrencyEffects.Exalted(item, rolls, rng)!;
            var added = Assert.Single(exalt.Mods, m => !item.Mods.Contains(m));
            Assert.DoesNotContain(tag, catalog.Mods.ByHash(added.Hash)!.Tags);
        }
        Assert.Empty(CurrencyEffects.Scouring(item, rolls)!.Mods);
        Assert.Null(CurrencyEffects.Scouring(item with { Rarity = ItemRarity.Unique }, rolls));
    }
}
