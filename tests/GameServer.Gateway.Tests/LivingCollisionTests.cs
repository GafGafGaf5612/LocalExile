using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class LivingCollisionTests
{
    [Fact]
    public void Fast_motion_stops_before_a_body_instead_of_tunnelling()
    {
        ClientPathfinder.Blocker[] bodies = [new(30, 20, 3)];
        var stop = LivingCollision.Sweep((10, 20), (70, 20), 1, (_, _) => true, bodies);
        Assert.Equal((25, 20), stop);
        Assert.Equal((70, 20), LivingCollision.Sweep((10, 20), (70, 20), 1, (_, _) => true, []));
    }

    [Fact]
    public void Large_bodies_block_across_spatial_bucket_boundaries()
    {
        Assert.True(ClientPathfinder.ObjectBlocks([new(47, 20, 20)], 28, 20, 2, 5));
        Assert.False(ClientPathfinder.ObjectBlocks([new(47, 20, 20)], 20, 20, 2, 5));
    }

    [Fact]
    public void Friendly_living_monsters_block_but_corpses_do_not()
    {
        var conn = new ClientState(new(0, "test", 100, 100, 10, 10, (_, _) => true, []), 0, 1);
        var mob = new SpawnedMonster("test", new(30, 30, 10), 20, 4, 100) { Hostile = false };
        conn.Monsters[7] = mob;
        Assert.Contains(conn.Blockers, b => b.X == 30 && b.Y == 30);
        Assert.DoesNotContain(conn.MonsterBlockers(7), b => b.X == 30 && b.Y == 30);
        mob.Life = 0;
        Assert.DoesNotContain(conn.Blockers, b => b.X == 30 && b.Y == 30);
    }

    [Fact]
    public void Moving_monster_paths_around_other_bodies()
    {
        ClientPathfinder.Blocker[] bodies = [new(30, 30, 3)];
        var walk = new WalkEstimator(10, 30, 10, (_, _) => true, 100, 100, () => bodies, limit: ClientPathfinder.SearchLimit.Expansions1000);
        walk.Order(60, 30, 0);
        for (double t = 0; t <= 10; t += .02)
        {
            var p = walk.Position(t);
            Assert.False(ClientPathfinder.ObjectBlocks(bodies, p.X, p.Y, 2, 5));
        }
        Assert.Equal((60, 30), walk.Position(10));
    }

    [Fact]
    public void Published_players_update_leave_and_stay_in_their_instance()
    {
        var a = new InstanceState();
        var b = new InstanceState();
        var player = new PlayerObject("peer", 0, 20, 20, 100, 100);
        a.PublishPlayer(new(1, 7, player, 20, 20, 100));
        Assert.Empty(a.PlayersExcept(1));
        Assert.Empty(b.PlayersExcept(2));
        Assert.Single(a.PlayersExcept(2));
        a.PublishPlayer(new(1, 7, player, 25, 20, 0));
        var pose = Assert.Single(a.PlayersExcept(2));
        Assert.Equal(25, pose.X);
        Assert.Equal(0u, pose.Life);
        a.RemovePlayer(1);
        Assert.Empty(a.PlayersExcept(2));
    }
    [Fact]
    public void Peer_bodies_release_on_death_and_departure()
    {
        var conn = new ClientState(new(0, "test", 100, 100, 10, 10, (_, _) => true, []), 0, 1)
            { Instance = new InstanceState() };
        var peer = new InstancePlayer(1, 7, new PlayerObject("peer", 0, 30, 30, 100, 100), 30, 30, 100);
        conn.Instance.PublishPlayer(peer);
        Assert.Contains(conn.Blockers, b => b.X == 30 && b.Y == 30);
        Assert.Contains(conn.CorrectionBlockers(), b => b.X == 30 && b.Y == 30);
        conn.Instance.PublishPlayer(peer with { Life = 0 });
        Assert.DoesNotContain(conn.Blockers, b => b.X == 30 && b.Y == 30);
        conn.Instance.RemovePlayer(1);
        Assert.Empty(conn.Blockers);
    }

    [Theory]
    [InlineData(1, 25)]
    [InlineData(3, 23)]
    [InlineData(7, 19)]
    public void Each_movers_size_changes_its_clearance(int size, int expectedX)
    {
        var stop = LivingCollision.Sweep((10, 20), (70, 20), size, (_, _) => true, [new(30, 20, 3)]);
        Assert.Equal(expectedX, stop.X);
    }

}
