using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    // Flask charge gains (13-items.md §8b, FlaskCharges): each flask in the bar that `chargesFor` gives something gets it
    // times its gain multiplier (the character's increased flask charges gained, the flask's increased charge recovery),
    // whole charges now and the rest kept, up to its maximum with its mods; the changed flasks in one S→C 0xbf, saved with
    // the next save.
    static async Task GainFlaskCharges(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        Func<InventoryItem, IReadOnlyDictionary<string, int>, double> chargesFor, string why)
    {
        var items = ItemsOf(conn);
        var character = CharacterPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint key ? character.GetValueOrDefault(key) : 0;
        List<InventoryItem> changed = [];
        foreach (var flask in items.Items(PlayerInventories.Flask))
        {
            if (Items.ByHash(flask.Item.TypeHash) is not { } type || Items.FlaskChargesOf(type) is not { } baseCharges) continue;
            var local = ItemModStats.Local(flask.Item, Items.Mods);
            var charges = FlaskCharges.Of(baseCharges, local);
            double gain = chargesFor(flask, local);
            if (gain <= 0 || flask.Item.Charges >= charges.MaxCharges) continue;
            int whole = conn.FlaskChargeCarry.Take(flask.Id, gain * FlaskCharges.GainMultiplier(local, Get));
            if (whole > 0 && items.ChangeUnsaved(PlayerInventories.Flask, flask.Id, i => Flasks.Gain(i, charges, whole)) is { } done)
                changed.Add(done);
        }
        if (changed.Count == 0) return;
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Flask),
            items.NextRevision(PlayerInventories.Flask), [.. changed.Select(c => c.Id)], changed),
            $"0x00bf flasks: {string.Join(", ", changed.Select(c => $"{c.X}: {c.Item.Charges}"))} charges ({why})");
    }

    // A kill: 1 / 3.5 / 6 / 11 charges to every flask by the monster's rarity (FlaskCharges.KillCharges), or with
    // Game_FLASK_CHARGES=full every flask full.
    static Task GainKillCharges(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, MonsterRarity rarity, string why) =>
        FlaskCharges.FullShortcut ? GainFlaskCharges(remote, stream, ciphers, conn, int.MaxValue, why)
            : GainFlaskCharges(remote, stream, ciphers, conn, (_, _) => FlaskCharges.KillCharges(rarity, Items.Mods), why);

    // A critical strike by the player: each flask with "#% chance to gain a Flask Charge when you deal a Critical Strike"
    // (local_recharge_on_crit_%) rolls for one charge, and the character's gain_flask_charge_when_crit_% gives one flask that
    // is not full gain_flask_charge_when_crit_amount (1 without it). One cooldown for all of them (FlaskCharges.CritCooldown).
    static async Task GainFlaskChargesOnCrit(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var character = CharacterPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint key ? character.GetValueOrDefault(key) : 0;
        var bar = ItemsOf(conn).Items(PlayerInventories.Flask);
        int global = Get("gain_flask_charge_when_crit_%");
        if (global <= 0 && !bar.Any(f => ItemModStats.Local(f.Item, Items.Mods).GetValueOrDefault("local_recharge_on_crit_%") > 0)) return;
        if (!conn.FlaskChargeCarry.TryCrit(conn.Clock.Elapsed.TotalSeconds)) return;
        uint? globalFlask = global > 0 && Random.Shared.Next(100) < global
            ? bar.FirstOrDefault(f => Items.ByHash(f.Item.TypeHash) is { } t && Items.FlaskChargesOf(t) is { } c
                && f.Item.Charges < FlaskCharges.Of(c, ItemModStats.Local(f.Item, Items.Mods)).MaxCharges)?.Id
            : null;
        int globalAmount = Math.Max(1, Get("gain_flask_charge_when_crit_amount"));
        await GainFlaskCharges(remote, stream, ciphers, conn, (f, local) =>
            (Random.Shared.Next(100) < local.GetValueOrDefault("local_recharge_on_crit_%") ? 1 : 0) + (f.Id == globalFlask ? globalAmount : 0),
            "critical strike");
    }

    // The player was hit (not evaded or blocked): "Gain # Charges when you are Hit by an Enemy" (local_flask_gain_X_charges_when_hit).
    static Task GainFlaskChargesWhenHit(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        GainFlaskCharges(remote, stream, ciphers, conn, (_, local) => local.GetValueOrDefault("local_flask_gain_X_charges_when_hit"), "hit");

    // "Flasks gain # charges every 3 seconds": the character's flask_charges_recovered_per_3_seconds (every flask),
    // life_flask_charges_recovered_per_3_seconds and mana_flasks_gain_X_charges_every_3_seconds, every 3 s of game time
    // (a pause holds it) while the connection is open.
    static async Task RunFlaskChargeRegen(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        double next = conn.Clock.Elapsed.TotalSeconds + 3;
        try
        {
            while (!conn.Closed)
            {
                await Task.Delay(250);
                if (conn.Closed || conn.Clock.Elapsed.TotalSeconds < next || conn.PlayerLife == 0) continue;
                next += 3;
                var character = CharacterPlayerStats(conn);
                int Get(string name) => Stats.Key(name) is uint key ? character.GetValueOrDefault(key) : 0;
                int all = Get("flask_charges_recovered_per_3_seconds"), life = Get("life_flask_charges_recovered_per_3_seconds"),
                    mana = Get("mana_flasks_gain_X_charges_every_3_seconds");
                if (all + life + mana <= 0) continue;
                await GainFlaskCharges(remote, stream, ciphers, conn, (f, _) => all
                    + (Items.ByHash(f.Item.TypeHash) is { } t && Items.FlaskOf(t) is { } use ? (use.LifePerUse > 0 ? life : 0) + (use.ManaPerUse > 0 ? mana : 0) : 0),
                    "every 3 seconds");
            }
        }
        catch (Exception ex)
        {
            if (!conn.Closed) Console.WriteLine($"[gateway] {remote} flask charges every 3 s: {ex.Message}");
        }
    }

    // A flask put on the flask bar in a hostile area has no charges (FlaskCharges.WhenEquipped).
    static InventoryItem EmptyIfEquippedInHostileArea(ClientState conn, PlayerInventories items, InventoryItem placed)
    {
        var equipped = FlaskCharges.WhenEquipped(placed.Item, safeArea: InTown(conn) || HideoutCatalog.ByArea(conn.Layout.Hash) is not null);
        return equipped == placed.Item ? placed : items.Change(PlayerInventories.Flask, placed.Id, _ => equipped) ?? placed;
    }
}
