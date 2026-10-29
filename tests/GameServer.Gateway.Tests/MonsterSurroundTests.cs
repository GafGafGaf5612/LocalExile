using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class MonsterSurroundTests
{
    static bool Open(int x, int y, int size) => true;

    [Fact]
    public void A_pack_from_one_side_spreads_around_the_player_instead_of_queueing_in_a_wedge()
    {
        // Twenty bodies of size 1 coming from the east, one behind the other.
        var askers = Enumerable.Range(0, 20).Select(i =>
            new MonsterSurround.Asker((uint)(i + 1), 120 + i * 3, 100 + (i % 3) - 1, 1, null)).ToList();
        var slots = MonsterSurround.Assign(askers, (100, 100), [], Open);

        Assert.Equal(20, slots.Count);
        // No two cells overlap (Chebyshev ≥ 1 + 1 + 2), and none is inside the player's body.
        var cells = slots.Values.ToList();
        for (int i = 0; i < cells.Count; i++)
        {
            Assert.True(Math.Max(Math.Abs(cells[i].X - 100), Math.Abs(cells[i].Y - 100)) >= MonsterSurround.ContactRadius(1) - 1);
            for (int j = i + 1; j < cells.Count; j++)
                Assert.True(Math.Max(Math.Abs(cells[i].X - cells[j].X), Math.Abs(cells[i].Y - cells[j].Y)) >= 4);
        }
        // The innermost ring is used on both sides of the player, not only on the side the pack came from.
        int contact = MonsterSurround.ContactRadius(1);
        var inner = cells.Where(c => Math.Sqrt(Math.Pow(c.X - 100, 2) + Math.Pow(c.Y - 100, 2)) <= contact + 1).ToList();
        Assert.True(inner.Count >= 5);
        Assert.Contains(inner, c => c.X < 100);
    }

    [Fact]
    public void A_slot_stays_while_it_is_free_and_the_player_stands()
    {
        var first = MonsterSurround.Assign([new(1, 130, 100, 1, null)], (100, 100), [], Open)[1];
        var again = MonsterSurround.Assign([new(1, first.X + 1, first.Y, 1, first)], (100, 100), [], Open)[1];
        Assert.Equal(first, again);
    }

    [Fact]
    public void Standing_bodies_and_unwalkable_cells_are_never_given_out()
    {
        var slot = MonsterSurround.Assign([new(1, 130, 100, 1, null)], (100, 100), [(104, 100, 1)],
            (x, y, _) => y >= 100)[1];
        Assert.True(slot.Y >= 100);
        Assert.True(Math.Max(Math.Abs(slot.X - 104), Math.Abs(slot.Y - 100)) >= 4);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    public void Every_bearing_assigns_a_cell_the_monster_can_reach_past_the_players_body(int size)
    {
        (int X, int Y) player = (100, 100);
        ClientPathfinder.Blocker[] body = [new(player.X, player.Y, LivingCollision.PlayerSize)];
        foreach (var (dx, dy) in new[] { (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1) })
        {
            var start = (X: player.X + 30 * dx, Y: player.Y + 30 * dy);
            var slot = MonsterSurround.Assign([new(1, start.X, start.Y, size, null)], player, [], Open)[1];
            Assert.False(ClientPathfinder.ObjectBlocks(body, slot.X, slot.Y, size + 1, size + 4),
                $"Size {size}, bearing ({dx},{dy}): the assigned cell {slot} overlaps the player.");
            var walk = new WalkEstimator(start.X, start.Y, 30, width: 200, height: 200,
                blockers: () => body, size: size, limit: ClientPathfinder.SearchLimit.Expansions1000);
            walk.Order(slot.X, slot.Y, 0);
            Assert.Equal(slot, walk.Position(walk.ArrivesAt + 1));
            Assert.Equal(slot, MonsterSurround.Assign([new(1, slot.X, slot.Y, size, slot)], player, [], Open)[1]);
        }
    }

    [Fact]
    public void A_previous_diagonal_slot_inside_the_players_body_is_replaced()
    {
        var slot = MonsterSurround.Assign([new(1, 105, 104, 3, (104, 104))], (100, 100), [], Open)[1];
        Assert.NotEqual((104, 104), slot);
        Assert.False(ClientPathfinder.ObjectBlocks([new(100, 100, LivingCollision.PlayerSize)], slot.X, slot.Y, 4, 7));
    }

    [Theory]
    [InlineData(150)]
    [InlineData(300)]
    public void Dense_surround_assignments_have_a_bounded_allocation_budget(int count)
    {
        var askers = Enumerable.Range(1, count).Select(i => new MonsterSurround.Asker((uint)i,
            130 + i % 40, 90 + i % 20, 2, null)).ToArray();
        for (int i = 0; i < 3; i++) MonsterSurround.Assign(askers, (100, 100), [], Open);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = MonsterSurround.Assign(askers, (100, 100), [], Open);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.NotEmpty(result);
        Assert.True(allocated < 256 * 1024, $"Surround assignment allocated {allocated:N0} bytes for {count} chasers.");
    }
}
