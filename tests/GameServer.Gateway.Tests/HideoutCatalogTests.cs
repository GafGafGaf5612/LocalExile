using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class HideoutCatalogTests
{
    [Fact]
    public void All_template_rows_are_owned_including_high_mtx_rows_without_duplicates()
    {
        var campaign = new HideoutCatalog.Hideout(0, 1, "Campaign", 1, "Campaign", []);
        var mtx = campaign with { Row = 102, AreaId = "MTX" };
        Assert.Equal(new byte[] { 0, 102 }, HideoutCatalog.SelectableRows([mtx, campaign, mtx]));
        Assert.Empty(HideoutCatalog.SelectableRows([]));
    }
    [Fact]
    public void Crafting_bench_is_placed_left_of_guild_stash_with_its_original_appearance()
    {
        var bench = new HideoutCatalog.Doodad("Crafting Bench", 123, HideoutCatalog.CraftingBenchType,
            "bench.ao", 251, 351, 1.5f, 2);
        var hideout = new HideoutCatalog.Hideout(0, 1, HideoutCatalog.DefaultArea, 1, "Test", [bench]);
        var placed = HideoutCatalog.BenchBesideGuild(hideout, 224, 358, (_, _) => true)!;
        Assert.Equal(bench with { X = 212, Y = 370 }, placed);
        Assert.False(HideoutCatalog.IsPlaced(placed)); // The bench must not become a map device/portal owner.
    }

    [Fact]
    public void Crafting_bench_avoids_blocked_ground_and_does_not_spawn_without_a_template()
    {
        var bench = new HideoutCatalog.Doodad("Crafting Bench", 123, HideoutCatalog.CraftingBenchType,
            null, 0, 0, 0, 0);
        var hideout = new HideoutCatalog.Hideout(0, 1, "Test", 1, "Test", [bench]);
        Assert.Equal((208, 374), HideoutCatalog.BenchBesideGuild(hideout, 224, 358, (x, _) => x <= 210) is { } p ? (p.X, p.Y) : default);
        Assert.Null(HideoutCatalog.BenchBesideGuild(hideout, 224, 358, (_, _) => false));
        Assert.Null(HideoutCatalog.BenchBesideGuild(hideout with { Doodads = [] }, 224, 358, null));
    }

    [Fact]
    public void Every_character_has_the_hideout_unlocked()
    {
        Assert.Contains("HideoutUnlocked", Quests.EveryCharacter);
    }

    [Fact]
    public void Doodads_resolve_through_MtxTypeGameSpecific_to_their_HideoutDoodads_type_and_variation_model()
    {
        string dir = Directory.CreateTempSubdirectory("hideouts").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "MtxTypeGameSpecific.json"), """[{"HASH32":7},{"HASH32":1224707366}]""");
            File.WriteAllText(Path.Combine(dir, "HideoutDoodads.json"), """
                [{"MtxTypeGameSpecific":1,"InheritsFrom":"Metadata/MiscellaneousObjects/Waypoint","Variation_AOFiles":["a.ao","b.ao"]},
                 {"MtxTypeGameSpecific":0,"InheritsFrom":"Metadata/MiscellaneousObjects/Stash","Variation_AOFiles":[]}]
                """);
            string file = Path.Combine(dir, "hideouts.json");
            File.WriteAllText(file, """
                {"hideouts":[{"row":0,"hash16":65379,"area":"HideoutForest","areaHash16":65379,"name":"Lush Hideout","file":"x",
                  "doodads":[{"name":"Waypoint","hash":1224707366,"x":218,"y":335,"r":16384,"fv":1},
                             {"name":"Stash","hash":7,"x":230,"y":364,"r":0,"fv":0},
                             {"name":"Unknown","hash":99,"x":1,"y":2,"r":0,"fv":0}]}]}
                """);

            var h = Assert.Single(HideoutCatalog.Load(file, dir));
            Assert.Equal((ushort)0xff63, h.Hash16);
            var waypoint = h.First(WaypointObject.MetadataPath)!;
            Assert.Equal(("b.ao", 218, 335), (waypoint.Model, waypoint.X, waypoint.Y));
            Assert.Equal(MathF.PI / 2, waypoint.Rotation, 4);
            var stash = h.First(StashObject.MetadataPath)!;
            Assert.Null(stash.Model);
            Assert.Null(h.Doodads[2].Type);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_Lush_Hideout_has_its_waypoint_and_stash()
    {
        string? json = FindUp("data/hideouts.json"), tables = FindUp("data/tables/HideoutDoodads.json");
        if (json is null || tables is null) return;
        var lush = HideoutCatalog.Load(json, Path.GetDirectoryName(tables)!).Single(h => h.AreaId == HideoutCatalog.DefaultArea);
        var waypoint = lush.First(WaypointObject.MetadataPath)!;
        Assert.Equal((218, 335), (waypoint.X, waypoint.Y));
        Assert.Equal("Metadata/Terrain/Missions/Hideouts/Objects/WaypointVariants/waypointcore_forest.ao", waypoint.Model);
        Assert.Equal((230, 364), (lush.First(StashObject.MetadataPath)!.X, lush.First(StashObject.MetadataPath)!.Y));
        var device = lush.Doodads.Single(HideoutCatalog.IsPlaced);
        Assert.Equal(("Map Device", 257, 283), (device.Name, device.X, device.Y));   // moved from the decoration's (449, 460)
        var bench = HideoutCatalog.BenchBesideGuild(lush, 224, 358, null)!;
        Assert.Equal(("Crafting Bench", 212, 370), (bench.Name, bench.X, bench.Y));
    }

    static string? FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        return null;
    }
}

public class LeagueQuestFlagBitTests
{
    [Fact]
    public void League_flag_ids_go_to_the_client_as_bit_id_minus_one()
    {
        // UnlockedMapDevice is id 350 in the client's table; its Targetable tests bit 349 (live trace 2026-10-20).
        // DelveEncounterComplete (68) too, for Niko's Delve options, and AtlasUnlocked (296) for the hideout Atlas
        // device (21-hideouts.md).
        Assert.Equal([2, 199, 295, 349, 67], Quests.League.Bits(Quests.League.EveryCharacter));
        Assert.Empty(Quests.League.Bits([0]));
        Assert.Contains("EndGameAvailable", Quests.EveryCharacter);
    }
}
