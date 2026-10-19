using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ClientInboxTests
{
    static readonly byte[] Tick1 = [0x00, 0x1b, 0x01, 0, 0, 0, 0], Tick2 = [0x00, 0x1b, 0x02, 0, 0, 0, 0];
    static readonly byte[] Move = [0x00, 0x25, 1, 2, 3], Bind = [0x00, 0x4e, 0, 0x29, 0x09];

    static async Task<List<ClientInbox.Batch>> Drain(ClientInbox inbox)
    {
        inbox.Complete();
        var batches = new List<ClientInbox.Batch>();
        await foreach (var b in inbox.ReadAllAsync()) batches.Add(b);
        return batches;
    }

    [Fact]
    public async Task Ticks_are_returned_and_the_rest_is_queued_in_order()
    {
        var inbox = new ClientInbox();
        var ticks = inbox.Post([(0x25, Move), (InstanceProtocol.TickId, Tick1), (0x4e, Bind), (InstanceProtocol.TickId, Tick2)], []);

        Assert.Equal([Tick1, Tick2], ticks);
        var batch = Assert.Single(await Drain(inbox));
        Assert.Equal([(ushort)0x25, (ushort)0x4e], batch.Messages.Select(m => m.Id));
        Assert.Same(Move, batch.Messages[0].Bytes);
        Assert.Empty(batch.Unknown);
    }

    [Fact]
    public async Task A_read_of_only_ticks_queues_nothing()
    {
        var inbox = new ClientInbox();
        Assert.Single(inbox.Post([(InstanceProtocol.TickId, Tick1)], []));
        Assert.Empty(await Drain(inbox));
    }

    [Fact]
    public async Task Unframed_bytes_are_queued_after_the_framed_messages()
    {
        var inbox = new ClientInbox();
        byte[] unknown = [0x00, 0x35, 0, 1, 2, 3];
        inbox.Post([(InstanceProtocol.TickId, Tick1)], unknown);
        inbox.Post([(0x25, Move)], []);

        var batches = await Drain(inbox);
        Assert.Equal(2, batches.Count);
        Assert.Empty(batches[0].Messages);
        Assert.Same(unknown, batches[0].Unknown);
        Assert.Equal(0x25, Assert.Single(batches[1].Messages).Id);
    }
}
