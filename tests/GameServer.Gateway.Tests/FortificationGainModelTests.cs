using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class FortificationGainModelTests
{
    [Theory]
    [InlineData(MonsterRarity.Normal, 2)]
    [InlineData(MonsterRarity.Magic, 3)]
    [InlineData(MonsterRarity.Rare, 4)]
    [InlineData(MonsterRarity.Unique, 6)]
    public void Version_one_rarity_factors_are_explicit_LocalExile_policy(MonsterRarity rarity, int expected)
    {
        Assert.Equal(1, FortificationGainModel.Version);
        Assert.Equal(expected, FortificationGainModel.Calculate(1, 100, rarity, 30));
    }

    [Fact]
    public void Exact_rational_square_comparison_keeps_floor_boundaries_stable()
    {
        Assert.Equal(1, FortificationGainModel.Calculate(1m, 400m, MonsterRarity.Normal, 20));
        Assert.Equal(0, FortificationGainModel.Calculate(.9999999999999999999999999999m, 400m, MonsterRarity.Normal, 20));
        Assert.Equal(0, FortificationGainModel.Calculate(.0001m, 100m, MonsterRarity.Normal, 20));
        Assert.Equal(1, FortificationGainModel.Calculate(1.000000000000000000000000001m, 400m, MonsterRarity.Normal, 20));
    }

    [Fact]
    public void Increases_add_before_floor_and_unique_increases_are_scoped()
    {
        var modifiers = new FortificationGainModel.Modifiers(50, 100);
        Assert.Equal(3, FortificationGainModel.Calculate(1, 100, MonsterRarity.Normal, 30, modifiers));
        Assert.Equal(15, FortificationGainModel.Calculate(1, 100, MonsterRarity.Unique, 30, modifiers));
        Assert.Equal(0, FortificationGainModel.Calculate(1, 100, MonsterRarity.Normal, 30, new(-101, int.MaxValue)));
    }

    [Fact]
    public void Threshold_defaults_to_maximum_life_and_an_explicit_threshold_overrides_it()
    {
        Assert.Equal(2, FortificationGainModel.Calculate(1, 100, MonsterRarity.Normal, 20));
        Assert.Equal(1, FortificationGainModel.Calculate(1, 100, MonsterRarity.Normal, 20, ailmentThreshold: 400));
        Assert.Equal(0, FortificationGainModel.Calculate(1, 100, MonsterRarity.Normal, 20, ailmentThreshold: 0));
    }

    [Fact]
    public void Per_hit_cap_and_signed_extreme_inputs_do_not_overflow()
    {
        Assert.Equal(30, FortificationGainModel.Calculate(decimal.MaxValue, .0000000000000000000000000001m,
            MonsterRarity.Unique, 30, new(int.MaxValue, int.MaxValue)));
        Assert.Equal(int.MaxValue, FortificationGainModel.Calculate(decimal.MaxValue, 1, MonsterRarity.Unique,
            int.MaxValue, new(int.MaxValue, int.MaxValue)));
        Assert.Equal(0, FortificationGainModel.Calculate(-1, 100, MonsterRarity.Normal, 30));
        Assert.Equal(0, FortificationGainModel.Calculate(100, -1, MonsterRarity.Normal, 30));
        Assert.Equal(0, FortificationGainModel.Calculate(100, 100, MonsterRarity.Normal, -1));
    }

    [Fact]
    public void Modifiers_read_actual_catalog_keys_without_a_catalog_dependency_in_the_model()
    {
        var catalog = new StatCatalog();
        Assert.Equal(15042u, catalog.Key(FortificationGainModel.GainedFromHitsStat));
        Assert.Equal(15308u, catalog.Key(FortificationGainModel.GainedAgainstUniqueStat));
        var values = new Dictionary<string, int>
        { [FortificationGainModel.GainedFromHitsStat] = 40, [FortificationGainModel.GainedAgainstUniqueStat] = 100 };
        Assert.Equal(new FortificationGainModel.Modifiers(40, 100),
            FortificationGainModel.Modifiers.From(name => values.GetValueOrDefault(name)));
    }
}
