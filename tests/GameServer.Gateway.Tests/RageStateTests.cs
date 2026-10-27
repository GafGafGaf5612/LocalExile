namespace GameServer.Gateway.Tests;

public class RageStateTests
{
    [Theory]
    [InlineData(-100, 30)]
    [InlineData(-20, 22)]
    [InlineData(0, 20)]
    [InlineData(100, 10)]
    public void Loss_speed_changes_tenths_per_second_after_the_same_two_second_delay(int percent, int expected)
    {
        var input = new RageState.Inputs(LossRatePercent: percent);
        var rage = new RageState(); rage.Gain(30, 0, input);
        Assert.Equal(2, input.LossDelaySeconds);
        Assert.Equal(30, rage.Read(2, input).Current);
        Assert.Equal(expected, rage.Read(3, input).Current);
    }

    [Fact]
    public void Carry_preserves_fractional_loss_delay_and_interval_on_an_unrelated_destination_clock()
    {
        var old = new RageState();
        var input = new RageState.Inputs(GainOnAttackHit: 5);
        Assert.True(old.Hit(50, input, true, false, false, 99));
        var snapshot = old.Carry(50.2, input);
        Assert.Equal(0, old.Read(50.2, input).Current);
        var next = new RageState(); next.Restore(snapshot, 100);
        Assert.False(next.Hit(100.299, input, true, false, false, 99));
        Assert.True(next.Hit(100.3, input, true, false, false, 99));
        Assert.Equal(10, next.Read(102.399, input).Current);
        Assert.Equal(9, next.Read(102.4, input).Current);

        var decaying = new RageState(); decaying.Gain(30, 0, default);
        var fraction = decaying.Carry(2.15, default);
        next.Restore(fraction, 500);
        Assert.Equal(29, next.Read(500.049, default).Current);
        Assert.Equal(28, next.Read(500.05, default).Current);
    }

    [Fact]
    public void Version_one_capped_attempts_neither_refresh_the_delay_nor_consume_the_interval()
    {
        Assert.Equal(1, RageState.PolicyVersion);
        var rage = new RageState(); var input = new RageState.Inputs(GainOnHit: 1);
        rage.Gain(30, 0, input);
        Assert.False(rage.Hit(2, input, true, true, true, 99));
        Assert.Equal(29, rage.Read(2.1, input).Current);
        Assert.True(rage.Hit(2.1, input, true, true, true, 99));
    }

    [Fact]
    public void Recovery_changes_only_delay_and_invalid_rate_stalls_without_infinite_loss()
    {
        var rage = new RageState(); var fast = new RageState.Inputs(LossDelayRecoveryPercent: 100);
        rage.Gain(30, 0, fast);
        Assert.Equal(30, rage.Read(1.099, fast).Current);
        Assert.Equal(29, rage.Read(1.1, fast).Current);
        Assert.Equal(20, rage.Read(2, fast).Current); // Still ten per second, not twenty.
        var stalled = fast with { LossDelayRecoveryPercent = -100 };
        rage.Read(2, stalled);
        Assert.Equal(20, rage.Read(10000, stalled).Current);
        Assert.Throws<ArgumentOutOfRangeException>(() => rage.Read(double.NaN, default));
        Assert.Equal(0, new RageState.Inputs(MaximumBonus: int.MinValue).Maximum);
        Assert.Equal(int.MaxValue, new RageState.Inputs(MaximumBonus: int.MaxValue).Maximum);
    }

    static RageState.Inputs Sheer => new(MaximumBonus: 15, MaximumOnHitGainChance: 15,
        GainOnHit: 1, GainOnAttackHit: 2, GainOnMeleeHit: 3, GainOnAttackCritical: 4, GainOnMeleeCritical: 5,
        GainWhenHit: 6);

    [Fact]
    public void Sheer_fury_requires_a_real_gain_and_rolls_15_percent_at_the_shared_hit_boundary()
    {
        var rage = new RageState();
        var noGrant = new RageState.Inputs(MaximumBonus: 15, MaximumOnHitGainChance: 15);
        Assert.False(rage.Hit(0, noGrant, true, true, true, 0));
        Assert.Equal(new RageState.Value(0, 45, 0), rage.Read(0, noGrant));
        Assert.True(rage.Hit(0, Sheer, true, true, true, 15));
        Assert.Equal(15, rage.Read(0, Sheer).Current);
        Assert.False(rage.EnemyHit(.499, Sheer, 0));
        Assert.True(rage.Hit(.5, Sheer, false, false, false, 14));
        Assert.Equal(45, rage.Read(.5, Sheer).Current);
    }

