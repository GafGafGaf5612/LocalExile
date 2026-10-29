using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class CrowdRegressionTests
{
    static T Call<T>(string name, params object?[] args) => (T)typeof(Program)
        .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;

    static async Task Until(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), "The expected simulation transition did not occur.");
            await Task.Delay(5);
        }
    }

    sealed class Connection : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new(), reader;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public NetworkStream Stream => writer.GetStream();
        public NetworkStream ReadStream => reader.GetStream();
        public readonly ClientState State;
        public readonly PlayerInventories Items;
        public readonly SpawnedMonster Target;
        public readonly (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) Gem;
        public SemaphoreSlim Gate => Program.SendGates.GetValue(Ciphers, _ => new(1, 1));
        public Connection(bool cyclone = false)
        {
            TestSaves.Ensure();
            State = new(new(ushort.MaxValue, "CrowdRegression", 200, 200, 100, 100, (_, _) => true, []), 1, 1)
            {
                Character = "CrowdRegression" + Guid.NewGuid().ToString("N"), ClassRow = 1,
                PlayerSent = true, PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            };
            Program.ConnectionMetrics.Add(Ciphers, State.Performance);
            Items = PlayerInventories.For(State.Character, Program.Items);
            if (cyclone)
            {
                var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemCyclone")!);
                var host = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!) with
                    { Sockets = [new(SocketColour.White, gem)], Links = [1] };
                Items.Add(PlayerInventories.BodyArmour, 0, 0, host);
                Gem = Call<IEnumerable<(ushort, int, string, int, int, uint)>>("GemSkillEffects", State).Single();
            }
            Set("global_cannot_crit", 1); Set("physical_damage_+%", -100);
            Set("attack_minimum_added_chaos_damage", 100); Set("attack_maximum_added_chaos_damage", 100);
            State.Pools = Program.PoolsFor(State, 40) with
                { Life = 1000, Mana = 1000, Accuracy = int.MaxValue, Strength = 1000, Dexterity = 1000, Intelligence = 1000 };
            Target = new("old position target", new(102, 100, 30), 0, 2, 1000000);
            State.Monsters[42] = Target;
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
        }
        public void Set(string name, int value) => State.PlayerStats[Program.Stats.Key(name)!.Value] = value;
        public Task Message(ushort id, byte[] bytes) => Program.OnClientMessages("crowd-regression", Stream, Ciphers,
            [(id, bytes)], [], State);
        public void Dispose()
        {
            State.Closed = true;
            writer.Dispose(); reader.Dispose(); listener.Stop();
            PlayerInventories.Forget(State.Character!);
            Program.GemSkillIdsOf.TryRemove(State.Character!, out _);
        }
    }

    // A freeze (a critical cold hit of the player's maximum life) or a stun's hit recovery, both longer than the
    // channel's silence timeout.
    static void Interrupt(ClientState state, string how)
    {
        double now = state.Clock.Elapsed.TotalSeconds;
        var cold = DamagePacket.Of(DamageType.Cold, state.PlayerMaxLife, state.PlayerMaxLife);
        lock (state.Monsters)
            if (how == "stun") Assert.True(state.HitRecovery.Begin(now, 10));
            else state.ElementalAilments.Apply(cold, cold, true, state.PlayerMaxLife, now, _ => 0, new Random(1));
        Assert.True(Program.PlayerActionBlocked(state, now, now));
    }

    [Theory]
    [InlineData("end")]
    [InlineData("replace")]
    [InlineData("move")]
    [InlineData("pause")]
    [InlineData("silence")]
    [InlineData("freeze")]
    [InlineData("stun")]
    public async Task A_queued_channel_sweep_obeys_stop_generation_pause_and_current_position(string change)
    {
        using var f = new Connection(cyclone: true);
        f.State.ActionCount = 7; f.State.ChannelSkill = f.Gem.Id;
        f.State.NextChannelHitAt = f.State.Clock.Elapsed.TotalSeconds - 1;
        var newTarget = new SpawnedMonster("new position target", new(152, 100, 30), 0, 2, 1000000);
        f.State.Monsters[43] = newTarget;
        await f.Gate.WaitAsync();
        bool held = true;
        // This fixture starts the timer directly; production stamps this at paid channel admission.
        f.State.LastChannelUpdateAt = f.State.Clock.Elapsed.TotalSeconds;
        var channel = Call<Task>("RunChannel", "crowd-regression", f.Stream, f.Ciphers, f.State, f.Gem.Id, f.Gem, 7);
        try
        {
            await Until(() => f.State.PlayerMana < 1000); // Sweep has paid and is waiting to publish mana.
            uint paid = f.State.PlayerMana;
            if (change == "end") await f.Message(InstanceProtocol.ChannelEndId, [1, 0x99]);
            else if (change is "freeze" or "stun") Interrupt(f.State, change);
            else if (change == "replace") lock (f.State.Monsters) f.State.ChannelGeneration++;
            else if (change == "pause") f.State.Clock.Stop();
            else if (change == "silence") lock (f.State.Monsters)
                f.State.LastChannelUpdateAt = f.State.Clock.Elapsed.TotalSeconds - 1.1;
            else await f.Message(InstanceProtocol.ChannelUpdateId,
                new LoginWriter().U16(InstanceProtocol.ChannelUpdateId).U32(150).U32(100).ToArray());
            f.Gate.Release(); held = false;
            if (change == "move")
            {
                await Until(() => newTarget.Life < newTarget.MaxLife);
                await f.Message(InstanceProtocol.ChannelEndId, [1, 0x99]);
            }
            else if (change == "pause") { await Task.Delay(100); f.State.Closed = true; }
            await channel.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(f.Target.MaxLife, f.Target.Life);
            if (change != "move") Assert.Equal(newTarget.MaxLife, newTarget.Life);
            // Interrupted, the channel paid no further stage before its silence ended it.
            if (change is "freeze" or "stun") Assert.Equal(paid, f.State.PlayerMana);
        }
        finally
        {
            f.State.Closed = true;
            if (held) f.Gate.Release();
            await channel.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData("freeze")]
    [InlineData("stun")]
    public async Task An_interrupted_channel_pays_and_sweeps_nothing_until_the_player_recovers(string how)
    {
        using var f = new Connection(cyclone: true);
        f.State.ActionCount = 7; f.State.ChannelSkill = f.Gem.Id;
        f.State.NextChannelHitAt = f.State.Clock.Elapsed.TotalSeconds - 1;
        Interrupt(f.State, how);
        f.State.LastChannelUpdateAt = f.State.Clock.Elapsed.TotalSeconds;
        var channel = Call<Task>("RunChannel", "crowd-regression", f.Stream, f.Ciphers, f.State, f.Gem.Id, f.Gem, 7);
        try
        {
            await Task.Delay(300);
            Assert.False(channel.IsCompleted);
            Assert.Equal(1000u, f.State.PlayerMana);
            Assert.Equal(f.Target.MaxLife, f.Target.Life);
            lock (f.State.Monsters)
            {
                f.State.ElementalAilments.Clear(); f.State.HitRecovery.Clear();
                // The held button's updates resume with the action (they are dropped while it is blocked).
                f.State.LastChannelUpdateAt = f.State.Clock.Elapsed.TotalSeconds;
            }
            await Until(() => f.State.PlayerMana < 1000 && f.Target.Life < f.Target.MaxLife);
        }
        finally { f.State.Closed = true; await channel.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task A_failed_initial_channel_payment_never_starts_a_timer()
    {
        using var f = new Connection(cyclone: true);
        f.State.PlayerMana = 0; f.State.ActionCount = 7;
        Assert.False(await Call<Task<bool>>("CastGemSkill", "crowd-regression", f.Stream, f.Ciphers, f.State,
            f.Gem.Id, (100, 100), (100, 100), 0u, (ushort)0, (int?)7, null, false));
        Assert.Equal(0, f.State.NextChannelHitAt);
        Assert.Equal((ushort)0, f.State.ChannelSkill);
    }

    [Fact]
    public async Task Removing_the_channel_gem_stops_the_timer_before_another_payment()
    {
        using var f = new Connection(cyclone: true);
        f.State.ActionCount = 7; f.State.ChannelSkill = f.Gem.Id;
        f.State.LastChannelUpdateAt = f.State.Clock.Elapsed.TotalSeconds;
        f.State.NextChannelHitAt = f.State.Clock.Elapsed.TotalSeconds - 1;
        var host = Assert.Single(f.Items.Items(PlayerInventories.BodyArmour));
        f.Items.Change(PlayerInventories.BodyArmour, host.Id,
            i => i with { Sockets = [new(SocketColour.White)], Links = [1] });

        await Call<Task>("RunChannel", "crowd-regression", f.Stream, f.Ciphers, f.State,
            f.Gem.Id, f.Gem, 7).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1000u, f.State.PlayerMana);
        Assert.Equal((ushort)0, f.State.ChannelSkill);
        Assert.Equal(f.Target.MaxLife, f.Target.Life);
    }

    [Theory]
    [InlineData("ai")]
    [InlineData("collision")]
    public async Task Crowd_movement_commits_prior_projectile_contacts_before_building_its_body_snapshot(string pass)
    {
        var conn = new ClientState(new(1, "crowd-projectile-order", 300, 300, 10, 10, (_, _) => true, []), 1, 1)
            { PlayerSent = true, PlayerLife = 100, Instance = new() };
        await using var f = new ProjectileRuntimeFixture(conn);
        var target = ProjectileRuntimeFixture.Monster(30, 10);
        conn.Monsters[42] = target;
        f.Release(f.Shot() with { Profile = f.Shot().Profile with { Speed = 30000000 } });
        await Until(() => conn.Clock.Elapsed.TotalSeconds > .001);

        if (pass == "collision")
            await Call<Task>("CorrectMonsterCollisions", "crowd-projectile-order", f.Stream, f.Ciphers, conn);
        else
        {
            var ai = Call<Task>("RunMonsterAi", "crowd-projectile-order", f.Stream, f.Ciphers, conn);
            try { await Until(() => conn.Performance.Read(LatencyPhase.BlockerBuild).Count > 0); }
            finally { conn.Closed = true; await ai.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        Assert.Equal(90u, target.Life);
        Assert.True(f.Session.Runtime.Now >= ProjectileRuntime.Time(.001));
        Assert.True(conn.Performance.Read(LatencyPhase.BlockerBuild).Count > 0);
        Assert.True(conn.Performance.Read(LatencyPhase.CombatHold).Count > 0);
    }

    [Fact]
    public async Task Stopping_during_the_initial_mana_update_cannot_restart_the_channel()
    {
        using var f = new Connection(cyclone: true);
        f.State.ActionCount = 7;
        await f.Gate.WaitAsync();
        var cast = Call<Task<bool>>("CastGemSkill", "crowd-regression", f.Stream, f.Ciphers, f.State,
            f.Gem.Id, (100, 100), (100, 100), 0u, (ushort)0, (int?)7, null, false);
        try
        {
            await Until(() => f.State.PlayerMana < 1000);
            await f.Message(InstanceProtocol.ChannelEndId, [1, 0x99]);
        }
        finally { f.Gate.Release(); }
        Assert.False(await cast.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal((ushort)0, f.State.ChannelSkill);
        Assert.Equal(0, f.State.NextChannelHitAt);
        Assert.Equal(f.Target.MaxLife, f.Target.Life);
    }

    [Fact]
    public async Task Pong_bypasses_a_waiting_sender_and_the_shared_keystream_stays_ordered()
    {
        using var f = new Connection();
        byte[] normal = InstanceProtocol.LifeUpdate(Program.PlayerId, 100);
        byte[] expectedNormal = [.. normal];
        byte[] pong = InstanceProtocol.Pong(ClientInbox.ServerDelayMs, 0, 0x12345678);
        byte[] expectedPong = [.. pong];
        var decrypt = new ConnectionCiphers(new byte[64], true);
        await f.Gate.WaitAsync();
        var send = Program.Send("crowd-regression", f.Stream, f.Ciphers, normal, null);
        try
        {
            await Call<Task>("SendPong", f.Stream, f.Ciphers, pong).WaitAsync(TimeSpan.FromSeconds(5));
            var bytes = new byte[pong.Length];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await f.ReadStream.ReadExactlyAsync(bytes, timeout.Token);
            decrypt.Receive.Process(bytes);
            Assert.Equal(expectedPong, bytes);
            Assert.False(send.IsCompleted);
        }
        finally { f.Gate.Release(); }
        await send.WaitAsync(TimeSpan.FromSeconds(5));
        var rest = new byte[normal.Length];
        await f.ReadStream.ReadExactlyAsync(rest);
        decrypt.Receive.Process(rest);
        Assert.Equal(expectedNormal, rest);
        Assert.Equal(1, f.State.Performance.Read(LatencyPhase.SendWait).Count);
        Assert.Equal(2, f.State.Performance.Read(LatencyPhase.WireHold).Count);
        Assert.Equal(2, f.State.Performance.Read(LatencyPhase.SocketWrite).Count);
    }

    [Fact]
    public async Task A_Rage_refresh_waiting_at_the_send_gate_uses_current_skill_stats()
    {
        using var f = new Connection();
        await f.Gate.WaitAsync();
        var send = Program.SendRage("crowd-regression", f.Stream, f.Ciphers, f.State);
        f.Set("attack_minimum_added_chaos_damage", 200); f.Set("attack_maximum_added_chaos_damage", 200);
        var skills = Call<IReadOnlyList<GrantedSkill>>("PlayerSkills", f.State);
        byte[] actor = InstanceEntities.PlayerSkillsChange(Program.PlayerId, skills, []);
        byte[] stats = InstanceEntities.StatsUpdate(Program.PlayerId,
            Call<List<(uint, int)>>("PlayerStatsWithCleared", f.State));
        f.Gate.Release();
        await send.WaitAsync(TimeSpan.FromSeconds(5));
        var received = new byte[stats.Length + actor.Length];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await f.ReadStream.ReadExactlyAsync(received, timeout.Token);
        new ConnectionCiphers(new byte[64], true).Receive.Process(received);
        Assert.True(received.AsSpan().EndsWith(actor));
    }

    [Fact]
    public void A_prepared_skill_snapshot_is_invalidated_by_gem_only_changes()
    {
        using var f = new Connection(cyclone: true);
        var inputs = Program.SkillSnapshotInputs.Capture(f.State, Program.CharacterPlayerStats(f.State));
        Assert.True(inputs.Matches(f.State, Program.CharacterPlayerStats(f.State)));
        var host = Assert.Single(f.Items.Items(PlayerInventories.BodyArmour));
        f.Items.Change(PlayerInventories.BodyArmour, host.Id, i => i with { Sockets = [new(SocketColour.White)], Links = [1] });
        Assert.False(inputs.Matches(f.State, Program.CharacterPlayerStats(f.State)));
    }

    [Fact]
    public async Task A_short_reach_monster_can_attack_from_its_inner_surround_slot()
    {
        using var f = new Connection();
        f.State.Monsters.Clear();
        var variety = Program.Monsters.Find("Metadata/Monsters/InsectSpawner/InsectSpawnerParasiteBoss")!;
        var mob = new SpawnedMonster("short reach", f.State.NewMonsterWalk(42, 130, 100, 300, variety.ObjectSize - 1, true), 100, 6, 100)
        {
            Variety = variety, Attack = new(0x102, 10, variety.AttackRange, DamagePacket.Of(DamageType.Physical, 1, 1)),
        };
        f.State.Monsters[42] = mob;
        var ai = Call<Task>("RunMonsterAi", "crowd-regression", f.Stream, f.Ciphers, f.State);
        try
        {
            // The AI sets BusyUntil before it stops the walk; read both in its combat-lock section.
            (int X, int Y) p = default;
            await Until(() => { lock (f.State.Monsters) { p = mob.Walk.Position(f.State.Clock.Elapsed.TotalSeconds); return mob.BusyUntil > 0; } });
            Assert.True(Math.Sqrt(Math.Pow(p.X - 100, 2) + Math.Pow(p.Y - 100, 2)) <= variety.AttackRange);
        }
        finally { f.State.Closed = true; await ai.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task A_surround_slot_excludes_stationary_objects()
    {
        using var f = new Connection();
        f.State.Monsters.Clear(); f.State.SetBlocker(9, 105, 100, 4);
        var mob = new SpawnedMonster("blocked chaser", f.State.NewMonsterWalk(42, 130, 100, 300, 2, true), 100, 4, 100)
            { Variety = new(1, "test", "test", "test", null, null, ObjectSize: 3) };
        f.State.Monsters[42] = mob;
        var ai = Call<Task>("RunMonsterAi", "crowd-regression", f.Stream, f.Ciphers, f.State);
        try
        {
            // The AI sets LastTarget before it orders the walk; read both in its combat-lock section.
            (int X, int Y)? slot = null;
            (int X, int Y) end = default;
            await Until(() => { lock (f.State.Monsters) { slot = mob.LastTarget; end = mob.Walk.Position(mob.Walk.ArrivesAt + 1); return slot is not null; } });
            Assert.False(ClientPathfinder.ObjectBlocks(f.State.StationaryBlockers, slot!.Value.X, slot.Value.Y, 3, 6));
            Assert.Equal(slot.Value, end);
        }
        finally { f.State.Closed = true; await ai.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task A_crowd_escape_survives_living_correction_but_stationary_objects_still_stop_it()
    {
        using var f = new Connection();
        f.State.Monsters.Clear(); f.State.Walk.Teleport(50, 50, 0); f.State.ActionCount = 7;
        for (int i = 0; i < 16; i++)
        {
            int x = 50 + (int)Math.Round(5 * Math.Cos(i * Math.PI / 8));
            int y = 50 + (int)Math.Round(5 * Math.Sin(i * Math.PI / 8));
            f.State.Monsters[(uint)(42 + i)] = new("ring", new(x, y, 1), 0, 4, 100);
        }
        f.State.Walk.Order(80, 50, f.State.Clock.Elapsed.TotalSeconds - .12);
        Assert.True(f.State.Walk.EscapingCrowd(f.State.Clock.Elapsed.TotalSeconds));
        await Call<Task>("PushOutOfCollision", "crowd-regression", f.Stream, f.Ciphers, f.State, true);
        Assert.Equal(7, f.State.ActionCount);
        var p = f.State.Walk.Position(f.State.Clock.Elapsed.TotalSeconds);
        f.State.SetBlocker(9, p.X, p.Y, 2);
        await Call<Task>("PushOutOfCollision", "crowd-regression", f.Stream, f.Ciphers, f.State, true);
        Assert.Equal(8, f.State.ActionCount);
        Assert.False(f.State.Walk.EscapingCrowd(f.State.Clock.Elapsed.TotalSeconds));
    }
}
