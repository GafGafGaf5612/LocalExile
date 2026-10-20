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
        Inactive(baseAttributes, level, null, worn);

    static HashSet<uint> Inactive((int, int, int) baseAttributes, int level, HashSet<uint>? previous, params ItemModStats.Worn[] worn) =>
        ItemModStats.Inactive(worn, baseAttributes, level, i => Gives[i.TypeHash], previous);

    [Fact]
    public void Taking_off_a_ring_leaves_items_the_rest_still_supports()
    {
        // Live 2026-10-20: 14 strength, a +30 ring, a +30 Chestplate (needs 25), a Cone Helmet (needs 21): 74.
        // Without the ring 44 is left, enough for both.
        var chest = Item(1, (25, 0, 0), gives: (30, 0, 0));
        var helmet = Item(2, (21, 0, 0));
        Assert.Empty(Inactive((14, 0, 0), 10, chest, helmet));
    }

    [Fact]
    public void Switching_one_off_is_counted_again_before_the_next()
    {
        // 14 base; a +10 amulet needs 30 (only reachable with itself and the belt): with the belt gone it fails, and the
        // boots that needed its +10 fail after it.
        var amulet = Item(10, (30, 0, 0), gives: (10, 0, 0));
        var boots = Item(11, (24, 0, 0));
        Assert.Equal([10u, 11u], Inactive((14, 0, 0), 10, amulet, boots).Order());
    }

    [Fact]
    public void An_inactive_item_does_not_count_its_own_attributes_to_come_back()
    {
        var chest = Item(20, (25, 0, 0), gives: (30, 0, 0));
        // Off before, and 14 + nothing else < 25: it stays off even though 14 + its own 30 would be enough.
        Assert.Equal([20u], Inactive((14, 0, 0), 10, [20], chest));
        // A ring that gives the rest comes back on.
        var ring = Item(21, (0, 0, 0), gives: (11, 0, 0));
        Assert.Empty(Inactive((14, 0, 0), 10, [20], chest, ring));
    }

    [Fact]
    public void Two_items_can_qualify_each_other()
    {
        Assert.Empty(Inactive((0, 0, 0), 1, Item(30, (20, 0, 0), gives: (0, 20, 0)), Item(31, (0, 20, 0), gives: (20, 0, 0))));
    }

    [Fact]
    public void An_inactive_items_attributes_do_not_count()
    {
        // The amulet needs more intelligence than anyone has, so its +30 strength cannot carry the helmet.
        Assert.Equal([40u, 41u], Inactive((0, 0, 0), 1, Item(40, (0, 0, 50), gives: (30, 0, 0)), Item(41, (30, 0, 0))).Order());
    }

    [Fact]
    public void The_level_requirement_counts()
    {
        Assert.Equal([30u], Inactive((100, 100, 100), 5, Item(30, (0, 0, 0), level: 10)));
        Assert.Empty(Inactive((100, 100, 100), 10, Item(30, (0, 0, 0), level: 10)));
    }

    [Fact]
    public void A_mods_item_level_is_not_a_character_level_requirement()
    {
        var mods = new ItemCatalog(Data()).Mods;
        var item = new Item(1, ["Mods"], 0) { Mods = [mods.ById("IncreasedLife12")!.Roll(ModSection.Explicit, Random.Shared)] };
        Assert.Equal(5, ItemModStats.LevelRequirement(item, 5, mods));
    }

    [Fact]
    public void Level_requirement_mods_raise_lower_or_override_it()
    {
        var mods = new ItemCatalog(Data()).Mods;
        var plus = mods.All.First(m => m.Stats.Any(s => s.Id == "local_level_requirement_+"));
        var over = mods.ById("LevelRequirementOverrideUnique__7")!;
        Item With(ModCatalog.Mod m, int v) => new(1, ["Mods"], 0) { Mods = [m.With(ModSection.Explicit, [v], Random.Shared)] };
        Assert.Equal(15, ItemModStats.LevelRequirement(With(plus, 10), 5, mods));
        Assert.Equal(7, ItemModStats.LevelRequirement(With(over, 7), 60, mods));
    }

    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }
}
