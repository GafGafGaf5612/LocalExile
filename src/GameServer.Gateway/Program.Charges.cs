using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

partial class Program
{
    // Endurance, Frenzy and Power charges (issue #237, 11-skills.md §15). Each connection has a ChargeSession: the combat
    // code enqueues what may grant charges (ChargesOnHit, ChargesOnKill, ChargesWhenHit) and RunCharges rolls, times,
    // expires and presents them. The charges held count in the character's stats through WithCharges.
    static readonly ConditionalWeakTable<ClientState, ChargeSession> ChargeSessions = new();

    // The charges an area change hands on, by the destination's ticket (Transfer): kept with the duration they had left.
    // A ticket nobody connects with (a failed load) is forgotten after a few minutes.
    static readonly ConcurrentDictionary<uint, (DateTime At, List<Charges.Carried> Charges)> CarriedCharges = new();

    static ChargeSession ChargesOf(ClientState conn) => ChargeSessions.GetValue(conn, _ => new ChargeSession());

    // A player hit on a monster that landed (Attack, AreaSkillHit, a projectile's impact): its "on hit", "on critical
    // strike" and "on non-critical strike" gains. effect: the gem skill's GrantedEffects row (-1: the default attack).
    static void ChargesOnHit(ClientState conn, SpawnedMonster m, bool critical, bool spell, int effect = -1, int level = 1,
        int quality = 0, bool? melee = null)
    {
        Func<string, int>? skill = null;
        if (effect >= 0)
        {
            var own = Skills.EffectStats(effect, level, quality).ToDictionary(kv => kv.Item1, kv => kv.Item2);
            skill = name => Stats.Key(name) is uint key ? own.GetValueOrDefault(key) : 0;
        }
        ChargesOf(conn).Enqueue(new ChargeEvent.Hit(critical, spell, melee, m.Rarity, effect >= 0 ? Skills.EffectId(effect) : null,
            skill, Killed: m.Dead || m.Life == 0));
    }

    // A hostile monster the player killed (MonsterKilled): its "on kill" gains.
    static void ChargesOnKill(ClientState conn, SpawnedMonster m)
    {
        if (conn.PlayerLife > 0) ChargesOf(conn).Enqueue(new ChargeEvent.Kill(m.Rarity));
    }

    // A monster's hit on the player that landed (when hit) or was blocked (on block).
    static void ChargesWhenHit(ClientState conn, bool blocked, bool spell = false) =>
        ChargesOf(conn).Enqueue(blocked ? new ChargeEvent.Block(spell) : new ChargeEvent.WhenHit());

    // Transfer: the charges go with the player to the area of the new ticket.
    static void CarryCharges(ClientState conn, uint ticket)
    {
        List<Charges.Carried> carried;
        lock (conn.Monsters) carried = ChargesOf(conn).State.Carry(conn.Clock.Elapsed.TotalSeconds);
        foreach (var (old, entry) in CarriedCharges)
            if (DateTime.UtcNow - entry.At > TimeSpan.FromMinutes(5)) CarriedCharges.TryRemove(old, out _);
        if (carried.Count > 0) CarriedCharges[ticket] = (DateTime.UtcNow, carried);
    }

    // The character's stat sources with its charges (CharacterStatSources): Character.ot's per-charge stats, what the
    // charges held add (ChargeEffects.Expand) and their counts for the panel and the buffs.
    static Dictionary<uint, int> WithCharges(ClientState conn, Dictionary<uint, int> sources)
    {
        var stats = new Dictionary<uint, int>(sources);
        foreach (var (stat, value) in ChargeEffects.Inherent)
            if (Stats.Key(stat) is uint key) stats[key] = stats.GetValueOrDefault(key) + value;
        // A stat /set_stat set keeps its value here too (as in CharacterPlayerStats), so a per-charge stat can be tried out.
        Dictionary<uint, int> dev;
        lock (conn.Monsters) dev = new(conn.PlayerStats);
        int Get(string name) => Stats.Key(name) is uint k ? dev.TryGetValue(k, out int set) ? set : stats.GetValueOrDefault(k) : 0;
        var session = ChargesOf(conn);
        var limits = new Dictionary<ChargeType, Charges.Limits>();
        var held = new Dictionary<ChargeType, int>();
        lock (conn.Monsters)
            foreach (var type in Charges.Types)
            {
                limits[type] = Charges.LimitsOf(type, Get, session.State.Held(type));
                held[type] = session.State.Effective(type, limits[type]);
            }
        var added = ChargeEffects.Expand(Get, t => held[t]);
        foreach (var (stat, value) in added.Select(kv => (Stat: kv.Key, kv.Value)).Concat(ChargeEffects.Display(t => held[t], t => limits[t])))
            if (Stats.Key(stat) is uint key) stats[key] = stats.GetValueOrDefault(key) + value;
        return stats;
    }

