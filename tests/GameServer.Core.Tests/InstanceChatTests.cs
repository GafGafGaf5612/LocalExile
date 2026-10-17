using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Core.Tests;

public class InstanceChatTests
{
    [Fact]
    public void Chat_input_is_framed_and_read()
    {
        // Captured live 2026-10-16: "/spawn_mob 42" then "#hello server", in one chunk, followed by a tick fragment.
        byte[] data = Convert.FromHexString("0015000D2F737061776E5F6D6F6220343200" + "0015000D2368656C6C6F2073657276657200" + "001B00");
        var messages = InstanceProtocol.SplitClientMessages(data, out byte[] partial, out byte[] unknown);
        Assert.Equal(2, messages.Count);
        Assert.Equal("001B00", Convert.ToHexString(partial));
        Assert.Empty(unknown);
        var command = InstanceChat.ReadChat(messages[0].Bytes);
        Assert.Equal(("/spawn_mob 42", true), (command.Text, command.IsCommand));
        Assert.False(InstanceChat.ReadChat(messages[1].Bytes).IsCommand);
    }

    [Fact]
    public void A_chat_message_cut_off_waits_for_more_bytes()
    {
        InstanceProtocol.SplitClientMessages(Convert.FromHexString("0015000D2F7370"), out byte[] partial, out _);
        Assert.Equal("0015000D2F7370", Convert.ToHexString(partial));
    }

    [Fact]
    public void Message_box_is_a_wide_string()
    {
        Assert.Equal("000D" + "0002" + "4869", Convert.ToHexString(InstanceChat.MessageBox("Hi")));
    }

    [Fact]
    public void Stats_update_uses_compact_keys_and_values()
    {
        // Key 244 (0x80F4 as a 2-byte compact) = 100, and a negative value.
        byte[] m = InstanceEntities.StatsUpdate(0x1000, [(244, 100), (5, -1)]);
        Assert.Equal("01B3" + "00001000" + "00000000" + "0000" + "02" + "80F4" + "8064" + "05" + "7F", Convert.ToHexString(m));
    }

    [Theory]
    [InlineData(0x7fu, "7F")]
    [InlineData(0x80u, "8080")]
    [InlineData(0x3fffu, "BFFF")]
    [InlineData(0x4000u, "C04000")]
    [InlineData(0x200000u, "E0200000")]
    [InlineData(0x10000000u, "F010000000")]
    public void Compact_uint_matches_the_client_reader(uint value, string hex)
    {
        var w = new GameServer.Core.Protocol.Login.LoginWriter();
        InstanceProtocol.CompactUInt(w, value);
        Assert.Equal(hex, Convert.ToHexString(w.ToArray()));
    }

    [Fact]
    public void Changing_speed_keeps_the_distance_walked()
    {
        var walk = new WalkEstimator(0, 0, 10);
        walk.Order(100, 0, 0);
        Assert.Equal((20, 0), walk.Position(2));
        walk.SetSpeed(20, 2);
        Assert.Equal((20, 0), walk.Position(2));
        Assert.Equal((40, 0), walk.Position(3));
    }
}
