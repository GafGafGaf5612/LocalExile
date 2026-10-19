using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class SavedMonsterTests
{
    static SpawnedMonster Rhoa(int x, int y, uint life = 100, bool dead = false) =>
        new("Rhoa@14", new WalkEstimator(x, y, 30, null, 2000, 2000), aggroRange: 60, stopRange: 3, maxLife: 100)
        {
            Variety = new MonsterCatalog.Variety(42, "Metadata/Monsters/Rhoa/RhoaBare", "Rhoa", "base", null, null),
            Level = 14, Rarity = MonsterRarity.Magic, Life = life, Dead = dead, Aggro = true,
        };

    [Fact]
    public void Living_monsters_are_saved_where_they_are_now_with_their_damage()
    {
        var walking = Rhoa(100, 100, life: 40);
        walking.Walk.Order(160, 100, 0);   // 60 cells at 30 cells/s: there after 2 s

        var saved = Assert.Single(SavedMonster.Save([walking], now: 5));

        Assert.Equal(new SavedMonster(42, 14, MonsterRarity.Magic, Hostile: true, 160, 100, Life: 40), saved);
    }

    [Fact]
    public void Dead_monsters_stay_dead()
    {
        Assert.Empty(SavedMonster.Save([Rhoa(1, 1, life: 0, dead: true)], now: 0));
    }

    [Fact]
    public void Leaving_keeps_the_monsters_for_the_next_connection_until_the_instance_expires()
    {
        var store = new InstanceStore(TimeProvider.System, TimeSpan.FromMinutes(6));
        var first = store.Enter(4, 1);
        Assert.Null(first.Monsters);   // never populated: plan the packs
        first.Monsters = SavedMonster.Save([Rhoa(7, 8)], now: 0);
        store.Leave(first);

        var back = Assert.Single(store.Enter(4, 1).Monsters!);
        Assert.Equal((7, 8), (back.X, back.Y));
    }
}
