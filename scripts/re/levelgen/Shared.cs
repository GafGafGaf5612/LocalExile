using System.Text.Json;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;
using GgDump;

namespace LevelGenTool;

/// <summary>What every command shares: the game files and their terrain cache, opened once per process (so `serve`
/// and the gate's workers keep them across commands), and the repository root.</summary>
static class Shared
{
    static GameFileSystem? _fs;
    static TerrainCache? _files;

    /// <summary>The game files (Client/Content.ggpk), opened on first use and kept open: callers do not dispose it.</summary>
    public static GameFileSystem OpenGame()
    {
        if (_fs is not null) return _fs;
        string root = Root();
        var oodle = new NodeOodleDecoder("node", Path.Combine(root, @"Tools\ooz\ooz_worker.mjs"));
        return _fs = GameFileSystem.Open(Path.Combine(root, "Client", "Content.ggpk"), oodle);
    }

    /// <summary>The terrain cache over <see cref="OpenGame"/>, shared by every command of the process.</summary>
    public static TerrainCache Files()
    {
        var fs = OpenGame();
        return _files ??= new TerrainCache(p => fs.TryReadFile(p, out var d) ? d : null);
    }

    /// <summary>Reads a game file, null when it is not there.</summary>
    public static byte[]? Read(string path) => OpenGame().TryReadFile(path, out var d) ? d : null;

    /// <summary>The generator's static hooks back to none (a long-lived process must not keep a command's).</summary>
    public static void ResetHooks()
    {
        LevelGenerator.FillStarting = null;
        LevelGenerator.PaintStarting = null;
        LevelGenerator.DungeonStage = null;
        LevelGenerator.DungeonRooms = null;
    }

    public static string Root()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Tools", "ooz", "ooz_worker.mjs")))
            root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar))
                ?? throw new InvalidOperationException("Tools/ooz/ooz_worker.mjs not found above the build output");
        return root;
    }

    /// <summary>The trace's 'n' / 'u' / 'l' draws, without the LCG draws of 0x142063a60 (call site 0x142063b9c): an
    /// animated entity's random start time (rand() bits × length / 256), made when the doodad is created. It reads only
    /// the LCG, which 0x142046a70 reseeds from the MT before each pick, so it moves nothing in the generation.</summary>
    public static List<(char Kind, string Caller, uint Arg, uint Result)> TraceDraws(JsonElement t) =>
        t.GetProperty("draws").EnumerateArray()
            .Where(d => d[0].GetString() is "n" or "u" or "l" && d[1].GetString() != "0x142063b9c")
            .Select(d => (Kind: d[0].GetString()![0], Caller: d[1].GetString()!, Arg: d[2].GetUInt32(), Result: d[3].GetUInt32())).ToList();

    /// <summary>The (area, seed) → (level, tile hash) pairs the client logged (Client/logs/Client.txt), only from the
    /// sessions of the local client's version: each client start logs its patch root ("Web root:
    /// https://patch.poecdn.com/3.29.3.3/"), and the log's last session is the local client. Older versions' hashes are
    /// no oracle: the log holds 3.27 (February) and 3.28 (April) sessions of the official game, and every (area, seed,
    /// level) logged by both 3.27 and 3.29 has another hash (game data changed between patches; 3.29 cannot even
    /// generate 1_3_8_1 seed 2339687786, which 3.27 did).</summary>
    public static SortedDictionary<(string Area, uint Seed), (int Level, uint Hash)> LoggedHashes() => LoggedHashes(out _, out _);

    public static SortedDictionary<(string Area, uint Seed), (int Level, uint Hash)> LoggedHashes(out string? version, out int otherVersions)
    {
        // The running client keeps its log open for writing.
        string path = Path.Combine(Root(), "Client", "logs", "Client.txt");
        StreamReader Open() => new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        var webRoot = new System.Text.RegularExpressions.Regex(@"\] Web root: \S*/([0-9][0-9.]*[0-9])/?\s*$");
        version = null;
        using (var scan = Open())
            for (string? line; (line = scan.ReadLine()) is not null;)
                if (webRoot.Match(line) is { Success: true } v) version = v.Groups[1].Value;
        var pairs = new SortedDictionary<(string Area, uint Seed), (int Level, uint Hash)>();
        (string, uint, int)? cur = null;
        string? session = null;
        otherVersions = 0;
        using var log = Open();
        for (string? line; (line = log.ReadLine()) is not null;)
        {
            if (webRoot.Match(line) is { Success: true } w) { session = w.Groups[1].Value; continue; }
            var m = System.Text.RegularExpressions.Regex.Match(line, @"Generating level (\d+) area ""([^""]+)"" with seed (\d+)");
            if (m.Success && session != version) { otherVersions++; cur = null; continue; }
            if (m.Success) { cur = (m.Groups[2].Value, uint.Parse(m.Groups[3].Value), int.Parse(m.Groups[1].Value)); continue; }
            m = System.Text.RegularExpressions.Regex.Match(line, @"Tile hash: (\d+)");
            if (m.Success && cur is { } c) { pairs[(c.Item1, c.Item2)] = (c.Item3, uint.Parse(m.Groups[1].Value)); cur = null; }
        }
        return pairs;
    }
}
