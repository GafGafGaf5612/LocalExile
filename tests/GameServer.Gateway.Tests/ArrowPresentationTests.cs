using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// How an ordinary arrow looks to the client: created where it left the bow, one move at its speed, the hit packet on
// its target, and a second stuck in that target before it goes.
public class ArrowPresentationTests
{
    const uint TargetId = 42;

    static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }

    static byte[] ObjectHeader(ushort id, uint objectId) => new LoginWriter().U16(id).U32(objectId).U32(0).U16(0).ToArray();

    static byte[] ArrowBlob(int x, int y) =>
        new ProjectileObject("Metadata/Projectiles/DefaultArrow", x, y, (float)Math.Atan2(1, -0.0), Team: 0, Height: -5).Blob();

    [Fact]
    public async Task The_arrow_object_is_created_at_the_release_point_and_moves_from_there_without_a_stop()
    {
        await using var f = new ProjectileRuntimeFixture();
        uint id = f.Conn.NextObjectId;
        // The output waits, as a busy socket would, so the snapshots are taken after the simulation moved on.
        await f.HoldOutput();
        f.Release();          // (10, 10) toward (110, 10) at 10 cells per second
        f.Advance(0.3);       // three cells downrange by the time the packets are written
        f.ReleaseOutput();
        byte[] wire = await f.Read();

        Assert.True(IndexOf(wire, ArrowBlob(10, 10)) >= 0, "created where it was released");
        Assert.Equal(-1, IndexOf(wire, ArrowBlob(13, 10)));
        Assert.Equal(-1, IndexOf(wire, ObjectHeader(InstanceProtocol.TeleportId, id)));
        Assert.True(IndexOf(wire, ObjectHeader(InstanceProtocol.MoveToId, id)) >= 0, "one move to the flight's end");
    }

    [Fact]
    public async Task An_arrow_sends_the_hit_packet_and_stays_in_its_target_for_a_second()
    {
        await using var f = new ProjectileRuntimeFixture();
        f.Conn.Monsters.Add(TargetId, ProjectileRuntimeFixture.Monster(50, 10));
        uint id = f.Conn.NextObjectId;
        var shot = f.Shot(offense: new ProjectileOffense(DamagePacket.Of(DamageType.Physical, 10, 10), false, int.MaxValue, 0, 150, "ordinary bow")
            { Presentation = Program.OrdinaryArrowPresentation, HitPacket = true, ActionSequence = 0x1234 });
        f.Release(shot);
        await f.Read();       // the arrow object exists before its flight ends (an ended visual is never created)
        f.Advance(4.5);       // contact at x = 50 after 4 s
        byte[] contact = await f.Read();

        // Blood and particles of a physical hit (code 0) for the shot's action, from the arrow's direction (east = 64).
        byte[] expectedHit = InstanceProtocol.ActionHit(Program.PlayerId, 0x1234, TargetId, flags: 0x4001, kind: 0x03, code: 0, angle: 64);
        int header = IndexOf(contact, ObjectHeader(InstanceProtocol.ActionHitId, Program.PlayerId));
        Assert.True(IndexOf(contact, expectedHit) >= 0, header < 0 ? "no 0x1af at all"
            : $"0x1af differs: {Convert.ToHexString(contact.AsSpan(header, Math.Min(expectedHit.Length, contact.Length - header)))} vs {Convert.ToHexString(expectedHit)}");
        Assert.True(IndexOf(contact, InstanceProtocol.LifeUpdate(TargetId, 90)) >= 0);
        Assert.True(IndexOf(contact, ObjectHeader(InstanceProtocol.TeleportId, id)) >= 0, "the arrow stops where it hit");
        Assert.Equal(-1, IndexOf(contact, InstanceProtocol.RemoveObject(id)));

        f.Advance(4.5 + OrdinaryRangedAttack.ArrowStuckSeconds + 0.1);
        byte[] later = await f.Read();
        Assert.True(IndexOf(later, InstanceProtocol.RemoveObject(id)) >= 0, "removed after its second in the target");
    }
}
