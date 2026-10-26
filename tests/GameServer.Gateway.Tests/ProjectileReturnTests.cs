namespace GameServer.Gateway.Tests;

public class ProjectileReturnTests
{
    static Func<string, int> Stats(params (string Name, int Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void Always_return_stats_return_and_cannot_return_wins()
    {
        Assert.True(ProjectileReturn.ShouldReturn("Fireball", false, Stats(("projectiles_return", 1)), 99));
        Assert.True(ProjectileReturn.ShouldReturn("BurningArrow", true, Stats(("attack_projectiles_return", 1)), 99));
        Assert.False(ProjectileReturn.ShouldReturn("Fireball", false, Stats(("attack_projectiles_return", 1)), 0));
        Assert.False(ProjectileReturn.ShouldReturn("Fireball", false,
            Stats(("projectiles_return", 1), ("skill_projectiles_cannot_return", 1)), 0));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(49, true)]
    [InlineData(50, false)]
    [InlineData(99, false)]
    public void A_chance_rolls_against_the_projectile_return_chance(int roll, bool expected)
    {
        Assert.Equal(expected, ProjectileReturn.ShouldReturn("Fireball", false,
            Stats(("projectile_return_%_chance", 50)), roll));
    }

    [Fact]
    public void The_chance_is_clamped_and_the_dev_flag_forces_either_way()
    {
        Assert.False(ProjectileReturn.ShouldReturn("Fireball", false, Stats(("projectile_return_%_chance", -10)), 0));
        Assert.True(ProjectileReturn.ShouldReturn("Fireball", false, Stats(("projectile_return_%_chance", 500)), 99));
        Assert.False(ProjectileReturn.ShouldReturn("Fireball", false, Stats(), 0, "0"));
        Assert.True(ProjectileReturn.ShouldReturn("Fireball", false, Stats(), 99, "1"));
    }

    [Theory]
    [InlineData("Spark")]
    [InlineData("SparkAltX")]
    [InlineData("RainOfArrows")]
    [InlineData("VenomGyre")]
    [InlineData("WinterOrb")]
    [InlineData("RollingMagma")]
    public void Wiki_exceptions_cannot_return(string effect)
    {
        Assert.False(ProjectileReturn.ShouldReturn(effect, false, Stats(("projectiles_return", 1)), 0));
        Assert.False(ProjectileReturn.ShouldReturn(effect, false, Stats(), 99, "1"));
    }

    [Fact]
    public void Freezing_pulse_only_returns_from_a_collision()
    {
        Assert.True(ProjectileReturn.CannotReturnFromDistance("FreezingPulse"));
        Assert.False(ProjectileReturn.CannotReturnFromDistance("Fireball"));
        Assert.False(ProjectileReturn.CannotReturnFromDistance(null));
    }

    [Fact]
    public void The_support_less_damage_and_the_returning_finals_multiply()
    {
        Assert.Equal(0.3, ProjectileReturn.DamageMore(Stats(("support_return_returning_projectiles_damage_+%_final", -70))), 6);
        Assert.Equal(0.9, ProjectileReturn.DamageMore(Stats(
            ("support_return_returning_projectiles_damage_+%_final", -70),
            ("damage_+%_final_from_returning_projectile", 200))), 6);
        Assert.Equal(1, ProjectileReturn.DamageMore(Stats()));
    }

    [Fact]
    public void Distance_to_the_return_line_finds_monsters_on_it()
    {
        Assert.Equal(0, ProjectileReturn.DistanceToSegment((5, 0), (0, 0), (10, 0)), 6);
        Assert.Equal(3, ProjectileReturn.DistanceToSegment((5, 3), (0, 0), (10, 0)), 6);
        Assert.Equal(5, ProjectileReturn.DistanceToSegment((15, 0), (0, 0), (10, 0)), 6);
        Assert.Equal(0, ProjectileReturn.DistanceToSegment((5, 5), (5, 5), (5, 5)), 6);
    }
}
