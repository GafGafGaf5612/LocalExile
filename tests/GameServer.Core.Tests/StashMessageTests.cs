using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

// C→S 0xd3 (tab affinities), read from the bytes the client sent live on 2026-10-20.
public class StashMessageTests
{
    [Fact]
    public void The_affinity_window_sends_the_tab_and_each_change()
    {
        var m = InstanceStash.ReadSetAffinities(Convert.FromHexString("00D3000500010301"));

        Assert.Equal((ushort)5, m.Tab);
        Assert.Equal(InstanceStash.Personal, m.Kind);
        Assert.Equal([((byte)3, true)], m.Changes);
    }

    [Fact]
    public void It_is_framed_so_what_follows_it_in_a_read_is_not_lost()
    {
        const string Tick = "001B00D7E61FA4";
        var data = Convert.FromHexString("00D3000500010301" + Tick);

        var messages = InstanceProtocol.SplitClientMessages(data, out var partial, out var unknown);

        Assert.Equal([(ushort)0xd3, 0x1b], messages.Select(m => m.Id));
        Assert.Empty(partial);
        Assert.Empty(unknown);
        Assert.Equal(8, messages[0].Bytes.Length);
    }

    [Fact]
    public void A_message_cut_off_waits_for_the_rest()
    {
        InstanceProtocol.SplitClientMessages(Convert.FromHexString("00D3000500"), out var partial, out var unknown);
        Assert.Equal(5, partial.Length);
        Assert.Empty(unknown);
    }
}
