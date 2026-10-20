using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ItemRequirementsTests
{
    // Items carry their attribute bonus in a lookup by id (the real code reads the item's mods).
    static readonly Dictionary<uint, (int, int, int)> Gives = [];

    static ItemModStats.Worn Item(uint id, (int Str, int Dex, int Int) needs, (int Str, int Dex, int Int) gives = default, int level = 0)
    {
        Gives[id] = gives;
        return new(id, new Item(id, ["Base"], 0), needs.Str, needs.Dex, needs.Int, level);
    }

    static HashSet<uint> Inactive((int, int, int) baseAttributes, int level, params ItemModStats.Worn[] worn) =>
        ItemModStats.Inactive(worn, baseAttributes, level, i => Gives[i.TypeHash]);

    [Fact]
    public void An_item_does_not_meet_its_own_requirement()
    {
        Assert.Equal([1u], Inactive((10, 0, 0), 1, Item(1, (20, 0, 0), gives: (20, 0, 0))));
    }

    [Fact]
    public void Two_items_can_qualify_each_other()
    {
        Assert.Empty(Inactive((0, 0, 0), 1, Item(1, (20, 0, 0), gives: (0, 20, 0)), Item(2, (0, 20, 0), gives: (20, 0, 0))));
    }

    [Fact]
    public void Losing_an_item_cascades()
    {
        // The ring gives the strength the armour needs, the armour gives the dexterity the boots need.
        var ring = Item(10, (0, 0, 0), gives: (20, 0, 0));
        var armour = Item(11, (20, 0, 0), gives: (0, 20, 0));
        var boots = Item(12, (0, 20, 0));
        Assert.Empty(Inactive((0, 0, 0), 1, ring, armour, boots));
        Assert.Equal([11u, 12u], Inactive((0, 0, 0), 1, armour, boots).Order());
    }

    [Fact]
    public void An_inactive_items_attributes_do_not_count()
    {
        // The amulet needs more intelligence than anyone has, so its +30 strength cannot carry the helmet.
        Assert.Equal([20u, 21u], Inactive((0, 0, 0), 1, Item(20, (0, 0, 50), gives: (30, 0, 0)), Item(21, (30, 0, 0))).Order());
    }

    [Fact]
    public void The_level_requirement_counts()
    {
        Assert.Equal([30u], Inactive((100, 100, 100), 5, Item(30, (0, 0, 0), level: 10)));
        Assert.Empty(Inactive((100, 100, 100), 10, Item(30, (0, 0, 0), level: 10)));
    }

    [Fact]
    public void The_level_requirement_is_the_base_or_80_percent_of_the_highest_mod()
    {
        var mods = new ItemCatalog(Data()).Mods;
        var life = mods.All.First(m => m.Id.StartsWith("IncreasedLife") && m.Level >= 50);
        var item = new Item(1, ["Mods"], 0) { Mods = [life.Roll(ModSection.Explicit, Random.Shared)] };
        Assert.Equal(life.Level * 8 / 10, ItemModStats.LevelRequirement(item, 5, mods));
        Assert.Equal(70, ItemModStats.LevelRequirement(item, 70, mods));
    }

    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }
}
