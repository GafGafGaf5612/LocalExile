using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// Gems as monster drops (ItemDrops): a small share of the item drops, only gems whose DropLevel the monster meets, and
// unlevelled like a vendor's gem.
public class GemDropsTests
{
    static readonly Lazy<(ItemCatalog Catalog, ItemDrops Drops)> Setup = new(() =>
    {
        string data = BenchCraftingTests.Data();
        var catalog = new ItemCatalog(data);
        return (catalog, new ItemDrops(catalog, new ItemRolls(catalog, data), data));
    });

    static List<(ItemDrops.Drop Drop, ItemCatalog.BaseType Type, ItemCatalog.GemType Gem)> Gems(IEnumerable<ItemDrops.Drop> drops)
    {
        var catalog = Setup.Value.Catalog;
        return [.. drops.Select(d => (Drop: d, Type: catalog.ByHash(d.Item.TypeHash)!))
            .Where(d => catalog.Gem(d.Type) is not null)
            .Select(d => (d.Drop, d.Type, catalog.Gem(d.Type)!))];
    }

    [Fact]
    public void Gems_drop_unlevelled_and_are_part_of_the_item_drops()
    {
        var (_, drops) = Setup.Value;
        var gems = Gems(Enumerable.Range(0, 4000).SelectMany(seed =>
            drops.Roll(80, MonsterRarity.Normal, new Random(seed))));
        Assert.NotEmpty(gems);
        Assert.All(gems, g =>
        {
            Assert.InRange(g.Type.DropLevel, 1, 80);
            Assert.Equal(1, g.Drop.Item.ItemLevel);
            Assert.Equal(0, g.Drop.Item.GemExperience);
            Assert.Equal(0, g.Drop.Item.Quality);
            Assert.Empty(g.Drop.Item.Sockets);
            Assert.Equal(ItemRarity.Normal, g.Drop.Item.Rarity);
            Assert.True(g.Drop.Item.Identified);
            Assert.False(g.Gem.IsVaalOrAwakened);
            Assert.DoesNotContain("Royale", g.Type.Id);
            Assert.DoesNotContain("Graft", g.Type.Id);
            Assert.NotEqual("...", g.Type.Name);
        });
    }

    [Fact]
    public void A_monster_only_drops_gems_at_or_below_its_level()
    {
        var (catalog, drops) = Setup.Value;
        var pool = GemSources.DropPool(catalog, BenchCraftingTests.Data());
        int first = pool.Min(r => catalog.Row(r)!.DropLevel);
        for (int level = first; level <= first + 12; level++)
        {
            int seen = 0;
            foreach (var seed in Enumerable.Range(0, 400))
            {
                var gems = Gems(drops.Roll(level, MonsterRarity.Normal, new Random(seed)));
                Assert.All(gems, g => Assert.True(g.Type.DropLevel <= level, $"{g.Type.Name} (drop level {g.Type.DropLevel}) at level {level}"));
                seen += gems.Count;
            }
            Assert.True(seen > 0, $"no gems at level {level}");
        }
    }

    [Fact]
    public void The_drop_pool_keeps_obtainable_gems_and_leaves_out_the_data_junk()
    {
        var (catalog, _) = Setup.Value;
        var pool = GemSources.DropPool(catalog, BenchCraftingTests.Data());
        bool Named(string name) => pool.Any(r => catalog.Row(r)?.Name == name);
        Assert.True(Named("Fireball"));                // starting gem
        Assert.True(Named("Faster Attacks Support"));  // quest gem
        Assert.True(Named("Empower Support"));         // drop-only exceptional support
        Assert.False(Named("..."));                    // placeholder
        Assert.False(Named("The Great Avalanche"));    // Graft skill (Chayula)
        Assert.False(Named("Envy"));                   // item-granted/legacy skill
        Assert.False(Named("Death Aura"));
        Assert.True(pool.Count > 300);
        Assert.DoesNotContain(pool, r => catalog.Row(r)?.Id.Contains("Royale") == true);
        Assert.DoesNotContain(pool, r => catalog.Row(r)?.Id.Contains("Tutorial") == true);
    }
}
