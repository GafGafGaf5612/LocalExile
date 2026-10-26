using GameServer.Core.Protocol.Instance;

/// <summary>
/// Life and mana flasks (Agents/docs/protocol/13-items.md §8): a use (C→S 0x70) spends ComponentCharges PerCharge and
/// recovers Flasks.dat LifePerUse / ManaPerUse evenly over RecoveryTime (with its mods: FlaskRecovery). Charges come back
/// from kills and other sources (FlaskCharges), and in full in towns and hideouts and on resurrection.
/// </summary>
static class Flasks
{
    /// <summary>What one use recovers (life, mana) over <paramref name="Seconds"/>, and its charges: spent per use, and the
    /// most it holds.</summary>
    public sealed record Flask(int LifePerUse, int ManaPerUse, double Seconds, int ChargesPerUse, int MaxCharges);

    /// <summary>The flask after a use, or null when it has fewer charges than a use needs.</summary>
    public static Item? Spend(Item flask, Flask f) => flask.Charges < f.ChargesPerUse ? null : flask with { Charges = flask.Charges - f.ChargesPerUse };

    /// <summary>Gives each flask in the flask bar (life, mana and utility: <see cref="ItemCatalog.FlaskChargesOf"/>) up to
    /// <paramref name="charges"/> charges, up to its maximum with its mods (<see cref="FlaskCharges.Of"/>), saved now or, with <paramref name="saveNow"/> false, with the next save
    /// (PlayerInventories.ChangeUnsaved): the flasks that changed.</summary>
    public static List<InventoryItem> Fill(PlayerInventories items, ItemCatalog catalog, int charges, bool saveNow = true)
    {
        List<InventoryItem> changed = [];
        foreach (var flask in items.Items(PlayerInventories.Flask))
            if (catalog.ByHash(flask.Item.TypeHash) is { } type && catalog.FlaskChargesOf(type) is { } baseUse
                && FlaskCharges.Of(baseUse, ItemModStats.Local(flask.Item, catalog.Mods)) is var use && flask.Item.Charges < use.MaxCharges &&
                (saveNow ? items.Change(PlayerInventories.Flask, flask.Id, i => Gain(i, use, charges)) : items.ChangeUnsaved(PlayerInventories.Flask, flask.Id, i => Gain(i, use, charges))) is { } done)
                changed.Add(done);
        return changed;
    }

    /// <summary>The flask with <paramref name="charges"/> more, up to its maximum.</summary>
    public static Item Gain(Item flask, Flask f, int charges) =>
        flask with { Charges = (int)Math.Min(f.MaxCharges, (long)flask.Charges + charges) };

    /// <summary>A flask's buff on the player (S→C 0x1c1): its duration in flags bit1's field, which the client reads as an
    /// f32 in seconds (+0x68, reader 0x141fdf870; without it +infinity), and the flask-bar cell as the source skill (+0x60):
    /// the client draws the effect's draining timer bar under that flask (live 2026-10-25). <paramref name="values"/>: the
    /// IsRecovery rows' one u32 per GrantedStats entry.</summary>
    public static byte[] Buff(uint player, ushort buff, ushort row, byte slot, double? seconds, IReadOnlyList<uint>? values = null) =>
        InstanceEntities.AddBuff(player, buff, row, player, slot, values,
            u68: seconds is double s ? BitConverter.SingleToUInt32Bits((float)s) : null);

    /// <summary>A flask's recovery under way: the flask-bar cell it came from, its buff (S→C 0x1c1; 0: none, the extra life
    /// of a life flask's suffix) and how much life and mana it gives per second from <paramref name="Started"/> to
    /// <paramref name="Ends"/> (connection clock seconds); mana that comes all at once when it runs out
    /// (<paramref name="ManaAtEnd"/>), and whether it stays on a full pool (FlaskRecovery.Use).</summary>
    public sealed record Effect(byte Slot, ushort Buff, ushort BuffRow, double LifePerSecond, double ManaPerSecond, double Started, double Ends,
        double ManaAtEnd = 0, bool LifeStays = false, bool ManaStays = false)
    {
        /// <summary>Whether a full pool ends it: a life effect when life is full, a mana one when mana is (a hybrid one when
        /// both are), unless it stays at full.</summary>
        public bool EndsWhenFull(bool lifeFull, bool manaFull) =>
            (LifePerSecond == 0 || lifeFull && !LifeStays) && (ManaPerSecond == 0 && ManaAtEnd == 0 || manaFull && !ManaStays);
    }

    /// <summary>The flasks recovering on one connection, at most one per flask-bar cell.</summary>
    public sealed class Effects
    {
        readonly List<Effect> _effects = [];
        readonly Lock _lock = new();

        /// <summary>Whether any flask is recovering.</summary>
        public bool Any
        {
            get { lock (_lock) return _effects.Count > 0; }
        }

        /// <summary>Starts an effect; a use of a flask whose effect is still running replaces it (the rest of the old one is
        /// lost): the replaced effect, whose buff ends, or null. A flask's extra life (no buff) only replaces its own.</summary>
        public Effect? Start(Effect effect)
        {
            lock (_lock)
            {
                var old = _effects.FirstOrDefault(e => e.Slot == effect.Slot && (e.Buff == 0) == (effect.Buff == 0));
                if (old is not null) _effects.Remove(old);
                _effects.Add(effect);
                return old;
            }
        }

        /// <summary>The life and mana recovered between <paramref name="from"/> and <paramref name="to"/>, and the effects
        /// that ended by <paramref name="to"/> (removed); an effect that runs out then gives its mana at the end too.</summary>
        public (double Life, double Mana, List<Effect> Ended) Advance(double from, double to)
        {
            double life = 0, mana = 0;
            List<Effect> ended = [];
            lock (_lock)
                foreach (var e in _effects.ToList())
                {
                    double seconds = Math.Max(0, Math.Min(to, e.Ends) - Math.Max(from, e.Started));
                    life += e.LifePerSecond * seconds;
                    mana += e.ManaPerSecond * seconds;
                    if (e.Ends <= to)
                    {
                        mana += e.ManaAtEnd;
                        _effects.Remove(e);
                        ended.Add(e);
                    }
                }
            return (life, mana, ended);
        }

        /// <summary>Of the effects a full pool ended (<paramref name="ended"/>), the ones that stay at a full pool or still
        /// owe their mana at the end (<see cref="Effect.EndsWhenFull"/>) and have time left go back: the rest, which end.</summary>
        public List<Effect> KeepRunning(List<Effect> ended, bool lifeFull, bool manaFull, double now)
        {
            lock (_lock)
            {
                var back = ended.Where(e => e.Ends > now && !e.EndsWhenFull(lifeFull, manaFull)).ToList();
                _effects.AddRange(back);
                return [.. ended.Except(back)];
            }
        }

        /// <summary>Ends the effects <paramref name="which"/> picks (a full pool, death): them, removed.</summary>
        public List<Effect> End(Func<Effect, bool> which)
        {
            lock (_lock)
            {
                List<Effect> ended = [.. _effects.Where(which)];
                foreach (var e in ended) _effects.Remove(e);
                return ended;
            }
        }
    }
}
