using GameServer.Core.World;

namespace GameServer.Core.Tests;

public class ClientPathfinderTests
{
    static List<(int X, int Y)> Path((int, int) start, (int, int) target, Func<int, int, bool>? walkable = null,
        IReadOnlyList<ClientPathfinder.Blocker>? blockers = null, int w = 100, int h = 100) =>
        ClientPathfinder.FindPath(start, target, walkable ?? ((x, y) => x >= 0 && y >= 0 && x < w && y < h), w, h, blockers);

    [Fact]
    public void A_clear_line_is_one_segment()
    {
        Assert.Equal([(10, 10), (40, 22)], Path((10, 10), (40, 22)));
    }

    [Fact]
    public void Standing_on_the_target_or_aiming_off_the_map_stays()
    {
        Assert.Empty(Path((10, 10), (10, 10)));
        Assert.Empty(Path((10, 10), (-1, 10)));
        Assert.Empty(Path((10, 10), (10, 100)));
    }

    [Fact]
    public void Goes_around_a_wall_by_its_corner()
    {
        // A wall at x = 20 for y < 15: the path's only corner is next to the wall's end.
        bool open(int x, int y) => x >= 0 && y >= 0 && x < 100 && y < 100 && !(x == 20 && y < 15);
        var path = Path((10, 5), (30, 5), open);
        Assert.Equal((10, 5), path[0]);
        Assert.Equal((30, 5), path[^1]);
        Assert.Equal(3, path.Count);
        Assert.Equal(20, path[1].X);
        Assert.True(path[1].Y >= 15, $"corner {path[1]}");
    }

    [Fact]
    public void A_detour_outside_the_ellipse_is_not_taken()
    {
        // The only way from (10, 50) to (30, 50) loops 40 cells north; the search ellipse (clamp(2 × 20, 20, 136) = 40)
        // does not reach it, so the walker stops at the explored cell closest to the target.
        bool open(int x, int y) => x >= 0 && y >= 0 && x < 100 && y < 100 && !(x == 20 && y > 8);
        var path = ClientPathfinder.FindPath((10, 50), (30, 50), open, 100, 100, null, 1, 0, 0, out bool reached);
        Assert.False(reached);
        Assert.Equal((19, 50), path[^1]);
    }

    [Fact]
    public void A_detour_inside_the_ellipse_is_taken()
    {
        bool open(int x, int y) => x >= 0 && y >= 0 && x < 100 && y < 100 && !(x == 20 && y > 40);
        var path = ClientPathfinder.FindPath((10, 50), (30, 50), open, 100, 100, null, 1, 0, 0, out bool reached);
        Assert.True(reached);
        Assert.Equal((30, 50), path[^1]);
    }

    [Fact]
    public void An_npc_blocks_a_square_of_chebyshev_radius_five()
    {
        // NPC size 4 (object_size 5), player size 1: a cell is blocked when max(|dx|, |dy|) − 4 < 2.
        var npc = new ClientPathfinder.Blocker(50, 50, 4);
        Assert.True(ClientPathfinder.ObjectBlocks([npc], 55, 45, 2, 5));
        Assert.False(ClientPathfinder.ObjectBlocks([npc], 56, 50, 2, 5));
        var path = Path((30, 50), (70, 50), blockers: [npc]);
        Assert.Equal((70, 50), path[^1]);
        foreach (var (a, b) in path.Zip(path.Skip(1)))
            foreach (var (x, y) in ClientPathfinder.Line(a, b))
                Assert.True(Math.Max(Math.Abs(x - 50), Math.Abs(y - 50)) >= 6, $"({x}, {y}) is inside the NPC");
    }

    [Fact]
    public void Line_is_the_clients_bresenham()
    {
        Assert.Equal([(0, 0), (1, 0), (2, 1), (3, 1), (4, 2)], ClientPathfinder.Line((0, 0), (4, 2)));
        Assert.Equal([(0, 0), (0, 1), (1, 2), (1, 3)], ClientPathfinder.Line((0, 0), (1, 3)));
        Assert.Equal([(3, 3)], ClientPathfinder.Line((3, 3), (3, 3)));
    }
}
