namespace GameServer.Gateway.Tests;

public class CriticalStrikePolicyTests
{
    [Theory]
    [InlineData(0, 300, 1, 1)]
    [InlineData(10000, 300, -1, 3)]
    [InlineData(5000, 200, 0, 1.5)]
    [InlineData(5000, 200, 1, 1.75)]
    [InlineData(5000, 200, -1, 1.25)]
    [InlineData(2500, 300, 1, 1.875)]
    [InlineData(2500, 300, -1, 1.125)]
    [InlineData(5000, -100, 0, .5)]
    [InlineData(-100, 300, 0, 1)]
    [InlineData(20000, 300, 0, 3)]
    public void Expected_damage_accounts_for_ordinary_lucky_and_unlucky_critical_outcomes(
        int chance, int multiplier, int extra, double expected) =>
        Assert.Equal(expected, CriticalStrikePolicy.DamageFactor(chance, multiplier, extra), 10);

    [Theory]
    [InlineData(5000, 300, 0, 50, .75)]
    [InlineData(5000, 300, 1, 50, .875)]
    [InlineData(5000, 300, -1, 50, .625)]
    [InlineData(10000, 300, 1, 50, 1)]
    [InlineData(0, 300, 1, 50, .5)]
    [InlineData(5000, -100, 0, 50, .375)]
    [InlineData(5000, 300, 1, -100, 0)]
    [InlineData(5000, 300, 1, 200, 2.5)]
    public void Attack_estimates_apply_hit_probability_twice_only_to_the_critical_contribution(
        int chance, int multiplier, int luck, int hitChance, double expected) =>
        Assert.Equal(expected, CriticalStrikePolicy.DamageFactor(chance, multiplier, luck, hitChance), 10);

    sealed class CountRandom(params int[] values) : Random
    {
        public int Calls { get; private set; }
        public override int Next(int maxValue)
        {
            Assert.Equal(10_000, maxValue);
            Assert.True(Calls < values.Length, "Unexpected extra critical draw");
            return values[Calls++];
        }
    }

    [Theory]
    [InlineData(50u, 100u, true)]
    [InlineData(51u, 100u, false)]
    [InlineData(49u, 99u, true)]
    [InlineData(50u, 99u, false)]
    [InlineData(0u, 100u, false)]
    [InlineData(1u, 0u, false)]
    [InlineData(2147483647u, 4294967295u, true)]
    [InlineData(2147483648u, 4294967295u, false)]
    public void Living_low_life_uses_the_inclusive_maximum_life_boundary(uint life, uint maximum, bool expected)
        => Assert.Equal(expected, CriticalStrikePolicy.OnLowLife(life, maximum));

    [Fact]
    public void The_computed_low_life_input_can_include_threshold_override_and_count_as_low_life()
    {
        Assert.True(CriticalStrikePolicy.OnLowLife(55, 100, thresholdOverride: 55, thresholdPercent: 60));
        Assert.False(CriticalStrikePolicy.OnLowLife(56, 100, thresholdOverride: 55, thresholdPercent: 60));
        Assert.True(CriticalStrikePolicy.OnLowLife(100, 100, countsAsLowLife: true));
        Assert.False(CriticalStrikePolicy.OnLowLife(0, 100, countsAsLowLife: true));
    }

    [Fact]
    public void Conditional_luck_is_generic_and_opposing_luck_cancels_before_the_native_clamp()
    {
        Assert.Equal(0, CriticalStrikePolicy.ExtraRolls(0, 1, onLowLife: false));
        Assert.Equal(1, CriticalStrikePolicy.ExtraRolls(0, 1, onLowLife: true));
        Assert.Equal(0, CriticalStrikePolicy.ExtraRolls(-1, 1, onLowLife: true));
        Assert.Equal(1, CriticalStrikePolicy.ExtraRolls(1, 1, onLowLife: true)); // Two lucky sources remain two rolls.
        Assert.Equal(-1, CriticalStrikePolicy.ExtraRolls(int.MinValue, int.MinValue, true, int.MinValue, true));
        Assert.Equal(1, CriticalStrikePolicy.ExtraRolls(int.MaxValue, int.MaxValue, true, int.MaxValue, true));
        Assert.Equal(0, CriticalStrikePolicy.ExtraRolls(-1, 0, false, sharpAndBrittle: true));
        Assert.Equal(1, CriticalStrikePolicy.ExtraRolls(0, 0, false, focus: 1, focused: true));
    }

    [Fact]
    public void Extra_zero_preserves_the_original_draw_and_luck_adds_one_following_draw()
    {
        // This specifies LocalExile's draw ordering, not an established official server RNG sequence.
        var ordinary = new CountRandom(600);
        Assert.False(CriticalStrikePolicy.Roll(500, ordinary, 0));
        Assert.Equal(1, ordinary.Calls);
        var lucky = new CountRandom(600, 400);
        Assert.True(CriticalStrikePolicy.Roll(500, lucky, 1));
        Assert.Equal(2, lucky.Calls);
        var unlucky = new CountRandom(400, 600);
        Assert.False(CriticalStrikePolicy.Roll(500, unlucky, -1));
        Assert.Equal(2, unlucky.Calls);
        var noCrit = new CountRandom();
        Assert.False(CriticalStrikePolicy.Roll(0, noCrit, 1));
        Assert.Equal(0, noCrit.Calls);
        var certain = new CountRandom(9999);
        Assert.True(CriticalStrikePolicy.Roll(10_000, certain, 0)); // Existing first draw is retained at 100%.
        Assert.Equal(1, certain.Calls);
    }

    [Fact]
    public void Even_extreme_direct_policy_inputs_spend_at_most_one_extra_draw()
    {
        var lucky = new CountRandom(400, 600);
        Assert.True(CriticalStrikePolicy.Roll(500, lucky, int.MaxValue));
        Assert.Equal(2, lucky.Calls);
        var unlucky = new CountRandom(400, 600);
        Assert.False(CriticalStrikePolicy.Roll(500, unlucky, int.MinValue));
        Assert.Equal(2, unlucky.Calls);
    }

    [Theory]
    [InlineData(true, true, false, 200)]
    [InlineData(false, true, false, 150)]
    [InlineData(true, false, false, 150)]
    [InlineData(true, true, true, 150)]
    public void Only_the_empty_active_hand_melee_attack_receives_the_fifty_point_multiplier(bool empty,
        bool melee, bool spell, int expected)
        => Assert.Equal(expected, CriticalStrikePolicy.Multiplier(150, 50, empty, melee, spell));
}
