using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

// C→S 0xe1 (Ctrl + click to an affinity tab), 0xd3 (tab affinities) and 0x8a (Ctrl + click buy), read from the bytes the
// client sent live on 2026-10-20.
public class StashMessageTests
{
    [Fact]
    public void Ctrl_click_to_an_affinity_tab_names_the_item_and_the_tab()
    {
        var m = InstanceStash.ReadMoveToAffinityTab(Convert.FromHexString("00E1000000080000225E01000503"));

        Assert.Equal(8u, m.ItemId);
        Assert.Equal([((ushort)5, (byte)3)], m.Tabs);   // the Currency tab, affinity CurrencyItems
        Assert.Equal(0x5e22, m.Token);
        Assert.Equal(0x40000000u, InstanceStash.ReadMoveToAffinityTab(Convert.FromHexString("00E14000000000007C7201000503")).ItemId);
    }

    [Fact]
    public void The_affinity_window_sends_the_tab_and_each_change()
    {
        var m = InstanceStash.ReadSetAffinities(Convert.FromHexString("00D3000500010301"));

        Assert.Equal((ushort)5, m.Tab);
        Assert.Equal(InstanceStash.Personal, m.Kind);
        Assert.Equal([((byte)3, true)], m.Changes);
    }

    [Fact]
    public void Ctrl_click_buy_names_the_shop_item()
    {
        var m = InstanceNpcs.ReadCtrlBuy(Convert.FromHexString("008A00000061000000A777"));

        Assert.Equal(0x61u, m.ItemId);
        Assert.Equal(0x77a7, m.Token);
    }

    [Fact]
    public void All_three_are_framed_so_what_follows_them_in_a_read_is_not_lost()
    {
        // As the client sent them, each followed by a tick (0x1b) that must still be read.
        const string Tick = "001B00D7E61FA4";
        var data = Convert.FromHexString("00E1000000080000225E01000503" + Tick + "00D3000500010301" + Tick + "008A00000061000000A777" + Tick);

        var messages = InstanceProtocol.SplitClientMessages(data, out var partial, out var unknown);

        Assert.Equal([(ushort)0xe1, 0x1b, 0xd3, 0x1b, 0x8a, 0x1b], messages.Select(m => m.Id));
        Assert.Empty(partial);
        Assert.Empty(unknown);
        Assert.Equal(14, messages[0].Bytes.Length);
        Assert.Equal(8, messages[2].Bytes.Length);
        Assert.Equal(11, messages[4].Bytes.Length);
    }

    [Fact]
    public void A_message_cut_off_waits_for_the_rest()
    {
        InstanceProtocol.SplitClientMessages(Convert.FromHexString("00E1000000080000225E01"), out var partial, out var unknown);
        Assert.Equal(11, partial.Length);
        Assert.Empty(unknown);
    }
}