    [Fact]
    public void Inherent_loss_has_a_two_second_grace_then_integer_tenths_independent_of_read_frequency()
    {
        var coarse = new RageState(); var fine = new RageState();
        coarse.Gain(30, 0, default); fine.Gain(30, 0, default);
        Assert.Equal(30, coarse.Read(2.099, default).Current);
        Assert.Equal(29, coarse.Read(2.1, default).Current);
        for (int i = 1; i <= 40; i++) fine.Read(i / 10.0, default);
        Assert.Equal(10, coarse.Read(4, default).Current);
        Assert.Equal(10, fine.Read(4, default).Current);
        Assert.Equal(0, fine.Read(100, default).Current);
    }

    [Fact]
    public void Delay_recovery_divides_the_grace_and_enemy_hits_delay_loss_without_a_grant()
    {
        var input = new RageState.Inputs(LossDelayMilliseconds: 1000, LossDelayRecoveryPercent: 50);
        var rage = new RageState(); rage.Gain(30, 0, input);
        Assert.Equal(30, rage.Read(2, input).Current); // (2 + 1) / 1.5 = 2 seconds.
        Assert.Equal(29, rage.Read(2.1, input).Current);
        Assert.False(rage.EnemyHit(2.1, input, 99));
        Assert.Equal(29, rage.Read(4.1, input).Current);
        Assert.Equal(28, rage.Read(4.2, input).Current);
    }

    [Fact]
    public void Prevention_blocks_all_positive_gains_but_neither_spending_nor_enemy_hit_activity()
    {
        var input = Sheer with { CannotGainDuringSoulGainPrevention = true };
        var rage = new RageState(); rage.Gain(30, 0, input);
        Assert.False(rage.Gain(20, 1, input, soulGainPrevention: true));
        Assert.False(rage.Hit(1, input, true, true, true, 0, soulGainPrevention: true));
        Assert.False(rage.EnemyHit(1.5, input, 0, soulGainPrevention: true));
        Assert.True(rage.Spend(15, 1.5, input));
        Assert.Equal(15, rage.Read(3.5, input).Current);
        Assert.True(rage.Hit(3.5, input, true, false, false, 15)); // Blocked gain did not take the cooldown.
        Assert.Equal(18, rage.Read(3.5, input).Current);
        Assert.False(rage.Gain(1, 3.5, input with { CannotGain = true }));
    }

    [Fact]
    public void Effect_scales_damage_value_after_the_current_cap_and_refund_removes_only_the_extra_capacity()
    {
        var input = new RageState.Inputs(MaximumBonus: 15, EffectPercent: 50, NoInherentLoss: true);
        var rage = new RageState(); rage.Gain(99, 0, input);
        Assert.Equal(new RageState.Value(45, 45, 67), rage.Read(100, input));
        Assert.Equal(new RageState.Value(30, 30, 45), rage.Read(100, input with { MaximumBonus = 0 }));
        Assert.Equal(new RageState.Value(22, 22, 33), rage.Read(100, input with { HalfMaximum = true }));
        rage.Clear();
        Assert.Equal(0, rage.Read(100, input).Current);
        Assert.True(rage.Hit(100, Sheer, true, false, false, 15));
    }

    [Fact]
    public void A_payment_is_atomic_and_does_not_count_as_gaining_rage()
    {
        var rage = new RageState(); rage.Gain(20, 0, default);
        Assert.False(rage.Spend(21, 1.5, default));
        Assert.Equal(20, rage.Read(1.5, default).Current);
        Assert.True(rage.Spend(15, 1.5, default));
        Assert.Equal(4, rage.Read(2.1, default).Current);
        Assert.False(rage.Spend(5, 2.1, default));
        Assert.True(rage.Spend(4, 2.1, default));
        Assert.Equal(0, rage.Read(2.1, default).Current);
    }

    [Fact]
    public void Removing_no_loss_settles_the_old_state_before_adopting_new_parameters()
    {
        var input = new RageState.Inputs(NoInherentLoss: true);
        var rage = new RageState(); rage.Gain(30, 0, input);
        Assert.Equal(30, rage.Read(100, input with { NoInherentLoss = false }).Current);
        Assert.Equal(20, rage.Read(101, default).Current);
        Assert.Equal(20, rage.Read(100, default).Current); // A backwards clock cannot restore lost Rage.
    }
}
