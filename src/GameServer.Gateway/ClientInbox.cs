using System.Threading.Channels;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// One connection's client messages, split as they are read (issue #46). The client's "network: N ms" is the time until
/// its oldest 0x1b tick is answered (Agents/docs/protocol/07 §1), so the read loop answers ticks at once, and every other
/// message waits here, in the order received, for the connection's one handler task. A slow handler (0x7d populating
/// the area, an area change) no longer holds the 0x1c answers back.
/// </summary>
sealed class ClientInbox
{
    /// <summary>A tick answered, or a handler run, later than this is logged.</summary>
    public static readonly TimeSpan SlowAfter = TimeSpan.FromMilliseconds(50);

    /// <summary>The messages framed from one read, without its ticks, and the read's unframed rest.</summary>
    public sealed record Batch(IReadOnlyList<(ushort Id, byte[] Bytes)> Messages, byte[] Unknown);

    /// <summary>0x1c's index that clears the client's whole send-time queue (≥ its size, 0x142155930).</summary>
    public const ushort ClearQueue = ushort.MaxValue;

    readonly Channel<Batch> _queue = Channel.CreateUnbounded<Batch>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    bool _resync = true;

    /// <summary>
    /// The ack index for the next 0x1c. The client keeps the send time of every 0x1b in a queue and index i drops entries
    /// 0..i. Ticks no server answered (sent while the client left the last area, or lost in unframed bytes) stay there
    /// for good, and index 0 then always pops a stale entry: the meter trailed by two tick intervals (~330 ms, live
    /// 2026-10-19). So the first 0x1c of a connection, and the first after an unframed read, clears the queue; the
    /// client then measures from its latest tick (+0xc470). The rest answer one tick each.
    /// </summary>
    public ushort NextAckIndex()
    {
        if (!_resync) return 0;
        _resync = false;
        return ClearQueue;
    }

    /// <summary>Queues one read's messages for the handler task and returns its ticks, for the read loop to answer.</summary>
    public List<byte[]> Post(IReadOnlyList<(ushort Id, byte[] Bytes)> messages, byte[] unknown)
    {
        if (unknown.Length > 0) _resync = true;   // a tick may be in the bytes that could not be framed
        var ticks = new List<byte[]>();
        var rest = new List<(ushort, byte[])>();
        foreach (var m in messages)
            if (m.Id == InstanceProtocol.TickId) ticks.Add(m.Bytes);
            else rest.Add(m);
        if (rest.Count > 0 || unknown.Length > 0) _queue.Writer.TryWrite(new Batch(rest, unknown));
        return ticks;
    }

    /// <summary>No more reads: the handler task ends after the queued batches.</summary>
    public void Complete() => _queue.Writer.TryComplete();

    public IAsyncEnumerable<Batch> ReadAllAsync(CancellationToken token = default) => _queue.Reader.ReadAllAsync(token);
}
