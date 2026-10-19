using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// NPCs (Agents/docs/protocol/17-npcs.md): NPCs.dat rows from the extracted tables with their object type's deserialized
/// components (data/object-types.json), and where each town's NPCs stand. The client files do not place town NPCs (the
/// town room has only its entrance points and the tiles only the waypoint and area transition), so the cells are ours.
/// </summary>
sealed class NpcCatalog
{
    public sealed record Npc(int Row, string Id, string Name, IReadOnlyList<string>? Components, StateMachineData? StateMachine,
        byte TransitionState)
    {
        public bool Supported => Components is { } c && NpcObject.CanSerialize(c, StateMachine is not null);
        public override string ToString() => $"{Name} ({Id}, row {Row})";
    }

    /// <summary>An NPC standing in a town: its NPCs.dat Id, cell and facing (radians).</summary>
    public sealed record Placement(string Id, int X, int Y, float Rotation);

    // Lioneye's Watch (spawn (197, 261), waypoint (256, 169), gate to The Coast (388, 231)): where they stand in the real
    // game, picked live with /pos on 2026-10-19. Facing is not set yet.
    static readonly Dictionary<ushort, Placement[]> Towns = new()
    {
        [AreaLayouts.LioneyesWatch] =
        [
            new("Metadata/NPC/Act1/Tarkleigh", 385, 188, 0),
            new("Metadata/NPC/Act1/Nessa", 355, 263, 0),
            new("Metadata/NPC/Act1/Bestel", 362, 249, 0),
        ],
    };

    public IReadOnlyList<Npc> Rows { get; }
    public string? Error { get; }
    readonly Dictionary<string, Npc> _byId;

    NpcCatalog(List<Npc> rows, string? error)
    {
        Rows = rows;
        Error = error;
        _byId = rows.GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public static NpcCatalog Load(string folder = "data")
    {
        try { return Read(folder); }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new([], $"NPCs: {ex.Message}");
        }
    }

    /// <summary>The town's NPCs, or none.</summary>
    public static IReadOnlyList<Placement> In(ushort areaHash) => Towns.GetValueOrDefault(areaHash) ?? [];

    /// <summary>An NPC by row, Id or name (exact name first, then the first Id or name containing the text).</summary>
    public Npc? Find(string query)
    {
        if (int.TryParse(query, out int row)) return row >= 0 && row < Rows.Count ? Rows[row] : null;
        if (_byId.TryGetValue(query, out var byId)) return byId;
        return Rows.FirstOrDefault(r => r.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
            ?? Rows.FirstOrDefault(r => r.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || r.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    static NpcCatalog Read(string folder)
    {
        string table = Path.Combine(folder, "tables", "NPCs.json");
        if (!File.Exists(table)) return new([], $"{table} is missing: extract the game tables (Tools/PoEDataExtractor)");
        var components = new Dictionary<string, IReadOnlyList<string>>();
        var stateMachines = new Dictionary<string, StateMachineData>();
        var transitionStates = new Dictionary<string, byte>();
        string types = Path.Combine(folder, "object-types.json");
        if (File.Exists(types))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(types));
            foreach (var t in doc.RootElement.GetProperty("types").EnumerateArray())
            {
                string type = t.GetProperty("type").GetString()!;
                if (!type.StartsWith("Metadata/NPC/", StringComparison.OrdinalIgnoreCase)) continue;
                components[type] = [.. t.GetProperty("components").EnumerateArray().Take(t.GetProperty("read").GetInt32()).Select(c => c.GetString()!)];
                if (t.TryGetProperty("shared_states", out var ss) && t.TryGetProperty("shared_timers", out var st))
                    stateMachines[type] = new([.. ss.EnumerateArray().Select(e => e[1].GetInt32())], st.GetArrayLength());
                if (t.TryGetProperty("transitionable", out var tr) && tr.TryGetProperty("num_states", out var ns))
                    transitionStates[type] = (byte)Math.Clamp(ns.GetInt32(), 1, 127);
            }
        }
        var rows = new List<Npc>();
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(table)))
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                string id = r.GetProperty("Id").GetString() ?? "";
                // Without object-types.json, assume the base NPC list.
                IReadOnlyList<string>? list = components.Count == 0 ? NpcObject.BaseComponents : components.GetValueOrDefault(id);
                rows.Add(new Npc(rows.Count, id, r.GetProperty("Name").GetString() ?? "", list,
                    stateMachines.GetValueOrDefault(id), transitionStates.GetValueOrDefault(id, (byte)1)));
            }
        return new(rows, null);
    }
}
