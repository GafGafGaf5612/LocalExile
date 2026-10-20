using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

/// <summary>
/// Skills the server grants: a GrantedEffects Id at a level becomes the GrantedEffectsPerLevel row and the
/// GrantedEffectStatSetsPerLevel row (of the effect's StatSet1 at that gem level) the Actor skill list refers to
/// (Agents/docs/protocol/11-skills.md). Reads data/tables (game data, loaded on first use). Without the tables it
/// knows only the default attack, PlayerMelee at level 1 (GEPL 4474, GESSPL 4430 in 3.29.3b).
/// </summary>
sealed class SkillCatalog(string folder = "data")
{
    /// <summary>The ids the server gives its skills (+0x40). Basic actions use their ActionTypes HASH16 instead, so these
    /// must not collide with the type's basic actions (Character.ot: Move 0x2909, Interaction 0x0266 …). 0x0101 did: a
    /// Monster.ot basic action masks to it. 0x0102 is no ActionType's id in 3.29.3b.</summary>
    public const ushort FirstSkillId = 0x0102;

    private readonly Lazy<Tables?> _tables = new(() => Tables.Load(folder));
    private readonly StatCatalog _stats = new(folder);

    /// <summary>Skill ids for the server's skills from <see cref="FirstSkillId"/> on, skipping every id an ActionType's
    /// HASH16 masks to (0x3fff): 0x0106, 0x010a and 0x0114 are basic actions in 3.29.3b, and a gem skill numbered after
    /// the default attack would replace one.</summary>
    public IEnumerable<ushort> SkillIds()
    {
        var taken = _actionIds.Value;
        for (int id = FirstSkillId; id <= 0x3fff; id++)
            if (!taken.Contains((ushort)id)) yield return (ushort)id;
    }

    /// <summary>Whether an id is one of the gem skills' (<see cref="SkillIds"/> after the default attack, the first 64), not a
    /// basic action such as Move.</summary>
    public bool IsGemSkill(ushort id) => id > FirstSkillId && id < FirstSkillId + 64 && !_actionIds.Value.Contains(id);

    private readonly Lazy<HashSet<ushort>> _actionIds = new(() =>
    {
        string path = Path.Combine(folder, "tables", "ActionTypes.json");
        if (!File.Exists(path)) return [0x0101, 0x0106, 0x010a, 0x0114];
        using var d = JsonDocument.Parse(File.ReadAllBytes(path));
        return [.. d.RootElement.EnumerateArray().Select(r => (ushort)(r.GetProperty("HASH16").GetInt32() & 0x3fff))];
    });

    /// <summary>
    /// A gem skill's own stats at a level (client keys = Stats.dat row + 1): the effect's stat set (GrantedEffectStatSets
    /// ImplicitStats = 1, ConstantStats with their values) and its per-level row (GESSPL FloatStats with
    /// BaseResolvedValues, e.g. Fireball's spell_minimum/maximum_base_fire_damage 9–14 at level 1, and AdditionalStats),
    /// then the cast: base_spell_cast_time_ms (GrantedEffects CastTime), hundred_times_casts_per_second, and the mana cost
    /// (GEPL CostAmounts of CostTypes 0 = mana). No modifiers yet: the base values are the final ones.
    /// </summary>
    public IReadOnlyList<(uint, int)> EffectStats(int effectRow, int level)
    {
        if (_tables.Value is not { } t || effectRow < 0 || effectRow >= t.Effects.Count) return [];
        var e = t.Effects[effectRow];
        var stats = new Dictionary<uint, int>();
        var constant = new HashSet<uint>();
        if (e.StatSet >= 0 && e.StatSet < t.StatSetBase.Count)
        {
            var (implicits, constants) = t.StatSetBase[e.StatSet];
            foreach (int s in implicits) stats[(uint)s + 1] = 1;
            foreach (var (s, v) in constants) { stats[(uint)s + 1] = v; constant.Add((uint)s + 1); }
        }
        int gesspl = PickLevel(t.StatSetsPerLevel, e.StatSet, level);
        // A per-level stat that is also a constant adds to it (Righteous Fire: radius 18 + 0 at level 1, + 5 at level 20).
        if (gesspl >= 0) foreach (var (s, v) in t.StatSetLevelStats[gesspl]) stats[(uint)s + 1] = (constant.Contains((uint)s + 1) ? stats[(uint)s + 1] : 0) + v;
        int gepl = PickLevel(t.PerLevel, effectRow, level);
        if (e.CastTime > 0)
        {
            Put("base_spell_cast_time_ms", e.CastTime);
            // what the client reads when it starts the cast (traced live 2026-10-18; 0 = an instant cast)
            Put("virtual_base_spell_cast_time_ms", e.CastTime);
            Put("hundred_times_casts_per_second", (int)Math.Round(100_000.0 / e.CastTime));
        }
        if (gepl >= 0 && t.ManaCost[gepl] > 0) { Put("base_mana_cost", t.ManaCost[gepl]); Put("mana_cost", t.ManaCost[gepl]); }
        return [.. stats.Select(kv => (kv.Key, kv.Value))];

        void Put(string name, int value) { if (_stats.Key(name) is uint k) stats[k] = value; }
    }

