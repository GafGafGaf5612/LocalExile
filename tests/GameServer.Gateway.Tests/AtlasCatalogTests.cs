using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class AtlasCatalogTests
{
    static ItemCatalog Catalog => BenchCraftingTests.Setup.Value.Catalog;

    [Fact]
    public void Loads_the_normal_atlas_layouts_and_a_starting_default()
    {
        var atlas = new AtlasCatalog(Catalog);
        Assert.True(atlas.Layouts.Count > 100);
        Assert.NotNull(atlas.Default);
        Assert.True(atlas.Default!.Starting);
        Assert.Equal("Cells", atlas.Default.Name);
    }

    [Fact]
    public void Atlas_node_pick_by_hash16_selects_the_layout()
    {
        var atlas = new AtlasCatalog(Catalog);
        var estuary = atlas.ByHash16(40753);
        Assert.NotNull(estuary);
        Assert.Equal("Estuary", estuary!.Name);
        atlas.Select("AtlasTester", estuary);
        Assert.Equal(estuary.Hash16, atlas.Selected("AtlasTester")!.Hash16);
    }

    [Fact]
    public void Generic_map_keys_carry_their_tier_and_open_the_selected_layout()
    {
        var atlas = new AtlasCatalog(Catalog);
        var key = Catalog.Find("Metadata/Items/Maps/MapKeyTier1")!;
        Assert.Equal(1, atlas.TierOf(key.Hash));
        var device = new MapDevice(new Dictionary<uint, MapDevice.MapArea>(), atlas);
        var area = device.Check([new MapDevice.Slot(key.Hash, MapDevice.MapKeyClass, key.Name)], "AtlasTester", out string why);
        Assert.Equal("", why);
        Assert.NotNull(area);
        Assert.Equal(atlas.Default!.Hash16, area!.Hash16);
        Assert.Equal(1, area.Tier);
    }

    [Fact]
    public void Map_keys_give_their_tier_and_serialize_as_generic_map_items()
    {
        var atlas = new AtlasCatalog(Catalog);
        Assert.NotNull(atlas.KeyOfTier(1));
        Assert.Equal(16, atlas.TierOf(Catalog.Find("Metadata/Items/Maps/MapKeyTier16")!.Hash));
        var type = Catalog.Find("Metadata/Items/Maps/MapKeyTier1")!;
        Assert.True(Item.CanSerialize(type.Components));
        Assert.NotEmpty(ItemCatalog.Create(type).Payload());

        var drops = new ItemDrops(Catalog, BenchCraftingTests.Setup.Value.Rolls, maps: atlas);
        var map = Enumerable.Range(0, 10_000).Select(i => drops.Map(83, MonsterRarity.Normal, 16, new Random(i)))
            .FirstOrDefault(d => d is not null);
        Assert.NotNull(map);
        Assert.StartsWith("Map (Tier 16)", map!.Type.Name);
    }
}