    // The more multiplier of the charges on a hit (AttackHit, SpellDamage) or a burn, from the final stats.
    static double ChargeDamageMore(IReadOnlyDictionary<uint, int> stats, bool spell = false) =>
        ChargeEffects.DamageMore(name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0, spell);

    // The player's additional physical and elemental damage reduction (Endurance Charges, passives) for hits taken.
    static (int Physical, int Elemental) PlayerDamageReduction(ClientState conn)
    {
        var stats = CharacterPlayerStats(conn);
        return ChargeEffects.Reduction(name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0);
    }

    // A hit's damage taken after the player's mitigation (the dev /hit commands, MonsterSwing's rules): physical through
    // armour plus the additional physical damage reduction, the other types through resistance, and the elemental ones
    // through the additional elemental damage reduction after it.
    static int MitigateTaken(ClientState conn, DamageType type, int amount, int resistance, int maxResistance)
    {
        var reduction = PlayerDamageReduction(conn);
        if (type == DamageType.Physical) return DamageModel.MitigatePhysical(amount, conn.Pools.Armour, reduction.Physical);
        int resisted = DamageModel.Mitigate(amount, resistance, maxResistance);
        return DamageTypes.IsElemental(type) ? DamageModel.Reduce(resisted, reduction.Elemental) : resisted;
    }

    // What MitigateTaken applied, for the dev commands' replies: armour or resistance, and the additional reduction.
    static string MitigationLabel(ClientState conn, DamageType type, int amount, int resistance, int maxResistance)
    {
        var (physical, elemental) = PlayerDamageReduction(conn);
        if (type == DamageType.Physical)
            return $"{DamageModel.ArmourReduction(conn.Pools.Armour, amount)}% armour" + (physical > 0 ? $" + {physical}% physical damage reduction" : "");
        return $"{DamageModel.Capped(resistance, maxResistance)}% resistance" +
            (DamageTypes.IsElemental(type) && elemental > 0 ? $" + {elemental}% elemental damage reduction" : "");
    }

