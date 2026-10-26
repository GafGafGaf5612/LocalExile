using GameServer.Core.Protocol.Instance;

/// <summary>
/// Life and mana flasks (Agents/docs/protocol/13-items.md §8): a use (C→S 0x70) spends ComponentCharges PerCharge and
/// recovers Flasks.dat LifePerUse / ManaPerUse evenly over RecoveryTime. Charges come back in full on every kill
/// (<see cref="KillCharges"/>), in towns and hideouts and on resurrection.
/// </summary>
static class Flasks
{
    /// <summary>What one use recovers (life, mana) over <paramref name="Seconds"/>, and its charges: spent per use, and the
    /// most it holds.</summary>
    public sealed record Flask(int LifePerUse, int ManaPerUse, double Seconds, int ChargesPerUse, int MaxCharges);

    /// <summary>The flask after a use, or null when it has fewer charges than a use needs.</summary>
    public static Item? Spend(Item flask, Flask f) => flask.Charges < f.ChargesPerUse ? null : flask with { Charges = flask.Charges - f.ChargesPerUse };

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

    // shortcut: the real game's charges per kill are not known yet; "rarity" grows with rarity.
    /// <summary>Charges every flask gains when the player kills a monster: all it holds (int.MaxValue, the default), or
    /// with <paramref name="mode"/> "rarity" (Game_FLASK_KILL_CHARGES) 1 normal, 2 magic, 5 rare, 10 unique.</summary>
    public static int KillCharges(MonsterRarity rarity, string? mode = null) => mode != "rarity" ? int.MaxValue : rarity switch
    {
        MonsterRarity.Magic => 2, MonsterRarity.Rare => 5, MonsterRarity.Unique => 10, _ => 1,
    };

    /// <summary>A flask's recovery under way: the flask-bar cell it came from, its buff (S→C 0x1c1) and how much life
    /// and mana it gives per second from <paramref name="Started"/> to <paramref name="Ends"/> (connection clock seconds).</summary>
    public sealed record Effect(byte Slot, ushort Buff, ushort BuffRow, double LifePerSecond, double ManaPerSecond, double Started, double Ends);

    /// <summary>The flasks recovering on one connection, at most one per flask-bar cell.</summary>
    public sealed class Effects
    {
        readonly Dictionary<byte, Effect> _bySlot = [];
        readonly Lock _lock = new();

        /// <summary>Whether any flask is recovering.</summary>
        public bool Any
        {
            get { lock (_lock) return _bySlot.Count > 0; }
        }

        /// <summary>Starts an effect; a use of a flask whose effect is still running replaces it (the rest of the old one is
        /// lost): the replaced effect, whose buff ends, or null.</summary>
        public Effect? Start(Effect effect)
        {
            lock (_lock)
            {
                _bySlot.Remove(effect.Slot, out var old);
                _bySlot[effect.Slot] = effect;
                return old;
            }
        }

        /// <summary>The life and mana recovered between <paramref name="from"/> and <paramref name="to"/>, and the effects
        /// that ended by <paramref name="to"/> (removed).</summary>
        public (double Life, double Mana, List<Effect> Ended) Advance(double from, double to)
        {
            double life = 0, mana = 0;
            List<Effect> ended = [];
            lock (_lock)
                foreach (var e in _bySlot.Values.ToList())
                {
                    double seconds = Math.Max(0, Math.Min(to, e.Ends) - Math.Max(from, e.Started));
                    life += e.LifePerSecond * seconds;
                    mana += e.ManaPerSecond * seconds;
                    if (e.Ends <= to)
                    {
                        _bySlot.Remove(e.Slot);
                        ended.Add(e);
                    }
                }
            return (life, mana, ended);
        }

        /// <summary>Ends the effects <paramref name="which"/> picks (a full pool, death): them, removed.</summary>
        public List<Effect> End(Func<Effect, bool> which)
        {
            lock (_lock)
            {
                List<Effect> ended = [.. _bySlot.Values.Where(which)];
                foreach (var e in ended) _bySlot.Remove(e.Slot);
                return ended;
            }
        }
    }
}
