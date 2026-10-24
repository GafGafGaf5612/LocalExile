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

    /// <summary>The local client's version. Its sessions log the patch root as https://patch.poecdn.com/3.29.3.3/
    /// or, started by live.ps1, our patch server's http://127.0.0.1:8080/ (no version in it).</summary>
    public const string LocalVersion = "3.29.3.3";

    /// <summary>The (area, seed) → (level, tile hash) pairs the client logged, only from the local client's sessions:
    /// Client/logs/Client-*-archive.txt (older log parts kept aside, in log order) and then Client/logs/Client.txt. Each
    /// client start logs its patch root ("Web root: …"). Other versions' hashes are no oracle: the log held 3.27
    /// (February) and 3.28 (April) sessions of the official game, and 46 of the 78 (area, seed, level) logged by both
    /// 3.27 and 3.29 have another hash (per seed, not per area; 3.29 cannot even generate 1_3_8_1 seed 2339687786, which
    /// 3.27 did).</summary>
    public static SortedDictionary<(string Area, uint Seed), (int Level, uint Hash)> LoggedHashes() => LoggedHashes(out _, out _);

    public static SortedDictionary<(string Area, uint Seed), (int Level, uint Hash)> LoggedHashes(out string? version, out int otherVersions, string? only = null)
    {
        string logs = Path.Combine(Root(), "Client", "logs");
        var paths = Directory.GetFiles(logs, "Client-*-archive.txt").Order().Append(Path.Combine(logs, "Client.txt"));
        var webRoot = new System.Text.RegularExpressions.Regex(@"\] Web root: (\S*)\s*$");
        var versioned = new System.Text.RegularExpressions.Regex(@"/([0-9][0-9.]*[0-9])/?$");
        // Another version's loads only to compare (the oracle's LEVELGEN_ORACLE_VERSION), never as an answer.
        version = only ?? LocalVersion;
        var pairs = new SortedDictionary<(string Area, uint Seed), (int Level, uint Hash)>();
        otherVersions = 0;
        foreach (string path in paths.Where(File.Exists))
        {
            (string, uint, int)? cur = null;
            string? session = null;
            // The running client keeps its log open for writing.
            using var log = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            for (string? line; (line = log.ReadLine()) is not null;)
            {
                if (webRoot.Match(line) is { Success: true } w)
                {
                    string url = w.Groups[1].Value;
                    session = versioned.Match(url) is { Success: true } v ? v.Groups[1].Value
                        : url.Contains("127.0.0.1") || url.Contains("localhost") ? LocalVersion : null;
                    continue;
                }
                var m = System.Text.RegularExpressions.Regex.Match(line, @"Generating level (\d+) area ""([^""]+)"" with seed (\d+)");
                if (m.Success && session != version) { otherVersions++; cur = null; continue; }
                if (m.Success) { cur = (m.Groups[2].Value, uint.Parse(m.Groups[3].Value), int.Parse(m.Groups[1].Value)); continue; }
                m = System.Text.RegularExpressions.Regex.Match(line, @"Tile hash: (\d+)");
                if (m.Success && cur is { } c) { pairs[(c.Item1, c.Item2)] = (c.Item3, uint.Parse(m.Groups[1].Value)); cur = null; }
            }
        }
        return pairs;
    }
}
