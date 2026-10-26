using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class StationaryCollisionTests
{
    [Fact]
    public void Every_supported_npc_has_metadata_and_blocking_npcs_offer_a_safe_exit()
    {
        var npcs = NpcCatalog.Load().Rows.Where(n => n.Supported).ToArray();
        Assert.NotEmpty(npcs);
        foreach (var npc in npcs)
        {
            var profile = StationaryCollision.For(npc.Id);
            var (size, hull) = StationaryCollision.Resolve(npc.Id, npc.Model);
            if (!profile.Blocking && hull is null) { Assert.Equal(-1, size); continue; }
            ClientPathfinder.Blocker blocker = new(50, 50, size) { CollisionRadius = hull };
            var exit = CollisionEscape.Find((50, 50), 150, 150, (_, _) => true, [blocker]);
            Assert.NotNull(exit);
            Assert.True(CollisionEscape.Clear(exit.Value, 150, 150, (_, _) => true, [blocker]), npc.Id);
        }
    }

    [Theory]
    [InlineData("Metadata/NPC/Act1/Nessa", 4)]
    [InlineData("Metadata/NPC/Missions/Hideout/Helena", 4)]
    [InlineData("Metadata/MiscellaneousObjects/Stash", 2)]
    [InlineData("Metadata/MiscellaneousObjects/GuildStash", 2)]
    [InlineData("Metadata/MiscellaneousObjects/Heist/HeistStash_Hideout", 2)]
    [InlineData("Metadata/MiscellaneousObjects/Harvest/CraftingSilo_Hideout", 7)]
    public void Native_footprint_blocks_paths_and_releases_old_position_after_an_edit(string type, int size)
    {
        var resolved = StationaryCollision.Resolve(type);
        Assert.Equal(size, resolved.Size);
        var conn = new ClientState(new(0, "test", 100, 100, 40, 40, (_, _) => true, []), 0, 1);
        conn.SetBlocker(7, 40, 40, resolved.Size, resolved.Hull);
        Assert.Equal(size, conn.StationaryInteractionSize(7));
        var free = CollisionEscape.Find((40, 40), 100, 100, (_, _) => true, conn.StationaryBlockers)!.Value;
        Assert.True(CollisionEscape.Clear(free, 100, 100, (_, _) => true, conn.StationaryBlockers));
        var path = ClientPathfinder.FindPath((20, 40), (60, 40), (_, _) => true, 100, 100, conn.StationaryBlockers);
        Assert.Equal((60, 40), path[^1]);
        Assert.All(path, p => Assert.False(ClientPathfinder.ObjectBlocks(conn.StationaryBlockers, p.X, p.Y, 2, 5)));
        conn.SetBlocker(7, 70, 70, resolved.Size, resolved.Hull);
        Assert.True(CollisionEscape.Clear((40, 40), 100, 100, (_, _) => true, conn.StationaryBlockers));
        conn.RemoveBlocker(7);
        Assert.Empty(conn.StationaryBlockers);
    }

    [Fact]
    public void Crafting_bench_uses_model_hull_instead_of_a_stash_footprint()
    {
        var shape = StationaryCollision.Resolve(HideoutCatalog.CraftingBenchType,
            "Metadata/Terrain/Missions/Hideouts/Objects/HideoutCraftingBench.ao");
        Assert.Equal(0, shape.Size);
        Assert.InRange(shape.Hull!.Value, 9.27, 9.28);
        var conn = new ClientState(new(0, "test", 100, 100, 40, 40, (_, _) => true, []), 0, 1);
        conn.SetBlocker(7, 40, 40, shape.Size, shape.Hull);
        Assert.Equal(10, conn.StationaryInteractionSize(7));
        var exit = CollisionEscape.Find((40, 40), 100, 100, (_, _) => true, conn.StationaryBlockers)!.Value;
        Assert.True(CollisionEscape.Clear(exit, 100, 100, (_, _) => true, conn.StationaryBlockers));
    }

    [Theory]
    [InlineData("Metadata/MiscellaneousObjects/Waypoint")]
    [InlineData("Metadata/MiscellaneousObjects/WorldItem")]
    public void Nonblocking_objects_do_not_obstruct_arrival_or_loot(string type)
    {
        Assert.Equal(-1, StationaryCollision.Resolve(type).Size);
    }
}