    /// <summary>
    /// A skill's base hit damage by type, from its per-level stats (<c>spell_minimum_base_fire_damage</c> and so on).
    /// The packet keeps the types separate so conversion and mitigation can be applied per type. Added damage and
    /// increased/more modifiers are applied by the caller; this is the skill's own base range.
    /// </summary>
    public DamagePacket HitDamage(int effectRow, int level)
    {
        var stats = EffectStats(effectRow, level).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        var result = DamagePacket.Empty;
        foreach (var type in DamageTypes.ConversionOrder)
        {
            string slug = type switch
            {
                DamageType.Physical => "physical",
                DamageType.Lightning => "lightning",
                DamageType.Cold => "cold",
                DamageType.Fire => "fire",
                DamageType.Chaos => "chaos",
                _ => throw new ArgumentOutOfRangeException(),
            };
            int min = Value(stats, $"spell_minimum_base_{slug}_damage", $"minimum_base_{slug}_damage");
            int max = Value(stats, $"spell_maximum_base_{slug}_damage", $"maximum_base_{slug}_damage");
            if (min > 0 || max > 0) result = result.Add(type, new DamageRange(min, Math.Max(min, max)));
        }
        return result;
    }

    private int Value(Dictionary<uint, int> stats, params string[] names)
    {
        foreach (string name in names)
            if (_stats.Key(name) is uint key && stats.TryGetValue(key, out int value)) return value;
        return 0;
    }

    /// <summary>The ActionTypes Id of a basic action's skill id (HASH16 &amp; 0x3fff: Move 0x2909), or null.</summary>
    public string? ActionName(ushort skillId) => _actions.Value.FirstOrDefault(a => a.Id == skillId).Name;

    /// <summary>A basic action's skill id by ActionTypes Id ("Move" → 0x2909), or null.</summary>
    public ushort? ActionSkillId(string name) => _actions.Value.FirstOrDefault(a => a.Name == name) is { Name: not null } a ? a.Id : null;

    private readonly Lazy<List<(ushort Id, string Name)>> _actions = new(() =>
    {
        string path = Path.Combine(folder, "tables", "ActionTypes.json");
        if (!File.Exists(path)) return [(0x2909, "Move")];
        using var d = JsonDocument.Parse(File.ReadAllBytes(path));
        return [.. d.RootElement.EnumerateArray().Select(r => ((ushort)(r.GetProperty("HASH16").GetInt32() & 0x3fff), r.GetProperty("Id").GetString() ?? ""))];
    });

    /// <summary>A GrantedEffects row's Id, or null.</summary>
    public string? EffectId(int effectRow) =>
        _tables.Value?.EffectRows.FirstOrDefault(kv => kv.Value.Row == effectRow).Key;

    /// <summary>A GrantedEffects row by its Id, or -1.</summary>
    public int EffectRow(string effectId) =>
        _tables.Value is { } t && t.EffectRows.TryGetValue(effectId, out var effect) ? effect.Row : -1;

    /// <summary>
    /// The mana a skill reserves at a level: GrantedEffectsPerLevel ManaReservationFlat (a flat amount) and
    /// ManaReservationPercent (permyriad: 5000 = 50%). Null when the skill reserves nothing.
    /// </summary>
    public (int Flat, int Percent)? Reservation(int effectRow, int level)
    {
        if (_tables.Value is not { } t) return null;
        int row = t.PerLevel.FindIndex(r => r.Effect == effectRow && r.Level == level);
        if (row < 0 || row >= t.Reservations.Count) return null;
        var r = t.Reservations[row];
        return r.ManaFlat == 0 && r.ManaPercent == 0 ? null : (r.ManaFlat, r.ManaPercent);
    }

