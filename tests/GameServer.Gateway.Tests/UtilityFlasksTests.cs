namespace GameServer.Gateway.Tests;

public class UtilityFlasksTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly Lazy<ItemCatalog> Catalog = new(() => new ItemCatalog(Data()));

    static UtilityFlasks.Utility? Of(string name) => Catalog.Value.UtilityFlaskOf(Catalog.Value.Find(name)!);

    static UtilityFlasks.Utility Utility(params (string, int)[] stats) =>
        new(0, 121, "test", stats, 6.0, 30, 60);

    [Fact]
    public void The_flasks_come_from_Flasks_dat_BuffDefinitions_and_ComponentCharges()
    {
        var quicksilver = Of("Quicksilver Flask")!;
        Assert.Equal(121, quicksilver.BuffRow);
        Assert.Equal("flask_utility_sprint", quicksilver.BuffId);
        Assert.Equal([("base_movement_velocity_+%", 40)], quicksilver.Stats);
        Assert.Equal(6.0, quicksilver.Seconds);
        Assert.Equal((30, 60), (quicksilver.ChargesPerUse, quicksilver.MaxCharges));

        var ruby = Of("Ruby Flask")!;
        Assert.Equal("flask_utility_resist_fire", ruby.BuffId);
        Assert.Equal([("base_fire_damage_resistance_%", 40), ("base_maximum_fire_damage_resistance_%", 5)], ruby.Stats);
        Assert.Equal(8.0, ruby.Seconds);

        Assert.Equal([("base_physical_damage_reduction_rating", 1500)], Of("Granite Flask")!.Stats);
        Assert.Equal([("base_evasion_rating", 1500)], Of("Jade Flask")!.Stats);
        Assert.Equal([("base_chaos_damage_resistance_%", 35)], Of("Amethyst Flask")!.Stats);
        Assert.Equal([("base_resist_all_elements_%", 35)], Of("Bismuth Flask")!.Stats);
        Assert.Equal([("basalt_flask_armour_+%_final", 20)], Of("Basalt Flask")!.Stats);
        foreach (string name in new[] { "Sapphire Flask", "Topaz Flask", "Diamond Flask", "Quartz Flask", "Stibnite Flask", "Sulphur Flask" })
            Assert.NotNull(Of(name));
        // Life and mana flasks and other items are not utility flasks.
        Assert.Null(Of("Small Life Flask"));
        Assert.Null(Catalog.Value.UtilityFlaskOf(Catalog.Value.Row(384)!));   // Driftwood Wand
    }

    [Fact]
    public void Flasks_whose_stats_the_server_does_not_model_are_not_usable()
    {
        // Frozen effect on self, item rarity, ward and stun threshold.
        foreach (string name in new[] { "Aquamarine Flask", "Gold Flask", "Iron Flask", "Corundum Flask" })
            Assert.Null(Of(name));
        // Quartz phasing is left out; its spell suppression counts.
        Assert.Equal([("base_spell_suppression_chance_%", 10)], Of("Quartz Flask")!.Applied);
    }

    [Fact]
    public void The_silver_flask_grants_onslaught_from_BuffTemplates()
    {
        var silver = Of("Silver Flask")!;
        Assert.Empty(silver.Stats);
        Assert.Equal("onslaught", silver.Extra!.BuffId);
        Assert.Equal(215, silver.Extra.BuffRow);
        Assert.Equal([("attack_speed_+%", 20), ("base_cast_speed_+%", 20), ("base_movement_velocity_+%", 20)], silver.Extra.Stats);
        Assert.Equal(silver.Extra.Stats, UtilityFlasks.For(silver, 0, new Dictionary<string, int>()).Stats);
    }

    [Fact]
    public void Quality_and_duration_mods_lengthen_a_use_and_effect_mods_scale_its_stats()
    {
        var u = Utility(("base_movement_velocity_+%", 40), ("phase_through_objects", 1));

        var plain = UtilityFlasks.For(u, 0, new Dictionary<string, int>());
        Assert.Equal(6.0, plain.Seconds, 6);
        Assert.Equal([("base_movement_velocity_+%", 40)], plain.Stats);

        // 20% quality is 20% increased duration, added to the flask's own increased duration.
        Assert.Equal(7.2, UtilityFlasks.For(u, 20, new Dictionary<string, int>()).Seconds, 6);
        Assert.Equal(9.0, UtilityFlasks.For(u, 20, new Dictionary<string, int> { ["local_flask_duration_+%"] = 30 }).Seconds, 6);
        var tradeOff = UtilityFlasks.For(u, 0, new Dictionary<string, int> { ["local_flask_duration_+%"] = -33, ["local_flask_effect_+%"] = 25 });
        Assert.Equal(4.02, tradeOff.Seconds, 6);
        Assert.Equal([("base_movement_velocity_+%", 50)], tradeOff.Stats);
    }

    static UtilityFlasks.Active Active(uint item, UtilityFlasks.Utility u, double started, ushort buff = 1) =>
        new(item, (byte)item, "test", u, buff, null, u.Stats, started, started + u.Seconds);

    [Fact]
    public void An_effect_runs_for_its_duration_and_a_flask_cannot_be_used_again_meanwhile()
    {
        var effects = new UtilityFlasks.Effects();
        var u = Utility(("base_movement_velocity_+%", 40));

        var (replaced, startTicker) = effects.Start(Active(7, u, started: 10));
        Assert.Null(replaced);
        Assert.True(startTicker);
        Assert.NotNull(effects.Of(7));
        Assert.Null(effects.Of(8));
        Assert.Equal([("base_movement_velocity_+%", 40)], effects.Stats());

        Assert.Empty(effects.End(15.9, all: false, out bool stop));
        Assert.False(stop);
        Assert.Equal(7u, Assert.Single(effects.End(16, all: false, out stop)).ItemId);
        Assert.True(stop);
        Assert.Null(effects.Of(7));
        Assert.Empty(effects.Stats());
        // The next use starts a ticker again.
        Assert.True(effects.Start(Active(7, u, started: 20)).StartTicker);
    }

    [Fact]
    public void Two_kinds_stack_one_kind_replaces_and_death_ends_all()
    {
        var effects = new UtilityFlasks.Effects();
        var quicksilver = Utility(("base_movement_velocity_+%", 40));
        var ruby = new UtilityFlasks.Utility(0, 117, "ruby", [("base_fire_damage_resistance_%", 40)], 8, 20, 50);

        effects.Start(Active(1, quicksilver, 0, buff: 1));
        Assert.False(effects.Start(Active(2, ruby, 0, buff: 2)).StartTicker);
        Assert.Equal(2, effects.Stats().Count);

        var (replaced, _) = effects.Start(Active(3, quicksilver, 1, buff: 3));
        Assert.Equal(1, replaced!.Buff);
        Assert.Equal(2, effects.Stats().Count);

        Assert.Equal(2, effects.End(1.5, all: true, out bool stop).Count);
        Assert.True(stop);
        Assert.False(effects.Any);
    }

    [Fact]
    public void Running_flasks_add_to_the_pools()
    {
        var b = UtilityFlasks.Apply(new CharacterStats.Bonus(FireResistance: 10, ArmourPct: 100, ArmourMorePct: 15, MovementVelocityPct: 5),
        [
            ("base_fire_damage_resistance_%", 40), ("base_maximum_fire_damage_resistance_%", 5),
            ("base_resist_all_elements_%", 35), ("base_chaos_damage_resistance_%", 35),
            ("base_physical_damage_reduction_rating", 1500), ("basalt_flask_armour_+%_final", 20),
            ("base_evasion_rating", 1500), ("stibnite_flask_evasion_rating_+%_final", 20),
            ("base_movement_velocity_+%", 40), ("attack_speed_+%", 20), ("critical_strike_chance_+%", 100),
            ("base_spell_suppression_chance_%", 10),
        ]);

        Assert.Equal(10 + 40 + 35, b.FireResistance);
        Assert.Equal(35, b.ColdResistance);
        Assert.Equal(35, b.LightningResistance);
        Assert.Equal(35, b.ChaosResistance);
        Assert.Equal(5, b.FireMaxResistance);
        Assert.Equal(1500, b.Armour);
        Assert.Equal(38, b.ArmourMorePct);   // 15% more and 20% more: 1.15 × 1.2 = 1.38
        Assert.Equal(20, b.EvasionMorePct);
        Assert.Equal(45, b.MovementVelocityPct);
        Assert.Equal(20, b.AttackSpeedPct);
        Assert.Equal(100, b.CriticalStrikeChancePct);
        Assert.Equal(100, b.SpellCriticalStrikeChancePct);
        Assert.Equal(10, b.SpellSuppressionChance);

        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1, b);
        Assert.Equal((int)(1500 * 2 * 1.38), pools.Armour);   // flat armour raised by increased armour and both mores
        Assert.Equal(DamageModel75() + 5, pools.FireMaxResistance);
        Assert.Equal(45, pools.MovementVelocityPct);

        Assert.Same(b, UtilityFlasks.Apply(b, []));
    }

    static int DamageModel75() => GameServer.Core.World.DamageModel.DefaultMaxResistance;

    [Fact]
    public void A_quicksilver_flask_speeds_up_the_walk_estimate_and_a_pause_holds_its_end()
    {
        var conn = new ClientState(new AreaLayout(1, "test", 200, 200, 10, 10, null, []), 1, 1);
        var quicksilver = Utility(("base_movement_velocity_+%", 40));
        double now = conn.Clock.Elapsed.TotalSeconds;
        conn.UtilityFlaskEffects.Start(Active(1, quicksilver, now));
        conn.Pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1, UtilityFlasks.Apply(CharacterStats.Bonus.None, conn.UtilityFlaskEffects.Stats()));
        conn.ApplyMovementSpeed();
        Assert.Equal(Program.WalkSpeed * 1.4, conn.Walk.Speed, 6);

        // The connection clock stops while paused, so the effect's end does not come nearer.
        conn.Clock.Stop();
        Assert.Empty(conn.UtilityFlaskEffects.End(conn.Clock.Elapsed.TotalSeconds, all: false, out _));
        Assert.Single(conn.UtilityFlaskEffects.End(conn.Clock.Elapsed.TotalSeconds + quicksilver.Seconds, all: false, out _));
        conn.Pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1, UtilityFlasks.Apply(CharacterStats.Bonus.None, conn.UtilityFlaskEffects.Stats()));
        conn.ApplyMovementSpeed();
        Assert.Equal(Program.WalkSpeed, conn.Walk.Speed, 6);
    }

    [Fact]
    public void Maximum_resistances_crit_damage_and_cast_speed_also_go_out_as_plain_stats()
    {
        var stats = new StatCatalog(Data());
        var raw = UtilityFlasks.RawStats(
        [
            ("base_maximum_fire_damage_resistance_%", 5), ("critical_strike_chance_+%", 100), ("damage_+%", 40),
            ("base_cast_speed_+%", 20), ("base_movement_velocity_+%", 40), ("base_fire_damage_resistance_%", 40),
        ], stats).ToDictionary(s => stats.Name(s.Key), s => s.Value);

        Assert.Equal(new Dictionary<string, int>
        {
            ["base_maximum_fire_damage_resistance_%"] = 5, ["critical_strike_chance_+%"] = 100, ["damage_+%"] = 40,
            ["cast_speed_+%"] = 20,
        }, raw);
    }
}
