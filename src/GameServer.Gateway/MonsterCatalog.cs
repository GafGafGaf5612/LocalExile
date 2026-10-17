using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// The monsters /spawn_mob can create: MonsterVarieties rows from the extracted tables
/// (data/tables/MonsterVarieties.json, Agents/GettingStarted.md "Extracting the game's data tables"), with each type's
/// deserialized components from data/object-types.json (`python -I scripts/re/ot_components.py --all --json
/// data/object-types.json`). Without that file every variety is assumed to have the base Monster list, which is wrong
/// for about half of them (StateMachine, DiesAfterTime …) and then the client shows "Buffer Underflow".
/// Both files are game data: they stay in data/ and are loaded on first use.
/// </summary>
sealed class MonsterCatalog(string folder = "data")
{
    public sealed record Variety(int Row, string Id, string Name, string BaseType, string? AnimatedObject, IReadOnlyList<string>? Components,
        int LifeMultiplier = 100, int MonsterType = -1, StateMachineData? StateMachine = null, byte TransitionStates = 1)
    {
        public IReadOnlyList<string> ComponentsOrBase => Components ?? MonsterObject.BaseComponents;
        public bool Supported => MonsterObject.CanSerialize(ComponentsOrBase, StateMachine is not null);
        public override string ToString() => $"{Name} ({Id}, row {Row})";
    }

    private readonly Lazy<(List<Variety> Rows, string? Error)> _rows = new(() => Load(folder));
    private readonly Lazy<LifeTables?> _life = new(() => LifeTables.Load(folder));

    public string? Error => _rows.Value.Error;

    /// <summary>
    /// The maximum life the client gives a monster of this variety at this level, before mods: the client's Monster type
    /// (0x141e701d0) sets stat 0x7d to max(1, column × LifeMultiplier / 100) from DefaultMonsterStats row level − 1 (0x48-byte
    /// rows). The column depends on the variety's MonsterTypes row: AltLife1 → AltLife1, else AltLife2 → AltLife2, else
    /// IsPlayerMinion → MinionLife, else MonsterLife. Null when the tables are missing.
    /// </summary>
    public int? BaseLife(Variety v, byte level)
    {
        if (_life.Value is not { } t || level < 1 || level > t.Levels.Count) return null;
        var row = t.Levels[level - 1];
        var type = v.MonsterType >= 0 && v.MonsterType < t.Types.Count ? t.Types[v.MonsterType] : default;
        int column = type.AltLife1 ? row.AltLife1 : type.AltLife2 ? row.AltLife2 : type.Minion ? row.MinionLife : row.MonsterLife;
        return Math.Max(1, column * v.LifeMultiplier / 100);
    }

    private sealed record LifeTables(List<(int MonsterLife, int MinionLife, int AltLife1, int AltLife2)> Levels,
        List<(bool Minion, bool AltLife1, bool AltLife2)> Types)
    {
        public static LifeTables? Load(string folder)
        {
            string stats = Path.Combine(folder, "tables", "DefaultMonsterStats.json"), types = Path.Combine(folder, "tables", "MonsterTypes.json");
            if (!File.Exists(stats) || !File.Exists(types)) return null;
            using var s = JsonDocument.Parse(File.ReadAllBytes(stats));
            using var t = JsonDocument.Parse(File.ReadAllBytes(types));
            return new LifeTables(
                [.. s.RootElement.EnumerateArray().Select(r => (r.GetProperty("MonsterLife").GetInt32(), r.GetProperty("MinionLife").GetInt32(),
                    r.GetProperty("AltLife1").GetInt32(), r.GetProperty("AltLife2").GetInt32()))],
                [.. t.RootElement.EnumerateArray().Select(r => (r.GetProperty("IsPlayerMinion").GetBoolean(), r.GetProperty("AltLife1").GetBoolean(),
                    r.GetProperty("AltLife2").GetBoolean()))]);
        }
    }

    /// <summary>A MonsterVarieties row by number, or by Id: exact, then the last path segment, then any part of it,
    /// then the display name. Several matches: the first one the blob writer supports.</summary>
    public Variety? Find(string query)
    {
        var rows = _rows.Value.Rows;
        if (int.TryParse(query, out int row)) return row >= 0 && row < rows.Count ? rows[row] : null;
        foreach (Func<Variety, bool> match in new Func<Variety, bool>[]
        {
            v => v.Id.Equals(query, StringComparison.OrdinalIgnoreCase),
            v => v.Id.EndsWith("/" + query, StringComparison.OrdinalIgnoreCase),
            v => v.Name.Equals(query, StringComparison.OrdinalIgnoreCase),
            v => v.Id.Contains(query, StringComparison.OrdinalIgnoreCase),
        })
        {
            var hits = rows.Where(match).ToList();
            if (hits.Count > 0) return hits.FirstOrDefault(v => v.Supported) ?? hits[0];
        }
        return null;
    }

    private static (List<Variety>, string?) Load(string folder)
    {
        string varieties = Path.Combine(folder, "tables", "MonsterVarieties.json");
        if (!File.Exists(varieties)) return ([], $"{varieties} is missing: extract the game tables (Tools/PoEDataExtractor)");
        var components = new Dictionary<string, IReadOnlyList<string>>();
        var stateMachines = new Dictionary<string, StateMachineData>();
        var transitionStates = new Dictionary<string, byte>();
        string types = Path.Combine(folder, "object-types.json");
        if (File.Exists(types))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(types));
            foreach (var t in doc.RootElement.GetProperty("types").EnumerateArray())
            {
                int read = t.GetProperty("read").GetInt32();
                string type = t.GetProperty("type").GetString()!;
                components[type] = [.. t.GetProperty("components").EnumerateArray().Take(read).Select(c => c.GetString()!)];
                // Files generated before the StateMachine data have no "shared_states": those types stay unsupported.
                if (t.TryGetProperty("shared_states", out var ss) && t.TryGetProperty("shared_timers", out var st))
                    stateMachines[type] = new([.. ss.EnumerateArray().Select(e => e[1].GetInt32())], st.GetArrayLength());
                if (t.TryGetProperty("transitionable", out var tr) && tr.TryGetProperty("num_states", out var ns))
                    transitionStates[type] = (byte)Math.Clamp(ns.GetInt32(), 1, 127);
            }
        }
        var rows = new List<Variety>();
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(varieties)))
        {
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                string id = r.GetProperty("Id").GetString() ?? "";
                string baseType = r.GetProperty("BaseMonsterTypeIndex").GetString() ?? "";
                string? ao = r.GetProperty("AOFiles").EnumerateArray().Select(a => a.GetString()).FirstOrDefault();
                rows.Add(new Variety(rows.Count, id, r.GetProperty("Name").GetString() ?? "", baseType, ao,
                    components.Count == 0 ? null : components.GetValueOrDefault(baseType),
                    r.GetProperty("LifeMultiplier").GetInt32(),
                    r.GetProperty("MonsterTypesKey").ValueKind == JsonValueKind.Number ? r.GetProperty("MonsterTypesKey").GetInt32() : -1,
                    stateMachines.GetValueOrDefault(baseType), transitionStates.GetValueOrDefault(baseType, (byte)1)));
            }
        }
        // With object-types.json, a type missing from it has no .ot: do not guess.
        if (components.Count > 0)
            rows = [.. rows.Select(v => v.Components is null ? v with { Components = [] } : v)];
        return (rows, null);
    }
}