    /// <summary>The mana a GrantedEffects Id reserves at a level, or null.</summary>
    public (int Flat, int Percent)? Reservation(string effectId, int level) =>
        EffectRow(effectId) is int row and >= 0 ? Reservation(row, level) : null;

    /// <summary>A GrantedEffects row's CastTime in ms (0 when unknown).</summary>
    public int CastMs(int effectRow) =>
        _tables.Value is { } t && effectRow >= 0 && effectRow < t.Effects.Count ? t.Effects[effectRow].CastTime : 0;

    /// <summary>A projectile type for an effect: the Projectiles.dat row whose Id is Metadata/Projectiles/(GrantedEffects
    /// Id), e.g. Fireball → Metadata/Projectiles/Fireball at speed 1040, or null. The real link between a skill and its
    /// projectile is not known yet.</summary>
    public (string Id, int Speed)? ProjectileFor(int effectRow)
    {
        if (_tables.Value is not { } t) return null;
        string? effect = t.EffectRows.FirstOrDefault(kv => kv.Value.Row == effectRow).Key;
        if (effect is null) return null;
        string id = "Metadata/Projectiles/" + effect;
        return _projectiles.Value.TryGetValue(id, out int speed) ? (id, speed) : null;
    }

    private readonly Lazy<Dictionary<string, int>> _projectiles = new(() =>
    {
        string path = Path.Combine(folder, "tables", "Projectiles.json");
        var result = new Dictionary<string, int>();
        if (!File.Exists(path)) return result;
        using var d = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var r in d.RootElement.EnumerateArray())
            if (r.GetProperty("Id").GetString() is { } id)
                result.TryAdd(id, r.GetProperty("ProjectileSpeed") is { ValueKind: JsonValueKind.Number } v ? v.GetInt32() : 0);
        return result;
    });

    // The highest level at or below `level` among rows with this key, else the lowest; -1 when none.
    private static int PickLevel(List<(int Key, int Level)> rows, int key, int level)
    {
        int best = -1, bestLevel = int.MinValue, low = -1, lowLevel = int.MaxValue;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Key != key) continue;
            int l = rows[i].Level;
            if (l <= level && l > bestLevel) (best, bestLevel) = (i, l);
            if (l < lowLevel) (low, lowLevel) = (i, l);
        }
        return best >= 0 ? best : low;
    }

    public GrantedSkill? Find(ushort id, string effect, int level, IReadOnlyList<(uint, int)>? stats = null)
    {
        if (_tables.Value is not { } t)
            return effect == "PlayerMelee" && level == 1 ? new GrantedSkill(id, 4474, 4430, stats) : null;
        if (!t.EffectRows.TryGetValue(effect, out var ge)) return null;
        int gepl = t.PerLevel.FindIndex(r => r.Effect == ge.Row && r.Level == level);
        int gesspl = t.StatSetsPerLevel.FindIndex(r => r.StatSet == ge.StatSet && r.Level == level);
        return gepl < 0 || gesspl < 0 ? null : new GrantedSkill(id, (uint)gepl, (uint)gesspl, stats);
    }

    /// <summary>
    /// A GrantedEffects row at a level, for monsters: their effects have per-level rows only for some levels (Hungry
    /// Corpse's Melee only level 1), so the highest GEPL level at or below <paramref name="level"/> (else the lowest), and
    /// the effect's StatSet1 at that level the same way. Null without the tables or rows.
    /// </summary>
    public GrantedSkill? FindByRow(ushort id, int effectRow, int level, IReadOnlyList<(uint, int)>? stats = null)
    {
        if (_tables.Value is not { } t || effectRow < 0 || effectRow >= t.StatSets.Count || t.StatSets[effectRow] < 0) return null;
        int statSet = t.StatSets[effectRow];
        int gepl = PickLevel(t.PerLevel, effectRow, level), gesspl = PickLevel(t.StatSetsPerLevel, statSet, level);
        return gepl < 0 || gesspl < 0 ? null : new GrantedSkill(id, (uint)gepl, (uint)gesspl, stats);
    }

    /// <summary>The player's default skills: the default attack, with the class's unarmed weapon stats.</summary>
    public IReadOnlyList<GrantedSkill> PlayerDefaults(Unarmed weapon) => PlayerDefaults(weapon.Stats());

    /// <summary>The player's default skills with these stats.</summary>
    public IReadOnlyList<GrantedSkill> PlayerDefaults(IReadOnlyList<(uint, int)> stats)
    {
        string effect = Environment.GetEnvironmentVariable("Game_PLAYER_ATTACK") ?? "PlayerMelee";
        return Find(FirstSkillId, effect, 1, stats) is { } s ? [s] : [];
    }

    /// <summary>A class's unarmed "weapon" (Characters.dat WeaponSpeed, MinDamage, MaxDamage, MaxAttackDistance).</summary>
    public sealed record Unarmed(int AttackMs, int MinDamage, int MaxDamage, int Range)
    {
        public static readonly Unarmed Witch = new(833, 2, 5, 8);

        /// <summary>Unarmed attacks alternate hands, i.e. count as dual wielding, and the client adds dual wielding's 10%
        /// attack speed to the skill's main_hand_attack_speed_+% (live 2026-10-17: the skill read 10 that the server never
        /// sent, and the swings came every ~757 ms = 833 / 1.1).</summary>
        public const int DualWieldAttackSpeedPct = 10;

        /// <summary>The final main_hand_attack_speed_+% (passives and dual wielding, from CharacterStats), or the raw
        /// stats' sum when no final value was sent.</summary>
        public int IncreasedAttackSpeed(IReadOnlyDictionary<uint, int>? player)
        {
            // The server's stats carry the final value (passives, weapon conditions, dual wielding's 10% more).
            // The fallback is for callers that only have the raw stats.
            if (player?.TryGetValue(StatKeys.MainHandAttackSpeedPct, out int final) == true) return final;
            int increased = DualWieldAttackSpeedPct;
            foreach (uint k in StatKeys.AttackSpeedInputs)
                increased += player?.GetValueOrDefault(k) ?? 0;
            return increased;
        }

        /// <summary>The attack's total increased physical damage: the player's damage, physical, melee and melee physical
        /// damage stats.</summary>
        public int IncreasedDamage(IReadOnlyDictionary<uint, int>? player)
        {
            int increased = 0;
            foreach (uint k in (uint[])[StatKeys.DamagePct, StatKeys.PhysicalDamagePct, StatKeys.MeleeDamagePct, StatKeys.MeleePhysicalDamagePct])
                increased += player?.GetValueOrDefault(k) ?? 0;
            return increased;
        }

        /// <summary>The physical hit packet: Characters.dat MinDamage..MaxDamage × (1 + increased / 100).</summary>
        public DamagePacket HitDamage(IReadOnlyDictionary<uint, int>? player)
        {
            double scale = Math.Max(0, 100 + IncreasedDamage(player)) / 100.0;
            return DamagePacket.Of(DamageType.Physical, new DamageRange(MinDamage * scale, MaxDamage * scale));
        }

        /// <summary>The hit's damage range: Characters.dat MinDamage..MaxDamage × (1 + increased / 100), rounded down,
        /// at least 0.</summary>
        public (int Min, int Max) Damage(IReadOnlyDictionary<uint, int>? player)
        {
            var damage = HitDamage(player)[DamageType.Physical];
            return ((int)Math.Floor(damage.Min), (int)Math.Floor(damage.Max));
        }

        /// <summary>The attack time: base / (1 + increased / 100), as the client computes the swing from the skill's
        /// main_hand_attack_speed_+% (the final value already includes more multipliers), at least one server tick
        /// (<see cref="Program.TickSeconds"/>: at most 30 attacks per second).</summary>
        public double AttackSeconds(IReadOnlyDictionary<uint, int>? player) =>
            Math.Max(Program.TickSeconds, AttackMs / 1000.0 / Math.Max(0.01, 1 + IncreasedAttackSpeed(player) / 100.0));

        /// <summary>
        /// The skill's final main-hand stats (client keys = Stats.dat row + 1). The server computes them, as the real one
        /// does: the client derives nothing from attack_speed_+% (live 2026-10-17: a skill holding attack_speed_+% 2000
        /// still swung at ~757 ms and the character panel kept showing 1.2). The swing follows main_hand_attack_speed_+%
        /// (the client puts only dual wielding's 10 there when it is not sent), the panel's "Attacks per Second" is
        /// hundred_times_attacks_per_second / 100.
        /// </summary>
        public IReadOnlyList<(uint, int)> Stats(IReadOnlyDictionary<uint, int>? player = null)
        {
            var (min, max) = Damage(player);
            double attacksPerSecond = 1 / AttackSeconds(player);
            double averageHit = (min + max) / 2.0;
            return
            [
                (StatKeys.MainHandMinPhysical, min), (StatKeys.MainHandMaxPhysical, max),
                (StatKeys.MainHandMinTotal, min), (StatKeys.MainHandMaxTotal, max),
                (StatKeys.MainHandAttackDurationMs, AttackMs), (StatKeys.MainHandTotalAttackDurationMs, AttackMs),
                (StatKeys.MainHandAttackSpeedPct, IncreasedAttackSpeed(player)),
                (StatKeys.SkillUsesMainHand, 1),
                (StatKeys.HundredTimesAttacksPerSecond, (int)Math.Round(100 * attacksPerSecond)),
                // The character panel's DPS and Damage per Use rows read these; the client derives neither.
                (StatKeys.HundredTimesDamagePerSecond, (int)Math.Round(100 * averageHit * attacksPerSecond)),
                (StatKeys.DisplayHundredTimesDamagePerSkillUse, (int)Math.Round(100 * averageHit)),
                (StatKeys.MainHandBaseMaxAttackDistance, Range), (StatKeys.MainHandMaxAttackDistance, Range),
            ];
        }
    }

    /// <summary>A class from Characters.dat: IntegerId (what the Player component's class byte holds: Marauder 1, Witch 3),
    /// the base attributes, and the player object's type (the row's Id, e.g. Metadata/Characters/StrInt/StrInt for the
    /// Templar) and model (AOFile).</summary>
    public sealed record ClassInfo(int IntegerId, int Strength, int Dexterity, int Intelligence,
        string ObjectType = "Metadata/Characters/Int/Int", string AnimatedObject = "Metadata/Characters/Int/Int.ao",
        int BaseMaxLife = 38, int BaseMaxMana = 34)
    {
        public static readonly ClassInfo Witch = new(3, 14, 14, 32);
    }

    /// <summary>The Witch's Characters.dat row, used when a class is unknown.</summary>
    public const int WitchRow = 1;

    /// <summary>The Characters.dat row of a class by IntegerId (CharacterClass): Marauder 1 → row 0, Witch 3 → row 1,
    /// Templar 5 → row 6.</summary>
    public int RowOfClass(int integerId)
    {
        string path = Path.Combine(folder, "tables", "Characters.json");
        if (!File.Exists(path)) return WitchRow;
        using var d = JsonDocument.Parse(File.ReadAllBytes(path));
        int i = 0;
        foreach (var r in d.RootElement.EnumerateArray())
        {
            if (r.GetProperty("IntegerId").GetInt32() == integerId) return i;
            i++;
        }
        return WitchRow;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, ClassInfo> _classes = new();

    public ClassInfo ClassFor(int characterRow) => _classes.GetOrAdd(characterRow, row =>
    {
        string path = Path.Combine(folder, "tables", "Characters.json");
        if (!File.Exists(path)) return ClassInfo.Witch;
        using var d = JsonDocument.Parse(File.ReadAllBytes(path));
        if (row < 0 || row >= d.RootElement.GetArrayLength()) return ClassInfo.Witch;
        var r = d.RootElement[row];
        return new(r.GetProperty("IntegerId").GetInt32(), r.GetProperty("BaseStrength").GetInt32(),
            r.GetProperty("BaseDexterity").GetInt32(), r.GetProperty("BaseIntelligence").GetInt32(),
            r.GetProperty("Id").GetString()!, r.GetProperty("AOFile").GetString()!,
            r.GetProperty("BaseMaxLife").GetInt32(), r.GetProperty("BaseMaxMana").GetInt32());
    });

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Unarmed> _unarmed = new();

    public Unarmed UnarmedFor(int characterRow) => _unarmed.GetOrAdd(characterRow, LoadUnarmed);

    private Unarmed LoadUnarmed(int characterRow)
    {
        string path = Path.Combine(folder, "tables", "Characters.json");
        if (!File.Exists(path)) return Unarmed.Witch;
        using var d = JsonDocument.Parse(File.ReadAllBytes(path));
        if (characterRow < 0 || characterRow >= d.RootElement.GetArrayLength()) return Unarmed.Witch;
        var r = d.RootElement[characterRow];
        return new Unarmed(r.GetProperty("WeaponSpeed").GetInt32(), r.GetProperty("MinDamage").GetInt32(),
            r.GetProperty("MaxDamage").GetInt32(), r.GetProperty("MaxAttackDistance").GetInt32());
    }

    // StatSets: each GrantedEffects row's StatSet1 (-1 when none). Effects: per GrantedEffects row its StatSet1 and CastTime.
    // StatSetBase: per GrantedEffectStatSets row its ImplicitStats and (ConstantStats, ConstantStatsValues).
    // StatSetLevelStats: per GESSPL row its (FloatStats, BaseResolvedValues) and (AdditionalStats, AdditionalStatsValues).
    // ManaCost: per GEPL row the CostAmounts entry of CostTypes 0 (mana), else 0.
    // Reservations: per GEPL row the ManaReservationFlat and ManaReservationPercent (permyriad), parallel to PerLevel.
    private sealed record Tables(Dictionary<string, (int Row, int StatSet)> EffectRows, List<int> StatSets,
        List<(int Effect, int Level)> PerLevel, List<(int StatSet, int Level)> StatSetsPerLevel,
        List<(int StatSet, int CastTime)> Effects, List<(int[] Implicit, (int, int)[] Constant)> StatSetBase,
        List<(int, int)[]> StatSetLevelStats, List<int> ManaCost,
        List<(int ManaFlat, int ManaPercent, int LifeFlat, int LifePercent)> Reservations)
    {
        public static Tables? Load(string folder)
        {
            string Table(string name) => Path.Combine(folder, "tables", name + ".json");
            string ge = Table("GrantedEffects"), gepl = Table("GrantedEffectsPerLevel"), gesspl = Table("GrantedEffectStatSetsPerLevel"),
                gess = Table("GrantedEffectStatSets");
            if (!File.Exists(ge) || !File.Exists(gepl) || !File.Exists(gesspl)) return null;
            using var g = JsonDocument.Parse(File.ReadAllBytes(ge));
            using var p = JsonDocument.Parse(File.ReadAllBytes(gepl));
            using var s = JsonDocument.Parse(File.ReadAllBytes(gesspl));
            static int Int(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : -1;
            static int[] Ints(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? [.. v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? (int)Math.Round(x.GetDouble()) : -1)] : [];
            static (int, int)[] Pairs(int[] keys, int[] values) => [.. keys.Zip(values).Where(kv => kv.First >= 0)];
            var effects = new Dictionary<string, (int, int)>();
            var statSets = new List<int>();
            var effectInfo = new List<(int, int)>();
            int row = 0;
            foreach (var r in g.RootElement.EnumerateArray())
            {
                string? id = r.GetProperty("Id").GetString();
                int statSet = Int(r, "StatSet1");
                if (id is not null) effects.TryAdd(id, (row, statSet));
                statSets.Add(statSet);
                effectInfo.Add((statSet, Math.Max(0, Int(r, "CastTime"))));
                row++;
            }
            var baseStats = new List<(int[], (int, int)[])>();
            if (File.Exists(gess))
            {
                using var b = JsonDocument.Parse(File.ReadAllBytes(gess));
                foreach (var r in b.RootElement.EnumerateArray())
                    baseStats.Add((Ints(r, "ImplicitStats"), Pairs(Ints(r, "ConstantStats"), Ints(r, "ConstantStatsValues"))));
            }
            var perLevel = new List<(int, int)>();
            var mana = new List<int>();
            var reservations = new List<(int ManaFlat, int ManaPercent, int LifeFlat, int LifePercent)>();
            foreach (var r in p.RootElement.EnumerateArray())
            {
                perLevel.Add((Int(r, "GrantedEffect"), Int(r, "Level")));
                int[] types = Ints(r, "CostTypes"), amounts = Ints(r, "CostAmounts");
                int i = Array.IndexOf(types, 0);
                mana.Add(i >= 0 && i < amounts.Length ? amounts[i] : 0);
                reservations.Add((Math.Max(0, Int(r, "ManaReservationFlat")), Math.Max(0, Int(r, "ManaReservationPercent")),
                    Math.Max(0, Int(r, "LifeReservationFlat")), Math.Max(0, Int(r, "LifeReservationPercent"))));
            }
            var setLevels = new List<(int, int)>();
            var levelStats = new List<(int, int)[]>();
            foreach (var r in s.RootElement.EnumerateArray())
            {
                setLevels.Add((Int(r, "StatSet"), Int(r, "GemLevel")));
                levelStats.Add([.. Pairs(Ints(r, "FloatStats"), Ints(r, "BaseResolvedValues")),
                    .. Pairs(Ints(r, "AdditionalStats"), Ints(r, "AdditionalStatsValues"))]);
            }
            return new Tables(effects, statSets, perLevel, setLevels, effectInfo, baseStats, levelStats, mana, reservations);
        }
    }
}
