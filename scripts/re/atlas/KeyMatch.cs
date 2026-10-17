using GameServer.Data.Terrain;

namespace Atlas;

/// <summary>Matches a room's tile key against a tile definition's header under the 8 placement codes.</summary>
static class KeyMatch
{
    // Placement code c is the symmetry Survey.Perms[c] on edges (E0..E3 = top, right, bottom, left) and its corner partner on
    // corners (G0..G3 = (0,0), (W,0), (W,H), (0,H)); settled with "survey" and "survey2" (08-area-transitions.md §6).
    public static readonly int[] SymmetryOfCode = [0, 1, 2, 3, 4, 5, 6, 7];

    // Placement code → (edge index map, corner index map): placed[i] = tile[map[i]]. See SymmetryOfCode.
    static readonly (int[] Edges, int[] Grounds)[] Maps = Enumerable.Range(0, 8).Select(c => (Survey.Perms[SymmetryOfCode[c]], Survey.Perms[GroundSymmetry(SymmetryOfCode[c])])).ToArray();


    /// <summary>The corner map that goes with an edge map: the same rotation; a mirror m_j on edges is m_(j+1) on corners.</summary>
    public static int GroundSymmetry(int s) => s < 4 ? s : 4 + ((s - 4 + 1) & 3);

    /// <summary>The codes under which the tile fits the key: size, tag, edges and corners, and (when that leaves several)
    /// the edge point pairs, which a mirror reverses. <paramref name="pointsDecided"/> is false when no code also fits the
    /// points, so the result is the wider set.</summary>
    public static int[] Codes(RoomFile room, TileKey k, TileDefinition t) => Codes(room, k, t, out _);

    public static int[] Codes(RoomFile room, TileKey k, TileDefinition t, out bool pointsDecided)
    {
        var all = CodesWithoutPoints(room, k, t);
        pointsDecided = true;
        if (all.Length <= 1 || t.Points.Length != 8) return all;
        var withPoints = all.Where(c => PointsFit(k, t, c)).ToArray();
        if (withPoints.Length == 0) { pointsDecided = false; return all; }
        return withPoints;
    }

    static bool PointsFit(TileKey k, TileDefinition t, int code)
    {
        var me = Maps[code].Edges;
        for (int i = 0; i < 4; i++)
        {
            int a = t.Points[2 * me[i]], b = t.Points[2 * me[i] + 1];
            if (code >= 4) (a, b) = (b, a);
            if (k.Points[2 * i] != a || k.Points[2 * i + 1] != b) return false;
        }
        return true;
    }

    static int[] CodesWithoutPoints(RoomFile room, TileKey k, TileDefinition t)
    {
        if (!room.Dict(k.Tag).Equals(t.Tag, StringComparison.OrdinalIgnoreCase)) return [];
        var edges = k.Edges.Select(room.Dict).ToArray();
        var grounds = k.Grounds.Select(room.Dict).ToArray();
        var codes = new List<int>();
        for (int c = 0; c < 8; c++)
        {
            var (w, h) = TilePlacement.Swaps(c) ? (t.Height, t.Width) : (t.Width, t.Height);
            if (w != k.Width || h != k.Height) continue;
            var (me, mg) = Maps[c];
            bool ok = true;
            for (int i = 0; i < 4 && ok; i++)
                ok = edges[i].Equals(t.Edges[me[i]], StringComparison.OrdinalIgnoreCase) && grounds[i].Equals(t.Grounds[mg[i]], StringComparison.OrdinalIgnoreCase);
            if (ok) codes.Add(c);
        }
        return codes.ToArray();
    }
}
