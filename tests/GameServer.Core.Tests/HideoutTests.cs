using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Core.Tests;

public class HideoutTests
{
    [Fact]
    public void Hideouts_update_is_player_component_flag_0x10_with_two_keys()
    {
        // Lush Hideout: Hideouts.dat HASH16 0xff63 (21-hideouts.md §1).
        string hex = Convert.ToHexString(InstanceEntities.PlayerHideoutsUpdate(0x1000, 0xff63, 0));
        Assert.Equal("02BA" + "00001000" + "00000000" + "0000" + "0007" + "06" + "1000" + "63FF" + "0000", hex);
    }

    [Fact]
    public void Player_blob_carries_the_hideout_keys_in_place_of_the_zeros()
    {
        string none = Convert.ToHexString(new PlayerObject("A", 1, 0, 0, 10, 10).Blob());
        string lush = Convert.ToHexString(new PlayerObject("A", 1, 0, 0, 10, 10, Hideout: 0xff63, Hideout2: 0x1234).Blob());
        Assert.Equal(none.Length, lush.Length);
        int at = lush.IndexOf("63FF3412", StringComparison.Ordinal);
        Assert.True(at > 0);
        Assert.Equal(none[..at] + "63FF3412" + none[(at + 8)..], lush);
    }

    [Fact]
    public void Hideout_areas_come_from_WorldAreas_IsHideout()
    {
        string? table = FindUp("data/tables/WorldAreas.json");
        if (table is null) return;
        var hideouts = WorldAreaHideouts.Load(table);
        Assert.Contains((ushort)0xff63, hideouts);       // HideoutForest, Lush Hideout
        Assert.DoesNotContain((ushort)0x235c, hideouts); // Lioneye's Watch
    }

    static string? FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        return null;
    }
}

public class VisitHideoutTests
{
    [Fact]
    public void The_Hideout_button_names_the_character_and_the_waypoint()
    {
        // Live 2026-10-20: the World panel opened from Lioneye's Watch's waypoint (0x2f00), DevWitch's own hideout.
        byte[] m = Convert.FromHexString("017A0008446576576974636800002F0000");
        Assert.Equal(m.Length, InstanceWaypoints.VisitHideoutLength(m));
        Assert.Equal(new InstanceWaypoints.VisitHideout("DevWitch", 0x2f00, 0), InstanceWaypoints.ReadVisitHideout(m));
        var split = InstanceProtocol.SplitClientMessages(m, out var partial, out var unknown);
        Assert.Equal((ushort)0x17a, Assert.Single(split).Id);
        Assert.Empty(partial);
        Assert.Empty(unknown);
    }
}

public class HideoutWaypointTests
{
    [Fact]
    public void A_hideout_waypoint_carries_its_HideoutDoodad_data_after_Animated()
    {
        var town = new WaypointObject(218, 335, "w.ao").Blob();
        var hideout = new WaypointObject(218, 335, "w.ao", Hideout: new HideoutDoodad(1224707366, 5)).Blob();
        // u32 LE MtxTypeGameSpecific HASH32 (Waypoint), u8 variation 5, u8 flag 0, right before MinimapIcon.
        string doodad = Convert.ToHexString(BitConverter.GetBytes(1224707366u)) + "05" + "00";
        // After Animated: MinimapIcon, InteractionAction, StateMachine (1 state, 0 timers, "active" 0, flags), tasks.
        string t = Convert.ToHexString(town);
        Assert.Equal(t[..^14] + doodad + t[^14..], Convert.ToHexString(hideout));
    }
}

public class HideoutObjectTests
{
    [Fact]
    public void The_map_device_writes_its_components_in_type_order()
    {
        // StrDexIntMappingDevice: Positioned, BaseEvents, Animated, InteractionAction, HideoutDoodad, StateMachine (opening,
        // active), MinimapIcon. Lush Hideout's Map Device: (449, 460), MTX hash 2306038834, variation 0.
        string[] components = ["Positioned", "BaseEvents", "Animated", "InteractionAction", "HideoutDoodad", "StateMachine", "MinimapIcon"];
        var blob = new HideoutObject(449, 460, 0, components, new HideoutDoodad(2306038834, 0), SharedStates: 2).Blob();
        string hex = Convert.ToHexString(blob);
        Assert.Equal("0000" + "C1010000" + "CC010000" + "00000000" + "0000" + "00" + "02" + "00" + "01"   // flags, Positioned
            + "00" + "00"                                                                                // Animated, InteractionAction
            + Convert.ToHexString(BitConverter.GetBytes(2306038834u)) + "00" + "00"                      // HideoutDoodad
            + "02" + "00" + "0000" + "0000"                                                              // StateMachine
            + "00" + "00", hex);                                                                         // MinimapIcon, tasks
    }

    [Fact]
    public void Types_with_other_components_are_refused()
    {
        Assert.False(HideoutObject.CanSerialize(["Positioned", "NPC"]));
        Assert.True(HideoutObject.CanSerialize(["Positioned", "BaseEvents", "Animated", "HideoutDoodad"]));
    }
}
