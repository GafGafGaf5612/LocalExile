using GameServer.Core.World;

namespace GameServer.Core.Tests;

public class DamageModelTests
{
    [Fact]
    public void Resistances_are_capped_and_floored()
    {
        Assert.Equal(75, DamageModel.Capped(90));
        Assert.Equal(75, DamageModel.Capped(75));
        Assert.Equal(0, DamageModel.Capped(0));
        Assert.Equal(-60, DamageModel.Capped(-60));
        Assert.Equal(-200, DamageModel.Capped(-300));
        Assert.Equal(90, DamageModel.Capped(100, 90));
        Assert.Equal(90, DamageModel.Capped(100, 200));
    }

    [Fact]
    public void A_hit_is_reduced_by_the_capped_resistance()
    {
        Assert.Equal(100, DamageModel.Mitigate(100, 0));
        Assert.Equal(50, DamageModel.Mitigate(100, 50));
        Assert.Equal(25, DamageModel.Mitigate(100, 75));
        Assert.Equal(25, DamageModel.Mitigate(100, 90));
        Assert.Equal(160, DamageModel.Mitigate(100, -60));
    }

    [Fact]
    public void Damage_over_time_keeps_its_fraction()
    {
        Assert.Equal(50.0, DamageModel.Mitigate(100.0, 50), 3);
        Assert.Equal(25.0, DamageModel.Mitigate(100.0, 75), 3);
        Assert.Equal(160.0, DamageModel.Mitigate(100.0, -60), 3);
    }

    [Fact]
    public void Spell_suppression_caps_at_100_and_reduces_by_40_percent()
    {
        Assert.True(DamageModel.Suppresses(100, 99));
        Assert.True(DamageModel.Suppresses(25, 24));
        Assert.False(DamageModel.Suppresses(25, 25));
        Assert.False(DamageModel.Suppresses(0, 0));
        Assert.Equal(60, DamageModel.Suppress(100));
        Assert.Equal(30, DamageModel.Suppress(50));
        Assert.Equal(50, DamageModel.Suppress(100, 50));
        Assert.Equal(0, DamageModel.Suppress(100, 100));
    }

    [Fact]
    public void Block_is_capped_at_90()
    {
        Assert.True(DamageModel.Blocks(75, 74.9));
        Assert.False(DamageModel.Blocks(75, 75.0));
        Assert.True(DamageModel.Blocks(100, 89.9));
        Assert.False(DamageModel.Blocks(100, 90.0));
        Assert.False(DamageModel.Blocks(0, 0));
    }

    [Fact]
    public void Lucky_block_keeps_the_better_of_two_rolls()
    {
        Assert.Equal(75.0, DamageModel.EffectiveBlockChance(50, lucky: true), 3);
        Assert.Equal(99.0, DamageModel.EffectiveBlockChance(90, lucky: true), 3);
        Assert.True(DamageModel.Blocks(50, 74.9, lucky: true));
        Assert.False(DamageModel.Blocks(50, 75.0, lucky: true));
    }
}
