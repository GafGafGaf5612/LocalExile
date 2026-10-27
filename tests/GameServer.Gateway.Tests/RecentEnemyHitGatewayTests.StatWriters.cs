using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public partial class RecentEnemyHitGatewayTests
{
    static void PrepareFullStatWriter(Fixture f, string writer)
    {
        if (writer == "mageblood")
        {
            var items = PlayerInventories.For(f.Conn.Character!, Program.Items);
            items.Add(PlayerInventories.Belt, 0, 0, Program.Uniques.Create("Mageblood", new Random(1))!);
            items.Add(PlayerInventories.Flask, 0, 0,
                ItemCatalog.Create(Program.Items.Find("Granite Flask")!) with { Rarity = ItemRarity.Magic });
        }
        f.Set("no_inherent_rage_loss", 1);
        f.Conn.Pools = Program.PoolsFor(f.Conn, 100);
        f.Conn.PlayerLife = f.Conn.Pools.Life;
        _ = f.Chance; // Warm catalogs before arranging the four-second window.
        if (writer == "rage")
            Assert.True(f.Conn.Rage.Gain(3, f.Conn.Clock.Elapsed.TotalSeconds, new(NoInherentLoss: true)));
        if (writer == "mageblood") Assert.True(f.Conn.Pools.Armour >= 1500);
    }

    static Task SendFullStatWriter(Fixture f, string writer)
    {
        if (writer == "rage") return Program.SendRage("recent-writer-test", f.Stream, f.Ciphers, f.Conn);
        if (writer == "aura") f.Conn.AuraStatsDirty = true;
        string method = writer == "aura" ? "SendOwnAuraChanges" : "RefreshMagebloodFlasks";
        object?[] args = writer == "aura"
            ? ["recent-writer-test", f.Stream, f.Ciphers, f.Conn, false]
            : ["recent-writer-test", f.Stream, f.Ciphers, f.Conn];
        return (Task)Method(method).Invoke(null, args)!;
    }

    static void AssertFullRecentPacket(Fixture f, byte[] bytes, int count, int chance)
    {
        var stats = (List<(uint, int)>)Method("PlayerStatsWithCleared").Invoke(null, [f.Conn])!;
        Assert.Contains((4534u, count), stats);
        Assert.Contains((12268u, chance), stats);
        // Match the actual full player Stats packet, not the Actor's separate embedded stat rows.
        int at = bytes.AsSpan().IndexOf(InstanceEntities.StatsUpdate(Program.PlayerId, [])[..12]);
        Assert.True(at >= 0, "Expected a full player Stats packet from the real writer.");
        Assert.True(bytes.AsSpan(at).StartsWith(InstanceEntities.StatsUpdate(Program.PlayerId, stats)),
            "The full player snapshot encoded stale recent-hit values.");
    }

    [Theory]
    [InlineData("rage")]
    [InlineData("aura")]
    [InlineData("mageblood")]
    public async Task Remaining_full_writer_acknowledges_changed_tuple_before_expiry_returns_to_previous_tuple(string writer)
    {
        using var f = new Fixture();
        PrepareFullStatWriter(f, writer);
        double first = f.Conn.Clock.Elapsed.TotalSeconds - 2;
        f.Conn.RecentEnemyHits.Record(first);
        await f.Present(); await f.Read();
        Assert.Equal((1, 10), f.Conn.PresentedRecentEnemyHits);
        f.Conn.RecentEnemyHits.Record(f.Conn.Clock.Elapsed.TotalSeconds);
        _ = Method("PlayerStatsWithCleared").Invoke(null, [f.Conn]);
        Assert.Equal((1, 10), f.Conn.PresentedRecentEnemyHits);
        await SendFullStatWriter(f, writer).WaitAsync(TimeSpan.FromSeconds(5));
        AssertFullRecentPacket(f, await f.Read(), 2, 20);
        var acknowledged = f.Conn.PresentedRecentEnemyHits;
        await WaitFor(() => f.Conn.Clock.Elapsed.TotalSeconds - first >= 4.02);
        Assert.Equal(1, f.Count);
        await f.Present();
        Assert.Equal(InstanceEntities.StatsUpdate(Program.PlayerId, [(4534u, 1), (12268u, 10)]), await f.Read());
        Assert.Equal((2, 20), acknowledged);
        Assert.Equal((1, 10), f.Conn.PresentedRecentEnemyHits);
        if (writer == "rage") Assert.Equal(new RageState.Value(3, 30, 3), f.Conn.SentRage);
    }

    [Theory]
    [InlineData("rage")]
    [InlineData("aura")]
    [InlineData("mageblood")]
    public async Task Remaining_queued_full_writer_cannot_overwrite_an_earlier_expiry_refresh_with_stale_values(string writer)
    {
        using var f = new Fixture();
        PrepareFullStatWriter(f, writer);
        double contact = f.Conn.Clock.Elapsed.TotalSeconds - 3;
        f.Conn.RecentEnemyHits.Record(contact);
        await f.Present(); await f.Read();
        Assert.Equal((1, 10), f.Conn.PresentedRecentEnemyHits);
        await f.Gate.WaitAsync();
        var expiry = f.Present();
        var full = SendFullStatWriter(f, writer);
        try
        {
            Assert.False(expiry.IsCompleted); Assert.False(full.IsCompleted);
            await WaitFor(() => f.Conn.Clock.Elapsed.TotalSeconds - contact >= 4.02);
        }
        finally { f.Gate.Release(); }
        await Task.WhenAll(expiry, full).WaitAsync(TimeSpan.FromSeconds(5));
        byte[] bytes = await f.Read();
        var zero = InstanceEntities.StatsUpdate(Program.PlayerId, [(4534u, 0), (12268u, 0)]);
        Assert.True(bytes.AsSpan().StartsWith(zero));
        AssertFullRecentPacket(f, bytes[zero.Length..], 0, 0);
        Assert.Equal((0, 0), f.Conn.PresentedRecentEnemyHits);
        await f.Present();
        Assert.Empty(await f.Read()); // Deduplication is safe because the last full packet also encoded zero.
    }

    [Theory]
    [InlineData("rage", "closed")]
    [InlineData("rage", "leaving")]
    [InlineData("rage", "retired")]
    [InlineData("aura", "closed")]
    [InlineData("aura", "leaving")]
    [InlineData("mageblood", "closed")]
    [InlineData("mageblood", "leaving")]
    public async Task Rejected_queued_full_writer_cannot_acknowledge_a_pair_it_did_not_send(string writer, string state)
    {
        using var f = new Fixture();
        PrepareFullStatWriter(f, writer);
        f.Conn.RecentEnemyHits.Record(f.Conn.Clock.Elapsed.TotalSeconds);
        await f.Present(); await f.Read();
        f.Conn.RecentEnemyHits.Record(f.Conn.Clock.Elapsed.TotalSeconds);
        await f.Gate.WaitAsync();
        var full = SendFullStatWriter(f, writer);
        try
        {
            Assert.False(full.IsCompleted);
            lock (f.Conn.Monsters)
            {
                if (state == "closed") f.Conn.Closed = true;
                if (state == "leaving") f.Conn.LeavingArea = true;
                if (state == "retired") f.Conn.RageRetired = true;
            }
        }
        finally { f.Gate.Release(); }
        await full.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(await f.Read());
        Assert.Equal((1, 10), f.Conn.PresentedRecentEnemyHits);
        if (writer == "rage") { Assert.Null(f.Conn.SentRage); Assert.Equal((ushort)0, f.Conn.RageBuff); }
        if (writer == "aura") Assert.True(f.Conn.AuraStatsDirty);
    }

    [Fact]
    public async Task Unchanged_Rage_build_cannot_acknowledge_recent_values_without_sending_player_stats()
    {
        using var f = new Fixture();
        PrepareFullStatWriter(f, "rage");
        await SendFullStatWriter(f, "rage"); await f.Read();
        f.Conn.RecentEnemyHits.Record(f.Conn.Clock.Elapsed.TotalSeconds);
        await f.Present(); await f.Read();
        f.Conn.RecentEnemyHits.Record(f.Conn.Clock.Elapsed.TotalSeconds);
        await SendFullStatWriter(f, "rage");
        Assert.Empty(await f.Read());
        Assert.Equal((1, 10), f.Conn.PresentedRecentEnemyHits);
        await f.Present();
        Assert.Equal(InstanceEntities.StatsUpdate(Program.PlayerId, [(4534u, 2), (12268u, 20)]), await f.Read());
        Assert.Equal((2, 20), f.Conn.PresentedRecentEnemyHits);
    }
}
