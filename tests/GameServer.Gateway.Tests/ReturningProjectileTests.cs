using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// A returning projectile's damage is swept as the client's projectile actually travels back, not applied in a batch
// when the turn happens at the impact.
public class ReturningProjectileTests
{
    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        public readonly TcpClient Writer = new(), Reader;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], isClient: false);
        public Wire()
        {
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            Writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            Reader = accepting.GetAwaiter().GetResult();
        }
        public void Dispose() { Writer.Dispose(); Reader.Dispose(); listener.Stop(); }
    }

    static ClientState Connection() => new(new AreaLayout(1, "test", 300, 300, 10, 10, null, []), 1, 1)
        { PlayerLife = 100, ActionCount = 7 };

    [Fact]
    public async Task Return_damage_waits_until_the_projectile_reaches_a_target_behind_the_caster()
    {
        using var wire = new Wire();
        var conn = Connection();
        // Behind the player (the caster stands at 10,10) so the outbound leg never touches it.
        var target = new SpawnedMonster("target", new(0, 10, 10, width: 300, height: 300), 0, 2, 100);
        conn.Monsters.Add(42, target);
        double hitAt = -1;
        var flight = Program.FlyProjectile("return-test", wire.Writer.GetStream(), wire.Ciphers, conn,
            ("Metadata/Projectiles/DefaultArrow", 50), (10, 10), (210, 10), damageTo: null, conn.ProjectileEpoch,
            log: false, impact: "none", returning: true,
            returnDamageTo: (_, _) => { hitAt = conn.Clock.Elapsed.TotalSeconds; return (10, false); });
        await flight.WaitAsync(TimeSpan.FromSeconds(10));
        // Outbound ~2.36 s (118 cells) and 130 cells back to the target at 50 cells/s (~2.6 s): not before ~4.9 s.
        // Only the lower bound is asserted: the upper one depends on wall-clock scheduling under a parallel test run.
        Assert.True(hitAt >= 4.6, $"return hit at {hitAt:0.00} s, before the projectile travelled back");
        Assert.Equal(90u, target.Life);
    }
}
