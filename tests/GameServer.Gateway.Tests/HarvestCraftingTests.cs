using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class HarvestCraftingTests
{
    static readonly Lazy<(ItemCatalog Catalog, ItemRolls Rolls, HarvestCrafting Harvest)> Setup = new(() =>
    {
        string data = BenchCraftingTests.Data();
        var catalog = new ItemCatalog(data);
        var rolls = new ItemRolls(catalog, data);
        return (catalog, rolls, new HarvestCrafting(catalog, rolls, data));
    });

    static HarvestCrafting Harvest => Setup.Value.Harvest;
    static ItemCatalog Catalog => Setup.Value.Catalog;
    static ItemRolls Rolls => Setup.Value.Rolls;
    static ItemCatalog.BaseType Ring() => Catalog.Find("Two-Stone Ring")!;

    static Item Rare(ItemCatalog.BaseType type, int seed)
    {
        var rng = new Random(seed);
        return Rolls.Roll(ItemCatalog.Create(type) with { ItemLevel = 100 }, ItemRarity.Rare, rng, (4, 3)) with { Identified = true };
    }

    [Fact]
    public void Every_raw_row_loads_with_its_hash_and_supported_item_recipes_have_their_lifeforce_cost()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(BenchCraftingTests.Data(), "tables/HarvestCraftOptions.json")));
        Assert.Equal(doc.RootElement.GetArrayLength(), Harvest.All.Count);
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            var recipe = Harvest.ByHash((ushort)row.GetProperty("HASH16").GetInt32());
            Assert.NotNull(recipe);
            Assert.Equal(row.GetProperty("Id").GetString(), recipe!.Id);
        }
        var fire = Harvest.ByHash(0xa3e5)!;
        Assert.Equal("ReforgeFire", fire.Id);
        Assert.Equal(HarvestCrafting.Operation.ReforgeTag, fire.Operation);
        Assert.Equal("fire", fire.Tag);
        Assert.Equal([(Catalog.Find(HarvestCrafting.Wild)!.Hash, 50)], fire.Costs);
        Assert.Equal(41, Harvest.All.Count(r => r.Supported));
    }

    [Fact]
    public void Targeted_reforge_makes_a_rare_item_with_the_requested_tag()
    {
        var item = Rare(Ring(), 1);
        var fired = Harvest.Apply(0xa3e5, item, new Random(2));
        Assert.NotNull(fired);
        Assert.Equal(ItemRarity.Rare, fired!.Item.Rarity);
        Assert.Equal(item.TypeHash, fired.Item.TypeHash);
        Assert.Equal(item.ItemLevel, fired.Item.ItemLevel);
        Assert.Contains(fired.Item.Mods, m => m.Section == ModSection.Explicit &&
            Catalog.Mods.ByHash(m.Hash)?.Tags.Contains("fire") == true);
        Assert.Equal(Harvest.ByHash(0xa3e5)!.Costs, fired.Costs);
    }

    [Fact]
    public void Augment_adds_a_tagged_mod_and_removes_one_other_mod()
    {
        var item = Rare(Ring(), 3);
        int before = item.Mods.Count(m => m.Section == ModSection.Explicit);
        var augmented = Harvest.Apply(Harvest.All.First(r => r.Operation == HarvestCrafting.Operation.AugmentTag && r.Tag == "fire").Hash,
            item, new Random(4));
        Assert.NotNull(augmented);
        Assert.Equal(item.Rarity, augmented!.Item.Rarity);
        Assert.Equal(before, augmented.Item.Mods.Count(m => m.Section == ModSection.Explicit));
        Assert.Contains(augmented.Item.Mods, m => m.Section == ModSection.Explicit &&
            Catalog.Mods.ByHash(m.Hash)?.Tags.Contains("fire") == true);
    }

    [Fact]
    public void Affinity_reforge_keeps_the_rare_shape_and_uses_its_cost()
    {
        var item = Rare(Ring(), 5);
        var recipe = Harvest.All.First(r => r.Operation == HarvestCrafting.Operation.ReforgeAffinity);
        var result = Harvest.Apply(recipe.Hash, item, new Random(6));
        Assert.NotNull(result);
        Assert.Equal(ItemRarity.Rare, result!.Item.Rarity);
        Assert.InRange(result.Item.Mods.Count(m => m.Section == ModSection.Explicit), 4, 6);
        Assert.Equal(recipe.Costs, result.Costs);
    }

    [Fact]
    public void Convert_replaces_the_source_modifier_with_the_mapped_same_tier_modifier()
    {
        var type = Catalog.Find("Two-Stone Ring")!;
        var source = Catalog.Mods.All.First(m => m.ModType is "ColdResistance" or "FireResistance" &&
            m.Domain == type.ModDomain && m.GenerationType is ItemRolls.Prefix or ItemRolls.Suffix && m.WeightFor(type.Tags) > 0);
        var recipe = Harvest.All.First(r => r.Operation == HarvestCrafting.Operation.Convert && r.Convert.ContainsKey(source.ModType));
        string targetType = recipe.Convert[source.ModType];
        var rng = new Random(7);
        var item = ItemCatalog.Create(type) with
        {
            ItemLevel = 100, Rarity = ItemRarity.Rare, Identified = true,
            Mods = [source.Roll(ModSection.Explicit, rng)],
        };
        var result = Harvest.Apply(recipe.Hash, item, rng);
        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Item.Mods, m => m.Hash == source.Hash);
        Assert.Contains(result.Item.Mods, m => Catalog.Mods.ByHash(m.Hash)?.ModType == targetType);
    }

    [Fact]
    public void Unsupported_recipes_and_bad_targets_are_refused_without_a_result()
    {
        var item = Rare(Ring(), 8);
        var unsupported = Harvest.All.First(r => !r.Supported);
        Assert.Null(Harvest.Apply(unsupported.Hash, item, new Random(9)));
        Assert.Null(Harvest.Apply(0xa3e5, item with { Corrupted = true }, new Random(9)));
        Assert.Null(Harvest.Apply(0xa3e5, item with { Mirrored = true }, new Random(9)));
        Assert.Null(Harvest.Apply(0xa3e5, Rare(Ring(), 10) with { Identified = false }, new Random(9)));
    }
}
