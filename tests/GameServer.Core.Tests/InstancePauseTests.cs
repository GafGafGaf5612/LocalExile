using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class InstancePauseTests
{
    [Fact]
    public void Pause_request_is_framed_and_read()
    {
        // The Escape menu opening (u8 1) then closing (u8 0), with a tick fragment after them.
        byte[] data = Convert.FromHexString("01D401" + "01D400" + "001B00");
        var messages = InstanceProtocol.SplitClientMessages(data, out byte[] partial, out byte[] unknown);
        Assert.Equal(2, messages.Count);
        Assert.Equal("001B00", Convert.ToHexString(partial));
        Assert.Empty(unknown);
        Assert.True(InstancePause.ReadRequest(messages[0].Bytes));
        Assert.False(InstancePause.ReadRequest(messages[1].Bytes));
    }

    [Fact]
    public void Pause_state_sets_both_world_flags()
    {
        Assert.Equal("01D303", Convert.ToHexString(InstancePause.State(true)));
        Assert.Equal("01D300", Convert.ToHexString(InstancePause.State(false)));
    }
}
