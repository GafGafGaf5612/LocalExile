namespace GameServer.Gateway.Tests;

public class FortificationStateTests
{
    [Fact]
    public void Real_Behemoth_nodes_supply_maximum_regeneration_and_speed_inputs()
    {
        var tree = new PassiveTree();
        var catalog = new StatCatalog();
        var stats = tree.Stats([28815, 37308]);
        int Get(string name) => stats.GetValueOrDefault(catalog.Key(name)!.Value);
        Assert.Equal(1, Get("fortify_on_hit"));
        Assert.Equal(10, Get(FortificationState.MaximumStat));
        Assert.Equal(12, Get(FortificationState.LifeRegenPerStack));
        Assert.Equal(1, Get(FortificationState.AttackSpeedPerStack));
        var state = new FortificationState();
        Assert.True(state.GrantExplicitStacks(0, 30));
        var value = state.Read(0, Get(FortificationState.MaximumStat));
        Assert.Equal(30, value.Current);
        Assert.Equal(360, value.LifeRegenerationPerMinutePercent(Get(FortificationState.LifeRegenPerStack)));
        Assert.Equal(30, value.IncreasedAttackSpeedPercent(Get(FortificationState.AttackSpeedPerStack)));
        Assert.Equal(.7, value.HitDamageFactor, 12);
        // Removing Tireless changes the effective limit; removing Dire Rampart removes its per-stack effects.
        Assert.Equal(20, state.Read(0).Current);
        Assert.Equal(0, value.LifeRegenerationPerMinutePercent(0));
        Assert.Equal(0, value.IncreasedAttackSpeedPercent(0));
    }

    [Fact]
    public void Independent_expiry_and_overflow_are_explicit_LocalExile_lifetime_policy()
    {
        var state = new FortificationState();
        state.GrantExplicitStacks(0, 18);
        state.GrantExplicitStacks(1, 18);
        Assert.Equal(36L, state.Read(1).Uncapped);
        Assert.Equal(20, state.Read(1).Current);
        Assert.Equal(18, state.Read(6).Current);
        Assert.Equal(0, state.Read(7).Current);
        Assert.Null(state.Read(7).NextExpiry);
    }

    [Fact]
    public void Hit_gain_throttle_is_shared_and_zero_gains_do_not_consume_it_under_our_policy()
    {
        var state = new FortificationState();
        Assert.False(state.TryGrantFromHit(0, 0));
        Assert.True(state.TryGrantFromHit(0, 3));
        Assert.False(state.TryGrantFromHit(.05, 10));
        Assert.True(state.TryGrantFromHit(.1, 4));
        Assert.Equal(7, state.Read(.1).Current);
        Assert.True(state.TryGrantFromHit(.2, 1));
        Assert.True(state.TryGrantFromHit(.3, 1));
    }

    [Fact]
    public void Supplied_game_time_controls_pause_duration_and_death_clear()
    {
        var state = new FortificationState();
        state.GrantExplicitStacks(1, 7, durationIncreasePercent: 100);
        // Repeated reads at the same game time represent a paused simulation, irrespective of wall time.
        Assert.Equal(state.Read(1), state.Read(1));
        Assert.Equal(7, state.Read(12.999).Current);
        Assert.Equal(0, state.Read(13).Current);
        state.GrantExplicitStacks(13, 10);
        state.Clear();
        Assert.Equal(0, state.Read(0).Current); // a new connection clock can start after Clear
        Assert.True(state.TryGrantFromHit(0, 1));
    }

    [Fact]
    public void Counts_limits_and_derived_signed_effects_saturate_without_wrapping()
    {
        var state = new FortificationState();
        Assert.False(state.GrantExplicitStacks(0, -1));
        Assert.False(state.GrantExplicitStacks(0, 1, -100));
        state.GrantExplicitStacks(0, int.MaxValue);
        state.GrantExplicitStacks(0, int.MaxValue);
        var value = state.Read(0, int.MaxValue);
        Assert.Equal(2L * int.MaxValue, value.Uncapped);
        Assert.Equal(int.MaxValue, value.Current);
        Assert.Equal(int.MaxValue, value.LifeRegenerationPerMinutePercent(int.MaxValue));
        Assert.Equal(int.MinValue, value.IncreasedAttackSpeedPercent(int.MinValue));
        Assert.Equal(0, value.HitDamageFactor);
        Assert.Equal(0, state.Read(0, int.MinValue).Current);
    }

    [Fact]
    public void Invalid_or_backwards_game_time_is_rejected()
    {
        var state = new FortificationState();
        state.Read(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Read(.9));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Read(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.GrantExplicitStacks(double.PositiveInfinity, 1));
    }
}
