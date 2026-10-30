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

    static string? FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        return null;
    }
}

public class VisitHideoutTests
{
}

public class HideoutWaypointTests
{
}

public class HideoutObjectTests
{
}

public class LeagueQuestFlagTests
{
}
