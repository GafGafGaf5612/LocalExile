using System.Text.Json;

/// <summary>
/// The passive skill tree's nodes: PassiveSkills.dat rows by PassiveSkillGraphId (the id the client allocates and S→C
/// 0x3d lists), with their stats (Stats.dat rows + 1 = the client's stat keys) and the skill points they grant. Game data:
/// data/tables/PassiveSkills.json, loaded on first use. The tree's connections (PassiveSkillGraph.psg) are not loaded:
/// the client only asks for connected nodes, and drops unconnected ones from a 0x3d list itself (0x141eed000).
/// </summary>
sealed class PassiveTree(string folder = "data")
{
    public sealed record Node(ushort GraphId, string Id, string Name, IReadOnlyList<(uint Key, int Value)> Stats, int PointsGranted,
        bool IsAscendancy, bool IsStart);

    private readonly Lazy<Dictionary<ushort, Node>> _nodes = new(() => Load(folder));

    public Node? Find(ushort graphId) => _nodes.Value.GetValueOrDefault(graphId);

    public bool Loaded => _nodes.Value.Count > 0;

    /// <summary>The skill points the allocated nodes grant themselves (the client counts them as extra points).</summary>
    public int PointsGranted(IEnumerable<ushort> nodes) => nodes.Sum(n => Find(n)?.PointsGranted ?? 0);

    /// <summary>The allocated nodes' stats, summed by key.</summary>
    public Dictionary<uint, int> Stats(IEnumerable<ushort> nodes)
    {
        var sum = new Dictionary<uint, int>();
        foreach (var n in nodes)
            if (Find(n) is { } node)
                foreach (var (k, v) in node.Stats) sum[k] = sum.GetValueOrDefault(k) + v;
        return sum;
    }

    private static Dictionary<ushort, Node> Load(string folder)
    {
        string path = Path.Combine(folder, "tables", "PassiveSkills.json");
        var nodes = new Dictionary<ushort, Node>();
        if (!File.Exists(path)) return nodes;
        using var d = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var r in d.RootElement.EnumerateArray())
        {
            int graphId = r.GetProperty("PassiveSkillGraphId").GetInt32();
            if (graphId is <= 0 or > ushort.MaxValue) continue;
            var keys = r.GetProperty("Stats").EnumerateArray().Select(s => s.GetInt32()).ToList();
            var stats = new List<(uint, int)>();
            for (int i = 0; i < keys.Count && i < 5; i++)
                stats.Add(((uint)keys[i] + 1, r.GetProperty($"Stat{i + 1}Value").GetInt32()));
            nodes.TryAdd((ushort)graphId, new Node((ushort)graphId, r.GetProperty("Id").GetString() ?? "", r.GetProperty("Name").GetString() ?? "",
                stats, r.GetProperty("SkillPointsGranted").GetInt32(), r.GetProperty("AscendancyKey").ValueKind == JsonValueKind.Number,
                r.GetProperty("Characters").GetArrayLength() > 0));
        }
        return nodes;
    }
}
