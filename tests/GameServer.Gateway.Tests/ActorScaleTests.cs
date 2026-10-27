namespace GameServer.Gateway.Tests;

public class ActorScaleTests
{
    [Theory]
    [InlineData(0, 0, 0, 100, 0)]
    [InlineData(10, 0, 0, 100, 10)]
    [InlineData(-10, 0, 0, 100, -10)]
    [InlineData(110, 0, 0, 100, 100)]
    [InlineData(110, 0, 0, 0, 110)]
    [InlineData(60, 0, 0, 40, 40)]
    [InlineData(0, 200, 0, 100, 50)]
    [InlineData(0, -200, 0, 100, -50)]
    [InlineData(0, 0, 149, 100, 1)]
    [InlineData(0, 0, 150, 100, 2)]
    [InlineData(0, 0, -149, 100, -1)]
    [InlineData(0, 0, -150, 100, -2)]
    [InlineData(-200, 0, 0, 100, -99)]
    [InlineData(0, 0, 0, -150, -150)] // Upper limit is applied after the lower floor, as in the native formula.
    [InlineData(8388609, 0, 0, 0, 8388610)] // Native float signed-half addition, not double/Math.Round.
    [InlineData(16777217, 0, 0, 0, 16777216)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue, 0, int.MaxValue)]
    [InlineData(int.MinValue, int.MinValue, int.MinValue, 0, -99)]
    [InlineData(int.MaxValue, 0, 0, 100, 100)]
    [InlineData(10, 0, 0, int.MinValue, int.MinValue)]
    public void Native_float_order_rounding_bounds_and_defined_domain_extremes(
        int raw, int capped, int permyriad, int limit, int expected) =>
        Assert.Equal(expected, ActorScale.Derive(raw, capped, permyriad, limit));

    [Fact]
    public void Real_table_keys_and_Rampager_supply_raw_size_without_granting_the_separate_melee_flag()
    {
        var stats = Program.Stats;
        Assert.Equal(720u, stats.Key("base_actor_scale_+%"));
        Assert.Equal(1973u, stats.Key("actor_scale_+%"));
        Assert.Equal(1974u, stats.Key("capped_actor_scale_+%"));
        Assert.Equal(1975u, stats.Key("actor_scale_+%_limit"));
        Assert.Equal(10778u, stats.Key("metamorphosis_boss_actor_scale_+%_permyriad"));
        Assert.Equal(9807u, stats.Key("scale_melee_range_to_actor_scale"));
        var node = new PassiveTree().Find(49937)!;
        Assert.Equal("Rampager", node.Name);
        Assert.Equal(5, node.Stats.Count);
        Assert.Contains((720u, 10), node.Stats);
        Assert.Contains((21148u, 1), node.Stats);
        Assert.Contains((5341u, 1), node.Stats);
        Assert.Contains((15713u, 1), node.Stats);
        Assert.Contains((5409u, 1), node.Stats);
        Assert.DoesNotContain(node.Stats, s => s.Item1 is 1973 or 9807);
    }

    [Fact]
    public void Only_size_input_aggregation_saturates_and_projection_keeps_explicit_zero_and_final_override()
    {
        Assert.Equal(int.MaxValue, ActorScale.Add(720, int.MaxValue, 10));
        Assert.Equal(int.MinValue, ActorScale.Add(1974, int.MinValue, -10));
        Assert.Equal(int.MaxValue, ActorScale.Add("actor_scale_+%_limit", int.MaxValue, 10));
        var sources = new Dictionary<uint, int> { [720] = 20, [10778] = 150 };
        var snapshot = new Dictionary<uint, int>(sources);
        ActorScale.Project(snapshot, new Dictionary<uint, int> { [720] = 0 }, [(720, 5)]);
        Assert.Equal(2, snapshot[1973]);
        Assert.Equal(20, sources[720]);
        snapshot = new(sources);
        ActorScale.Project(snapshot, new Dictionary<uint, int> { [1973] = int.MinValue }, [(720, 5)]);
        Assert.Equal(int.MinValue, snapshot[1973]);
        Assert.False(sources.ContainsKey(1973));
    }

    [Theory]
    [InlineData(60, -50, 0, 50, 50)]
    [InlineData(110, 0, 0, 100, 100)]
    [InlineData(110, -40, 10, 70, 70)]
    [InlineData(110, 0, 20, 120, 110)]
    [InlineData(110, int.MaxValue, 0, int.MaxValue, 110)]
    [InlineData(60, int.MinValue, 0, int.MinValue + 100, int.MinValue + 100)]
    public void Metadata_limit_adds_to_ordinary_and_cosmetic_inputs_before_dev_replacement(
        int raw, int ordinaryLimit, int cosmeticLimit, int expectedLimit, int expectedScale)
    {
        var sources = new Dictionary<uint, int> { [720] = raw, [1975] = ordinaryLimit };
        var dev = new Dictionary<uint, int>();
        Dictionary<uint, int> Build()
        {
            var snapshot = new Dictionary<uint, int>(sources);
            ActorScale.Project(snapshot, dev, [(1975, cosmeticLimit)]);
            return snapshot;
        }
        for (int i = 0; i < 2; i++)
        {
            var snapshot = Build();
            Assert.Equal(expectedLimit, snapshot[1975]);
            Assert.Equal(expectedScale, snapshot[1973]);
        }
        dev[1975] = 0;
        Assert.Equal(0, Build()[1975]);
        Assert.Equal(raw, Build()[1973]);
        dev[1973] = -123;
        Assert.Equal(-123, Build()[1973]);
        dev.Clear();
        Assert.Equal(expectedScale, Build()[1973]);
        Assert.Equal(ordinaryLimit, sources[1975]);
        Assert.False(sources.ContainsKey(1973));
    }
}
