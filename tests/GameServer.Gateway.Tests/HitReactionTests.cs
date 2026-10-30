using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class HitReactionTests
{
    static SpawnedMonster Monster(uint maxLife = 100, double seconds = 0, params int[] flinches) =>
        new("Rhoa@1", new WalkEstimator(0, 0, 30, null, 2000, 2000), aggroRange: 60, stopRange: 3, maxLife: maxLife)
        {
            HitAnimations = [.. flinches.Select(i => new HitAnimation(i, seconds))],
        };

    static HitAnimation? Flinch(SpawnedMonster m, int damage, double now) => HitReaction.TryFlinch(m, damage, now, new Random(1));

    [Fact]
    public void A_hit_below_a_tenth_of_max_life_does_not_flinch()
    {
        var m = Monster(100, 0, 7);
        Assert.Null(Flinch(m, 9, now: 10));
        Assert.Equal(7, Flinch(m, 10, now: 10)?.Index);
    }

}
