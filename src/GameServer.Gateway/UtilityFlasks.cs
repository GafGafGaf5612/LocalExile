using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// Utility flasks (Agents/docs/protocol/13-items.md §8a): Quicksilver, Granite, Jade, Ruby, Sapphire, Topaz, Amethyst,
/// Bismuth, Basalt, Stibnite, Diamond, Quartz, Sulphur and Silver. Everything comes from the tables: Flasks.dat names the
/// flask's BuffDefinitions row, its BuffStatValues (one per StatsKeys entry) and its duration (RecoveryTime, tenths of a
/// second); ComponentCharges its charges. A use puts the buff on the player for the duration and its stats count on the
/// character until it ends (<see cref="Apply"/>, <see cref="RawStats"/>). Only the stats the server models count
/// (<see cref="Modelled"/>); a flask with none of them (Aquamarine, Gold, Iron, Corundum) is not usable.
/// </summary>
static class UtilityFlasks
{
    /// <summary>One base type's use: its buff (BuffDefinitions row and Id), the buff's stats with their values, how long
    /// it lasts, and its charges. <paramref name="Extra"/>: a second buff the flask grants with its own stats (the Silver
    /// Flask's Onslaught).</summary>
    public sealed record Utility(int BaseRow, ushort BuffRow, string BuffId, IReadOnlyList<(string Stat, int Value)> Stats,
        double Seconds, int ChargesPerUse, int MaxCharges, ExtraBuff? Extra = null)
    {
        /// <summary>The charges as <see cref="Flasks"/> spends and refills them.</summary>
        public Flasks.Flask Charges => new(0, 0, Seconds, ChargesPerUse, MaxCharges);

        /// <summary>The stats this flask applies (its buff's and the extra buff's), with unmodelled ones left out.</summary>
        public IEnumerable<(string Stat, int Value)> Applied => Stats.Concat(Extra?.Stats ?? []).Where(s => Modelled.Contains(s.Stat));
    }

    /// <summary>A buff a flask grants besides its own (BuffDefinitions row, with its stats).</summary>
    public sealed record ExtraBuff(ushort BuffRow, string BuffId, IReadOnlyList<(string Stat, int Value)> Stats);

    /// <summary>The buff stats the server applies to the character, and where: the pools (<see cref="Apply"/>) or the raw
    /// stats (<see cref="RawStats"/>), or both.</summary>
    public static readonly IReadOnlySet<string> Modelled = new HashSet<string>(StringComparer.Ordinal)
    {
        "base_fire_damage_resistance_%", "base_cold_damage_resistance_%", "base_lightning_damage_resistance_%",
        "base_chaos_damage_resistance_%", "base_resist_all_elements_%",
        "base_maximum_fire_damage_resistance_%", "base_maximum_cold_damage_resistance_%",
        "base_maximum_lightning_damage_resistance_%", "base_maximum_chaos_damage_resistance_%",
        "base_physical_damage_reduction_rating", "base_evasion_rating",
        "basalt_flask_armour_+%_final", "stibnite_flask_evasion_rating_+%_final",
        "base_movement_velocity_+%", "attack_speed_+%", "base_cast_speed_+%",
        "critical_strike_chance_+%", "base_spell_suppression_chance_%", "damage_+%",
    };

    /// <summary>The flask buff flag that stands for Onslaught (the Silver Flask's flask_utility_haste has no stats of its
    /// own), and the BuffTemplates row with the player's Onslaught values (20% attack, cast and movement speed).</summary>
    const string OnslaughtFlag = "silver_flask_display_onslaught", OnslaughtBuff = "onslaught", OnslaughtTemplate = "MapModPlayerOnslaught";

