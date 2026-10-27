using System.Collections;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public partial class RecentEnemyHitGatewayTests
{
    // Pause a real stat-source read after a full snapshot. Only this fixture's raw node list is substituted;
    // production clocks, stat builders, acknowledgment, aura reconciliation and send path are unchanged.
    sealed class BoundaryNodes(Action beforeRead) : IReadOnlyList<ushort>
    {
        public int Count => 1;
        public ushort this[int index]
        {
            get
            {
                Assert.Equal(0, index);
                beforeRead();
                return 2103;
            }
        }
        public IEnumerator<ushort> GetEnumerator() { beforeRead(); yield return 2103; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    static uint ReadStatCompact(ref LoginReader reader, bool signed = false)
    {
        byte first = reader.U8();
        if (first >= 0xf0) { Assert.Equal(0xf0, first); return reader.U32(); }
        int following = first < 0x80 ? 0 : first < 0xc0 ? 1 : first < 0xe0 ? 2 : 3;
        int bits = 7 + following * 7;
        uint value = (uint)(first & (0x7f >> following));
        for (int i = 0; i < following; i++) value = value << 8 | reader.U8();
        if (signed && (value & (1u << (bits - 1))) != 0) value |= uint.MaxValue << bits;
        return value;
    }

    static List<Dictionary<uint, int>> DecodePlayerStatPackets(byte[] bytes)
    {
        byte[] header = InstanceEntities.StatsUpdate(Program.PlayerId, [])[..12];
        var packets = new List<Dictionary<uint, int>>();
        int scan = 0;
        while (bytes.AsSpan(scan).IndexOf(header) is int offset && offset >= 0)
        {
            int at = scan + offset + header.Length;
            var reader = new LoginReader(bytes.AsSpan(at));
            uint count = ReadStatCompact(ref reader);
            Assert.InRange(count, 1u, 10000u);
            var stats = new Dictionary<uint, int>();
            for (uint i = 0; i < count; i++)
            {
                uint key = ReadStatCompact(ref reader);
                Assert.True(stats.TryAdd(key, unchecked((int)ReadStatCompact(ref reader, signed: true))));
            }
            Assert.False(reader.Short);
            packets.Add(stats);
            scan = at + reader.Position;
        }
        return packets;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Dirty_aura_and_Fortification_snapshots_acknowledge_wire_order_across_recent_expiry(
        int crossAfterFullSnapshot)
    {
        using var f = new Fixture();
        f.Conn.PlayerObjectCreated = true;
        var aura = new AuraCatalog.Aura(500, Program.Skills.EffectRow("PurityOfFire"), "PurityOfFire",
            "Purity of Fire", 1000, AuraCatalog.BuffRow("PurityOfFire"), 0, 0, Level: 20);
        f.Conn.Auras[500] = aura;
        f.Conn.PresentedAuras[500] = aura;
        f.Conn.Pools = Program.PoolsFor(f.Conn, 100);
        f.Conn.PlayerLife = f.Conn.Pools.Life;
        _ = f.Chance;
        Assert.Null(f.Conn.PresentedRecentEnemyHits);
        var session = Program.FortificationOf(f.Conn);
        double contact = 0;
        bool crossed = false;
        int completedSnapshots = 0;
        var previousKeys = f.Conn.SentStatKeys;
        f.Conn.MainAscendancy = new(Nodes: new BoundaryNodes(() =>
        {
            if (crossed || ReferenceEquals(previousKeys, f.Conn.SentStatKeys)) return;
            previousKeys = f.Conn.SentStatKeys;
            if (++completedSnapshots != crossAfterFullSnapshot) return;
            // A new SentStatKeys set witnesses completion of a full player snapshot, before the next skill build.
            Assert.Equal((1, 10), f.Conn.PresentedRecentEnemyHits);
            Assert.True(f.Conn.Clock.Elapsed.TotalSeconds - contact < 4);
            while (f.Conn.Clock.Elapsed.TotalSeconds - contact < 4.02) Thread.Sleep(1);
            crossed = true;
        }));
        f.Set("base_your_auras_are_disabled", 1); // Reconciliation suspends the installed aura and dirties its stats.
        contact = f.Conn.Clock.Elapsed.TotalSeconds - 3;
        f.Conn.RecentEnemyHits.Record(contact);
        // This short grant expires during the forced wait, but Capture must preserve it in BOTH full snapshots.
        session.State.GrantExplicitStacks(f.Conn.Clock.Elapsed.TotalSeconds, 1, -99);
        await Program.SendFortification("recent-fort-aura-boundary", f.Stream, f.Ciphers, f.Conn)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(crossed);
        Assert.Equal(crossAfterFullSnapshot, completedSnapshots);
        Assert.Empty(f.Conn.Auras);
        Assert.Empty(f.Conn.PresentedAuras);
        byte[] wire = await f.Read();
        Assert.True(wire.AsSpan().StartsWith(AuraCatalog.EndPackets(aura, Program.PlayerId)
            .SelectMany(p => p).ToArray()));
        var full = DecodePlayerStatPackets(wire);
        Assert.Equal(2, full.Count);
        Assert.All(full, stats => Assert.Equal(1, stats[Key(FortificationSession.Current)]));
        var pairs = full.Select(stats => (Count: stats[4534], Chance: stats[12268])).ToArray();
        var remembered = f.Conn.PresentedRecentEnemyHits;
        await f.Present();
        byte[] idle = await f.Read();
        // Acknowledgment follows the last full packet on the wire, including an expiry during skill construction.
        Assert.Equal(remembered, pairs[^1]);
        Assert.Equal(new[] { (1, 10), crossAfterFullSnapshot == 1 ? (0, 0) : (1, 10) }, pairs);
        if (crossAfterFullSnapshot == 1) Assert.Empty(idle);
        else
        {
            var correction = Assert.Single(DecodePlayerStatPackets(idle));
            Assert.Equal(0, correction[4534]);
            Assert.Equal(0, correction[12268]);
        }
        Assert.Equal((0, 0), f.Conn.PresentedRecentEnemyHits);
        Assert.Equal(0, f.Count);
    }
}
