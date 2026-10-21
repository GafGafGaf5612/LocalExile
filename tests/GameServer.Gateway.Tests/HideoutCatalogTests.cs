using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class HideoutCatalogTests
{
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
        // DelveEncounterComplete (68) too, for Niko's Delve options (21-hideouts.md).
        Assert.Equal([2, 199, 349, 67], Quests.League.Bits(Quests.League.EveryCharacter));
        Assert.Empty(Quests.League.Bits([0]));
    }
}
