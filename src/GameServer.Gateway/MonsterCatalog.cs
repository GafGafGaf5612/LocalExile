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
    public sealed record Variety(int Row, string Id, string Name, string BaseType, string? AnimatedObject, IReadOnlyList<string>? Components)
    {
        public IReadOnlyList<string> ComponentsOrBase => Components ?? MonsterObject.BaseComponents;
        public bool Supported => MonsterObject.CanSerialize(ComponentsOrBase);
        public override string ToString() => $"{Name} ({Id}, row {Row})";
    }

    private readonly Lazy<(List<Variety> Rows, string? Error)> _rows = new(() => Load(folder));

    public string? Error => _rows.Value.Error;

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
        string types = Path.Combine(folder, "object-types.json");
        if (File.Exists(types))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(types));
            foreach (var t in doc.RootElement.GetProperty("types").EnumerateArray())
            {
                int read = t.GetProperty("read").GetInt32();
                components[t.GetProperty("type").GetString()!] =
                    [.. t.GetProperty("components").EnumerateArray().Take(read).Select(c => c.GetString()!)];
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
                    components.Count == 0 ? null : components.GetValueOrDefault(baseType)));
            }
        }
        // With object-types.json, a type missing from it has no .ot: do not guess.
        if (components.Count > 0)
            rows = [.. rows.Select(v => v.Components is null ? v with { Components = [] } : v)];
        return (rows, null);
    }
}
