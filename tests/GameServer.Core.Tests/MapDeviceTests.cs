using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class MapDeviceTests
{
    [Fact]
    public void Activate_is_0xd8_with_a_big_endian_target_and_an_index()
    {
        byte[] m = Convert.FromHexString("00D8012A03");
        Assert.Equal(InstanceMapDevice.ActivateLength, InstanceProtocol.KnownClientLength(InstanceMapDevice.ActivateId));
        Assert.Equal(((ushort)0x012a, (byte)3), InstanceMapDevice.ReadActivate(m));
        var split = InstanceProtocol.SplitClientMessages(Convert.FromHexString("00D8000000"), out var partial, out var unknown);
        Assert.Equal([InstanceMapDevice.ActivateId], split.Select(s => s.Id));
        Assert.Empty(partial);
        Assert.Empty(unknown);
    }

    [Fact]
    public void Map_portal_is_created_closed_and_opened_by_a_Transitionable_update()
    {
        string hex = Convert.ToHexString(new MapPortalObject(260, 300, 2).Blob());
        Assert.Equal("0000" + "04010000" + "2C010000" + "00000000" + "0000" + "00020001"   // flags, Positioned
            + "0400" + "0100" + "0300" + "0200" + "0000"                                       // StateMachine: 1, 3, 2, 0
            + "00" + "03" + "00" + "00" + "00" + "00", hex);                                   // Animated, Portal, Transitionable (closed), MinimapIcon, InteractionAction, tasks
        // Then opened: 0x2ba to component 5 (Transitionable), target state 1.
        Assert.Equal("02BA" + "00003003" + "00000000" + "0000" + "0002" + "05" + "01", Convert.ToHexString(MapPortalObject.OpenUpdate(0x3003)));
    }

    [Fact]
    public void A_portals_rotation_is_the_Positioned_f32_after_its_x_and_y()
    {
        // /portal_rotation (dev): the client turns the portal's visual by this, which a portal cosmetic needs to stand
        // upright. Flags (2 bytes), x, y, then the rotation f32.
        byte[] blob = new MapPortalObject(260, 300, 2, Rotation: MathF.PI / 2).Blob();
        Assert.Equal(0, blob[0]);
        Assert.Equal(260, BitConverter.ToInt32(blob, 2));
        Assert.Equal(300, BitConverter.ToInt32(blob, 6));
        Assert.Equal(MathF.PI / 2, BitConverter.ToSingle(blob, 10));
        Assert.Equal("0000" + "04010000" + "2C010000" + "00000000" + "0000" + "00020001" + "0400" + "0100" + "0300" + "0200" + "0000"
            + "00" + "03" + "00" + "00" + "00" + "00", Convert.ToHexString(new MapPortalObject(260, 300, 2).Blob()));
    }

    [Fact]
    public void Map_item_component_is_three_zero_bytes()
    {
        var plain = new Item(0x1234, ["Base", "Quality"], 0x10);
        var map = new Item(0x1234, ["Base", "Quality", "Map"], 0x10);
        Assert.True(Item.CanSerialize(map.Components));
        Assert.Equal(Convert.ToHexString(plain.Payload()) + "000000", Convert.ToHexString(map.Payload()));
    }

    [Fact]
    public void Portals_message_lists_each_portal_with_its_area_and_closes_with_area_zero()
    {
        string hex = Convert.ToHexString(InstanceMapDevice.Portals(0x3002, [new(0x3003, 0x8434, "Ab"), new(0x3004, 0)]));
        Assert.Equal("008F" + "00" + "00" + "00" + "00003002" + "0000" + "00" + "00" + "02"
            + "00003003" + "8434" + "0000" + "00" + "00" + "00" + "0002" + "4162"   // open: variation, u8 × 3, wstr "Ab"
            + "00003004" + "0000", hex);                                              // closed: nothing after the area
    }

    [Fact]
    public void A_portals_variation_is_sent_as_its_own_u16()
    {
        // The portal cosmetic: +0x26 of the 0x008f entry is the MicrotransactionPortalVariations row the client draws
        // instead of the base portal. Everything else in the message stays the same.
        string plain = Convert.ToHexString(InstanceMapDevice.Portals(0x3002, [new(0x3003, 0x8434, "Ab", Variation: 0)]));
        string black = Convert.ToHexString(InstanceMapDevice.Portals(0x3002, [new(0x3003, 0x8434, "Ab", Variation: 6)]));
        Assert.Equal(plain.Length, black.Length);
        int at = Enumerable.Range(0, plain.Length).First(i => plain[i] != black[i]);
        Assert.Equal("00", plain.Substring(at - 1, 2));     // the variation is 0000 …
        Assert.Equal("06", black.Substring(at - 1, 2));     // … and 0006 with Black Hole (index 6)
        Assert.Single(Enumerable.Range(0, plain.Length), i => plain[i] != black[i]);   // one nibble: 0 → 6
        // The u16 sits right after the area, before the three bytes and the owner.
        Assert.Contains("8434" + "0006" + "000000" + "0002" + "4162", black);
    }
}
