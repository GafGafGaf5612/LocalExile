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

    [Fact]
    public void The_threshold_follows_the_max_life()
    {
        var m = Monster(1000, 0, 7);
        Assert.Null(Flinch(m, 99, now: 10));
        Assert.NotNull(Flinch(m, 100, now: 10));
    }

    [Fact]
    public void A_monster_flinches_at_most_every_0_4_s()
    {
        var m = Monster(100, 0, 7);
        Assert.NotNull(Flinch(m, 50, now: 10));
        Assert.Null(Flinch(m, 50, now: 10.3));
        Assert.NotNull(Flinch(m, 50, now: 10.4));
    }

    [Fact]
    public void A_clip_longer_than_0_4_s_holds_the_next_flinch_until_it_has_played()
    {
        var m = Monster(100, 0.8, 7);
        Assert.NotNull(Flinch(m, 50, now: 10));
        Assert.Null(Flinch(m, 50, now: 10.45));
        Assert.Null(Flinch(m, 50, now: 10.79));
        Assert.NotNull(Flinch(m, 50, now: 10.8));
    }

    [Fact]
    public void A_dead_monster_does_not_flinch()
    {
        var m = Monster(100, 0, 7);
        m.Life = 0;
        Assert.Null(Flinch(m, 50, now: 0));
        var marked = Monster(100, 0, 7);
        marked.Dead = true;
        Assert.Null(Flinch(marked, 50, now: 0));
    }

    [Fact]
    public void A_variety_without_a_TakeHit_clip_does_not_flinch()
    {
        Assert.Null(Flinch(Monster(100), 50, now: 0));
    }
}
