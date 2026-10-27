using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// Live flights through Program.FlyProjectile: a piercing projectile's contacts are observed during flight, never
// selected at launch, and pause/transfer still govern the flight.
public class PiercingProjectileTests
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

    static Task Flight(Wire wire, ClientState conn, Func<SpawnedMonster, (int Damage, bool Critical)> damage,
        int pierceHits = 2) => Program.FlyProjectile("pierce-test", wire.Writer.GetStream(), wire.Ciphers, conn,
            ("Metadata/Projectiles/DefaultArrow", 50), (10, 10), (210, 10), damage, conn.ProjectileEpoch,
            log: false, impact: "none", pierceHits: pierceHits);

    static SpawnedMonster Monster(int x, int y) =>
        new("target", new(x, y, 10, width: 300, height: 300), 0, 2, 100);

    [Fact]
    public async Task A_piercing_projectile_does_not_hit_a_target_that_leaves_the_route_before_contact()
    {
        using var wire = new Wire();
        var conn = Connection();
        var target = Monster(110, 10);
        conn.Monsters.Add(42, target);
        int hits = 0;
        var flight = Flight(wire, conn, _ => { hits++; return (10, false); });
        await Task.Delay(300);
        lock (conn.Monsters) target.Walk.Teleport(110, 150, conn.Clock.Elapsed.TotalSeconds);
        await flight.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, hits);
        Assert.Equal(100u, target.Life);
    }

    [Fact]
    public async Task A_piercing_projectile_hits_a_target_that_enters_the_route_after_launch()
    {
        using var wire = new Wire();
        var conn = Connection();
        var target = Monster(110, 150);
        conn.Monsters.Add(42, target);
        int hits = 0;
        var flight = Flight(wire, conn, _ => { hits++; return (10, false); });
        await Task.Delay(300);
        lock (conn.Monsters) target.Walk.Teleport(110, 10, conn.Clock.Elapsed.TotalSeconds);
        await flight.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, hits);
        Assert.Equal(90u, target.Life);
    }

    [Fact]
    public async Task A_paused_piercing_flight_waits_and_hits_only_after_resume()
    {
        using var wire = new Wire();
        var conn = Connection();
        var target = Monster(60, 10);
        conn.Monsters.Add(42, target);
        int hits = 0;
        var flight = Flight(wire, conn, _ => { hits++; return (10, false); });
        await Task.Delay(150);
        conn.Clock.Stop();
        await Task.Delay(300);
        Assert.Equal(0, hits);
        Assert.Equal(100u, target.Life);
        conn.Clock.Start();
        await flight.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, hits);
        Assert.Equal(90u, target.Life);
    }

    [Fact]
    public async Task A_transfer_cancels_a_piercing_flight_before_its_contact()
    {
        using var wire = new Wire();
        var conn = Connection();
        var target = Monster(40, 10);
        conn.Monsters.Add(42, target);
        int hits = 0;
        var flight = Flight(wire, conn, _ => { hits++; return (10, false); });
        await Task.Delay(150);
        lock (conn.Monsters) Assert.True(conn.TryBeginAreaChange(new PendingTransfer(2, 0, false), out _));
        await flight.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, hits);
        Assert.Equal(100u, target.Life);
        Assert.Empty(conn.ProjectileObjects);
    }
}
