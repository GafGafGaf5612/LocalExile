namespace GameServer.Gateway.Tests;

public class RecoveryPoolTests
{
    [Theory]
    [InlineData(0.016)]
    [InlineData(0.020)]
    [InlineData(0.250)]
    public void Fractional_regeneration_keeps_the_same_rate_across_step_sizes(double step)
    {
        uint life = 100;
        double carry = 0, elapsed = 0;
        for (int i = 0; i < 100; i++)
        {
            life = RecoveryPool.Advance(life, 10000, 23.75 * step, ref carry, minimum: 1);
            elapsed += step;
        }
        Assert.Equal(100 + 23.75 * elapsed, life + carry, 8);
    }

    [Fact]
    public void Jitter_uses_elapsed_game_time_and_zero_elapsed_pause_adds_nothing()
    {
        uint life = 10;
        double carry = 0, elapsed = 0;
        foreach (double seconds in new[] { 0.016, 0.019, 0.032, 0.0, 0.417, 0.016 })
        {
            life = RecoveryPool.Advance(life, 10000, 1000 * seconds, ref carry, minimum: 1);
            elapsed += seconds;
        }
        Assert.Equal(10 + 1000 * elapsed, life + carry, 8);
    }

    [Fact]
    public void God_regeneration_is_small_updates_instead_of_quarter_life_jumps()
    {
        double carry = 0;
        uint life = 1;
        for (int i = 0; i < 62; i++)
        {
            uint before = life;
            life = RecoveryPool.Advance(life, 10000, 10000 * 0.016, ref carry, minimum: 1);
            Assert.InRange(life - before, 159u, 161u);
        }
        Assert.Equal(9921u, life);
    }

    [Fact]
    public void Full_pool_discards_fraction_and_excess_instead_of_banking_recovery()
    {
        double carry = 0.9;
        Assert.Equal(100u, RecoveryPool.Advance(99, 100, 1000, ref carry, minimum: 1));
        Assert.Equal(0, carry);
        Assert.Equal(1u, RecoveryPool.Advance(1, 100, 0.2, ref carry, minimum: 1));
    }

    [Fact]
    public void Regeneration_flask_and_RF_are_combined_before_rounding_and_RF_is_nonlethal()
    {
        double carry = 0;
        Assert.Equal(52u, RecoveryPool.Advance(50, 100, 0.8 + 2.1 - 0.4, ref carry, minimum: 1));
        Assert.Equal(0.5, carry, 8);
        Assert.Equal(1u, RecoveryPool.Advance(1, 100, -100.75, ref carry, minimum: 1));
        Assert.Equal(0, carry);
    }

    [Fact]
    public void Dead_life_stays_zero_but_unreserved_mana_recovers_from_zero_and_stops_at_cap()
    {
        double carry = 0.75;
        Assert.Equal(0u, RecoveryPool.Advance(0, 100, 50, ref carry, minimum: 1));
        Assert.Equal(0, carry);
        Assert.Equal(10u, RecoveryPool.Advance(0, 20, 10.5, ref carry));
        Assert.Equal(20u, RecoveryPool.Advance(10, 20, 100, ref carry));
        Assert.Equal(0, carry);
    }
}
