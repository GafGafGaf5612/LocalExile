using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class AreaArrivalTests
{
    const ushort Town = 0x235c, Coast = 0x7ad2, Strand = 0xf00a, MudFlats = 0x1234;

    // A 100 x 100 area whose walkable cells are x >= 50; spawn (70, 70); one exit to The Coast at (40, 20).
    static AreaLayout Layout(ushort hash = Coast, bool walkMap = true) => new(hash, "test", 100, 100, 70, 70,
        walkMap ? (x, y) => x >= 50 && x < 100 && y >= 0 && y < 100 : null,
        [(0x2000, new AreaTransitionObject(GridX: 40, GridY: 20, DestinationHash16: Town))]);

    [Fact]
    public void Coming_through_an_exit_arrives_on_a_walkable_cell_next_to_the_exit_back()
    {
        var (x, y) = Layout().Arrival(cameFrom: Town);
        Assert.True(x >= 50, $"({x}, {y}) is not walkable");
        Assert.InRange(Math.Max(Math.Abs(x - 40), Math.Abs(y - 20)), 6, 20);
    }

    [Fact]
    public void A_login_or_an_area_without_an_exit_back_arrives_at_the_spawn()
    {
        Assert.Equal((70, 70), Layout().Arrival(cameFrom: null));
        Assert.Equal((70, 70), Layout().Arrival(cameFrom: MudFlats));
    }

    [Fact]
    public void Without_a_walk_map_the_arrival_is_beside_the_exit()
    {
        Assert.Equal((48, 20), Layout(walkMap: false).Arrival(cameFrom: Town));
    }

    [Fact]
    public void The_Twilight_Strand_arrives_at_the_towns_one_way_entrance()
    {
        Assert.Equal((123, 308), Layout(hash: Town).Arrival(cameFrom: Strand));
    }
}
