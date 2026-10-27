using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class OrdinaryWandAttackTests
{
    // Observe the real serialized write while the send gate still belongs to it. A buffered reader may resume long
    // after contact, so its continuation is not a valid witness of the target's life at projectile creation.
    sealed class ObservedStream(Socket socket) : NetworkStream(socket, ownsSocket: false)
    {
        readonly ConnectionCiphers observer = new(new byte[64], isClient: true);
        public readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> Writes = new();
        public Action<byte[]>? AfterWrite;

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            byte[] plain = buffer.ToArray();
            observer.Receive.Process(plain);
            await base.WriteAsync(buffer, cancellationToken);
            Writes.Enqueue(plain);
            AfterWrite?.Invoke(plain);
        }
    }

    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly ConnectionCiphers incoming = new(new byte[64], isClient: true);
        public readonly TcpClient Writer = new(), Reader;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], isClient: false);
        public readonly ObservedStream Stream;

        public Wire()
        {
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            Writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            Reader = accepting.GetAwaiter().GetResult();
            Stream = new(Writer.Client);
        }

        public async Task<byte[]> Read(int count, CancellationToken cancellation)
        {
            byte[] bytes = new byte[count];
            await Reader.GetStream().ReadExactlyAsync(bytes, cancellation);
            incoming.Receive.Process(bytes);
            return bytes;
        }

        public void Dispose() { Stream.Dispose(); Writer.Dispose(); Reader.Dispose(); listener.Stop(); }
    }

    static (ClientState Conn, string Character) EquippedWand()
    {
        TestSaves.Ensure();
        string character = $"Wand{Guid.NewGuid():N}";
        var conn = new ClientState(new AreaLayout(1, "wand-test", 300, 300, 10, 10, null, []), 1, 1)
        {
            Character = character, PlayerLife = 100, ActionCount = 7,
            Pools = new CharacterStats.Pools(1, 100, 100, int.MaxValue, 0),
        };
        var type = Assert.IsType<ItemCatalog.BaseType>(Program.Items.Find("Driftwood Wand"));
        PlayerInventories.For(character, Program.Items).Add(PlayerInventories.Weapon1, 0, 0,
            new(type.Hash, type.Components, type.VisualIdentity));
        return (conn, character);
    }

    static SpawnedMonster Target(int x) => new("target", new(x, 10, 10, width: 300, height: 300), 0, 2, 1000);

    static async Task<uint> AssertWandLaunch(Wire wire, CancellationToken cancellation)
    {
        int create = InstanceEntities.CreateObject(0x3000, 0, new ProjectileObject(OrdinaryRangedAttack.Wand, 0, 0, Height: 0).Blob()).Length;
        int wake = InstanceProtocol.EnterWakeZone(0x3000).Length;
        int move = InstanceProtocol.MoveTo(0x3000, 0, 0, 1).Length;
        byte[] packets = await wire.Read(create + wake + move, cancellation);
        Assert.Equal(InstanceEntities.CreateObjectId, BinaryPrimitives.ReadUInt16BigEndian(packets));
        Assert.Equal(InstanceEntities.ObjectTypeHash(OrdinaryRangedAttack.Wand),
            BinaryPrimitives.ReadUInt32BigEndian(packets.AsSpan(12)));
        Assert.Equal(new byte[] { 0x01, 0xba }, packets.AsSpan(create + wake, 2).ToArray());
        return BinaryPrimitives.ReadUInt32BigEndian(packets.AsSpan(2));
    }

    [Fact]
    public void Ordinary_projectile_selection_and_wand_release_use_the_extracted_native_assets()
    {
        var wand = new WeaponProfile(false, false, false, false, false, Wand: true);
        Assert.Equal(OrdinaryRangedAttack.Wand, OrdinaryRangedAttack.ProjectileId(wand));
        Assert.Equal(OrdinaryRangedAttack.Arrow, OrdinaryRangedAttack.ProjectileId(wand with { Wand = false, Bow = true }));
        Assert.Null(OrdinaryRangedAttack.ProjectileId(wand with { Wand = false, Unarmed = true, Melee = true }));
        var projectile = Program.Skills.Projectile(OrdinaryRangedAttack.Wand);
        Assert.NotNull(projectile);
        Assert.Equal(3260, projectile.Value.Speed);
        string ao = Program.Skills.ClassFor(SkillCatalog.WitchRow).AnimatedObject;
        var animations = new AnimationContacts();
        Assert.Equal(0.5, animations.AttackContact(ao, wand), 6); // attack1_wand 300/600 ms.
        Assert.Equal(0.5, animations.AttackContact(ao, wand with { Shield = true }), 6);
        Assert.Equal(0.5, animations.AttackContact(ao, wand with { DualWield = true }), 6);
    }

    [Fact]
    public async Task Ground_default_wand_use_stays_in_place_and_damages_only_after_projectile_contact()
    {
        var (conn, character) = EquippedWand();
        using var wire = new Wire();
        var target = Target(100);
        conn.Monsters.Add(42, target);
        var created = new TaskCompletionSource<(uint Id, uint Life)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var damaged = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        uint projectileId = 0;
        wire.Stream.AfterWrite = plain =>
        {
            if (plain.Length < 6) return;
            ushort message = BinaryPrimitives.ReadUInt16BigEndian(plain);
            uint entity = BinaryPrimitives.ReadUInt32BigEndian(plain.AsSpan(2));
            if (plain.Length >= 16 && BinaryPrimitives.ReadUInt16BigEndian(plain) == InstanceEntities.CreateObjectId
                && BinaryPrimitives.ReadUInt32BigEndian(plain.AsSpan(12)) == InstanceEntities.ObjectTypeHash(OrdinaryRangedAttack.Wand))
            {
                projectileId = entity;
                created.TrySetResult((entity, target.Life));
            }
            if (message == InstanceProtocol.LifeUpdateId && entity == 42 && plain.Length >= 16)
                damaged.TrySetResult(BinaryPrimitives.ReadUInt32BigEndian(plain.AsSpan(12)));
            if (message == InstanceProtocol.RemoveObjectId && projectileId != 0 && entity == projectileId)
                removed.TrySetResult();
        };
        var packet = new LoginWriter().U16(InstanceProtocol.UseSkillAtLocationId).U32(110).U32(10)
            .U16(SkillCatalog.FirstSkillId).U16(0).U16(1).ToArray();
        try
        {
            await Program.OnClientMessages("wand-test", wire.Stream, wire.Ciphers,
                [(InstanceProtocol.UseSkillAtLocationId, packet)], [], conn).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal((10, 10), conn.Walk.Destination);
            // Each stage has its own deadline. Cold shared data initialization before dispatch must not consume the
            // projectile's terminal-lifetime budget, and completion is witnessed by writes, not HashSet polling.
            var launch = await created.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1000u, launch.Life);
            uint finalLife = await damaged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(finalLife < 1000);
            await removed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            byte[] action = InstanceProtocol.ActionStarted(Program.PlayerId, 10, 10, 110, 10, SkillCatalog.FirstSkillId, 0, 1);
            byte[][] writes = wire.Stream.Writes.ToArray();
            int actionAt = Array.FindIndex(writes, p => p.AsSpan().SequenceEqual(action));
            int createAt = Array.FindIndex(writes, p => p.Length >= 16
                && BinaryPrimitives.ReadUInt16BigEndian(p) == InstanceEntities.CreateObjectId
                && BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(2)) == launch.Id);
            int damageAt = Array.FindIndex(writes, p => p.AsSpan().SequenceEqual(InstanceProtocol.LifeUpdate(42, finalLife)));
            int removeAt = Array.FindIndex(writes, p => p.AsSpan().SequenceEqual(InstanceProtocol.RemoveObject(launch.Id)));
            Assert.True(actionAt >= 0 && createAt > actionAt);
            Assert.Equal(InstanceEntities.ObjectTypeHash(OrdinaryRangedAttack.Wand),
                BinaryPrimitives.ReadUInt32BigEndian(writes[createAt].AsSpan(12)));
            Assert.Equal(InstanceProtocol.EnterWakeZoneId, BinaryPrimitives.ReadUInt16BigEndian(writes[createAt + 1]));
            Assert.Equal(InstanceProtocol.MoveToId, BinaryPrimitives.ReadUInt16BigEndian(writes[createAt + 2]));
            Assert.True(damageAt > createAt + 2 && removeAt > damageAt);
            lock (conn.Monsters) Assert.DoesNotContain(launch.Id, conn.ProjectileObjects);
        }
        finally { conn.Closed = true; PlayerInventories.Forget(character); }
    }

    [Fact]
    public async Task Object_default_wand_use_aims_at_the_target_but_hits_the_first_actual_collision()
    {
        var (conn, character) = EquippedWand();
        using var wire = new Wire();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var selected = Target(110);
        var intervening = Target(60);
        conn.Monsters.Add(42, selected);
        conn.Monsters.Add(43, intervening);
        try
        {
            var attack = Program.Attack("wand-test", wire.Stream, wire.Ciphers, conn,
                new InstanceTransfer.UseSkillOnObject(42, SkillCatalog.FirstSkillId, 0, 0), selected, 0);
            byte[] action = InstanceProtocol.ActionStarted(Program.PlayerId, 10, 10, 110, 10,
                SkillCatalog.FirstSkillId, 0, 0, targetObjectId: 42);
            Assert.Equal(action, await wire.Read(action.Length, timeout.Token));
            uint projectileId = await AssertWandLaunch(wire, timeout.Token);
            await attack.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1000u, selected.Life);
            Assert.True(intervening.Life < 1000);
            Assert.Empty(conn.ProjectileObjects);
            byte[] remaining = await wire.Read(wire.Reader.Available, timeout.Token);
            int impact = remaining.AsSpan().IndexOf(InstanceProtocol.AnimationUpdate(projectileId, 0,
                OrdinaryRangedAttack.WandImpactAnimation, 1f));
            int removed = remaining.AsSpan().IndexOf(InstanceProtocol.RemoveObject(projectileId));
            Assert.True(impact >= 0); // The rig's native impact clip, with its own sound/particles.
            Assert.True(removed > impact);
        }
        finally { conn.Closed = true; PlayerInventories.Forget(character); }
    }

    [Theory]
    [InlineData("move")]
    [InlineData("death")]
    [InlineData("transfer")]
    public async Task A_default_wand_attack_cancelled_before_its_native_release_creates_no_projectile(string cancellation)
    {
        var (conn, character) = EquippedWand();
        using var wire = new Wire();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var target = Target(110);
        conn.Monsters.Add(42, target);
        wire.Stream.AfterWrite = plain =>
        {
            if (plain.Length < 2 || BinaryPrimitives.ReadUInt16BigEndian(plain) != InstanceProtocol.ActionStartedId) return;
            lock (conn.Monsters)
            {
                if (cancellation == "move") conn.ActionCount++;
                if (cancellation == "death") conn.PlayerLife = 0;
                if (cancellation == "transfer") conn.TryBeginAreaChange(new PendingTransfer(2, 0, false), out _);
            }
        };
        try
        {
            var attack = Program.Attack("wand-test", wire.Stream, wire.Ciphers, conn,
                new InstanceTransfer.UseSkillOnObject(42, SkillCatalog.FirstSkillId, 0, 0), target, 0);
            int actionLength = InstanceProtocol.ActionStarted(Program.PlayerId, 10, 10, 110, 10,
                SkillCatalog.FirstSkillId, 0, 0, targetObjectId: 42).Length;
            await wire.Read(actionLength, timeout.Token);
            await attack.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1000u, target.Life);
            Assert.Empty(conn.ProjectileObjects);
            Assert.Equal(0, wire.Reader.Available);
        }
        finally { conn.Closed = true; PlayerInventories.Forget(character); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wand_impact_tail_uses_game_time_and_transfer_invalidates_its_pending_removal(bool transfer)
    {
        using var wire = new Wire();
        var conn = new ClientState(new AreaLayout(1, "wand-tail-test", 300, 300, 10, 10, null, []), 1, 1)
            { PlayerLife = 100 };
        var target = Target(50);
        conn.Monsters.Add(42, target);
        var shot = new OrdinaryRangedShot(DamagePacket.Of(DamageType.Physical, 10, 10), int.MaxValue, 0, 150, 0);
        var impactWritten = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        var removalWritten = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        const uint projectileId = 0x3000;
        wire.Stream.AfterWrite = plain =>
        {
            if (plain.AsSpan().SequenceEqual(InstanceProtocol.AnimationUpdate(projectileId, 0,
                OrdinaryRangedAttack.WandImpactAnimation, 1f)))
            {
                // Pause inside the impact write itself, before FlyProjectile can begin its terminal wait.
                // This remains deterministic even if this test's awaiting continuation is scheduled much later.
                conn.Clock.Stop();
                impactWritten.TrySetResult(conn.Clock.Elapsed.TotalSeconds);
            }
            if (plain.AsSpan().SequenceEqual(InstanceProtocol.RemoveObject(projectileId)))
                removalWritten.TrySetResult(conn.Clock.Elapsed.TotalSeconds);
        };
        var flight = Program.FlyProjectile("wand-tail-test", wire.Stream, wire.Ciphers, conn,
            (OrdinaryRangedAttack.Wand, 100), (10, 10), (110, 10), null,
            conn.ProjectileEpoch, log: false, impact: "none", ordinaryShot: shot);
        try
        {
            double impactAt = await impactWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromSeconds(OrdinaryRangedAttack.WandImpactSeconds + 0.1));
            Assert.False(flight.IsCompleted);
            Assert.False(removalWritten.Task.IsCompleted);
            lock (conn.Monsters) Assert.Contains(projectileId, conn.ProjectileObjects);
            Assert.Equal(impactAt, conn.Clock.Elapsed.TotalSeconds, 6);

            if (transfer)
            {
                Assert.True(conn.TryBeginAreaChange(new PendingTransfer(2, 0, false), out var oldObjects));
                Assert.Contains(projectileId, oldObjects);
                await flight.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(removalWritten.Task.IsCompleted); // The obsolete tail cannot write into the new area.
            }
            else
            {
                conn.Clock.Start();
                await flight.WaitAsync(TimeSpan.FromSeconds(5));
                double removedAt = await removalWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(removedAt >= impactAt + OrdinaryRangedAttack.WandImpactSeconds);
            }
            Assert.Empty(conn.ProjectileObjects);
        }
        finally { conn.Closed = true; conn.Clock.Start(); }
    }
}
