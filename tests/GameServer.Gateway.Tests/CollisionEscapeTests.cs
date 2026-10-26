using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class CollisionEscapeTests
{
    [Fact]
    public void Moving_a_device_releases_its_old_cells_and_ejects_from_its_new_cells()
    {
        var conn = new ClientState(new(0, "test", 100, 100, 20, 20, (_, _) => true, []), 0, 1);
        conn.SetBlocker(7, 20, 20, ClientState.MapDeviceBlockerSize);
        Assert.NotNull(CollisionEscape.Find((20, 20), 100, 100, (_, _) => true, conn.StationaryBlockers));
        conn.SetBlocker(7, 40, 40, ClientState.MapDeviceBlockerSize);
        Assert.Null(CollisionEscape.Find((20, 20), 100, 100, (_, _) => true, conn.StationaryBlockers));
        var free = CollisionEscape.Find((40, 40), 100, 100, (_, _) => true, conn.StationaryBlockers)!.Value;
        Assert.True(CollisionEscape.Clear(free, 100, 100, (_, _) => true, conn.StationaryBlockers));
        Assert.Equal(5, Math.Max(Math.Abs(free.X - 40), Math.Abs(free.Y - 40)));
    }

    [Fact]
    public void Escape_considers_all_overlapping_objects_terrain_and_map_edges()
    {
        ClientPathfinder.Blocker[] blockers = [new(2, 2, 3), new(5, 2, 3)];
        bool Floor(int x, int y) => y >= 3 && x <= 12;
        var free = CollisionEscape.Find((2, 2), 15, 12, Floor, blockers)!.Value;
        Assert.True(CollisionEscape.Clear(free, 15, 12, Floor, blockers));
        Assert.Equal((2, 7), free);
        Assert.Null(CollisionEscape.Find((2, 2), 4, 4, (_, _) => false, blockers));
    }

    [Fact]
    public void Safe_players_are_not_moved_and_the_nearest_euclidean_exit_is_used()
    {
        Assert.Null(CollisionEscape.Find((4, 4), 10, 10, (_, _) => true, []));
        bool Floor(int x, int y) => (x, y) is (16, 16) or (17, 10);
        Assert.Equal((17, 10), CollisionEscape.Find((10, 10), 30, 30, Floor, []));
        Assert.Null(CollisionEscape.Find((0, 0), 0, 0, null, []));
    }

    [Theory]
    [InlineData(7.236)]
    [InlineData(30.663)]
    public void Model_hull_not_the_small_ot_size_controls_escape_and_walk_paths(double radius)
    {
        ClientPathfinder.Blocker device = new(100, 100, 3) { CollisionRadius = radius };
        var free = CollisionEscape.Find((100, 100), 250, 250, (_, _) => true, [device])!.Value;
        double distance = Math.Sqrt(Math.Pow(free.X - 100, 2) + Math.Pow(free.Y - 100, 2));
        Assert.True(distance >= radius + 2);
        Assert.True(ClientPathfinder.ObjectBlocks([device], 105, 100, 2, 5));
        Assert.False(ClientPathfinder.ObjectBlocks([device], free.X, free.Y, 2, 5));
        var path = ClientPathfinder.FindPath((40, 100), (160, 100), (_, _) => true, 250, 250, [device]);
        Assert.NotEmpty(path);
        // The native bounded search may return a partial route around a large hull.
        Assert.NotEqual((40, 100), path[^1]);
        Assert.All(path, p => Assert.False(ClientPathfinder.ObjectBlocks([device], p.X, p.Y, 2, 5)));
    }

    [Fact]
    public void Device_editor_preserves_the_native_model_clearance_after_moves()
    {
        var editor = new HideoutEditing();
        editor.Register(7, "map-device", 20, 20, 0, 3, 7.236);
        Assert.True(editor.TryMove(new(7, 40, 40, 0, 0), null, (_, _) => { }, out var moved));
        Assert.Equal(7.236, moved!.CollisionRadius);
    }
}
