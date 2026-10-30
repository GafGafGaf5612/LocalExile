using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    static void LifecycleSet(ClientState state, string name, int value) =>
        state.PlayerStats[Program.Stats.Key(name)!.Value] = value;

    static ClientState OlderStatObserver(GatewayCharacter game)
    {
        var bow = ItemCatalog.Create(Program.Items.Find("Short Bow")!) with
        {
            Sockets = [new(SocketColour.White,
                ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemFireball")!))],
        };
        uint host = game.Items.Add(PlayerInventories.Weapon1, 0, 0, bow).Id;
        var older = game.Reload();
        LifecycleSet(older, "base_intelligence", 100);
        older.Pools = Program.PoolsFor(older, 60);
        Assert.Contains(host, Program.InactiveItems[game.Character]);
        LifecycleSet(game.State, "base_intelligence", 100);
        LifecycleSet(game.State, "base_dexterity", 100);
        game.State.Pools = Program.PoolsFor(game.State, 60);
        Assert.DoesNotContain(host, Program.InactiveItems[game.Character]);
        Assert.Contains(ProgramValue<IReadOnlyList<GrantedSkill>>("PlayerSkills", game.State),
            s => s.Id == Program.GemSkillIdPool.Value[0]);
        return older;
    }

    static void EndStatLifecycle(ClientState state, string lifecycle)
    {
        lock (state.Monsters)
            switch (lifecycle)
            {
                case "closed": state.Closed = true; break;
                case "leaving": state.LeavingArea = true; break;
                case "retired": state.RageRetired = true; break;
                default: throw new ArgumentOutOfRangeException(nameof(lifecycle));
            }
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("leaving")]
    [InlineData("retired")]
    public void Character_stale_stat_calculations_cannot_publish_over_current_equipment_qualification(string lifecycle)
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(level: 60);
        var older = OlderStatObserver(game);
        var published = Program.InactiveItems[game.Character];
        EndStatLifecycle(older, lifecycle);
        for (int i = 0; i < 5; i++)
        {
            Program.CharacterPlayerStats(older);
            Assert.Same(published, Program.InactiveItems[game.Character]);
        }
        Assert.Contains(ProgramValue<IReadOnlyList<GrantedSkill>>("PlayerSkills", game.State),
            s => s.Id == Program.GemSkillIdPool.Value[0]);
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("leaving")]
    [InlineData("retired")]
    public async Task Character_queued_ES_refresh_checks_lifecycle_before_reading_sources(string lifecycle)
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(level: 60);
        var older = OlderStatObserver(game);
        var cache = older.StatSourcesCache;
        var published = Program.InactiveItems[game.Character];
        LifecycleSet(older, "base_dexterity", 1); // A source read would replace the connection-local cache.
        var result = await game.StatLifecycleExchange(async (stream, cipher) =>
        {
            var gate = Program.SendGates.GetValue(cipher, _ => new(1, 1));
            await gate.WaitAsync();
            var queued = ProgramValue<Task>("SendPlayerEnergyShield", "queued-ES-lifecycle", stream, cipher, older);
            try
            {
                Assert.False(queued.IsCompleted);
                EndStatLifecycle(older, lifecycle);
            }
            finally { gate.Release(); }
            await queued;
        });
        Assert.Empty(result.Bytes);
        Assert.Same(cache, older.StatSourcesCache);
        Assert.Same(published, Program.InactiveItems[game.Character]);
    }

    [Fact]
    public async Task Character_current_connection_still_publishes_ES_and_equipment_removal_and_restoration()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(level: 60);
        OlderStatObserver(game);
        var host = Assert.Single(game.Items.Items(PlayerInventories.Weapon1));
        LifecycleSet(game.State, "base_dexterity", 0);
        var result = await game.StatLifecycleExchange((stream, cipher) =>
            ProgramValue<Task>("SendPlayerEnergyShield", "current-ES-lifecycle", stream, cipher, game.State));
        Assert.NotEmpty(result.Bytes);
        Assert.Contains(host.Id, Program.InactiveItems[game.Character]);
        Assert.DoesNotContain(ProgramValue<IReadOnlyList<GrantedSkill>>("PlayerSkills", game.State),
            s => s.Id == Program.GemSkillIdPool.Value[0]);
        LifecycleSet(game.State, "base_dexterity", 100);
        Program.CharacterPlayerStats(game.State);
        Assert.DoesNotContain(host.Id, Program.InactiveItems[game.Character]);
        Assert.Contains(ProgramValue<IReadOnlyList<GrantedSkill>>("PlayerSkills", game.State),
            s => s.Id == Program.GemSkillIdPool.Value[0]);
    }

    [Fact]
    public async Task Character_retired_open_connection_resource_writer_finishes_without_changing_current_qualification_or_pools()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(level: 60);
        var older = OlderStatObserver(game);
        var published = Program.InactiveItems[game.Character];
        older.PlayerLife = 1;
        older.PlayerMana = 0;
        uint currentLife = game.State.PlayerLife, currentMana = game.State.PlayerMana;
        await game.StatLifecycleSession("RunResourceRecovery", older, async recovery =>
        {
            Assert.False(recovery.IsCompleted);
            // Use the actual successful-transfer retirement path, while the old socket remains open.
            older.LeavingArea = true;
            Program.CarryRage(older, (uint)Random.Shared.Next(1, int.MaxValue));
            Assert.True(older.RageRetired);
            await recovery.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(older.Closed);
            Assert.Equal(1u, older.PlayerLife);
            Assert.Equal(0u, older.PlayerMana);
            Assert.Equal(currentLife, game.State.PlayerLife);
            Assert.Equal(currentMana, game.State.PlayerMana);
            Assert.Same(published, Program.InactiveItems[game.Character]);
        });
    }

    [Fact]
    public async Task Character_temporarily_leaving_resource_writer_skips_loading_time_and_resumes_after_rollback()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(level: 60);
        LifecycleSet(game.State, "base_life_regeneration_rate_per_minute", 6000);
        game.State.PlayerLife = 1;
        game.State.LeavingArea = true;
        await game.StatLifecycleSession("RunResourceRecovery", game.State, async recovery =>
        {
            await Task.Delay(100);
            Assert.Equal(1u, game.State.PlayerLife);
            Assert.False(recovery.IsCompleted);
            var resumed = System.Diagnostics.Stopwatch.StartNew();
            game.State.ResumeAfterFailedTransfer();
            while (game.State.PlayerLife == 1 && resumed.Elapsed.TotalSeconds < 1) await Task.Delay(5);
            Assert.True(game.State.PlayerLife > 1);
            Assert.InRange(game.State.PlayerLife - 1, 1u, (uint)Math.Ceiling(resumed.Elapsed.TotalSeconds * 100) + 2);
            Assert.False(recovery.IsCompleted);
        });
    }

    [Theory]
    [InlineData("RunPlayerTick")]
    [InlineData("RunCharges")]
    [InlineData("RunFortification")]
    [InlineData("RunFlaskChargeRegen")]
    [InlineData("RunUtilityFlasks")]
    [InlineData("RunFlaskGrounds")]
    [InlineData("SendRage")]
    public async Task Character_retired_continuous_stat_publishers_finish_before_the_old_socket_closes(string method)
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(level: 60);
        var older = OlderStatObserver(game);
        var published = Program.InactiveItems[game.Character];
        older.RageRetired = true;
        await game.StatLifecycleSession(method, older, async task =>
        {
            await task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(older.Closed);
            Assert.Same(published, Program.InactiveItems[game.Character]);
        });
    }

    [Fact]
    public async Task Character_player_tick_skips_temporarily_leaving_sources_until_retirement()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(level: 60);
        var older = OlderStatObserver(game);
        var published = Program.InactiveItems[game.Character];
        older.LeavingArea = true;
        await game.StatLifecycleSession("RunPlayerTick", older, async task =>
        {
            await Task.Delay(300);
            Assert.False(task.IsCompleted);
            Assert.Same(published, Program.InactiveItems[game.Character]);
            EndStatLifecycle(older, "retired");
            await task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(older.Closed);
        });
    }

    sealed partial class GatewayCharacter
    {
        public Task<(string? Error, byte[] Bytes)> StatLifecycleExchange(
            Func<System.Net.Sockets.NetworkStream, ConnectionCiphers, Task> inspect) => Exchange(async (stream, cipher) =>
            {
                await inspect(stream, cipher);
                return null;
            });

        public Task<(string? Error, byte[] Bytes)> StatLifecycleSession(string method, ClientState state,
            Func<Task, Task> inspect) => StatLifecycleExchange(async (stream, cipher) =>
            {
                object?[] args = ["continuous-stat-lifecycle", stream, cipher, state];
                if (method == "RunUtilityFlasks") args = [.. args, 0];
                var task = ProgramValue<Task>(method, args);
                try { await inspect(task); }
                finally
                {
                    state.Closed = true;
                    await task.WaitAsync(TimeSpan.FromSeconds(2));
                }
            });
    }
}
