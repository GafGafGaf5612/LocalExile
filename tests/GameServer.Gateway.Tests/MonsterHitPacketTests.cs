using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// S→C 0x1af for a monster's hit on the player carries the outcome's presentation and is processed at once (kind 3):
// the 3.29.3b decode shows kind 2 only queues the package behind an action with the same sequence, which the fabricated
// 0xffff never guarantees, and a 0x0400 stun bit would ask the client for a second stun beside the authoritative 0x1d1.
public class MonsterHitPacketTests
{
    const uint MonsterId = 0x3000;

    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        public readonly TcpClient Writer = new(), Reader;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], isClient: false);
        readonly ConnectionCiphers decrypt = new(new byte[64], isClient: true);

        public Wire()
        {
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            Writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            Reader = accepting.GetAwaiter().GetResult();
        }

        public async Task<byte[]> Read(int length)
        {
            byte[] bytes = new byte[length];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Reader.GetStream().ReadExactlyAsync(bytes, timeout.Token);
            decrypt.Receive.Process(bytes);
            return bytes;
        }

        public void Dispose() { Writer.Dispose(); Reader.Dispose(); listener.Stop(); }
    }

    static (ClientState Connection, SpawnedMonster Monster) State(DamageType type, int blockChance = 0)
    {
        var conn = new ClientState(new AreaLayout(1, "test", 300, 300, 10, 10, null, []), 1, 1) { PlayerLife = 50 };
        conn.Pools = conn.Pools with { BlockChance = blockChance, BlockChanceMax = blockChance };
        var monster = new SpawnedMonster("test monster", new WalkEstimator(10, 10, 30, null, 300, 300), 60, 3, 100)
        {
            Attack = new MonsterAttack(0x102, 0.4, 10, DamagePacket.Of(type, 10, 10)),
        };
        conn.Monsters.Add(MonsterId, monster);
        return (conn, monster);
    }

    [Theory]
    [InlineData(DamageType.Physical, 0, 0x4201, 0)]
    [InlineData(DamageType.Fire, 0, 0x4201, 2)]
    [InlineData(DamageType.Cold, 0, 0x4201, 1)]
    [InlineData(DamageType.Lightning, 0, 0x4201, 3)]
    [InlineData(DamageType.Chaos, 0, 0x4201, 4)]
    [InlineData(DamageType.Physical, 100, 0x4241, 0)]
    public async Task A_monster_hit_is_an_immediate_hit_packet_with_outcome_flags_and_its_damage_type(DamageType type,
        int blockChance, int flags, int code)
    {
        using var wire = new Wire();
        var (conn, m) = State(type, blockChance);
        var contact = Program.MonsterContact("hit-packet-test", wire.Writer.GetStream(), wire.Ciphers, conn, MonsterId, m, m.ActionGeneration);

        byte[] expected = InstanceProtocol.ActionHit(MonsterId, 0xffff, Program.PlayerId, flags: (ushort)flags, kind: 0x03, code: (byte)code);
        byte[] hit = await wire.Read(expected.Length);
        // Everything but the trailing angle byte, which depends on the two positions.
        Assert.Equal(expected[..^1], hit[..^1]);
        await contact.WaitAsync(TimeSpan.FromSeconds(5));
        if (blockChance == 100) Assert.Equal(50u, conn.PlayerLife);
        else Assert.True(conn.PlayerLife < 50);
    }

    [Theory]
    [InlineData(DamageType.Physical, DamageType.Fire, 3, 0)]
    [InlineData(DamageType.Physical, DamageType.Fire, 20, 2)]
    public void The_type_code_is_the_largest_damage_component(DamageType first, DamageType second, int secondAmount, int code)
    {
        var packet = DamagePacket.Of(first, 10, 10).Add(second, new(secondAmount, secondAmount));
        Assert.Equal((byte)code, Program.HitCode(packet));
    }
}
