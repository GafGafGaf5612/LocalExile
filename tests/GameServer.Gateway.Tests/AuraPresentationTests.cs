using System.Buffers.Binary;

public class AuraPresentationTests
{
    [Fact]
    public void Source_art_and_received_icon_have_distinct_ids_and_end_together()
    {
        var aura = new AuraCatalog.Aura(0x103, 0, "Vitality", "Vitality", 7, 153, 28, 0, SourceBuff: 8);
        var packets = AuraCatalog.StartPackets(aura, 0x1000, 0xcbc2).ToArray();
        Assert.Equal(2, packets.Length);
        Assert.Equal((ushort)8, BinaryPrimitives.ReadUInt16BigEndian(packets[0].AsSpan(12)));
        Assert.Equal(0x21, packets[0][22]); // source art + explicitly hidden source entry
        Assert.Equal("010000000000000000", Convert.ToHexString(packets[0].AsSpan(23, 9)));
        Assert.Equal((ushort)7, BinaryPrimitives.ReadUInt16BigEndian(packets[1].AsSpan(12)));
        Assert.Equal(0x08, packets[1][22]); // ordinary visible buff, linked to its skill
        Assert.Equal("CBC20103", Convert.ToHexString(packets[1].AsSpan(23, 4)));
        Assert.Equal(packets.Select(Convert.ToHexString), AuraCatalog.StartPackets(aura, 0x1000, 0xcbc2).Select(Convert.ToHexString));
        var removed = AuraCatalog.EndPackets(aura, 0x1000).ToArray();
        Assert.Equal(new ushort[] { 7, 8 }, removed.Select(p => BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(12))));
    }
}
