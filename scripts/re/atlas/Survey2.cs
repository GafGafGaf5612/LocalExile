using GameServer.Data.Terrain;

namespace Atlas;

/// <summary>Which tile key index is which side/corner: compares a room's own key with the keys of the slots on its border.</summary>
static class Survey2
{
    public static void Run(GameFiles files, List<TopologyRow> topologies, string filter)
    {
        var re = new System.Text.RegularExpressions.Regex(filter, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // sides: 0 top (row 0), 1 right (last column), 2 bottom (last row), 3 left (column 0)
        var edgeScore = new int[4, 4]; var edgeTotal = new int[4, 4];
        // corners: 0 (0,0) 1 (W,0) 2 (W,H) 3 (0,H)
        var cornerScore = new int[4, 4]; var cornerTotal = new int[4, 4];
        int rooms = 0;
        foreach (var topo in topologies.Where(t => re.IsMatch(t.Graph)))
        {
            var graph = files.Graph(topo.Graph); if (graph is null) continue;
            var tsi = files.Tsi(graph.MasterFile); if (tsi?.GetValueOrDefault("RoomSet") is not { } rs) continue;
            foreach (var arm in files.List(TerrainText.Resolve(graph.MasterFile, rs), ".arm"))
            {
                if (!seen.Add(arm) || files.Room(arm) is not { } room || room.RoomKey is null || room.Slots.Count == 0) continue;
                rooms++;
                var rk = room.RoomKey;
                foreach (var s in room.Slots)
                {
                    bool[] onSide = [s.Row == 0, s.Column + s.Key.Width == room.Width, s.Row + s.Key.Height == room.Height, s.Column == 0];
                    for (int side = 0; side < 4; side++)
                    {
                        if (!onSide[side]) continue;
                        for (int i = 0; i < 4; i++)
                        {
                            if (rk.Edges[i] == 0) continue;
                            edgeTotal[side, i]++;
                            if (s.Key.Edges[i] == rk.Edges[i]) edgeScore[side, i]++;
                        }
                    }
                    bool[] atCorner = [onSide[0] && onSide[3], onSide[0] && onSide[1], onSide[2] && onSide[1], onSide[2] && onSide[3]];
                    for (int c = 0; c < 4; c++)
                    {
                        if (!atCorner[c]) continue;
                        for (int i = 0; i < 4; i++)
                        {
                            cornerTotal[c, i]++;
                            if (s.Key.Grounds[i] == rk.Grounds[i]) cornerScore[c, i]++;
                        }
                    }
                }
            }
        }
        Console.WriteLine($"rooms {rooms}");
        string[] sides = ["top", "right", "bottom", "left"], corners = ["(0,0)", "(W,0)", "(W,H)", "(0,H)"];
        for (int side = 0; side < 4; side++)
            Console.WriteLine($"side {sides[side],-6}: " + string.Join("  ", Enumerable.Range(0, 4).Select(i => $"E{i} {edgeScore[side, i]}/{edgeTotal[side, i]}")));
        for (int c = 0; c < 4; c++)
            Console.WriteLine($"corner {corners[c],-6}: " + string.Join("  ", Enumerable.Range(0, 4).Select(i => $"G{i} {cornerScore[c, i]}/{cornerTotal[c, i]}")));
    }
}
