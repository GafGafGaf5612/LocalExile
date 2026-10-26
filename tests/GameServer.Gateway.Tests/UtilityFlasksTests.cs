using GameServer.Core.Protocol.Instance;

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
    static readonly Lazy<StatCatalog> StatNames = new(() => new StatCatalog(Data()));

    static UtilityFlasks.Utility? Of(string name) => Catalog.Value.UtilityFlaskOf(Catalog.Value.Find(name)!);

    static UtilityFlasks.Utility Utility(params (string, int)[] stats) =>
        new(0, 121, "test", stats, 6.0, 30, 60);

    static Item Flask(int charges = 60, byte quality = 0, ItemRarity rarity = ItemRarity.Normal) =>
        new(0x1111, ["Base", "Charges", "Flask"], 0x2222) { Charges = charges, Quality = quality, Rarity = rarity };

    static int None(string _) => 0;

    [Fact]
    public void The_flasks_come_from_Flasks_dat_BuffDefinitions_and_ComponentCharges()
    {
        var quicksilver = Of("Quicksilver Flask")!;
        Assert.Equal(121, quicksilver.BuffRow);
        Assert.Equal("flask_utility_sprint", quicksilver.BuffId);
        Assert.Equal([("base_movement_velocity_+%", 40)], quicksilver.Stats);
        // RecoveryTime (60), as Path of Building and RePoE 3.29 give it: 6 s, not RecoveryTime2 (20).
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
            Assert.True(Of(name)!.Usable);
        // Life and mana flasks and other items are not utility flasks.
        Assert.Null(Of("Small Life Flask"));
        Assert.Null(Catalog.Value.UtilityFlaskOf(Catalog.Value.Row(384)!));   // Driftwood Wand
    }

    [Fact]
    public void Flasks_whose_stats_the_server_does_not_model_are_not_usable_but_have_charges()
    {
        // Frozen effect on self, item rarity, ward and stun threshold.
        foreach (string name in new[] { "Aquamarine Flask", "Gold Flask", "Iron Flask", "Corundum Flask" })
        {
            Assert.False(Of(name)!.Usable);
            Assert.NotNull(Catalog.Value.FlaskChargesOf(Catalog.Value.Find(name)!));
        }
        Assert.Equal((40, 60), (Of("Iron Flask")!.ChargesPerUse, Of("Iron Flask")!.MaxCharges));
        // Quartz phasing is left out; its spell suppression counts.
        Assert.Equal([("base_spell_suppression_chance_%", 10)], Of("Quartz Flask")!.Applied);
    }

    [Fact]
    public void Kills_and_towns_refill_utility_flasks_too()
    {
        TestSaves.Ensure();
        var catalog = Catalog.Value;
        var items = PlayerInventories.For($"Refill{Guid.NewGuid():N}"[..20], catalog);
        var quicksilver = items.Add(PlayerInventories.Flask, 2, 0, ItemCatalog.Create(catalog.Find("Quicksilver Flask")!) with { Charges = 0 });
        var iron = items.Add(PlayerInventories.Flask, 3, 0, ItemCatalog.Create(catalog.Find("Iron Flask")!) with { Charges = 5 });

        var changed = Flasks.Fill(items, catalog, int.MaxValue);

        Assert.Contains(changed, c => c.Id == quicksilver.Id && c.Item.Charges == 60);
        Assert.Contains(changed, c => c.Id == iron.Id && c.Item.Charges == 60);
        // Full flasks take no more.
        Assert.DoesNotContain(Flasks.Fill(items, catalog, int.MaxValue), c => c.Id == quicksilver.Id || c.Id == iron.Id);
    }

    [Fact]
    public void The_silver_flask_grants_onslaught_from_BuffTemplates()
    {
        var silver = Of("Silver Flask")!;
        Assert.Empty(silver.Stats);
        Assert.Equal("onslaught", silver.Extra!.BuffId);
        Assert.Equal(215, silver.Extra.BuffRow);
        Assert.Equal([("attack_speed_+%", 20), ("base_cast_speed_+%", 20), ("base_movement_velocity_+%", 20)], silver.Extra.Stats);
        Assert.Equal(silver.Extra.Stats, UtilityFlasks.For(silver, Flask(), new Dictionary<string, int>(), None).Stats);
    }

    [Fact]
    public void Quality_and_duration_multiply_and_flask_effect_scales_the_stats()
    {
        var u = Utility(("base_movement_velocity_+%", 40), ("phase_through_objects", 1));

        var plain = UtilityFlasks.For(u, Flask(), new Dictionary<string, int>(), None);
        Assert.Equal(6.0, plain.Seconds, 6);
        Assert.Equal([("base_movement_velocity_+%", 40)], plain.Stats);

        // Path of Building Item.lua: base × (1 + increased) × (1 + quality) × (1 + more).
        Assert.Equal(7.2, UtilityFlasks.For(u, Flask(quality: 20), new Dictionary<string, int>(), None).Seconds, 6);
        Assert.Equal(6 * 1.3 * 1.2, UtilityFlasks.For(u, Flask(quality: 20), new Dictionary<string, int> { ["local_flask_duration_+%"] = 30 }, None).Seconds, 6);
        var tradeOff = UtilityFlasks.For(u, Flask(), new Dictionary<string, int> { ["local_flask_duration_+%"] = -33, ["local_flask_effect_+%"] = 25 }, None);
        Assert.Equal(4.02, tradeOff.Seconds, 6);
        Assert.Equal([("base_movement_velocity_+%", 50)], tradeOff.Stats);
    }

    [Fact]
    public void The_characters_flask_duration_and_effect_count()
    {
        var u = Utility(("base_movement_velocity_+%", 40));
        var character = new Dictionary<string, int> { ["flask_duration_+%"] = 20, ["flask_effect_+%"] = 10, ["magic_utility_flask_effect_+%"] = 15 };
        int Get(string name) => character.GetValueOrDefault(name);
        var local = new Dictionary<string, int> { ["local_flask_duration_+%"] = 10, ["local_flask_effect_+%"] = 15 };

        // Increased duration from the tree adds to the flask's own; increased effect likewise.
        var normal = UtilityFlasks.For(u, Flask(), local, Get);
        Assert.Equal(6 * 1.3, normal.Seconds, 6);
        Assert.Equal([("base_movement_velocity_+%", 50)], normal.Stats);   // 40 × 1.25
        // Magic utility flasks also get magic_utility_flask_effect_+%.
        Assert.Equal([("base_movement_velocity_+%", 56)], UtilityFlasks.For(u, Flask(rarity: ItemRarity.Magic), local, Get).Stats);   // 40 × 1.4
    }

    static UtilityFlasks.Active Active(uint item, UtilityFlasks.Utility u, double started, ushort buff = 1, ushort? extra = null) =>
        new(item, (byte)item, "test", u, buff, extra, u.Applied.ToList(), started, started + u.Seconds);

    [Fact]
    public void An_effect_runs_for_its_duration_and_a_flask_cannot_be_used_again_meanwhile()
    {
        var effects = new UtilityFlasks.Effects();
        var u = Utility(("base_movement_velocity_+%", 40));

        var (replaced, ticker) = effects.Start(Active(7, u, started: 10));
        Assert.Null(replaced);
        Assert.NotNull(ticker);
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
        Assert.NotNull(effects.Start(Active(7, u, started: 20)).Ticker);
    }

    [Fact]
    public void Each_refusal_says_why()
    {
        var u = Utility(("base_movement_velocity_+%", 40));
        var running = Active(7, u, started: 10);

        Assert.Null(UtilityFlasks.Refusal(false, null, Flask(charges: 30), u, 11));
        Assert.Equal("dead", UtilityFlasks.Refusal(true, null, Flask(), u, 11));
        Assert.Equal("its effect still runs (2.5 s left)", UtilityFlasks.Refusal(false, running, Flask(), u, 13.5));
        Assert.Equal("29/60 charges, a use needs 30", UtilityFlasks.Refusal(false, null, Flask(charges: 29), u, 11));
    }

    [Fact]
    public void A_ticker_that_exits_lets_the_next_use_start_another()
    {
        var effects = new UtilityFlasks.Effects();
        var u = Utility(("base_movement_velocity_+%", 40));
        int first = effects.Start(Active(1, u, started: 0)).Ticker!.Value;
        Assert.Null(effects.Start(Active(2, Utility(("base_evasion_rating", 1)) with { BuffRow = 124 }, started: 0)).Ticker);

        // The ticker failed (a send threw, the connection broke …) with effects still running.
        effects.TickerExited(first);
        Assert.False(effects.Ticking);
        int second = effects.Start(Active(3, u, started: 1)).Ticker!.Value;
        Assert.NotEqual(first, second);
        // A late exit of the old ticker leaves the new one alone.
        effects.TickerExited(first);
        Assert.True(effects.Ticking);
        effects.TickerExited(second);
        Assert.False(effects.Ticking);
    }

    [Fact]
    public void Two_kinds_stack_one_kind_replaces_and_death_ends_all()
    {
        var effects = new UtilityFlasks.Effects();
        var quicksilver = Utility(("base_movement_velocity_+%", 40));
        var ruby = new UtilityFlasks.Utility(0, 117, "ruby", [("base_fire_damage_resistance_%", 40)], 8, 20, 50);

        effects.Start(Active(1, quicksilver, 0, buff: 1));
        Assert.Null(effects.Start(Active(2, ruby, 0, buff: 2)).Ticker);
        Assert.Equal(2, effects.Stats().Count);

        var (replaced, _) = effects.Start(Active(3, quicksilver, 1, buff: 3));
        Assert.Equal(1, replaced!.Buff);
        Assert.Equal(2, effects.Stats().Count);

        Assert.Equal(2, effects.End(1.5, all: true, out bool stop).Count);
        Assert.True(stop);
        Assert.False(effects.Any);
    }

    [Fact]
    public void An_effect_ends_when_its_flask_leaves_its_cell()
    {
        var effects = new UtilityFlasks.Effects();
        effects.Start(Active(4, Utility(("base_movement_velocity_+%", 40)), started: 0));

        Assert.Empty(effects.End(1, all: false, out _, gone: _ => false));
        Assert.Equal(4u, Assert.Single(effects.End(1, all: false, out bool stop, gone: e => e.ItemId == 4)).ItemId);
        Assert.True(stop);
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
        Assert.Equal(GameServer.Core.World.DamageModel.DefaultMaxResistance + 5, pools.FireMaxResistance);
        Assert.Equal(45, pools.MovementVelocityPct);

        Assert.Same(b, UtilityFlasks.Apply(b, []));
    }

    [Fact]
    public void Maximum_resistances_crit_damage_and_cast_speed_also_go_out_as_plain_stats()
    {
        var stats = StatNames.Value;
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

    [Fact]
    public void The_character_stats_get_the_running_flasks_plain_stats_on_top()
    {
        var names = StatNames.Value;
        uint crit = names.Key("critical_strike_chance_+%")!.Value, cast = names.Key("cast_speed_+%")!.Value;
        // What CharacterPlayerStats has before (passives' 30% crit, Haste's 5% cast speed), then the Diamond and Silver flasks.
        var stats = new Dictionary<uint, int> { [crit] = 30, [cast] = 5 };

        UtilityFlasks.AddRawStats(stats, [("critical_strike_chance_+%", 100), ("base_cast_speed_+%", 20), ("attack_speed_+%", 20)], names);

        Assert.Equal(130, stats[crit]);
        Assert.Equal(25, stats[cast]);
        Assert.Equal(2, stats.Count);   // attack speed goes through the pools, not as a plain stat
        UtilityFlasks.AddRawStats(stats, [], names);
        Assert.Equal(130, stats[crit]);
    }

    // The character as PoolsFor makes it, with the running flasks (Apply), and the walk estimate from its pools.
    static void Recompute(ClientState conn)
    {
        conn.Pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1, UtilityFlasks.Apply(CharacterStats.Bonus.None, conn.UtilityFlaskEffects.Stats()));
        conn.ApplyMovementSpeed();
    }

    static ClientState Connection() => new(new AreaLayout(1, "test", 200, 200, 10, 10, null, []), 1, 1);

    [Fact]
    public void A_use_recomputes_the_pools_and_walk_speed_and_its_end_puts_them_back()
    {
        var conn = Connection();
        var ruby = new UtilityFlasks.Utility(0, 117, "ruby", [("base_fire_damage_resistance_%", 40)], 8, 20, 50);
        var quicksilver = Utility(("base_movement_velocity_+%", 40));

        var (_, ticker) = UtilityFlasks.Start(conn, Active(1, quicksilver, 0), () => Recompute(conn));
        UtilityFlasks.Start(conn, Active(2, ruby, 0, buff: 2), () => Recompute(conn));
        Assert.NotNull(ticker);
        Assert.Equal(Program.WalkSpeed * 1.4, conn.Walk.Speed, 6);
        Assert.Equal(40, conn.Pools.FireResistance);

        // Quicksilver's 6 s are up, Ruby's 8 s are not.
        var ended = UtilityFlasks.EndDue(conn, 6, dead: false, gone: null, () => Recompute(conn), out bool stop);
        Assert.Equal(1u, Assert.Single(ended).ItemId);
        Assert.False(stop);
        Assert.Equal(Program.WalkSpeed, conn.Walk.Speed, 6);
        Assert.Equal(40, conn.Pools.FireResistance);

        // Death ends the rest.
        ended = UtilityFlasks.EndDue(conn, 6.5, dead: true, gone: null, () => Recompute(conn), out stop);
        Assert.Equal(2u, Assert.Single(ended).ItemId);
        Assert.True(stop);
        Assert.Equal(0, conn.Pools.FireResistance);
        // Nothing due: nothing is recomputed.
        Assert.Empty(UtilityFlasks.EndDue(conn, 7, dead: false, gone: null, () => throw new InvalidOperationException(), out _));
    }

    [Fact]
    public void A_pause_holds_an_effect_and_its_end_comes_after_the_resume()
    {
        var conn = Connection();
        var quicksilver = Utility(("base_movement_velocity_+%", 40)) with { Seconds = 0.5 };
        conn.Clock.Restart();
        Thread.Sleep(50);
        double started = conn.Clock.Elapsed.TotalSeconds, ends = started + quicksilver.Seconds;
        UtilityFlasks.Start(conn, Active(1, quicksilver, started), () => Recompute(conn));
        Thread.Sleep(50);

        // Paused for longer than the whole effect: the connection clock stands still, so it does not end.
        conn.Clock.Stop();
        double pausedAt = conn.Clock.Elapsed.TotalSeconds;
        Assert.True(pausedAt > started && pausedAt < ends);
        Thread.Sleep(700);
        Assert.Equal(pausedAt, conn.Clock.Elapsed.TotalSeconds);
        Assert.Empty(UtilityFlasks.EndDue(conn, conn.Clock.Elapsed.TotalSeconds, false, null, () => Recompute(conn), out _));
        Assert.Equal(Program.WalkSpeed * 1.4, conn.Walk.Speed, 6);

        // Resumed: the rest of it runs out on the clock, after the resume.
        conn.Clock.Start();
        var wall = System.Diagnostics.Stopwatch.StartNew();
        while (conn.Clock.Elapsed.TotalSeconds < ends && wall.Elapsed.TotalSeconds < 10) Thread.Sleep(10);
        Assert.True(wall.Elapsed.TotalSeconds >= ends - pausedAt - 0.05);   // the rest, not the whole, after the resume
        Assert.Single(UtilityFlasks.EndDue(conn, conn.Clock.Elapsed.TotalSeconds, false, null, () => Recompute(conn), out _));
        Assert.Equal(Program.WalkSpeed, conn.Walk.Speed, 6);
    }

    [Fact]
    public void The_silver_flask_puts_and_takes_its_buff_and_onslaught()
    {
        var silver = Of("Silver Flask")!;
        var effect = new UtilityFlasks.Active(9, 3, "Silver Flask", silver, 5, 6, silver.Applied.ToList(), 10, 16);

        var start = UtilityFlasks.StartPackets(effect, 0x1000).Select(Convert.ToHexString).ToList();
        // flask_utility_haste (row 130): flask-bar cell 3 as source skill, f32 6.0 (0x40C00000) at +0x68.
        Assert.Equal("01C1" + "00001000" + "00000000" + "0000" + "0005" + "0082" + "00001000" + "0003" + "02" + "40C00000" + "00000000", start[0]);
        // onslaught (row 215): no cell (shown in the buff row), the same duration.
        Assert.Equal("01C1" + "00001000" + "00000000" + "0000" + "0006" + "00D7" + "00001000" + "0000" + "02" + "40C00000" + "00000000", start[1]);
        Assert.Equal(2, start.Count);

        Assert.Equal(["01C2" + "00001000" + "00000000" + "0000" + "0005", "01C2" + "00001000" + "00000000" + "0000" + "0006"],
            UtilityFlasks.EndPackets(effect, 0x1000).Select(Convert.ToHexString));
        // A flask without an extra buff sends one of each.
        var quicksilver = Of("Quicksilver Flask")!;
        var single = new UtilityFlasks.Active(1, 2, "Quicksilver Flask", quicksilver, 7, null, quicksilver.Applied.ToList(), 0, 6);
        Assert.Single(UtilityFlasks.StartPackets(single, 0x1000));
        Assert.Single(UtilityFlasks.EndPackets(single, 0x1000));
    }
}