    /// <summary>The utility flasks by BaseItemTypes row, from data/tables Flasks, BuffDefinitions, BuffTemplates,
    /// ComponentCharges and Stats. Flasks with no modelled stat are left out.</summary>
    public static Dictionary<int, Utility> Load(string folder, IReadOnlyList<ItemCatalog.BaseType> rows)
    {
        var result = new Dictionary<int, Utility>();
        string Table(string name) => Path.Combine(folder, "tables", name + ".json");
        if (new[] { "Flasks", "BuffDefinitions", "BuffTemplates", "ComponentCharges", "Stats" }.Any(t => !File.Exists(Table(t)))) return result;
        var stats = new StatCatalog(folder);
        string StatName(JsonElement row) => stats.Name((uint)row.GetInt32() + 1);

        var charges = new Dictionary<string, (int Max, int PerUse)>();
        using (var c = JsonDocument.Parse(File.ReadAllBytes(Table("ComponentCharges"))))
            foreach (var r in c.RootElement.EnumerateArray())
                charges.TryAdd(r.GetProperty("BaseItemTypesKey").GetString() ?? "", (r.GetProperty("MaxCharges").GetInt32(), r.GetProperty("PerCharge").GetInt32()));
        using var buffs = JsonDocument.Parse(File.ReadAllBytes(Table("BuffDefinitions")));
        var buffRows = buffs.RootElement.EnumerateArray().ToList();
        int onslaught = buffRows.FindIndex(b => b.GetProperty("Id").GetString() == OnslaughtBuff);
        ExtraBuff? onslaughtBuff = null;
        if (onslaught >= 0)
        {
            using var templates = JsonDocument.Parse(File.ReadAllBytes(Table("BuffTemplates")));
            foreach (var t in templates.RootElement.EnumerateArray())
                if (t.GetProperty("Id").GetString() == OnslaughtTemplate && t.GetProperty("BuffDefinitionsKey") is { ValueKind: JsonValueKind.Number } k && k.GetInt32() == onslaught)
                    onslaughtBuff = new((ushort)onslaught, OnslaughtBuff,
                        [.. buffRows[onslaught].GetProperty("StatsKeys").EnumerateArray().Select(StatName)
                            .Zip(t.GetProperty("Buff_StatValues").EnumerateArray().Select(v => v.GetInt32()))]);
        }

        using var flasks = JsonDocument.Parse(File.ReadAllBytes(Table("Flasks")));
        foreach (var f in flasks.RootElement.EnumerateArray())
        {
            if (f.GetProperty("BaseItemTypesKey") is not { ValueKind: JsonValueKind.Number } b || b.GetInt32() >= rows.Count
                || f.GetProperty("BuffDefinitionsKey") is not { ValueKind: JsonValueKind.Number } bk || bk.GetInt32() >= buffRows.Count
                || f.GetProperty("LifePerUse").GetInt32() + f.GetProperty("ManaPerUse").GetInt32() > 0
                || !charges.TryGetValue(rows[b.GetInt32()].Id, out var use)) continue;
            var buff = buffRows[bk.GetInt32()];
            // BuffStatValues has one value per StatsKeys entry (Ruby Flask: base_fire_damage_resistance_% 40,
            // base_maximum_fire_damage_resistance_% 5).
            List<(string, int)> values = [.. buff.GetProperty("StatsKeys").EnumerateArray().Select(StatName)
                .Zip(f.GetProperty("BuffStatValues").EnumerateArray().Select(v => v.GetInt32()))];
            bool grantsOnslaught = buff.GetProperty("GrantedFlags").EnumerateArray().Any(s => StatName(s) == OnslaughtFlag);
            // RecoveryTime is in tenths of a second, as for life flasks (Quicksilver Flask: 60, 6 s).
            var utility = new Utility(b.GetInt32(), (ushort)bk.GetInt32(), buff.GetProperty("Id").GetString() ?? "", values,
                f.GetProperty("RecoveryTime").GetInt32() / 10.0, use.PerUse, use.Max, grantsOnslaught ? onslaughtBuff : null);
            if (utility.Applied.Any()) result.TryAdd(b.GetInt32(), utility);
        }
        return result;
    }

    /// <summary>One use of a flask: its stats scaled by the flask's increased effect, and its duration with its quality
    /// and increased duration (the flask's own local mods, ItemModStats.Local by Stats.dat Id).</summary>
    public static (IReadOnlyList<(string Stat, int Value)> Stats, double Seconds) For(Utility u, int quality, IReadOnlyDictionary<string, int> local)
    {
        int Get(string name) => local.GetValueOrDefault(name);
        // Quality on a utility flask is increased duration, added to the flask's other increased duration.
        double seconds = u.Seconds * (100 + quality + Get("local_flask_duration_+%")) / 100.0
            * (100 + Get("local_flask_duration_+%_final")) / 100.0;
        double effect = Math.Max(0, 100 + Get("local_flask_effect_+%")) / 100.0;
        List<(string, int)> stats = [.. u.Stats.Where(s => Modelled.Contains(s.Stat))
            .Select(s => (s.Stat, (int)Math.Round(s.Value * effect, MidpointRounding.AwayFromZero)))];
        // Onslaught has its own effect stat (onslaught_effect_+%), not the flask's.
        stats.AddRange(u.Extra?.Stats.Where(s => Modelled.Contains(s.Stat)) ?? []);
        return (stats, Math.Max(0.1, seconds));
    }

    /// <summary>A utility flask's effect under way: the flask (item id and flask-bar cell), its buffs on the player, what it
    /// applies, and when it started and ends (connection clock seconds, which stop while the game is paused).</summary>
    public sealed record Active(uint ItemId, byte Slot, string Name, Utility Flask, ushort Buff, ushort? ExtraBuff,
        IReadOnlyList<(string Stat, int Value)> Stats, double Started, double Ends);

    /// <summary>The utility flasks running on one connection, at most one per flask and one per buff.</summary>
    public sealed class Effects
    {
        readonly List<Active> _active = [];
        readonly Lock _lock = new();
        bool _ticking;

        /// <summary>The effect of this flask (item id) still running, or null.</summary>
        public Active? Of(uint itemId)
        {
            lock (_lock) return _active.FirstOrDefault(a => a.ItemId == itemId);
        }

