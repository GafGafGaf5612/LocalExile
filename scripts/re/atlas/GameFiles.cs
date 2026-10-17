using GameServer.Data.Ggpk;
using GameServer.Data.Terrain;

namespace Atlas;

/// <summary>Cached, parsed terrain files out of the bundle index. Paths keep the case they were written with (the
/// index itself is case-insensitive); a missing or unparsable file is null and noted in <see cref="Missing"/>.</summary>
sealed class GameFiles(GameFileSystem fs)
{
    private readonly Dictionary<string, object?> _cache = new(StringComparer.OrdinalIgnoreCase);
    public readonly SortedDictionary<string, string> Missing = new(StringComparer.OrdinalIgnoreCase);

    public byte[]? Bytes(string path) => fs.TryReadFile(path, out var d) ? d : null;

    private T? Get<T>(string kind, string path, Func<byte[], T> parse, bool noteMissing = true) where T : class
    {
        string key = kind + ":" + path;
        if (_cache.TryGetValue(key, out var hit)) return (T?)hit;
        T? value = null;
        var data = Bytes(path);
        if (data is null) { if (noteMissing) Missing[path] = "not in the bundle index"; }
        else
            try { value = parse(data); }
            catch (Exception e) { Missing[path] = $"unparsable: {e.Message}"; }
        _cache[key] = value;
        return value;
    }

    public GraphFile? Graph(string path) => Get("graph", path, d => GraphFile.Parse(TerrainText.Decode(d)));
    public RoomFile? Room(string path) => Get("room", path, d => RoomFile.Parse(TerrainText.Decode(d)));
    public Dictionary<string, string>? Tsi(string path) => Get("tsi", path, d => TerrainText.KeyValues(TerrainText.Decode(d)));
    public TileDefinition? Tdt(string path) => Get("tdt", path, d => TileDefinition.Parse(d));
    // Most tiles have no .tmd, so a missing one is not noted.
    public IReadOnlyList<TileObject>? Tmd(string path) => Get("tmd", path, d => TileObjectFile.Parse(d), noteMissing: false);

    /// <summary>The paths a list file names (.rs → .arm, .tst → .tdt), following nested list files of the same kind.</summary>
    public List<string> List(string path, string ext, HashSet<string>? seen = null)
    {
        seen ??= new(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        if (!seen.Add(path)) return result;
        var items = Get("list", path, d => TerrainText.QuotedPaths(TerrainText.Decode(d)));
        if (items is null) return result;
        string listExt = Path.GetExtension(path);
        foreach (var raw in items)
        {
            string p = TerrainText.Resolve(path, raw);
            if (p.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) result.Add(p);
            else if (p.EndsWith(listExt, StringComparison.OrdinalIgnoreCase)) result.AddRange(List(p, ext, seen));
        }
        return result;
    }

    private readonly Dictionary<string, bool> _transitionTypes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether an object type is an area transition: its .ot, or one it extends, is
    /// Metadata/MiscellaneousObjects/AreaTransition… or has an AreaTransition component (quest exits such as
    /// Metadata/QuestObjects/SouthBeachTownEntrance extend AreaTransition).</summary>
    public bool IsTransitionType(string type)
    {
        if (_transitionTypes.TryGetValue(type, out bool known)) return known;
        bool result = false;
        string? t = type;
        for (int depth = 0; t is not null && depth < 10 && !result; depth++)
        {
            if (t.Contains("AreaTransition", StringComparison.OrdinalIgnoreCase)) { result = true; break; }
            var text = Get("ot", t + ".ot", d => TerrainText.Decode(d), noteMissing: false);
            if (text is null) break;
            var lines = TerrainText.Lines(text).Select(l => l.Trim()).ToList();
            if (lines.Any(l => l == "AreaTransition")) { result = true; break; }
            t = lines.Where(l => l.StartsWith("extends ")).Select(l => TerrainText.Tokenize(l).Skip(1).FirstOrDefault().Text).FirstOrDefault();
        }
        _transitionTypes[type] = result;
        return result;
    }

    /// <summary>The objects of a tile: the .tmd next to its .tgt, else the one next to the .tdt itself
    /// (Metadata/Terrain/Beach/Transitions/beachcaveentrance_v01_01.tmd).</summary>
    public IReadOnlyList<TileObject>? TileObjects(string tdtPath, TileDefinition tdt) =>
        (tdt.Tgt.EndsWith(".tgt", StringComparison.OrdinalIgnoreCase) ? Tmd(tdt.Tgt[..^4] + ".tmd") : null)
        ?? (tdtPath.EndsWith(".tdt", StringComparison.OrdinalIgnoreCase) ? Tmd(tdtPath[..^4] + ".tmd") : null);
}
