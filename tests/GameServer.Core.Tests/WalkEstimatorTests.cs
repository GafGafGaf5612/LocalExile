using GameServer.Core.World;

namespace GameServer.Core.Tests;

public class WalkEstimatorTests
{
    [Fact]
    public void Follows_the_line_at_the_given_speed()
    {
        var w = new WalkEstimator(0, 0, speed: 10);
        Assert.Equal((0, 0), w.Order(100, 0, now: 0));
        Assert.Equal((50, 0), w.Position(5));
        Assert.Equal((100, 0), w.Position(60));
    }

    [Fact]
    public void A_new_order_starts_from_where_the_walker_is()
    {
        var w = new WalkEstimator(0, 0, speed: 10);
        w.Order(100, 0, 0);
        Assert.Equal((30, 0), w.Order(30, 50, 3));
        Assert.Equal((30, 50), w.Position(100));
    }

    [Fact]
    public void Ends_at_the_closest_reachable_cell()
    {
        var w = new WalkEstimator(0, 0, speed: 1000, walkable: (x, _) => x <= 40, width: 200, height: 10);
        w.Order(100, 0, 0);
        Assert.Equal((40, 0), w.Position(10));
    }

    [Fact]
    public void Walks_around_a_wall()
    {
        // A wall at x = 10 for y < 3 in a 20 x 10 grid: the path must detour through y >= 3.
        var w = new WalkEstimator(5, 0, speed: 1, walkable: (x, y) => x != 10 || y >= 3, width: 20, height: 10);
        w.Order(15, 0, 0);
        Assert.Equal((15, 0), w.Position(1000));
        var mid = w.Position(9);
        Assert.True(mid.Y > 0, $"expected a detour, got {mid}");
    }

    [Fact]
    public void Unreachable_target_does_not_take_a_long_detour()
    {
        // Open strip y in [0, 4) up to x = 20, plus a long corridor that loops far around (x = 0..20 at y = 70..73 and a
        // link at x = 0). The target (38, 20) is unreachable; the far corridor would get closer only after a long walk.
        bool open(int x, int y) => (y < 4 && x <= 20) || (x <= 1 && y < 74) || (y >= 70 && y < 74 && x <= 20);
        var w = new WalkEstimator(10, 1, speed: 1000, walkable: open, width: 40, height: 80);
        w.Order(38, 20, 0);
        var end = w.Position(100);
        Assert.Equal((20, 3), end);
    }
}
