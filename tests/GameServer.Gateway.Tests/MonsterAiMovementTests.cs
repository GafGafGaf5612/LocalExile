using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class MonsterAiMovementTests
{
    [Theory]
    [InlineData(130, 100)]
    [InlineData(130, 130)]
    public async Task A_monster_without_a_collision_body_chases_without_taking_a_surround_slot(int x, int y)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var writer = new TcpClient();
        var accepting = listener.AcceptTcpClientAsync();
        await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var reader = await accepting;
        using var stream = writer.GetStream();
        var ciphers = new ConnectionCiphers(new byte[64], false);
        var conn = new ClientState(new(1, "MonsterAiMovementTest", 200, 200, 100, 100, (_, _) => true, []), 0, 1)
        {
            Instance = new(), PlayerLife = 100, PlayerSent = true,
        };
        var variety = new MonsterCatalog.Variety(0, "test", "test", "test", null, null, ObjectSize: 0);
        var monster = new SpawnedMonster("nonblocking chaser", conn.NewMonsterWalk(42, x, y, 30, -1, false), 100, 2, 100)
        {
            Variety = variety,
            Attack = new(0x102, 10, 8, DamagePacket.Of(DamageType.Physical, 1, 1)),
        };
        // A nonblocking chaser is allowed to pass through this living body on the direct approach.
        conn.Monsters[41] = new("standing body", new(110, 100, 1), 0, 2, 100) { BusyUntil = double.MaxValue };
        conn.Monsters[42] = monster;
        Assert.False(LivingCollision.Blocks(monster));
        var ai = (Task)typeof(Program).GetMethod("RunMonsterAi", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, ["monster-ai-movement-test", stream, ciphers, conn])!;
        try
        {
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                lock (conn.Monsters)
                    if (monster.LastTarget is not null) break;
                Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), "The nonblocking monster never received a chase order.");
                await Task.Delay(10);
            }
            lock (conn.Monsters)
            {
                Assert.True(monster.Aggro);
                var target = monster.LastTarget!.Value;
                Assert.InRange(Math.Max(Math.Abs(target.X - 100), Math.Abs(target.Y - 100)), 0, 1);
                Assert.Equal(target, monster.Walk.Position(monster.Walk.ArrivesAt + 1));
                Assert.DoesNotContain(conn.Blockers, b => b.X == x && b.Y == y);
            }
        }
        finally
        {
            conn.Closed = true;
            await ai.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
