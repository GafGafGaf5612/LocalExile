namespace GameServer.Gateway.Tests;

public class RecentEnemyHitsTests
{
    [Fact]
    public void Real_node_stat_keys_and_four_second_reminder_are_required_inputs()
    {
        Assert.Equal(20680u, Program.Stats.Key(RecentEnemyHits.PerHit));
        Assert.Equal(4534u, Program.Stats.Key(RecentEnemyHits.Counter));
        Assert.Equal(8404u, Program.Stats.Key(RecentEnemyHits.GenericChance));
        Assert.Equal(12268u, Program.Stats.Key(RecentEnemyHits.VirtualChance));
        var node = new PassiveTree().Find(2103);
        Assert.NotNull(node);
        Assert.Equal("Steel Willed", node.Name);
        Assert.Contains((20680u, 10), node.Stats);
        Assert.Equal(4, RecentEnemyHits.RecentlySeconds);
    }

    [Fact]
    public void Prior_commits_including_equal_timestamps_count_and_exact_four_seconds_expires()
    {
        var hits = new RecentEnemyHits();
        foreach (int expected in new[] { 0, 10, 20, 30, 30 })
        {
            Assert.Equal(expected, RecentEnemyHits.Chance(hits.Count(10), 10, 0));
            hits.Record(10);
        }
        hits.Record(11);
        Assert.Equal(6, hits.Count(13.999999));
        Assert.Equal(1, hits.Count(14));
        Assert.Equal(0, hits.Count(15));
    }

    [Theory]
    [InlineData(10, 10, 40, 70)]
    [InlineData(0, 10, 40, 40)]
    [InlineData(10, 10, 100, 100)]
    [InlineData(10, 10, -40, 0)]
    [InlineData(10, -10, 50, 0)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue, 100)]
    [InlineData(int.MaxValue, int.MinValue, int.MaxValue, 0)]
    [InlineData(int.MaxValue, int.MaxValue, int.MinValue, 0)]
    public void Only_the_per_hit_contribution_is_capped_and_wide_math_clamps_the_total(
        int count, int perHit, int generic, int expected) =>
        Assert.Equal(expected, RecentEnemyHits.Chance(count, perHit, generic));

    [Theory]
    [InlineData(500, 29, true, 1000)]
    [InlineData(500, 30, false, 500)]
    [InlineData(int.MaxValue, 29, true, int.MaxValue)]
    [InlineData(-1, 29, false, 0)]
    public void Strict_chance_boundary_and_saturating_nonnegative_armour(int armour, int roll, bool doubled, int expected)
    {
        var random = new ArmourRoll(roll);
        var decision = RecentEnemyHits.Resolve(armour, 30, true, random);
        Assert.Equal(doubled, decision.Doubled);
        Assert.Equal(expected, decision.EffectiveArmour);
        Assert.Equal(armour > 0 ? 1 : 0, random.Calls);
    }

    sealed class ArmourRoll(int roll) : Random
    {
        public int Calls;
        public override int Next(int maxValue) { Calls++; Assert.Equal(100, maxValue); return roll; }
    }
}
