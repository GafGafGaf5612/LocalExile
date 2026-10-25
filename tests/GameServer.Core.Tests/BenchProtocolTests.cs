using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class BenchProtocolTests
{
    [Fact]
    public void Balance_inventory_is_separate_from_fixed_character_slots_and_regular_stash_tabs()
    {
        Assert.Equal("00C000FFFFFE00121AFFFF00000000", Convert.ToHexString(
            InstanceInventories.AddInventory(InstanceBench.BalanceInventoryId, InstanceBench.BalanceSlot)));
        Assert.DoesNotContain(InstanceInventories.PlayerDefaults, slot => slot.Kind == InstanceBench.BalanceSlot.Kind);
    }
    [Theory]
    [InlineData("015D0032", 50, false)]
    [InlineData("015D8330", 816, true)]
    public void Native_packed_recipe_request_is_decoded_and_framed(string hex, int row, bool replace)
    {
        byte[] packet = Convert.FromHexString(hex);
        Assert.Equal((row, replace), InstanceBench.ReadCraft(packet));
        Assert.Equal(4, InstanceProtocol.KnownClientLength(InstanceBench.CraftId));
        var messages = InstanceProtocol.SplitClientMessages([.. packet, .. packet], out var partial, out var unknown);
        Assert.Equal(2, messages.Count);
        Assert.Empty(partial);
        Assert.Empty(unknown);
        Assert.Empty(InstanceProtocol.SplitClientMessages(packet[..3], out partial, out unknown));
        Assert.Equal(packet[..3], partial);
    }
}
