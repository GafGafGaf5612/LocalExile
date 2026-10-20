namespace GameServer.Gateway.Tests;

public class PanelStatsTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly Lazy<StatCatalog> Stats = new(() => new StatCatalog(Data()));

    static Dictionary<string, int> Compute(params (string Stat, int Value)[] stats)
    {
        var given = stats.ToDictionary(s => s.Stat, s => s.Value);
        return PanelStats.Compute(n => given.GetValueOrDefault(n)).ToDictionary(s => s.Stat, s => s.Value);
    }

    [Fact]
    public void Every_stat_in_the_table_exists_in_Stats_dat()
    {
        var names = PanelStats.Sums.SelectMany(s => s.Sources.Prepend(s.Display).Concat(s.PerHundredThousand ?? []));
        Assert.DoesNotContain(names, n => Stats.Value.Key(n) is null);
        Assert.Equal(PanelStats.Sums.Length, PanelStats.Sums.Select(s => s.Display).Distinct().Count());
    }

    [Fact]
    public void A_display_stat_sums_its_base_form()
    {
        var panel = Compute(("base_stun_recovery_+%", 10), ("base_projectile_speed_+%", 15));
        Assert.Equal(10, panel["stun_recovery_+%"]);
        Assert.Equal(15, panel["projectile_speed_+%"]);
    }

    [Fact]
    public void Combined_stats_count_for_each_row_they_apply_to()
    {
        var panel = Compute(("base_avoid_freeze_%", 20), ("avoid_all_elemental_status_%", 10),
            ("chance_to_freeze_shock_ignite_%", 5), ("base_chance_to_shock_%", 3));
        Assert.Equal(30, panel["avoid_freeze_%"]);
        Assert.Equal(10, panel["avoid_shock_%"]);
        Assert.Equal(5, panel["chance_to_freeze_%"]);
        Assert.Equal(8, panel["chance_to_shock_%"]);
    }

    [Fact]
    public void A_raw_value_of_the_display_stat_is_kept()
    {
        // Haste gives cast_speed_+% itself; a passive gives base_cast_speed_+%.
        Assert.Equal(25, Compute(("cast_speed_+%", 10), ("base_cast_speed_+%", 8), ("attack_and_cast_speed_+%", 7))["cast_speed_+%"]);
    }

    [Fact]
    public void Flags_are_one_when_any_source_is_set()
    {
        var panel = Compute(("base_cannot_be_frozen", 1), ("immune_to_ignite_and_shock", 2));
        Assert.Equal(1, panel["cannot_be_frozen"]);
        Assert.Equal(1, panel["immune_to_ignite"]);
        Assert.Equal(1, panel["immune_to_shock"]);
        Assert.False(panel.ContainsKey("immune_to_freeze"));
    }

    [Fact]
    public void Leech_per_hundred_thousand_is_added_as_permyriad()
    {
        var panel = Compute(("base_life_leech_from_fire_damage_permyriad", 20),
            ("base_life_leech_from_elemental_damage_permyriad", 10),
            ("horrible_ugly_life_leech_from_fire_damage_perhundredthousand", 300));
        Assert.Equal(60, panel["life_leech_from_fire_damage_permyriad"]);
        Assert.Equal(10, panel["life_leech_from_cold_damage_permyriad"]);
    }

    [Fact]
    public void Zero_values_are_not_listed()
    {
        Assert.Empty(Compute());
        Assert.Empty(Compute(("base_avoid_stun_%", 0)));
    }
}
