namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    [Fact]
    public async Task Level_up_capture_cannot_drop_a_concurrent_allocation_or_let_its_bridge_be_refunded()
    {
        using var game = new GatewayCharacter(level: 10, family: AscendancyFamily.Normal, choice: "Slayer");
        Assert.Null((await game.Change([39725], allocate: true)).Error);
        // Stage the production capture/commit phases around another task's completed mutation: no timing window.
        var captured = Program.CaptureLevelChange(game.State, 11);
        Assert.False(captured.Trimmed);
        Assert.Null((await Task.Run(() => game.Change([24377], allocate: true))).Error);
        string allocated = game.SavedJson;

        Program.CommitLevelChange(game.State, captured);
        var refund = await game.Change([39725], allocate: false, refundPoints: true);

        Assert.NotNull(refund.Error); // 24377 still depends on this bridge, in runtime and in the durable tree.
        Assert.Equal(allocated, game.SavedJson);
        Assert.Equal(new ushort[] { 39725, 24377 }, game.State.Passives);
        Assert.Equal(new ushort[] { 39725, 24377 }, CharacterSaves.Load(game.Character)!.Passives);
        Assert.Equal((ushort)20, game.State.RefundPoints);
        Assert.Equal(11, game.State.Pools.Level);
        Assert.True(game.State.SavedPassiveProgress!.Matches(CharacterSaves.Load(game.Character)!));
        Assert.Equal(new ushort[] { 24377, 39725 }, PassivePacket(refund.Bytes, 2).Nodes);
    }

    [Fact]
    public async Task Trimming_capture_rebases_after_a_concurrent_paid_refund_without_resurrecting_or_recharging_it()
    {
        using var game = new GatewayCharacter(level: 10, family: AscendancyFamily.Normal, choice: "Slayer");
        Assert.Null((await game.Change([39725, 24377, 35568], allocate: true)).Error);
        var captured = Program.CaptureLevelChange(game.State, 3);
        Assert.True(captured.Trimmed);
        Assert.Null((await Task.Run(() => game.Change([35568], allocate: false, refundPoints: true))).Error);
        string refunded = game.SavedJson;

        Program.CommitLevelChange(game.State, captured);

        Assert.Equal(refunded, game.SavedJson);
        Assert.Equal(new ushort[] { 39725, 24377 }, game.State.Passives);
        Assert.Equal((ushort)19, game.State.RefundPoints);
        Assert.Equal((ushort)19, CharacterSaves.Load(game.Character)!.RefundPoints);
        Assert.Equal(3, game.State.Pools.Level);
        Assert.True(game.State.SavedPassiveProgress!.Matches(CharacterSaves.Load(game.Character)!));
        Assert.NotNull((await game.Change([39725], allocate: false, refundPoints: true)).Error);
        Assert.Equal(refunded, game.SavedJson);
    }

    [Fact]
    public async Task Trimming_capture_rebases_after_allocation_and_commits_against_the_latest_saved_snapshot()
    {
        using var game = new GatewayCharacter(level: 10, family: AscendancyFamily.Normal, choice: "Slayer");
        Assert.Null((await game.Change([39725, 24377, 35568], allocate: true)).Error);
        var captured = Program.CaptureLevelChange(game.State, 3);
        Assert.True(captured.Trimmed);
        Assert.Null((await Task.Run(() => game.Change([63649], allocate: true))).Error);

        Program.CommitLevelChange(game.State, captured);

        // Recompute the normal last-first trim against all four current nodes, using the allocation's new snapshot.
        Assert.Equal(new ushort[] { 39725, 24377 }, game.State.Passives);
        Assert.Equal(new ushort[] { 39725, 24377 }, CharacterSaves.Load(game.Character)!.Passives);
        Assert.Equal(3, game.State.Pools.Level);
        Assert.Equal((ushort)20, game.State.RefundPoints);
        Assert.True(game.State.SavedPassiveProgress!.Matches(CharacterSaves.Load(game.Character)!));
        Assert.NotNull((await game.Change([39725], allocate: false, refundPoints: true)).Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queued_tree_publication_uses_the_graph_after_allocation_or_refund_and_level_change(bool lowerLevel)
    {
        using var game = new GatewayCharacter(level: 10, family: AscendancyFamily.Normal, choice: "Slayer");
        Assert.Null((await game.Change(lowerLevel ? [39725, 24377, 35568] : [39725], allocate: true)).Error);

        var publication = await game.QueuedPassivePublication(async () =>
        {
            Assert.Null((await game.Change(lowerLevel ? [35568] : [24377], allocate: !lowerLevel,
                refundPoints: lowerLevel)).Error);
            await game.ApplyLevelOnly(lowerLevel ? 2 : 11);
        });

        var packet = PassivePacket(publication.Bytes, lowerLevel ? 1 : 2);
        Assert.Equal(lowerLevel ? new ushort[] { 39725 } : [24377, 39725], packet.Nodes);
        Assert.Equal(lowerLevel ? (ushort)19 : (ushort)20, packet.RefundPoints);
        Assert.Equal(lowerLevel ? (byte)2 : (byte)11, packet.Level);
        Assert.Equal(game.State.Passives.Order(), packet.Nodes);
        Assert.True(game.State.SavedPassiveProgress!.Matches(CharacterSaves.Load(game.Character)!));
    }

    [Fact]
    public async Task Failed_level_trim_keeps_the_saved_tree_runtime_snapshot_and_pools_and_sends_no_tree()
    {
        using var game = new GatewayCharacter(level: 10, family: AscendancyFamily.Normal, choice: "Slayer");
        Assert.Null((await game.Change([39725, 24377, 35568], allocate: true)).Error);
        string before = game.SavedJson;
        var snapshot = game.State.SavedPassiveProgress;
        var pools = game.State.Pools;
        Saves.FailWrite = name => name == game.Character;
        try { await Assert.ThrowsAsync<IOException>(() => game.ApplyLevelOnly(2)); }
        finally { Saves.FailWrite = null; }

        Assert.Equal(before, game.SavedJson);
        Assert.Equal(new ushort[] { 39725, 24377, 35568 }, game.State.Passives);
        Assert.Same(snapshot, game.State.SavedPassiveProgress);
        Assert.Same(pools, game.State.Pools);
        Assert.Equal((ushort)20, game.State.RefundPoints);
        Assert.Empty(game.LastBytes);
    }

    sealed partial class GatewayCharacter
    {
        public Task<(string? Error, byte[] Bytes)> ApplyLevelOnly(int level) => Exchange(async (stream, cipher) =>
        {
            await ProgramValue<Task>("ApplyLevel", "level-concurrency-test", stream, cipher, State, level);
            return null;
        });

        public Task<(string? Error, byte[] Bytes)> QueuedPassivePublication(Func<Task> mutation) =>
            Exchange(async (stream, cipher) =>
            {
                var gate = Program.SendGates.GetValue(cipher, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync();
                var publication = ProgramValue<Task>("SendPassives", "level-concurrency-test", stream, cipher, State);
                Assert.False(publication.IsCompleted);
                try { await mutation(); }
                finally { gate.Release(); }
                await publication;
                return null;
            });
    }
}
