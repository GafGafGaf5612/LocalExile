using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    // C→S 0x70 on a utility flask (13-items.md §8a): it spends its charges (S→C 0xbf), puts its buff on the player with
    // its duration (S→C 0x1c1) and its stats count in the pools until the duration ends (UtilityFlasks), with the stats
    // and movement speed sent again at the start and the end. Refused while dead, with too few charges, or while this
    // flask's own effect still runs. A flask of the same kind that still runs ends: effects of one kind do not stack.
    static async Task UseUtilityFlask(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint slot,
        InventoryItem flask, ItemCatalog.BaseType type, UtilityFlasks.Utility utility)
    {
        var charges = utility.Charges;
        string? refused = conn.PlayerLife == 0 ? "dead"
            : conn.UtilityFlaskEffects.Of(flask.Id) is { } running
                ? $"its effect still runs ({running.Ends - conn.Clock.Elapsed.TotalSeconds:0.##} s left)"
            : Flasks.Spend(flask.Item, charges) is null ? $"{flask.Item.Charges}/{charges.MaxCharges} charges, a use needs {charges.ChargesPerUse}"
            : null;
        if (refused is not null)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0070 use flask {slot} ({type.Name}): refused, {refused}");
            return;
        }
        var items = ItemsOf(conn);
        if (items.Change(PlayerInventories.Flask, flask.Id, i => Flasks.Spend(i, charges) ?? i) is not { } spent) return;
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Flask),
            items.NextRevision(PlayerInventories.Flask), [spent.Id], [spent]), $"0x00bf flask {slot}: {spent.Item.Charges}/{charges.MaxCharges} charges");

        var (stats, seconds) = UtilityFlasks.For(utility, flask.Item.Quality, ItemModStats.Local(flask.Item, Items.Mods));
        double now = conn.Clock.Elapsed.TotalSeconds;
        UtilityFlasks.Active effect;
        (UtilityFlasks.Active? Replaced, bool StartTicker) started;
        lock (conn.Monsters)
        {
            ushort buff = conn.NextBuff++;
            ushort? extra = utility.Extra is null ? null : conn.NextBuff++;
            effect = new(flask.Id, (byte)slot, type.Name, utility, buff, extra, stats, now, now + seconds);
            started = conn.UtilityFlaskEffects.Start(effect);
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
        }
        Console.WriteLine($"[gateway] {remote} <= 0x0070 use flask {slot} ({type.Name}): {utility.BuffId} for {seconds:0.##} s: " +
            string.Join(", ", stats.Select(s => $"{s.Stat} {s.Value}")));
        if (started.Replaced is { } replaced) await SendUtilityFlaskEnd(remote, stream, ciphers, replaced, $"replaced by flask {slot}");
        // The duration (flags bit1, u32 +0x68, milliseconds): the client's buff timer. Game_FLASK_BUFF_DURATION picks the
        // field as for life flasks ("f6c": f32 +0x6c in seconds; "0": none).
        string durationField = Environment.GetEnvironmentVariable("Game_FLASK_BUFF_DURATION") ?? "ms";
        uint? u68 = durationField == "ms" ? (uint)Math.Round(seconds * 1000) : null;
        float? f6c = durationField == "f6c" ? (float)seconds : null;
        await Send(remote, stream, ciphers, InstanceEntities.AddBuff(PlayerId, effect.Buff, utility.BuffRow, PlayerId, u68: u68, f6c: f6c),
            $"0x01c1 player buff {effect.Buff}: {utility.BuffId} (BuffDefinitions row {utility.BuffRow}), {seconds:0.##} s, duration field {durationField}");
        if (utility.Extra is { } x && effect.ExtraBuff is ushort extraBuff)
            await Send(remote, stream, ciphers, InstanceEntities.AddBuff(PlayerId, extraBuff, x.BuffRow, PlayerId, u68: u68, f6c: f6c),
                $"0x01c1 player buff {extraBuff}: {x.BuffId} (BuffDefinitions row {x.BuffRow}) from {type.Name}");
        await SendCharacterStats(remote, stream, ciphers, conn, $"{type.Name} on");
        if (started.StartTicker) _ = RunUtilityFlasks(remote, stream, ciphers, conn);
    }

    // Ends the utility flask effects whose time is up (by the connection clock: a pause holds them) or, on death, all of
    // them: their buffs come off and the stats and movement speed go back. Runs while any effect runs.
    static async Task RunUtilityFlasks(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        try
        {
            while (!conn.Closed)
            {
                await Task.Delay(RecoveryStepMs);
                if (conn.Closed) break;
                bool dead = conn.PlayerLife == 0;
                var ended = conn.UtilityFlaskEffects.End(conn.Clock.Elapsed.TotalSeconds, all: dead, out bool stop);
                if (ended.Count > 0)
                {
                    lock (conn.Monsters)
                    {
                        conn.Pools = PoolsFor(conn, conn.Pools.Level);
                        conn.ApplyMovementSpeed();
                    }
                    foreach (var e in ended) await SendUtilityFlaskEnd(remote, stream, ciphers, e, dead ? "death" : "its time is up");
                    await SendCharacterStats(remote, stream, ciphers, conn, $"{string.Join(", ", ended.Select(e => e.Name))} off");
                }
                if (stop) break;
            }
        }
        catch (Exception ex)
        {
            // A closed connection ends its effects with it (an area change starts a new connection without them).
            if (!conn.Closed) Console.WriteLine($"[gateway] {remote} utility flasks: {ex.Message}");
        }
    }

    static async Task SendUtilityFlaskEnd(string remote, NetworkStream stream, ConnectionCiphers ciphers, UtilityFlasks.Active e, string why)
    {
        await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, e.Buff),
            $"0x01c2 player buff {e.Buff}: {e.Name} (flask {e.Slot}) effect ends ({why})");
        if (e.ExtraBuff is ushort extra) await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, extra), null);
    }
}
