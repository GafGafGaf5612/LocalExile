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
