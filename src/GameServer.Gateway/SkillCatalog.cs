using System.Text.Json;
using GameServer.Core.Protocol.Instance;

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
        static int Pick(List<(int Key, int Level)> rows, int key, int level)
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
        int gepl = Pick(t.PerLevel, effectRow, level), gesspl = Pick(t.StatSetsPerLevel, statSet, level);
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

        /// <summary>The attack's total increased attack speed: dual wielding's 10 plus the player's attack speed stats
        /// (main_hand_attack_speed_+% set by hand counts as one more input).</summary>
        public int IncreasedAttackSpeed(IReadOnlyDictionary<uint, int>? player)
        {
            int increased = DualWieldAttackSpeedPct;
            foreach (uint k in (uint[])[StatKeys.AttackSpeedPct, StatKeys.MeleeAttackSpeedPct, StatKeys.MainHandAttackSpeedPct,
                StatKeys.UnarmedMeleeAttackSpeedPct])
                increased += player?.GetValueOrDefault(k) ?? 0;
            return increased;
        }

        /// <summary>The attack time: base / (1 + increased / 100), as the client computes the swing from the skill's
        /// main_hand_attack_speed_+% (more multipliers are not modelled), at least one server tick
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
        public IReadOnlyList<(uint, int)> Stats(IReadOnlyDictionary<uint, int>? player = null) =>
        [
            (StatKeys.MainHandMinPhysical, MinDamage), (StatKeys.MainHandMaxPhysical, MaxDamage),
            (StatKeys.MainHandMinTotal, MinDamage), (StatKeys.MainHandMaxTotal, MaxDamage),
            (StatKeys.MainHandAttackDurationMs, AttackMs), (StatKeys.MainHandTotalAttackDurationMs, AttackMs),
            (StatKeys.MainHandAttackSpeedPct, IncreasedAttackSpeed(player)),
            (StatKeys.HundredTimesAttacksPerSecond, (int)Math.Round(100 / AttackSeconds(player))),
            (StatKeys.MainHandBaseMaxAttackDistance, Range), (StatKeys.MainHandMaxAttackDistance, Range),
        ];
    }

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

    // StatSets: each GrantedEffects row's StatSet1 (-1 when none).
    private sealed record Tables(Dictionary<string, (int Row, int StatSet)> EffectRows, List<int> StatSets,
        List<(int Effect, int Level)> PerLevel, List<(int StatSet, int Level)> StatSetsPerLevel)
    {
        public static Tables? Load(string folder)
        {
            string Table(string name) => Path.Combine(folder, "tables", name + ".json");
            string ge = Table("GrantedEffects"), gepl = Table("GrantedEffectsPerLevel"), gesspl = Table("GrantedEffectStatSetsPerLevel");
            if (!File.Exists(ge) || !File.Exists(gepl) || !File.Exists(gesspl)) return null;
            using var g = JsonDocument.Parse(File.ReadAllBytes(ge));
            using var p = JsonDocument.Parse(File.ReadAllBytes(gepl));
            using var s = JsonDocument.Parse(File.ReadAllBytes(gesspl));
            var effects = new Dictionary<string, (int, int)>();
            var statSets = new List<int>();
            int row = 0;
            foreach (var r in g.RootElement.EnumerateArray())
            {
                string? id = r.GetProperty("Id").GetString();
                int statSet = r.GetProperty("StatSet1") is { ValueKind: JsonValueKind.Number } v ? v.GetInt32() : -1;
                if (id is not null) effects.TryAdd(id, (row, statSet));
                statSets.Add(statSet);
                row++;
            }
            static int Int(JsonElement r, string name) => r.GetProperty(name) is { ValueKind: JsonValueKind.Number } v ? v.GetInt32() : -1;
            return new Tables(effects, statSets,
                [.. p.RootElement.EnumerateArray().Select(r => (Int(r, "GrantedEffect"), Int(r, "Level")))],
                [.. s.RootElement.EnumerateArray().Select(r => (Int(r, "StatSet"), Int(r, "GemLevel")))]);
        }
    }
}
