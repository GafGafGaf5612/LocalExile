using System.Diagnostics;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class RecentEnemyHitGatewayTests
{
    static async Task WaitFor(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), "Expected the actual route's collection witness.");
            await Task.Delay(10);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Full_send_acknowledges_recent_values_and_tick_corrects_return_to_previous_tuple(bool fortification)
    {
        using var f = new Fixture();
        _ = f.Chance;
        double first = f.Conn.Clock.Elapsed.TotalSeconds - 2;
        f.Conn.RecentEnemyHits.Record(first);
        await f.Present(); await f.Read();
        Assert.Equal((1, 10), f.Conn.PresentedRecentEnemyHits);
        f.Conn.RecentEnemyHits.Record(f.Conn.Clock.Elapsed.TotalSeconds);
        // Pure construction must not acknowledge a packet that was never sent.
        var built = (List<(uint, int)>)Method("PlayerStatsWithCleared").Invoke(null, [f.Conn])!;
        Assert.Contains((4534u, 2), built);
        Assert.Equal((1, 10), f.Conn.PresentedRecentEnemyHits);
        if (fortification)
        {
            Program.FortificationOf(f.Conn).State.GrantExplicitStacks(f.Conn.Clock.Elapsed.TotalSeconds, 1);
            await Program.SendFortification("recent-full-send", f.Stream, f.Ciphers, f.Conn);
        }
        else await (Task)Method("SendCharacterStats").Invoke(null,
            ["recent-full-send", f.Stream, f.Ciphers, f.Conn, "recent counter repro"])!;
        await f.Read();
        Assert.Equal((2, 20), f.Conn.PresentedRecentEnemyHits);
        await WaitFor(() => f.Conn.Clock.Elapsed.TotalSeconds - first >= 4.02);
        Assert.Equal(1, f.Count);
        await f.Present();
        Assert.Equal(InstanceEntities.StatsUpdate(Program.PlayerId, [(4534u, 1), (12268u, 10)]), await f.Read());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_AI_collection_keeps_its_epoch_across_queued_movement_and_owner_replacement(bool resurrect)
    {
        using var f = new Fixture();
        var random = new TraceRandom(); f.Conn.IncomingHitRandom = random;
        f.Conn.EvasionEntropy = 99; f.Conn.LastEvasionAt = f.Conn.Clock.Elapsed.TotalSeconds;
        var attacker = new SpawnedMonster("queued AI attacker", new(34, 32, 30), 30, 2, 1000)
        { Attack = new(SkillCatalog.FirstSkillId, .002, 20, DamagePacket.Of(DamageType.Physical, 100, 100)) };
        var mover = new SpawnedMonster("movement send witness", new(90, 32, 30), 100, 2, 1000);
        f.Conn.Monsters.Add(42, attacker); f.Conn.Monsters.Add(43, mover);
        await f.Gate.WaitAsync();
        bool held = true;
        var ai = (Task)Method("RunMonsterAi").Invoke(null, ["recent-ai-repro", f.Stream, f.Ciphers, f.Conn])!;
        try
        {
            await WaitFor(() =>
            {
                lock (f.Conn.Monsters) return attacker.BusyUntil > 0 && mover.LastTarget is not null;
            });
            (int X, int Y)? firstMove;
            Task death = Task.CompletedTask, revival = Task.CompletedTask;
            lock (f.Conn.Monsters)
            {
                firstMove = mover.LastTarget;
                attacker.BusyUntil = double.MaxValue; // No fresh swings may mask the collected one.
                if (resurrect)
                {
                    f.Conn.PlayerLife = 0;
                    death = (Task)Method("PlayerDied").Invoke(null,
                        ["recent-ai-repro", f.Stream, f.Ciphers, f.Conn, "queued AI death"] )!;
                    revival = (Task)Method("Resurrect").Invoke(null,
                        ["recent-ai-repro", f.Stream, f.Ciphers, f.Conn, false])!;
                }
                else
                {
                    Assert.True(f.Conn.TryBeginAreaChange(new(2, 0, false), out _));
                    f.Conn.PendingTransfer = null; f.Conn.ResumeAfterFailedTransfer();
                }
            }
            f.Gate.Release(); held = false;
            await Task.WhenAll(death, revival).WaitAsync(TimeSpan.FromSeconds(5));
            f.Conn.Walk.Teleport(40, 32, f.Conn.Clock.Elapsed.TotalSeconds);
            // A second movement collection proves the first iteration invoked its collected swings.
            await WaitFor(() =>
            {
                lock (f.Conn.Monsters) return mover.LastTarget != firstMove;
            });
            f.Conn.AiEnabled = false;
            await Program.Send("recent-ai-repro", f.Stream, f.Ciphers, InstanceProtocol.FrameEnd(), null);
            var bytes = await f.Read();
            var staleEcho = InstanceProtocol.ActionStarted(42, 34, 32, 32, 32, SkillCatalog.FirstSkillId,
                0xffff, 0x0408, targetObjectId: Program.PlayerId);
            Assert.True(bytes.AsSpan().IndexOf(staleEcho) < 0, "The AI emitted the old admission's swing echo.");
            Assert.Equal(f.Conn.PlayerMaxLife, f.Conn.PlayerLife); Assert.Equal(0, f.Count); Assert.Empty(random.Draws);
        }
        finally
        {
            f.Conn.Closed = true;
            if (held) f.Gate.Release();
            await ai.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_source_construction_crossing_expiry_keeps_the_sampled_contact_membership(bool monster)
    {
        using var f = new Fixture();
        _ = f.Chance;
        double previous = f.Conn.Clock.Elapsed.TotalSeconds - 3;
        f.Conn.RecentEnemyHits.Record(previous);
        bool crossed = false;
        var random = new TraceRandom(armourRoll: 0, blockRoll: .99);
        random.BeforeDouble = () =>
        {
            Assert.True(f.Conn.Clock.Elapsed.TotalSeconds - previous < 4);
            while (f.Conn.Clock.Elapsed.TotalSeconds - previous < 4.02) Thread.Sleep(1);
            // Use the existing incoming RNG seam to force a real source build across the sampled boundary.
            var later = Program.CharacterPlayerStats(f.Conn);
            Assert.Equal(0, later[Key(RecentEnemyHits.Counter)]);
            crossed = true;
        };
        f.Conn.IncomingHitRandom = random;
        if (monster)
        {
            f.Conn.EvasionEntropy = 99; f.Conn.LastEvasionAt = f.Conn.Clock.Elapsed.TotalSeconds;
            await f.Swing(f.Monster()).WaitAsync(TimeSpan.FromSeconds(5));
        }
        else await f.Command("/blockhit attack physical 100");
        Assert.True(crossed);
        Assert.Equal(9967u, f.Conn.PlayerLife);
        Assert.Equal(1, f.Count);
        Assert.Single(random.Draws, draw => draw == "next:100");
    }
}
