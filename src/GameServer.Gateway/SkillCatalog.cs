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

    /// <summary>The player's default skills: the default attack, with the class's unarmed weapon stats.</summary>
    public IReadOnlyList<GrantedSkill> PlayerDefaults(Unarmed weapon)
    {
        string effect = Environment.GetEnvironmentVariable("Game_PLAYER_ATTACK") ?? "PlayerMelee";
        return Find(FirstSkillId, effect, 1, weapon.Stats()) is { } s ? [s] : [];
    }

    /// <summary>A class's unarmed "weapon" (Characters.dat WeaponSpeed, MinDamage, MaxDamage, MaxAttackDistance).</summary>
    public sealed record Unarmed(int AttackMs, int MinDamage, int MaxDamage, int Range)
    {
        public static readonly Unarmed Witch = new(833, 2, 5, 8);

        /// <summary>The main-hand stats the client reads for an attack (client keys = Stats.dat row + 1).</summary>
        public IReadOnlyList<(uint, int)> Stats() =>
        [
            (StatKeys.MainHandMinPhysical, MinDamage), (StatKeys.MainHandMaxPhysical, MaxDamage),
            (StatKeys.MainHandMinTotal, MinDamage), (StatKeys.MainHandMaxTotal, MaxDamage),
            (StatKeys.MainHandAttackDurationMs, AttackMs), (StatKeys.MainHandTotalAttackDurationMs, AttackMs),
            (StatKeys.HundredTimesAttacksPerSecond, 100_000 / AttackMs),
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

    private sealed record Tables(Dictionary<string, (int Row, int StatSet)> EffectRows, List<(int Effect, int Level)> PerLevel,
        List<(int StatSet, int Level)> StatSetsPerLevel)
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
            int row = 0;
            foreach (var r in g.RootElement.EnumerateArray())
            {
                string? id = r.GetProperty("Id").GetString();
                int statSet = r.GetProperty("StatSet1") is { ValueKind: JsonValueKind.Number } v ? v.GetInt32() : -1;
                if (id is not null) effects.TryAdd(id, (row, statSet));
                row++;
            }
            static int Int(JsonElement r, string name) => r.GetProperty(name) is { ValueKind: JsonValueKind.Number } v ? v.GetInt32() : -1;
            return new Tables(effects,
                [.. p.RootElement.EnumerateArray().Select(r => (Int(r, "GrantedEffect"), Int(r, "Level")))],
                [.. s.RootElement.EnumerateArray().Select(r => (Int(r, "StatSet"), Int(r, "GemLevel")))]);
        }
    }
}
