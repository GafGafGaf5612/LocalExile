using System.Diagnostics;

namespace GameServer.Gateway.Tests;

public class ConnectionPerformanceTests
{
    [Fact]
    public async Task Abandoning_a_closed_connections_backlog_clears_its_depth()
    {
        var inbox = new ClientInbox(new ConnectionPerformance());
        inbox.Post([(0x25, new byte[] { 0, 0x25 })], []);
        inbox.Complete(); inbox.DiscardPending();
        Assert.Equal(0, inbox.PendingBatches);
        inbox.Post([(0x25, new byte[] { 0, 0x25 })], []);
        Assert.Equal(0, inbox.PendingBatches);
        await foreach (var _ in inbox.ReadAllAsync()) Assert.Fail("A discarded batch survived connection shutdown.");
    }

    [Fact]
    public async Task Queue_age_is_measured_before_handler_execution_and_depth_drains()
    {
        var performance = new ConnectionPerformance();
        var inbox = new ClientInbox(performance);
        long readAt = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 2;
        inbox.Post([(0x25, new byte[] { 0, 0x25 })], [], readAt);
        inbox.Post([(0x25, new byte[] { 0, 0x25 })], [], readAt);
        Assert.Equal(2, inbox.PendingBatches);
        inbox.Complete();
        int count = 0;
        await foreach (var batch in inbox.ReadAllAsync())
        {
            count++;
            Assert.Equal(readAt, batch.ReadAt);
            Assert.True(performance.Read(LatencyPhase.InputQueue).MaximumMilliseconds >= 500);
            Assert.Equal(0, performance.Read(LatencyPhase.Handler).Count);
        }
        Assert.Equal(2, count);
        Assert.Equal(0, inbox.PendingBatches);
        Assert.Equal(2, performance.Read(LatencyPhase.InputQueue).Count);
    }

    [Fact]
    public void Measured_combat_locks_release_on_exception_and_preserve_reentrancy()
    {
        var performance = new ConnectionPerformance();
        var gate = new object();
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var outer = performance.Lock(gate, "test");
            using var inner = performance.Lock(gate, "test");
            Assert.True(Monitor.IsEntered(gate));
            throw new InvalidOperationException();
        }));
        Assert.False(Monitor.IsEntered(gate));
        Assert.Equal(2, performance.Read(LatencyPhase.CombatWait).Count);
        Assert.Equal(2, performance.Read(LatencyPhase.CombatHold).Count);
    }
}