        /// <summary>Starts an effect. A flask of the same kind (same buff) that still runs ends: effects of one kind do not
        /// stack. Returns that one, or null, and whether a ticker has to be started (none runs yet).</summary>
        public (Active? Replaced, bool StartTicker) Start(Active effect)
        {
            lock (_lock)
            {
                var replaced = _active.FirstOrDefault(a => a.Flask.BuffRow == effect.Flask.BuffRow);
                if (replaced is not null) _active.Remove(replaced);
                _active.Add(effect);
                bool start = !_ticking;
                _ticking = true;
                return (replaced, start);
            }
        }

        /// <summary>The effects that ended by <paramref name="now"/> (removed), or all of them with <paramref name="all"/>
        /// (death). <paramref name="stop"/> tells the ticker to stop: nothing runs any more.</summary>
        public List<Active> End(double now, bool all, out bool stop)
        {
            lock (_lock)
            {
                List<Active> ended = [.. _active.Where(a => all || a.Ends <= now)];
                foreach (var a in ended) _active.Remove(a);
                stop = _active.Count == 0;
                if (stop) _ticking = false;
                return ended;
            }
        }

        /// <summary>The stats of every running effect, summed by stat.</summary>
        public IReadOnlyList<(string Stat, int Value)> Stats()
        {
            lock (_lock) return [.. _active.SelectMany(a => a.Stats)];
        }

        /// <summary>Whether any effect runs.</summary>
        public bool Any
        {
            get { lock (_lock) return _active.Count > 0; }
        }
    }

    /// <summary>The character's pool bonuses with the running flasks' stats: resistances and their maxima, armour and
    /// evasion (flat, and the Basalt / Stibnite more multipliers on top of any others), movement and attack speed,
    /// critical strike chance and spell suppression.</summary>
    public static CharacterStats.Bonus Apply(CharacterStats.Bonus b, IEnumerable<(string Stat, int Value)> stats)
    {
        var s = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (stat, value) in stats) s[stat] = s.GetValueOrDefault(stat) + value;
        if (s.Count == 0) return b;
        int Get(string name) => s.GetValueOrDefault(name);
        int all = Get("base_resist_all_elements_%");
        static int More(int a, int c) => (int)Math.Round((100 + a) * (100 + c) / 100.0 - 100);
        return b with
        {
            FireResistance = b.FireResistance + all + Get("base_fire_damage_resistance_%"),
            ColdResistance = b.ColdResistance + all + Get("base_cold_damage_resistance_%"),
            LightningResistance = b.LightningResistance + all + Get("base_lightning_damage_resistance_%"),
            ChaosResistance = b.ChaosResistance + Get("base_chaos_damage_resistance_%"),
            FireMaxResistance = b.FireMaxResistance + Get("base_maximum_fire_damage_resistance_%"),
            ColdMaxResistance = b.ColdMaxResistance + Get("base_maximum_cold_damage_resistance_%"),
            LightningMaxResistance = b.LightningMaxResistance + Get("base_maximum_lightning_damage_resistance_%"),
            ChaosMaxResistance = b.ChaosMaxResistance + Get("base_maximum_chaos_damage_resistance_%"),
            Armour = b.Armour + Get("base_physical_damage_reduction_rating"),
            Evasion = b.Evasion + Get("base_evasion_rating"),
            ArmourMorePct = More(b.ArmourMorePct, Get("basalt_flask_armour_+%_final")),
            EvasionMorePct = More(b.EvasionMorePct, Get("stibnite_flask_evasion_rating_+%_final")),
            MovementVelocityPct = b.MovementVelocityPct + Get("base_movement_velocity_+%"),
            AttackSpeedPct = b.AttackSpeedPct + Get("attack_speed_+%"),
            CriticalStrikeChancePct = b.CriticalStrikeChancePct + Get("critical_strike_chance_+%"),
            SpellCriticalStrikeChancePct = b.SpellCriticalStrikeChancePct + Get("critical_strike_chance_+%"),
            SpellSuppressionChance = b.SpellSuppressionChance + Get("base_spell_suppression_chance_%"),
        };
    }

    /// <summary>The running flasks' stats that the client and the damage code read as plain stats rather than from the
    /// pools: the maximum resistances (the client adds them to its own 75), increased critical strike chance (gem
    /// tooltips, spell crits), increased damage (Sulphur) and cast speed (Onslaught), by client stat key.</summary>
    public static IEnumerable<(uint Key, int Value)> RawStats(IEnumerable<(string Stat, int Value)> stats, StatCatalog catalog)
    {
        var result = new Dictionary<uint, int>();
        foreach (var (stat, value) in stats)
        {
            string? name = stat switch
            {
                "base_maximum_fire_damage_resistance_%" or "base_maximum_cold_damage_resistance_%"
                    or "base_maximum_lightning_damage_resistance_%" or "base_maximum_chaos_damage_resistance_%"
                    or "critical_strike_chance_+%" or "damage_+%" => stat,
                "base_cast_speed_+%" => "cast_speed_+%",
                _ => null,
            };
            if (name is not null && catalog.Key(name) is uint key) result[key] = result.GetValueOrDefault(key) + value;
        }
        return result.Select(kv => (kv.Key, kv.Value));
    }
}
