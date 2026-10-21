using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class GoldDropsTests
{
    static List<int> Rolls(int level, MonsterRarity rarity) =>
        [.. Enumerable.Range(0, 2000).Select(i => ItemDrops.GoldAmount(level, rarity, new Random(i)))];

    [Fact]
    public void Normal_monsters_drop_gold_now_and_then_rares_always()
    {
        var normal = Rolls(10, MonsterRarity.Normal);
        Assert.InRange(normal.Count(g => g > 0), 700, 900);   // 40%
        Assert.All(Rolls(10, MonsterRarity.Rare), g => Assert.True(g > 0));
    }

    [Fact]
    public void Gold_grows_with_level_and_rarity()
    {
        double Mean(int level, MonsterRarity r) => Rolls(level, r).Where(g => g > 0).Average();
        Assert.InRange(Mean(1, MonsterRarity.Normal), 3, 6);           // (3 + 1.2) ±25%
        Assert.True(Mean(60, MonsterRarity.Normal) > 10 * Mean(1, MonsterRarity.Normal));
        Assert.InRange(Mean(10, MonsterRarity.Unique) / Mean(10, MonsterRarity.Normal), 13, 17);
    }

    [Fact]
    public void Ground_within_finds_only_the_chosen_items_in_range()
    {
        var ground = new InstanceGround();
        static WorldItemObject At(int x, int y, uint type) => new(x, y, new Item(type, [], 0));
        uint near = ground.Add(At(110, 100, 1));
        ground.Add(At(130, 100, 1));   // 30 cells away
        ground.Add(At(101, 100, 2));   // not chosen
        uint edge = ground.Add(At(109, 112, 1));   // exactly 15 cells
        var found = ground.Within(100, 100, 15, g => g.Item.TypeHash == 1);
        Assert.Equal([near, edge], found.Select(f => f.Id).Order());
    }
}
