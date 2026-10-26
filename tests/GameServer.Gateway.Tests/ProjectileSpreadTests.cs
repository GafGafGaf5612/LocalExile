namespace GameServer.Gateway.Tests;

public class ProjectileSpreadTests
{
    [Theory]
    [InlineData(1, "0")]
    [InlineData(2, "-5,5")]
    [InlineData(3, "-10,0,10")]
    [InlineData(5, "-20,-10,0,10,20")]
    public void Angles_are_centred_on_the_aim(int count, string degrees)
    {
        Assert.Equal(degrees, string.Join(',', ProjectileSpread.Angles(count, 10)));
    }

    [Fact]
    public void Angles_clamp_the_gap_and_a_bad_gap_falls_back()
    {
        Assert.Equal([0d], ProjectileSpread.Angles(1, 200));
        Assert.Equal([-90d, 0d, 90d], ProjectileSpread.Angles(3, 200));
        Assert.Equal([-ProjectileSpread.DefaultGapDegrees, 0d, ProjectileSpread.DefaultGapDegrees],
            ProjectileSpread.Angles(3, double.NaN));
    }

    [Fact]
    public void Rotate_turns_the_aim_around_the_caster()
    {
        Assert.Equal((10, 0), ProjectileSpread.Rotate((0, 0), (10, 0), 0));
        Assert.Equal((0, 10), ProjectileSpread.Rotate((0, 0), (10, 0), 90));
        Assert.Equal((-10, 0), ProjectileSpread.Rotate((0, 0), (10, 0), 180));
        Assert.Equal((-5, 0), ProjectileSpread.Rotate((0, 0), (0, 5), 90));
        Assert.Equal((7, 7), ProjectileSpread.Rotate((7, 7), (7, 7), 45));
    }
}
