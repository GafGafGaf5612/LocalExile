using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;
using GgDump;

/// <summary>Server-side level generation (GameServer.Data/Terrain/Generation, Agents/docs/re/011-level-generation.md):
/// for an area the port reproduces 1:1, the Gateway generates the layout itself from the seed instead of reading the
/// client's capture: the walkability grid (doodad collision included), every cell's ground height, and the area
/// transitions and waypoint. Supported areas are the ones whose seed-1 trace passes every check of the level
/// generator's gate (scripts/re/levelgen/gate-baseline.json, committed with each generator change) and whose logged
/// loads all match. Needs the game files (Client/Content.ggpk) and Node for their Oodle blocks (Tools/ooz);
/// Game_SERVER_LEVELS=0 turns it off.</summary>
static class ServerLevels
{
    public const string UnsupportedMessage = "This map is generated on client-side and is not supported yet by the Server.";

    /// <summary>A generated layout: Width × Height cells, one walk value (0 … 5, walkable from 2) and one ground height
    /// (× 7.8125 for world units) per cell, and the tile objects the server creates (area transitions, the waypoint).</summary>
    public sealed record Level(int Width, int Height, byte[] Walk, short[] Heights, IReadOnlyList<PlacedTileObject> Objects);

    /// <summary>Where the supported list comes from (the repository root is the Gateway's directory; tests replace it).</summary>
    public static string BaselineFile { get; set; } = "scripts/re/levelgen/gate-baseline.json";

    static readonly bool Enabled = Environment.GetEnvironmentVariable("Game_SERVER_LEVELS") != "0";
    static readonly object Gate = new();
    static HashSet<string>? _supported;
    static TerrainCache? _files;
    static GameFileSystem? _fs;
    static readonly ConcurrentDictionary<(string Area, uint Seed), Level?> Generated = new();

    /// <summary>Whether the Gateway generates this area itself (<see cref="BaselineFile"/>).</summary>
    public static bool IsSupported(string areaId)
    {
        if (!Enabled) return false;
        lock (Gate) return (_supported ??= LoadSupported(BaselineFile)).Contains(areaId);
    }

    /// <summary>Drops the supported list, so the next check reads <see cref="BaselineFile"/> again (tests).</summary>
    internal static void Reload() { lock (Gate) _supported = null; }

    /// <summary>The supported areas of a gate baseline: each "capture &lt;area&gt;" whose tile hash, both grids, every
    /// draw the port makes, the doodads, the cell heights and the transitions match (a check without a capture does not
    /// count against it), with every "oracle &lt;area&gt; &lt;seed&gt;" of the area matching too.</summary>
    internal static HashSet<string> LoadSupported(string file)
    {
        var supported = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(file)) return supported;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var failedOracle = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var r = p.Value;
                string hash = r.GetProperty("Hash").GetString() ?? "";
                if (p.Name.StartsWith("oracle ", StringComparison.Ordinal))
                {
                    if (hash != "match") failedOracle.Add(p.Name.Split(' ')[1]);
                    continue;
                }
                if (!p.Name.StartsWith("capture ", StringComparison.Ordinal) || hash != "match") continue;
                int Int(string name, int none) => r.TryGetProperty(name, out var v) ? v.GetInt32() : none;
                string? first = r.TryGetProperty("FirstDiff", out var f) ? f.GetString() : null;
                bool draws = first is null || first.EndsWith(" (port stopped)", StringComparison.Ordinal);
                bool grids = Int("Walk", -1) == 0 && Int("Projectile", -1) == 0;
                int captured = Int("Captured", 0);
                bool doodads = captured < 0 || (Int("Placed", 0) == captured && Int("Matched", 0) == captured);
                if (draws && grids && doodads && Int("Heights", -1) <= 0 && Int("Objects", -1) <= 0)
                    supported.Add(p.Name["capture ".Length..]);
            }
            supported.ExceptWith(failedOracle);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Console.WriteLine($"[gateway] server levels: {file}: {ex.Message}");
        }
        return supported;
    }

    /// <summary>The generated layout of (area, seed), or null when the area is not supported or generation failed
    /// (the Gateway then falls back to the capture). Generated once per (area, seed) and kept.</summary>
    public static Level? For(string areaId, uint seed)
    {
        if (!IsSupported(areaId)) return null;
        if (Generated.TryGetValue((areaId, seed), out var known)) return known;
        lock (Gate)
        {
            if (Generated.TryGetValue((areaId, seed), out known)) return known;
            return Generated[(areaId, seed)] = Generate(areaId, seed);
        }
    }

    static Level? Generate(string areaId, uint seed)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var files = Files();
            var area = AreaInfo.Load(Read, areaId, AreaLevel(areaId));
            var result = LevelGenerator.Generate(files, area, seed);
            var walk = Walkability.Build(result.Grid, t => files.Walk(t.Path));
            // The doodad stage stamps the doodads' collision into the grids (re/011 §6c).
            new DoodadStage(files, result.Random, result.Grid, walk, []).Run(result.Records);
            var heights = CellHeights.Build(result.Grid, t => files.Walk(t.Path));
            var objects = AreaObjects.Transitions(files, result);
            Console.WriteLine($"[gateway] server levels: generated {areaId} seed {seed} ({walk.Width}x{walk.Height} cells, " +
                $"{objects.Count} tile object(s)) in {clock.ElapsedMilliseconds} ms");
            return new Level(walk.Width, walk.Height, walk.Walk, heights, objects);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[gateway] server levels: {areaId} seed {seed} not generated ({ex.GetType().Name}: {ex.Message.Split('\n')[0]})");
            return null;
        }
    }

    // Opened on the first generation and kept.
    static TerrainCache Files()
    {
        if (_files is not null) return _files;
        var oodle = new NodeOodleDecoder("node", Path.GetFullPath(Path.Combine("Tools", "ooz", "ooz_worker.mjs")));
        _fs = GameFileSystem.Open(Path.GetFullPath(Path.Combine("Client", "Content.ggpk")), oodle);
        return _files = new TerrainCache(Read);
    }

    static byte[]? Read(string path) => _fs!.TryReadFile(path, out var d) ? d : null;

    static readonly Lazy<Dictionary<string, int>> Levels = new(() =>
    {
        var levels = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine("data", "tables", "WorldAreas.json")));
            foreach (var row in doc.RootElement.EnumerateArray())
                levels[row.GetProperty("Id").GetString() ?? ""] = row.GetProperty("AreaLevel").GetInt32();
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException) { }
        return levels;
    });

    // The WorldAreas row's area level (the generator's AreaInfo reads level-dependent rows); 1 without the table.
    static int AreaLevel(string areaId) => Math.Max(1, Levels.Value.GetValueOrDefault(areaId, 1));
}