    // Warcries that grant charges (CastGemSkill): Enduring Cry gives an Endurance Charge per 5 power
    // (endurance_charge_granted_per_X_monster_power_during_endurance_warcry) of the hostile monsters within its radius
    // (Path of Building: 60, grown by area of effect; power 1 normal, 2 magic, 10 rare, 20 unique), when its cast is over.
    // True when the skill was one of these.
    static bool UseChargeSkill(ClientState conn, int effect, int level, int quality, (int X, int Y) at)
    {
        if (Skills.EffectId(effect) != "EnduringCry") return false;
        var own = Skills.EffectStats(effect, level, quality).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        int Own(string name) => Stats.Key(name) is uint key ? own.GetValueOrDefault(key) : 0;
        int per = Own("endurance_charge_granted_per_X_monster_power_during_endurance_warcry");
        double castSeconds = Skills.CastTimeMs(effect) / 1000.0 / Math.Max(0.01, 1 + Own("warcry_speed_+%") / 100.0);
        int action = conn.ActionCount;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(castSeconds));
            await WhilePaused(conn);
            if (conn.Closed || conn.ActionCount != action || conn.PlayerLife == 0) return;
            var player = FinalPlayerStats(conn).ToDictionary(kv => kv.Item1, kv => kv.Item2);
            foreach (var (k, v) in own) player[k] = v;
            int radius = SkillArea.Radius(EnduringCryRadius, name => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0, spell: false);
            double now = conn.Clock.Elapsed.TotalSeconds;
            int power;
            lock (conn.Monsters)
            {
                var here = conn.Walk.Position(now);
                power = conn.Monsters.Values.Where(m => m.Hostile && !m.Dead && m.Life > 0 && Dist(m.Walk.Position(now), here) <= radius)
                    .Sum(m => WarcryPower(m.Rarity));
            }
            int charges = per > 0 ? power / per : 0;
            Console.WriteLine($"[gateway] Enduring Cry: {power} power within {radius} cells, {charges} endurance charge(s)");
            if (charges > 0) ChargesOf(conn).Enqueue(new ChargeEvent.Grant(ChargeType.Endurance, charges, $"Enduring Cry ({power} power)"));
        });
        return true;
    }

    // Path of Building's EnduringCry radius (skill("radius", 60)); one unit is one cell.
    const int EnduringCryRadius = 60;

    // A monster's warcry power (Path of Building's ConfigOptions): normal 1, magic 2, rare 10, unique 20.
    internal static int WarcryPower(MonsterRarity rarity) => rarity switch
    {
        MonsterRarity.Magic => 2,
        MonsterRarity.Rare => 10,
        MonsterRarity.Unique => 20,
        _ => 1,
    };

    // The charges ticker of one connection, started with the area's other loops: every ChargeSession.StepSeconds of game
    // time (a pause stops it) it steps the session and sends what changed, the charge buffs (ChargeBuffs) and, when the
    // counts the stats use changed, the character's stats (pools, walk speed, 0x1b3, skills).
    static async Task RunCharges(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var session = ChargesOf(conn);
        if (CarriedCharges.TryRemove(conn.Ticket, out var entry) && entry.Charges is var carried)
            lock (conn.Monsters)
            {
                session.State.Restore(carried, conn.Clock.Elapsed.TotalSeconds);
                Console.WriteLine($"[gateway] {remote} charges carried from the last area: " +
                    string.Join(", ", carried.Select(c => $"{c.Held} {Charges.Name(c.Type)} ({c.Remaining:0.0} s left)")));
            }
        while (!conn.Closed)
        {
            await Task.Delay(TimeSpan.FromSeconds(ChargeSession.StepSeconds));
            if (conn.Closed) break;
            if (conn.Paused) continue;
            try
            {
                double now = conn.Clock.Elapsed.TotalSeconds;
                ChargeSession.StepResult step;
                List<ChargeSession.BuffChange> buffs;
                lock (conn.Monsters)
                {
                    step = session.Step(now, () =>
                        {
                            var stats = CharacterPlayerStats(conn);
                            return name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
                        }, () => WeaponProfileFor(conn), alive: conn.PlayerLife > 0, moving: now < conn.Walk.ArrivesAt,
                        inTown: InTown(conn), Random.Shared);
                    if (step.CountsChanged)
                    {
                        conn.Pools = PoolsFor(conn, conn.Pools.Level);
                        conn.ApplyMovementSpeed();
                    }
                    buffs = session.Presentation(now, () => conn.NextBuff++);
                }
                foreach (string line in step.Log) Console.WriteLine($"[gateway] {remote} {line} (t={now:0.000})");
                foreach (var change in buffs)
                    foreach (var (packet, label) in ChargeBuffs.Packets(change, PlayerId))
                        await Send(remote, stream, ciphers, packet, label);
                if (step.CountsChanged)
                    await SendCharacterStats(remote, stream, ciphers, conn,
                        "charges " + string.Join(", ", Charges.Types.Select(t => $"{session.Effective(t)} {Charges.Name(t)}")));
            }
            catch (Exception ex) when (ex is not (IOException or ObjectDisposedException))
            {
                if (!conn.Closed) Console.WriteLine($"[gateway] {remote} charges: {ex.Message}");
            }
            catch (Exception) { break; }   // the connection has gone
        }
    }

    const string ChargesUsage = "/charges [endurance|frenzy|power <count>] | /charges add <type> [count] | /charges clear " +
        "(dev: the charges held, their maximum, minimum and time left; set, gain or clear them)";

    // /charges: shows the charges, or sets (no refresh roll), gains (as a skill would: refreshes, can reach the maximum
    // chance) or clears them. The ticker applies the change within a step.
    static async Task ChargesCommand(ClientState conn, string[] args)
    {
        var session = ChargesOf(conn);
        if (args.Length == 0)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            string text;
            lock (conn.Monsters)
                text = string.Join("; ", Charges.Types.Select(t =>
                {
                    var l = session.Limits(t);
                    return $"{Charges.Name(t)} {session.Effective(t)}/{l.Maximum} (minimum {l.Minimum}, " +
                        $"{l.DurationMs / 1000.0:0.###} s, {session.State.Remaining(t, now):0.0} s left)";
                }));
            await conn.Commands!.Reply($"Charges: {text}.");
            return;
        }
        if (args[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            session.Enqueue(new ChargeEvent.Set(null, 0));
            await conn.Commands!.Reply("Charges cleared.");
            return;
        }
        bool add = args[0].Equals("add", StringComparison.OrdinalIgnoreCase);
        if (add) args = args[1..];
        if (args.Length == 0 || Charges.Parse(args[0]) is not { } type) throw new ArgumentException("endurance, frenzy or power");
        int count = args.Length > 1 ? int.Parse(args[1]) : 1;
        if (count < 0) throw new ArgumentException("a count of 0 or more");
        session.Enqueue(add ? new ChargeEvent.Grant(type, count, "/charges add") : new ChargeEvent.Set(type, count));
        await conn.Commands!.Reply($"{(add ? "Gaining" : "Setting")} {count} {Charges.Name(type)} charge(s).");
    }
}
