using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class NessaFullGemStockTests
{
    [Fact]
    public void Full_stock_ignores_quest_filter_preserves_variants_and_paginates_every_colour()
    {
        var catalog = BenchCraftingTests.Setup.Value.Catalog;
        uint next = 1;
        var notes = new List<string>();
        var tabs = NpcShops.Tabs("Metadata/NPC/Act1/Nessa", catalog, () => next++, notes,
            BenchCraftingTests.Data(), gemStock: [], fullGemStock: true);
        var all = tabs.SelectMany(t => t.Items).ToList();
        Assert.Equal(all.Count, all.Select(i => i.Id).Distinct().Count());
        foreach (var (colour, label) in new[] { (1, "Red"), (2, "Green"), (3, "Blue"), (4, "White") })
            foreach (bool support in new[] { false, true })
            {
                if (colour == 4 && support) continue; // Both kinds belong in White Skills.
                string name = $"{label} {(support ? "Support" : "Skills")}";
                var pages = tabs.Where(t => t.Name == name || t.Name.StartsWith(name + " ")).ToList();
                var stock = pages.SelectMany(t => t.Items).ToList();
                var expected = catalog.FullShopGems(colour, support)
                    .Concat(colour == 4 ? catalog.FullShopGems(4, true) : [])
                    .Select(g => (g.Type.Hash, g.Variant.Index)).Order().ToArray();
                Assert.Equal(expected, stock.Select(i => (i.Item.TypeHash, i.Item.GemVariant)).Order());
                Assert.All(pages, p => Assert.InRange(p.Items.Count, 0, 144));
                if (expected.Length > 144) Assert.True(pages.Count > 1);
                Assert.All(stock, i =>
                {
                    Assert.InRange(i.X, (byte)0, (byte)11);
                    Assert.InRange(i.Y, (byte)0, (byte)11);
                    Assert.NotEmpty(i.Item.Price);
                    var saved = CharacterSaves.FromSaved(CharacterSaves.ToSaved(i.Item), catalog);
                    Assert.Equal(i.Item.GemVariant, saved.GemVariant);
                    Assert.Equal(catalog.GemName(i.Item), catalog.GemName(saved));
                    Assert.NotEmpty(i.Item.Payload());
                });
                Assert.All(pages, p => Assert.Equal(p.Items.Count, p.Items.Select(i => (i.X, i.Y)).Distinct().Count()));
            }
        Assert.Contains(all, i => catalog.GemName(i.Item) == "Ice Nova of Frostbolts" && i.Item.GemVariant > 0);
        Assert.Contains(all, i => catalog.GemName(i.Item) == "Vaal Fireball");
        Assert.DoesNotContain(tabs, t => t.Name.StartsWith("White Support"));
        Assert.Empty(Assert.Single(tabs, t => t.Name == "Skills").Items);
        Assert.DoesNotContain(notes, n => n.StartsWith("no room"));
    }

    [Fact]
    public void Skills_uses_class_and_quest_progression_while_colour_tabs_stay_complete()
    {
        string data = BenchCraftingTests.Data();
        var catalog = BenchCraftingTests.Setup.Value.Catalog;
        var vendor = new GemVendorStock(catalog, QuestRewardCatalog.Load(data), NpcCatalog.Load(data), new SkillCatalog(data));
        const string nessa = "Metadata/NPC/Act1/Nessa";
        List<NpcShops.ShopTab> Shop(int classRow, params string[] flags)
        {
            uint next = 1;
            return NpcShops.Tabs(nessa, catalog, () => next++, [], data,
                vendor.Stock(nessa, classRow, flags.ToHashSet()), vendor.Price, fullGemStock: true);
        }
        static bool Progression(NpcShops.ShopTab t) => t.Name == "Skills" || t.Name.StartsWith("Skills ");
        var empty = Shop(1);
        Assert.Empty(empty.Where(Progression).SelectMany(t => t.Items));
        foreach (int classRow in new[] { 0, 1 })
        {
            string[] flags = ["a1q1-KilledHillock", "a1q4-PlacedGlyphs"];
            var tabs = Shop(classRow, flags);
            var unlocked = tabs.Where(Progression).SelectMany(t => t.Items).ToList();
            var expected = vendor.Stock(nessa, classRow, flags.ToHashSet()).Order().ToArray();
            Assert.NotEmpty(expected);
            Assert.Equal(expected, unlocked.Select(i => catalog.ByHash(i.Item.TypeHash)!.Row).Order());
            Assert.All(unlocked, i => { Assert.Equal(0, i.Item.GemVariant); Assert.NotEmpty(i.Item.Price); });
            var before = empty.Where(t => !Progression(t)).SelectMany(t => t.Items)
                .Select(i => (i.Item.TypeHash, i.Item.GemVariant)).Order();
            var after = tabs.Where(t => !Progression(t)).SelectMany(t => t.Items)
                .Select(i => (i.Item.TypeHash, i.Item.GemVariant)).Order();
            Assert.Equal(before, after);
        }
        Assert.False(vendor.Stock(nessa, 0, new HashSet<string> { "a1q1-KilledHillock" }).SequenceEqual(
            vendor.Stock(nessa, 1, new HashSet<string> { "a1q1-KilledHillock" })));
        uint id = 1;
        var unknown = NpcShops.Tabs(nessa, catalog, () => id++, [], data, fullGemStock: true);
        Assert.Empty(unknown.Where(Progression).SelectMany(t => t.Items));
    }

    [Fact]
    public void Ordinary_vendor_still_respects_an_empty_quest_stock()
    {
        var catalog = BenchCraftingTests.Setup.Value.Catalog;
        uint next = 1;
        var tabs = NpcShops.Tabs("Metadata/NPC/Act2/Yeena", catalog, () => next++, [],
            BenchCraftingTests.Data(), gemStock: []);
        Assert.All(tabs.Where(t => !t.Name.StartsWith("Goods")), t => Assert.Empty(t.Items));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Skills_orders_active_gems_then_supports_by_colour_and_starts_supports_on_a_new_row(bool multiplePages)
    {
        var catalog = BenchCraftingTests.Setup.Value.Catalog;
        var available = new[] { false, true }.SelectMany(support => new[] { 1, 2, 3, 4 }
            .SelectMany(colour => multiplePages ? catalog.ShopGems(colour, support)
                : catalog.ShopGems(colour, support).Take(5))).ToList();
        uint next = 1;
        var tabs = NpcShops.Tabs("Metadata/NPC/Act1/Nessa", catalog, () => next++, [],
            BenchCraftingTests.Data(), gemStock: available.Select(t => t.Row).Reverse().ToArray(), fullGemStock: true)
            .Where(t => t.Name == "Skills" || t.Name.StartsWith("Skills ")).ToList();
        var gems = tabs.SelectMany(t => t.Items.OrderBy(i => i.Y).ThenBy(i => i.X)).ToList();
        ItemCatalog.GemType Kind(InventoryItem i) => catalog.Gem(catalog.ByHash(i.Item.TypeHash)!)!;
        Assert.Equal(available.Select(t => t.Hash).Order(), gems.Select(i => i.Item.TypeHash).Order());
        int firstSupport = gems.FindIndex(i => Kind(i).IsSupport);
        Assert.True(firstSupport > 0);
        Assert.All(gems.Take(firstSupport), i => Assert.False(Kind(i).IsSupport));
        Assert.All(gems.Skip(firstSupport), i => Assert.True(Kind(i).IsSupport));
        foreach (bool support in new[] { false, true })
        {
            var colours = gems.Where(i => Kind(i).IsSupport == support).Select(i => Kind(i).Colour).ToArray();
            Assert.Equal(colours.Order(), colours);
        }
        foreach (var page in tabs)
        {
            Assert.Equal(page.Items.Count, page.Items.Select(i => (i.X, i.Y)).Distinct().Count());
            var active = page.Items.Where(i => !Kind(i).IsSupport).ToList();
            var supports = page.Items.Where(i => Kind(i).IsSupport).OrderBy(i => i.Y).ThenBy(i => i.X).ToList();
            if (supports.Count == 0) continue;
            Assert.Equal(0, supports[0].X);
            Assert.Equal(active.Count == 0 ? 0 : active.Max(i => i.Y) + 1, supports[0].Y);
        }
        if (multiplePages) Assert.True(tabs.Count > 1);
        else Assert.Single(tabs);
    }
}
