using GameServer.Data.Terrain;

namespace Atlas;

/// <summary>Statistics used to settle the file conventions (run with "survey"): how room tile keys line up with .tdt
/// headers under the 8 symmetries of a square, graph rotation tokens, parse failures.</summary>
static class Survey
{
    // The 8 symmetries as index maps on a 4-cycle: rotations p[i] = (i - k) & 3, reflections p[i] = (k - i) & 3.
    public static readonly int[][] Perms = Enumerable.Range(0, 8)
        .Select(s => Enumerable.Range(0, 4).Select(i => s < 4 ? (i - s) & 3 : (s - 4 - i) & 3).ToArray()).ToArray();

    public static string Name(int s) => s < 4 ? $"r{s}" : $"m{s - 4}";

    public static void Run(GameFiles files, List<AreaRow> areas, List<TopologyRow> topologies)
    {
        var rot = new Dictionary<string, int>();
        var versions = new Dictionary<string, int>();
        var combos = new Dictionary<string, int>();
        var seenRooms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int keys = 0, matched = 0, unmatched = 0, roomProblems = 0;
        var unmatchedSamples = new List<string>();
        var topoById = topologies.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var topo in topologies)
        {
            var graph = files.Graph(topo.Graph);
            if (graph is null) continue;
            foreach (var n in graph.Nodes) rot[n.Rotation] = rot.GetValueOrDefault(n.Rotation) + 1;
            var tsi = files.Tsi(graph.MasterFile);
            if (tsi is null) continue;
            string? rs = tsi.GetValueOrDefault("RoomSet"), ts = tsi.GetValueOrDefault("TileSet");
            if (rs is null || ts is null) continue;
            var tdts = files.List(TerrainText.Resolve(graph.MasterFile, ts), ".tdt")
                .Select(p => files.Tdt(p)).Where(t => t is not null).Select(t => t!).ToList();
            foreach (var t in tdts) versions["tdt " + t.Version] = versions.GetValueOrDefault("tdt " + t.Version) + 1;
            foreach (var armPath in files.List(TerrainText.Resolve(graph.MasterFile, rs), ".arm"))
            {
                if (!seenRooms.Add(armPath)) continue;
                var room = files.Room(armPath);
                if (room is null) continue;
                versions["arm " + room.Version] = versions.GetValueOrDefault("arm " + room.Version) + 1;
                if (room.Problem is not null) { roomProblems++; if (roomProblems < 15) Console.WriteLine($"room problem {armPath}: {room.Problem}"); continue; }
                foreach (var slot in room.Slots)
                {
                    var k = slot.Key;
                    if (k.Tag == 0 || (k.Width == 1 && k.Height == 1)) continue;
                    string tag = room.Dict(k.Tag);
                    var cands = tdts.Where(t => t.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (cands.Count == 0) continue;
                    keys++;
                    var hits = new HashSet<string>();
                    foreach (var t in cands)
                        foreach (var (e, g, swap) in Matches(room, k, t)) hits.Add($"{Name(e)}/{Name(g)}{(swap ? " swap" : "")}");
                    if (hits.Count == 0)
                    {
                        unmatched++;
                        if (unmatchedSamples.Count < 12)
                            unmatchedSamples.Add($"{armPath} k {k.Width}x{k.Height} E[{string.Join(",", k.Edges.Select(room.Dict).Select(Short))}] " +
                                $"G[{string.Join(",", k.Grounds.Select(room.Dict).Select(Short))}] vs {cands.Count} tdt e.g. {cands[0].Width}x{cands[0].Height} " +
                                $"E[{string.Join(",", cands[0].Edges.Select(Short))}] G[{string.Join(",", cands[0].Grounds.Select(Short))}]");
                        continue;
                    }
                    matched++;
                    string key = string.Join(" | ", hits.OrderBy(x => x));
                    combos[key] = combos.GetValueOrDefault(key) + 1;
                }
            }
        }
        Console.WriteLine("rotation tokens: " + string.Join(", ", rot.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value}")));
        Console.WriteLine("versions: " + string.Join(", ", versions.OrderBy(x => x.Key).Select(x => $"{x.Key}: {x.Value}")));
        Console.WriteLine($"tagged keys {keys}: matched {matched}, unmatched {unmatched}; room problems {roomProblems}");
        foreach (var s in unmatchedSamples) Console.WriteLine("  unmatched " + s);
        foreach (var (k, v) in combos.OrderByDescending(x => x.Value).Take(40)) Console.WriteLine($"  {v,6}  {k}");
        foreach (var (p, why) in files.Missing.Take(20)) Console.WriteLine($"missing {p}: {why}");
        Console.WriteLine($"missing total {files.Missing.Count}");
    }

    static string Short(string s) => s.Length == 0 ? "-" : Path.GetFileNameWithoutExtension(s);

    /// <summary>Every (edge symmetry, ground symmetry, swapped size) under which the tdt's header equals the room key.</summary>
    public static IEnumerable<(int E, int G, bool Swap)> Matches(RoomFile room, TileKey k, TileDefinition t)
    {
        foreach (bool swap in new[] { false, true })
        {
            var (w, h) = swap ? (t.Height, t.Width) : (t.Width, t.Height);
            if (w != k.Width || h != k.Height) continue;
            for (int e = 0; e < 8; e++)
            {
                if (!Same(k.Edges.Select(room.Dict).ToArray(), t.Edges, Perms[e])) continue;
                for (int g = 0; g < 8; g++)
                    if (Same(k.Grounds.Select(room.Dict).ToArray(), t.Grounds, Perms[g])) yield return (e, g, swap);
            }
        }
    }

    static bool Same(string[] room, string[] tile, int[] p)
    {
        for (int i = 0; i < 4; i++)
            if (!room[i].Equals(tile[p[i]], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
