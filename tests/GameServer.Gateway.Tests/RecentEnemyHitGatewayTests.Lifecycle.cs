using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class RecentEnemyHitGatewayTests
{
    [Fact]
    public async Task Repeated_same_monster_contacts_count_and_fortification_and_energy_shield_keep_their_order()
    {
        using var f = new Fixture();
        f.Set(RecentEnemyHits.GenericChance, 100);
        f.Conn.Pools = f.Conn.Pools with { EnergyShield = 100 };
        f.Conn.EnergyShield.SetMaximum(100, refill: true);
        Program.FortificationOf(f.Conn).State.GrantExplicitStacks(f.Conn.Clock.Elapsed.TotalSeconds, 20);
        // Refresh its revision once, then preserve this test's explicit pool fixture.
        _ = f.Chance;
        f.Conn.Pools = f.Conn.Pools with { Life = 10000, Armour = 500, EnergyShield = 100 };
        f.Conn.EnergyShield.SetMaximum(100, refill: true);
        var random = new TraceRandom(armourRoll: 0, blockRoll: .99); f.Conn.IncomingHitRandom = random;
        f.Conn.EvasionEntropy = 99; f.Conn.LastEvasionAt = f.Conn.Clock.Elapsed.TotalSeconds;
        var monster = f.Monster(DamagePacket.Of(DamageType.Physical, 100, 100).Add(DamageType.Chaos, new(100, 100)));
        await f.Swing(monster).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(9920u, f.Conn.PlayerLife); // Chaos: 100 * .8 bypasses ES.
        Assert.Equal(74u, f.Conn.EnergyShield.Current); // Whole hit 133 * .8 = 106; 106 - 80 = 26 to ES.
        await f.Swing(monster).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(9840u, f.Conn.PlayerLife); Assert.Equal(48u, f.Conn.EnergyShield.Current);
        Assert.Equal(2, f.Count); Assert.Equal(2, random.Draws.Count(d => d == "next:100"));
    }

    [Theory]
    [InlineData(DamageType.Fire)]
    [InlineData(DamageType.Chaos)]
    public async Task Irrelevant_packet_components_do_not_consume_armour_rng(DamageType type)
    {
        using var f = new Fixture();
        f.Set(RecentEnemyHits.GenericChance, 100);
        var random = new TraceRandom(); f.Conn.IncomingHitRandom = random;
        f.Conn.EvasionEntropy = 99; f.Conn.LastEvasionAt = f.Conn.Clock.Elapsed.TotalSeconds;
        await f.Swing(f.Monster(DamagePacket.Of(type, 100, 100))).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(9900u, f.Conn.PlayerLife); Assert.Equal(1, f.Count);
        Assert.Equal(new[] { "range:100:101", "double" }, random.Draws);
    }

    [Fact]
    public async Task Old_death_notification_cannot_clear_a_resurrected_players_new_contact()
    {
        using var f = new Fixture();
        await f.Command("/hit fire 1");
        await (Task)Method("PlayerDied").Invoke(null, ["incoming-test", f.Stream, f.Ciphers, f.Conn, "stale death"])!;
        Assert.Equal(1, f.Count); Assert.Equal(10, f.Chance);
    }

    [Theory]
    [InlineData(29, 133)]
    [InlineData(30, 150)]
    public async Task Actual_MonsterMod_mixed_packet_uses_one_armour_draw_and_one_contact(int roll, int damage)
    {
        using var f = new Fixture();
        for (int i = 0; i < 3; i++) f.Conn.RecentEnemyHits.Record(f.Conn.Clock.Elapsed.TotalSeconds);
        var random = new TraceRandom(armourRoll: roll, blockRoll: .99); f.Conn.IncomingHitRandom = random;
        f.Conn.EvasionEntropy = 99; f.Conn.LastEvasionAt = f.Conn.Clock.Elapsed.TotalSeconds;
        var effects = new MonsterModEffects(new Dictionary<string, int>
            { ["non_chaos_damage_to_add_as_chaos_damage_%"] = 100 });
        var monster = f.Monster(effects.Hit(DamagePacket.Of(DamageType.Physical, 100, 100)),
            mods: new([], [], effects));
        await f.Swing(monster).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(10000u - (uint)damage, f.Conn.PlayerLife);
        Assert.Equal(4, f.Count);
        Assert.Equal(new[] { "range:100:101", "range:100:101", "double", "next:100" }, random.Draws);
        Assert.Equal(500, f.Conn.Pools.Armour);
    }

    [Fact]
    public async Task Feature_absent_preserves_seeded_monster_rng_sequence_and_damage()
    {
        using var f = new Fixture();
        f.Set(RecentEnemyHits.PerHit, 0);
        var expected = new Random(456);
        var random = new TraceRandom(456); f.Conn.IncomingHitRandom = random;
        var packet = DamagePacket.Of(DamageType.Physical, 50, 150).Add(DamageType.Fire, new(20, 40));
        var monster = f.Monster(packet);
        // Reproduce the pre-feature order: initial entropy, per-component rolls, then block.
        expected.Next(100);
        var rolled = packet.Roll(expected);
        expected.NextDouble();
        int damage = DamageModel.MitigatePhysical((int)rolled[DamageType.Physical].Min, 500)
            + (int)rolled[DamageType.Fire].Min;
        await f.Swing(monster).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(10000u - (uint)damage, f.Conn.PlayerLife);
        Assert.Equal(new[] { "next:100", "range:50:151", "range:20:41", "double" }, random.Draws);
        Assert.Equal(expected.Next(), random.Next());
        Assert.Equal(1, f.Count);
    }

    [Theory]
    [InlineData("block", 1)]
    [InlineData("immune", 1)]
    [InlineData("zero", 1)]
    [InlineData("range", 0)]
    [InlineData("evade", 0)]
    [InlineData("friendly", 0)]
    [InlineData("stale", 0)]
    public async Task Monster_contact_filters_distinguish_block_zero_miss_evade_and_rejection(string mode, int count)
    {
        using var f = new Fixture();
        f.Set(RecentEnemyHits.GenericChance, 100);
        var random = new TraceRandom(armourRoll: 0, blockRoll: 0); f.Conn.IncomingHitRandom = random;
        f.Conn.EvasionEntropy = 99; f.Conn.LastEvasionAt = f.Conn.Clock.Elapsed.TotalSeconds;
        var monster = f.Monster(mode == "zero" ? DamagePacket.Empty : null, hostile: mode != "friendly");
        if (mode == "block") f.Conn.Pools = f.Conn.Pools with { BlockChance = 100 };
        if (mode == "immune") f.Set("physical_immunity", 1);
        if (mode == "range") f.Conn.Walk.Teleport(100, 100, f.Conn.Clock.Elapsed.TotalSeconds);
        if (mode == "evade")
        {
            f.Conn.Pools = f.Conn.Pools with { Evasion = int.MaxValue };
            f.Conn.EvasionEntropy = 0;
        }
        if (mode == "stale") f.Monster();
        await f.Swing(monster).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(10000u, f.Conn.PlayerLife);
        Assert.Equal(count, f.Count);
        Assert.DoesNotContain("next:100", random.Draws); // Fixed entropy; no armour draw for these contacts.
    }

    [Theory]
    [InlineData("dead")]
    [InlineData("closed")]
    [InlineData("transfer-failed")]
    [InlineData("transfer-success")]
    [InlineData("replaced-monster")]
    public async Task Swing_epoch_is_captured_before_first_await_and_stale_work_cannot_land(string change)
    {
        using var f = new Fixture();
        var random = new TraceRandom(); f.Conn.IncomingHitRandom = random;
        var monster = f.Monster();
        await f.Gate.WaitAsync();
        var swing = f.Swing(monster); // Awaiting its first send, with generation already captured.
        Assert.False(swing.IsCompleted);
        if (change == "dead")
        {
            f.Conn.PlayerLife = 0;
            var death = (Task)Method("PlayerDied").Invoke(null, ["incoming-test", f.Stream, f.Ciphers, f.Conn, "test death"])!;
            Assert.Equal(0, f.Count); // Clear before death's first send can await the same gate.
            var resurrect = (Task)Method("Resurrect").Invoke(null, ["incoming-test", f.Stream, f.Ciphers, f.Conn, false])!;
            f.Gate.Release();
            await Task.WhenAll(swing, death, resurrect).WaitAsync(TimeSpan.FromSeconds(5));
        }
        else
        {
            if (change == "closed") f.Conn.Closed = true;
            if (change.StartsWith("transfer"))
            {
                Assert.True(f.Conn.TryBeginAreaChange(new(2, 0, false), out _));
                f.Conn.PendingTransfer = null;
                if (change == "transfer-failed") f.Conn.ResumeAfterFailedTransfer();
            }
            if (change == "replaced-monster") f.Monster();
            f.Gate.Release();
            await swing.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(f.Conn.PlayerMaxLife, f.Conn.PlayerLife);
        Assert.Equal(0, f.Count); Assert.Empty(random.Draws);
    }

    [Fact]
    public async Task Queued_swing_echo_cannot_survive_a_failed_transfer_generation_change()
    {
        using var f = new Fixture();
        await f.Gate.WaitAsync();
        var swing = f.Swing(f.Monster());
        Assert.False(swing.IsCompleted);
        Assert.True(f.Conn.TryBeginAreaChange(new(2, 0, false), out _));
        f.Conn.PendingTransfer = null;
        f.Conn.ResumeAfterFailedTransfer();
        f.Gate.Release();
        await swing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(f.Conn.PlayerMaxLife, f.Conn.PlayerLife);
        Assert.Equal(0, f.Count);
        Assert.Empty(await f.Read());
    }

    [Theory]
    [InlineData("dead-owner")]
    [InlineData("closed")]
    [InlineData("leaving")]
    [InlineData("epoch")]
    [InlineData("dead-monster")]
    [InlineData("zero-life-monster")]
    [InlineData("removed-monster")]
    [InlineData("replaced-monster")]
    public async Task Queued_swing_echo_rechecks_owner_and_monster_then_aborts_its_contact_delay(string change)
    {
        using var f = new Fixture();
        var random = new TraceRandom(); f.Conn.IncomingHitRandom = random;
        var monster = f.Monster(seconds: 60); // A discarded echo must return before its 30-second contact delay.
        await f.Gate.WaitAsync();
        var swing = f.Swing(monster);
        Assert.False(swing.IsCompleted);
        lock (f.Conn.Monsters)
        {
            if (change == "dead-owner") f.Conn.PlayerLife = 0;
            if (change == "closed") f.Conn.Closed = true;
            if (change == "leaving") Assert.True(f.Conn.TryBeginAreaChange(new(2, 0, false), out _));
            if (change == "epoch") f.Conn.InvalidateIncomingHits();
            if (change == "dead-monster") monster.Dead = true;
            if (change == "zero-life-monster") monster.Life = 0;
            if (change == "removed-monster") f.Conn.Monsters.Remove(42);
            if (change == "replaced-monster") f.Monster(hostile: false);
        }
        uint life = f.Conn.PlayerLife;
        f.Gate.Release();
        await swing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(life, f.Conn.PlayerLife); Assert.Equal(0, f.Count); Assert.Empty(random.Draws);
        Assert.Empty(await f.Read());
    }

    [Theory]
    [InlineData("dead")]
    [InlineData("closed")]
    [InlineData("transfer")]
    public async Task Swing_cannot_start_during_rejected_owner_state_then_land_after_admission_resumes(string state)
    {
        using var f = new Fixture();
        var random = new TraceRandom(); f.Conn.IncomingHitRandom = random;
        var monster = f.Monster();
        if (state == "dead") f.Conn.PlayerLife = 0;
        if (state == "closed") f.Conn.Closed = true;
        if (state == "transfer") Assert.True(f.Conn.TryBeginAreaChange(new(2, 0, false), out _));
        await f.Gate.WaitAsync();
        var swing = f.Swing(monster);
        Assert.True(swing.IsCompleted);
        f.Conn.PlayerLife = 10000; f.Conn.Closed = false;
        f.Conn.PendingTransfer = null; f.Conn.ResumeAfterFailedTransfer();
        f.Gate.Release(); await swing;
        Assert.Equal(10000u, f.Conn.PlayerLife); Assert.Equal(0, f.Count); Assert.Empty(random.Draws);
    }

    [Fact]
    public async Task Paused_swing_waits_and_history_ages_only_in_game_time()
    {
        using var f = new Fixture();
        f.Conn.RecentEnemyHits.Record(f.Conn.Clock.Elapsed.TotalSeconds);
        f.Conn.EvasionEntropy = 99; f.Conn.LastEvasionAt = f.Conn.Clock.Elapsed.TotalSeconds;
        var monster = f.Monster();
        await f.Gate.WaitAsync();
        var swing = f.Swing(monster);
        lock (f.Conn.Monsters) f.Conn.Clock.Stop();
        f.Gate.Release();
        await Task.Delay(120);
        Assert.False(swing.IsCompleted);
        Assert.Equal(1, f.Count); Assert.Equal(10000u, f.Conn.PlayerLife);
        Assert.Empty(await f.Read()); // The action state machine holds the echo as well as contact through pause.
        lock (f.Conn.Monsters) f.Conn.Clock.Start();
        await swing.WaitAsync(TimeSpan.FromSeconds(5));
        var expected = InstanceProtocol.ActionStarted(42, 34, 32, 32, 32, monster.Attack!.SkillId,
            0xffff, 0x0408, targetObjectId: Program.PlayerId);
        Assert.Equal(expected, (await f.Read()).Take(expected.Length));
        Assert.Equal(2, f.Count);
    }

    [Fact]
    public async Task Failed_transfer_retains_history_with_elapsed_expiry_and_new_area_or_relog_starts_empty()
    {
        using var first = new Fixture();
        await first.Command("/hit fire 1");
        Assert.True(first.Conn.TryBeginAreaChange(new(2, 0, false), out _));
        first.Conn.PendingTransfer = null;
        first.Conn.ResumeAfterFailedTransfer();
        Assert.Equal(1, first.Count); Assert.Equal(10, first.Chance);
        using var area = new Fixture(character: first.Conn.Character);
        Assert.Equal(0, area.Count); Assert.Equal(0, area.Chance);
        // Existing failed-transfer fixture seam; clock time still prunes the old connection's history.
        first.Conn.RecentEnemyHits.Clear();
        first.Conn.RecentEnemyHits.Record(first.Conn.Clock.Elapsed.TotalSeconds - 4);
        first.Conn.ResumeAfterFailedTransfer(); Assert.Equal(0, first.Count);
        using var relog = new Fixture(character: first.Conn.Character, instance: first.Conn.Instance);
        Assert.Equal(0, relog.Count); Assert.Equal(0, relog.Chance);
    }

    [Fact]
    public async Task Parsed_exit_clears_history_and_invalidates_pending_incoming_contacts()
    {
        using var f = new Fixture();
        await f.Command("/hit fire 1");
        int epoch = f.Conn.IncomingHitEpoch;
        await Program.OnClientMessages("incoming-test", f.Stream, f.Ciphers,
            [(InstanceProtocol.ExitToCharacterSelectId, new byte[] { 1, 0x53 })], [], f.Conn);
        Assert.True(f.Conn.Closed); Assert.Equal(0, f.Count);
        Assert.NotEqual(epoch, f.Conn.IncomingHitEpoch);
    }

    [Fact]
    public async Task Lethal_contact_clears_before_notification_await_and_parsed_resurrection_starts_empty()
    {
        using var f = new Fixture(0);
        await f.Command("/hit fire 1"); Assert.Equal(1, f.Count);
        await f.Gate.WaitAsync();
        var hit = f.Command("/hit physical 10000");
        Assert.Equal(0u, f.Conn.PlayerLife); Assert.Equal(0, f.Count);
        f.Gate.Release(); await hit;
        byte[] bytes = [0, 0x54, 0];
        await Program.OnClientMessages("incoming-test", f.Stream, f.Ciphers, [(0x54, bytes)], [], f.Conn);
        Assert.Equal(f.Conn.PlayerMaxLife, f.Conn.PlayerLife); Assert.Equal(0, f.Count);
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("death")]
    [InlineData("modifier")]
    [InlineData("pause")]
    public async Task Queued_stat_presentation_resamples_count_and_chance_inside_send_gate(string change)
    {
        using var f = new Fixture();
        double now = f.Conn.Clock.Elapsed.TotalSeconds;
        f.Conn.RecentEnemyHits.Record(now);
        await f.Present(); await f.Read();
        Assert.Equal((1, 10), f.Conn.PresentedRecentEnemyHits);
        await f.Gate.WaitAsync();
        var pending = f.Present(); Assert.False(pending.IsCompleted);
        if (change == "expiry") await Task.Delay(4100);
        if (change == "death") { f.Conn.PlayerLife = 0; f.Conn.InvalidateIncomingHits(); }
        if (change == "modifier") f.Set(RecentEnemyHits.PerHit, 0);
        if (change == "pause") f.Conn.Clock.Stop();
        f.Gate.Release(); await pending;
        byte[] bytes = await f.Read();
        if (change == "pause") { Assert.Empty(bytes); return; }
        int count = change == "modifier" ? 1 : 0;
        Assert.Equal(InstanceEntities.StatsUpdate(Program.PlayerId,
            [(4534u, count), (12268u, 0)]), bytes);
    }

    [Fact]
    public async Task Existing_player_tick_publishes_idle_expiry_without_a_new_contact()
    {
        using var f = new Fixture();
        _ = f.Chance; // Warm the real catalogs before arranging a short remaining lifetime.
        f.Conn.RecentEnemyHits.Record(f.Conn.Clock.Elapsed.TotalSeconds - 3.5);
        await f.Present(); await f.Read();
        Assert.Equal((1, 10), f.Conn.PresentedRecentEnemyHits);
        var tick = (Task)Method("RunPlayerTick").Invoke(null, ["incoming-test", f.Stream, f.Ciphers, f.Conn])!;
        for (int i = 0; i < 100 && f.Conn.PresentedRecentEnemyHits != (0, 0); i++) await Task.Delay(20);
        f.Conn.Closed = true;
        await tick.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, f.Count);
        byte[] bytes = await f.Read();
        byte[] zero = InstanceEntities.StatsUpdate(Program.PlayerId, [(4534u, 0), (12268u, 0)]);
        Assert.True(bytes.AsSpan().IndexOf(zero) >= 0);
    }
}
