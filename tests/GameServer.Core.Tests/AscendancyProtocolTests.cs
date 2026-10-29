using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class AscendancyProtocolTests
{
    [Fact]
    public void Class_identity_is_shared_by_the_player_blob_and_passive_packet_while_budgets_stay_separate()
    {
        byte packed = CharacterClassByte.Encode(4, 1);
        byte[] ordinary = new PlayerObject("Duelist", 4, 32, 32, Life: 50, Mana: 40).Blob();
        byte[] ascended = new PlayerObject("Duelist", packed, 32, 32, Life: 50, Mana: 40).Blob();
        Assert.Equal(ordinary.Length, ascended.Length);
        int difference = Assert.Single(Enumerable.Range(0, ordinary.Length), i => ordinary[i] != ascended[i]);
        Assert.Equal((byte)4, ordinary[difference]);
        Assert.Equal(packed, ascended[difference]);

        byte[] packet = InstanceEntities.CharacterPassives(10, packed, [54114, 39725],
            questPoints: 2, refundPoints: 15, ascendancyPoints: 8);
        Assert.Equal((ushort)15, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4)));
        Assert.Equal((ushort)8, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6)));
        Assert.Equal((byte)10, packet[8]);
        Assert.Equal(packed, packet[9]);
        Assert.Equal((byte)0, packet[10]); // Secondary ascendancy is independent of family.
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(12)));
        Assert.Equal((ushort)39725, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(14)));
        Assert.Equal((ushort)54114, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(16)));
    }
}
