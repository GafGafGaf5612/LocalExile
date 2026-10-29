using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ChargeGainsTests
{
    static Func<string, int> Stats(params (string Name, int Value)[] stats) =>
        name => stats.Where(s => s.Name == name).Sum(s => s.Value);

    static readonly WeaponProfile Unarmed = new(false, false, false, false, false, Unarmed: true, Melee: true);

    static ChargeEvent.Hit Hit(bool critical = false, bool spell = false, MonsterRarity rarity = MonsterRarity.Normal,
        string? skill = null, Func<string, int>? skillStats = null, bool killed = false, bool? melee = null) =>
        new(critical, spell, melee, rarity, skill, skillStats, killed);

    static List<(ChargeType, int)> Roll(ChargeEvent e, Func<string, int> get, Random rng, WeaponProfile? weapon = null) =>
        [.. ChargeGains.Roll(e, get, weapon ?? Unarmed, rng).Select(g => (g.Type, g.Count))];

    [Fact]
    public void Every_gain_stat_exists_in_Stats_dat()
    {
        var stats = new StatCatalog(ChargePanelStatsTestsData.Folder);
        foreach (var rule in ChargeGains.Rules) Assert.True(stats.Key(rule.Stat) is not null, rule.Stat);
        foreach (var p in ChargeGains.Periodics) Assert.True(stats.Key(p.Stat) is not null, p.Stat);
        foreach (var type in Charges.Types) Assert.True(stats.Key(ChargeGains.ToMaximumStat(type)) is not null, ChargeGains.ToMaximumStat(type));
    }

}

/// <summary>The data folder the Gateway tests read (data/tables).</summary>
static class ChargePanelStatsTestsData
{
    public static string Folder
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
            throw new DirectoryNotFoundException("data/tables");
        }
    }
}
