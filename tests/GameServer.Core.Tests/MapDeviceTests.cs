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
}
