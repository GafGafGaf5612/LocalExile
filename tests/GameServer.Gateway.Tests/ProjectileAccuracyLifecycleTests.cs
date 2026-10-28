using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class ProjectileAccuracyLifecycleTests
{
    sealed class Draws(params int[] values) : Random
    {
        public int Calls;
        public override int Next(int maxValue)
        {
            Assert.Equal(100, maxValue);
            Assert.True(Calls < values.Length, "Unexpected accuracy draw");
            return values[Calls++];
        }
    }

    sealed class Flight : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new(), reader;
        readonly ConnectionCiphers ciphers = new(new byte[64], isClient: false);
        public readonly ClientState Conn = new(new(ushort.MaxValue, "accuracy flight", 400, 400, 100, 100, null, []), 1, 1)
            { PlayerLife = 100 };
        public readonly ConcurrentDictionary<uint, byte> Hits = new();
        public readonly List<uint> Impacts = [];
        public readonly List<SpawnedMonster> Damaged = [];
        public SemaphoreSlim Gate => Program.SendGates.GetValue(ciphers, _ => new(1, 1));
        public Flight()
        {
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
        }
        public SpawnedMonster Target(uint id, int x)
        {
            var target = new SpawnedMonster("accuracy target", new(x, 100, 10, width: 400, height: 400), 0, 0, 100);
            Conn.Monsters.Add(id, target);
            return target;
        }
        public Task Start(int pierce = 1, bool returning = false, bool blockFirst = false, float speed = 200)
        {
            (int, bool) Damage(SpawnedMonster m, int chance)
            {
                Assert.Equal(5, chance);
                Damaged.Add(m);
                return (blockFirst && Damaged.Count == 1 ? Program.Blocked : 10, false);
            }
            Task Impact((int X, int Y) _, uint id) { Impacts.Add(id); return Task.CompletedTask; }
            return Program.FlyProjectile("accuracy-flight", writer.GetStream(), ciphers, Conn,
                ("Metadata/Projectiles/DefaultArrow", speed), (100, 100), (300, 100), Damage, Conn.ProjectileEpoch,
                log: false, impact: "none", shotgunned: returning ? null : Hits, onImpact: returning ? null : Impact,
                impactOnLanding: false, pierceHits: pierce, returning: returning,
                returnDamageTo: Damage, returnExplosion: Impact, returnShotgunned: returning ? Hits : null,
                attackAccuracy: () => new(0));
        }
        public async Task ReadLaunch()
        {
            int create = InstanceEntities.CreateObject(0x3000, 0, new ProjectileObject("arrow", 0, 0, Height: 0).Blob()).Length;
            int wake = InstanceProtocol.EnterWakeZone(0x3000).Length;
            int move = InstanceProtocol.MoveTo(0x3000, 0, 0, 1).Length;
            byte[] bytes = new byte[create + wake + move];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await reader.GetStream().ReadExactlyAsync(bytes, timeout.Token);
            new ConnectionCiphers(new byte[64], isClient: true).Receive.Process(bytes);
            Assert.Equal(new byte[] { 0x01, 0xba }, bytes.AsSpan(create + wake, 2).ToArray());
        }
        public void Dispose()
        {
            Conn.Closed = true;
            writer.Dispose(); reader.Dispose(); listener.Stop();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Evaded_projectile_contacts_spend_no_pierce_budget_and_trigger_no_impact(bool returning)
    {
        using var f = new Flight();
        var miss = f.Target(42, returning ? 90 : 130);
        var second = f.Target(43, returning ? 60 : 160);
        var third = f.Target(44, returning ? 30 : 190);
        miss.Impale.Apply(100, now: 0);
        var random = new Draws(0, 99, 99);
        f.Conn.AttackAccuracyRandom = random;
        await f.Start(pierce: 2, returning: returning).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(100u, miss.Life);
        Assert.Equal(90u, second.Life);
        Assert.Equal(90u, third.Life);
        Assert.Equal(5, miss.AttackEvasion.Value);
        Assert.Equal(4, second.AttackEvasion.Value);
        Assert.Equal(4, third.AttackEvasion.Value);
        Assert.Equal(3, random.Calls);
        Assert.Equal(new uint[] { 43, 44 }, f.Impacts);
        Assert.Equal(new[] { second, third }, f.Damaged);
        Assert.False(f.Hits.ContainsKey(42));
        Assert.True(f.Hits.ContainsKey(43));
        Assert.True(f.Hits.ContainsKey(44));
        Assert.Equal(10, miss.Impale.Bonus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Blocked_projectile_contacts_spend_pierce_budget_after_evasion(bool returning)
    {
        using var f = new Flight();
        var blocked = f.Target(42, returning ? 90 : 130);
        var second = f.Target(43, returning ? 60 : 160);
        var untouched = f.Target(44, returning ? 30 : 190);
        var random = new Draws(99, 99);
        f.Conn.AttackAccuracyRandom = random;
        await f.Start(pierce: 2, returning: returning, blockFirst: true).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(100u, blocked.Life);
        Assert.Equal(90u, second.Life);
        Assert.Equal(100u, untouched.Life);
        Assert.Equal(-1, untouched.AttackEvasion.Value);
        Assert.Equal(new uint[] { 42, 43 }, f.Impacts);
        Assert.Equal(2, random.Calls);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task Evaded_contact_does_not_reserve_the_target_for_later_projectiles(int pierce, bool returning)
    {
        using var f = new Flight();
        var target = f.Target(42, returning ? 90 : 130);
        // Either continuing 95 entropy or an inactivity reseed must let the second projectile hit.
        // Wall-clock flight scheduling is not the entropy-expiry contract (covered with explicit times in Core).
        var random = new Draws(90, 99);
        f.Conn.AttackAccuracyRandom = random;
        await f.Start(pierce, returning).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(100u, target.Life);
        Assert.Empty(f.Hits);
        Assert.Empty(f.Impacts);
        await f.Start(pierce, returning).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(90u, target.Life);
        Assert.True(f.Hits.ContainsKey(42));
        Assert.Equal(new uint[] { 42 }, f.Impacts);
        Assert.InRange(random.Calls, 1, 2);
    }

    [Theory]
    [InlineData(1, "pause")]
    [InlineData(2, "pause")]
    [InlineData(1, "transfer")]
    [InlineData(2, "transfer")]
    [InlineData(1, "target-death")]
    [InlineData(2, "target-death")]
    public async Task Queued_projectile_contact_checks_current_admission_before_accuracy(int pierce, string change)
    {
        using var f = new Flight();
        var target = f.Target(42, 130);
        var random = new Draws(99);
        f.Conn.AttackAccuracyRandom = random;
        var flight = f.Start(pierce: pierce, speed: 50);
        await f.ReadLaunch();
        await f.Gate.WaitAsync();
        try
        {
            // Let the flight's sample queue behind the held gate after passing the target's contact time.
            await Task.Delay(650);
            Assert.Equal(0, random.Calls);
            Assert.Equal(100u, target.Life);
            lock (f.Conn.Monsters)
            {
                if (change == "pause") f.Conn.Clock.Stop();
                if (change == "transfer") Assert.True(f.Conn.TryBeginAreaChange(new PendingTransfer(2, 0, false), out _));
                if (change == "target-death") { target.Dead = true; target.Life = 0; }
            }
        }
        finally { f.Gate.Release(); }
        if (change == "pause")
        {
            await Task.Delay(80);
            Assert.Equal(0, random.Calls);
            Assert.Equal(100u, target.Life);
            f.Conn.Clock.Start();
        }
        await flight.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(change == "pause" ? 1 : 0, random.Calls);
        Assert.Equal(change == "pause" ? 90u : change == "target-death" ? 0u : 100u, target.Life);
        Assert.Equal(change == "pause" ? 1 : 0, f.Damaged.Count);
    }
}
