using System.Globalization;

namespace GameServer.Data.Terrain.Generation;

/// <summary>One doodad line of a .ddt group (<c>0x142a17320</c>): the .ao, its weight (−1: an "All" line, placed at
/// every vertex of the group's grounds), an angle (radians; +∞ = random), its own spacing radius (runtime +0xc: no
/// other of the same .ao that close) and the group's clear radius (+0x10: nothing else on the same ground that close).</summary>
public sealed record DdtEntry(string Model, int Weight, float Angle, int Radius, int GroundRadius)
{
    public bool RandomAngle => float.IsPositiveInfinity(Angle);
}

/// <summary>A weighted list of .ddt lines with their total (runtime +0x18 list, +0x30 total).</summary>
public sealed class DdtGroup
{
    public List<DdtEntry> Weighted { get; } = [];
    public int Total { get; private set; }

    public void Add(DdtEntry e)
    {
        if (e.Weight == 0) return;
        Weighted.Add(e);
        Total += e.Weight;
    }

    /// <summary><c>0x1420061f0</c>: the entry the roll falls on (none when the list is empty).</summary>
    public DdtEntry? Pick(uint roll)
    {
        if (Total == 0 || Weighted.Count == 0) return null;
        int r = (int)roll;
        foreach (var e in Weighted)
        {
            if (r < e.Weight) return e;
            r -= e.Weight;
        }
        return Weighted[^1];
    }
}

/// <summary>A doodad table (.ddt, parser <c>0x142a17e00</c>, callbacks <c>0x141ff2540</c>…<c>0x141ff2640</c>): "version
/// N", the default density (× 1000) and from version 4 the default clear radius and an optional flag "1", then
/// "Default" and its lines; then groups: a line of quoted ground names (comma separated) followed by "All" (the group's
/// lines are picked once at every vertex of those grounds) or by a density ("D" = the default) and a clear radius, then
/// the group's lines. A line is "weight "file.ao" [radius|D] [angle]", weight "All" making it a fixed line.</summary>
public sealed class DoodadTable
{
    /// <summary>+0x13c: the highest density of any group (the random doodads' attempts per 1000 tiles).</summary>
    public int MaxDensity { get; private set; }

    /// <summary>+0x138 bit 0 (the flag after the default clear radius).</summary>
    public bool Flag { get; private set; }

    /// <summary>+0x138 &gt;&gt; 1: an attempt on a ground without a group is dropped while Next(1024) is below it.</summary>
    public uint DefaultThreshold { get; private set; }

    public DdtGroup Default { get; } = new();
    public List<DdtEntry> DefaultFixed { get; } = [];
    /// <summary>+0x38: the density groups by ground name; +0xf8 their thresholds.</summary>
    public Dictionary<string, DdtGroup> Grounds { get; } = [];
    public Dictionary<string, uint> Thresholds { get; } = [];
    /// <summary>+0x78: the "All" groups by ground name.</summary>
    public Dictionary<string, DdtGroup> AllGroups { get; } = [];
    /// <summary>+0xb8: fixed lines by ground name.</summary>
    public Dictionary<string, List<DdtEntry>> Fixed { get; } = [];

    public static DoodadTable Parse(string text)
    {
        var t = new DoodadTable();
        var r = new Reader(text);
        if (r.Word() != "version") throw new InvalidDataException("Error: Expected: \"version\"");
        int version = (int)r.Number();
        int density = (int)Math.Floor(r.Number() * 1000 + 0.5);
        int radius = 0;
        if (version > 3) radius = (int)r.Number();
        string rest = r.Line().Trim();
        if (version > 3 && rest == "1") t.Flag = true;
        if (r.Word() != "Default") throw new InvalidDataException("Expected: \"Default\"");
        var (weighted, fixedLines) = ReadLines(r, version, radius);
        t.MaxDensity = Math.Max(t.MaxDensity, density);
        foreach (var e in weighted) t.Default.Add(e);
        t.DefaultFixed.AddRange(fixedLines);
        var groupDensities = new List<(string Ground, int Density)>();
        while (!r.End)
        {
            string line = r.Line().Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            var h = new Reader(line);
            h.SkipSpace();
            if (h.Peek() != '"') throw new InvalidDataException("Error: could not read doodad ground type " + line);
            var grounds = h.Quoted().Split(',').Select(g => g.Trim()).ToList();
            string word = h.Word();
            if (word.Length >= 3 && word.StartsWith("All", StringComparison.Ordinal))
            {
                var (w, f) = ReadLines(r, version, radius);
                t.MaxDensity = Math.Max(t.MaxDensity, density);
                t.Store(t.AllGroups, grounds, w, f);
            }
            else
            {
                int groupDensity = density;
                if (word.Length > 0 && word[0] != 'D' && float.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out float d))
                    groupDensity = (int)Math.Floor(d * 1000 + 0.5);
                int groupRadius = radius;
                if (version > 3 && h.TryNumber(out double gr)) groupRadius = (int)gr;
                var (w, f) = ReadLines(r, version, groupRadius);
                t.MaxDensity = Math.Max(t.MaxDensity, groupDensity);
                foreach (var g in grounds) groupDensities.Add((g, groupDensity));
                t.Store(t.Grounds, grounds, w, f);
            }
        }
        // 0x141ff2730 after the parse: the thresholds out of 1024 against the highest density.
        int max = Math.Max(t.MaxDensity, 1);
        t.DefaultThreshold = (uint)((max - density) * 0x400 / max);
        foreach (var (g, d) in groupDensities) t.Thresholds[g] = (uint)((max - d) * 0x400 / max);
        return t;
    }

    /// <summary><c>0x141ff2260</c> / <c>0x141ff2350</c>: every ground of the header shares the lines; a ground without
    /// fixed lines leaves the fixed map.</summary>
    void Store(Dictionary<string, DdtGroup> groups, List<string> grounds, List<DdtEntry> weighted, List<DdtEntry> fixedLines)
    {
        foreach (var g in grounds)
        {
            if (!groups.TryGetValue(g, out var group)) groups[g] = group = new DdtGroup();
            foreach (var e in weighted) group.Add(e);
            if (fixedLines.Count == 0) Fixed.Remove(g);
            else
            {
                if (!Fixed.TryGetValue(g, out var list)) Fixed[g] = list = [];
                list.AddRange(fixedLines);
            }
        }
    }

    /// <summary><c>0x142a17320</c>: lines up to the next line starting with a quote.</summary>
    static (List<DdtEntry> Weighted, List<DdtEntry> Fixed) ReadLines(Reader r, int version, int groupRadius)
    {
        var weighted = new List<DdtEntry>();
        var fixedLines = new List<DdtEntry>();
        r.SkipSpace();
        while (!r.End && r.Peek() != '"')
        {
            string line = r.Line().Trim();
            r.SkipSpace();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            var l = new Reader(line);
            string first = l.Word();
            int weight = first == "All" ? -1 : int.Parse(first, CultureInfo.InvariantCulture);
            l.SkipSpace();
            if (l.Peek() != '"') throw new InvalidDataException("Error: could not read doodad filename " + line);
            string model = l.Quoted();
            int radius = 0;
            if (version > 3 && weight != -1)
            {
                string token = l.Word();
                if (token.Length > 0 && token[0] != 'D') radius = (int)uint.Parse(token, CultureInfo.InvariantCulture);
            }
            float angle = l.TryNumber(out double a) ? (float)a : float.PositiveInfinity;
            var e = new DdtEntry(model, weight, angle, radius, groupRadius);
            (weight == -1 ? fixedLines : weighted).Add(e);
        }
        return (weighted, fixedLines);
    }

    sealed class Reader(string s)
    {
        int _p;
        public bool End => _p >= s.Length;
        public char Peek() => End ? '\0' : s[_p];
        public void SkipSpace() { while (!End && s[_p] <= ' ') _p++; }

        public string Word()
        {
            SkipSpace();
            int start = _p;
            while (!End && s[_p] > ' ') _p++;
            return s[start.._p];
        }

        public double Number() => double.Parse(Word(), NumberStyles.Float, CultureInfo.InvariantCulture);

        public bool TryNumber(out double value)
        {
            int at = _p;
            if (double.TryParse(Word(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
            _p = at;
            return false;
        }

        public string Line()
        {
            int start = _p;
            while (!End && s[_p] != '\n') _p++;
            string line = s[start.._p];
            if (!End) _p++;
            return line;
        }

        public string Quoted()
        {
            _p++;
            int start = _p;
            while (!End && s[_p] != '"') _p++;
            string q = s[start.._p];
            if (!End) _p++;
            return q;
        }
    }
}

/// <summary>A subgraph record as the doodad stage reads it (generator +0x30 → +0x10, 0x98 bytes): its rectangle in tiles
/// (+0x40 … +0x4c), master (+0x50) and the rooms placed in it (+0x58, 0x38 bytes each, in placing order).</summary>
public sealed record DoodadRecord(int X, int Y, int W, int H, Dictionary<string, string> Master, string MasterPath,
    IReadOnlyList<PlacedRoom> Rooms);

/// <summary>The doodad stage (<c>0x1420425d0</c>, "Generating doodads..."), the parts ported so far: first the doodads
/// of every record's rooms (<c>0x142040770</c>), then per record the random doodads of its master's .ddt (<c>Next</c>
/// draws between a copy and a restore of the generator state) and the doodads at its inner vertices ("All" groups and
/// fixed lines). Each random or vertex doodad goes through <c>0x142044180</c>: <c>0x14202ede0</c> (an .ao without
/// AOSet: a random angle when the line has none), a mirror roll for random doodads, another angle roll. A doodad's
/// collision goes on the walkability grids as it is created, so later walk checks see it.</summary>
public sealed class DoodadStage(TerrainCache files, LevelRandom rng, TileGrid grid, Walkability walk, List<string> notes)
{
    public List<PlacedDoodad> Placed { get; } = [];

    readonly Dictionary<string, DoodadHull?> _hulls = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The object type of a client doodad. Room objects of other types (NPCs, monsters, blockers) are server
    /// objects (<c>0x14202f640</c> → <c>0x142041ac0</c>) or filtered out (type +0x2a8 not 0 or 1, vtable +0x98).</summary>
    const string DoodadType = "Metadata/MiscellaneousObjects/Doodad";

    public void Run(IReadOnlyList<DoodadRecord> records)
    {
        // 0x1420425d0's first loop: every record's rooms, in order.
        foreach (var rec in records)
            foreach (var room in rec.Rooms) RoomDoodads(room);
        for (int index = 0; index < records.Count; index++)
        {
            var rec = records[index];
            if (!rec.Master.TryGetValue("Doodads", out var ddtPath)) continue;
            var text = files.Text(TerrainText.Resolve(rec.MasterPath, ddtPath));
            if (text is null) continue;
            var ddt = DoodadTable.Parse(text);
            uint attempts = (uint)(ddt.MaxDensity * rec.W * rec.H) / 1000;
            if (attempts != 0)
            {
                var saved = rng.Save();
                RandomDoodads(records, index, ddt, attempts);
                rng.Restore(saved);
            }
            if (ddt.AllGroups.Count != 0 || ddt.DefaultFixed.Count != 0 || ddt.Fixed.Count != 0) VertexDoodads(records, index, ddt);
        }
    }

    /// <summary><c>0x141d77c60</c>: the tile is in record <paramref name="index"/> and in no later one.</summary>
    static bool Owns(IReadOnlyList<DoodadRecord> records, int index, int tx, int ty)
    {
        static bool In(DoodadRecord r, int x, int y) => x >= r.X && x < r.X + r.W && y >= r.Y && y < r.Y + r.H;
        if (!In(records[index], tx, ty)) return false;
        for (int k = index + 1; k < records.Count; k++)
            if (In(records[k], tx, ty)) return false;
        return true;
    }

    string? GroundName(string? path) => path is null ? null : files.Ground(path)?.Name;
    int GroundFlags(string? path) => path is null ? 0 : files.Ground(path)?.Flags ?? 0;

    void RandomDoodads(IReadOnlyList<DoodadRecord> records, int index, DoodadTable ddt, uint attempts)
    {
        var rec = records[index];
        var sameModel = new Dictionary<string, List<(int X, int Y, int R2)>>();
        var sameGround = new Dictionary<string, List<(int X, int Y, int R2)>>();
        for (uint n = 0; n < attempts; n++)
        {
            int x = rec.X * 23 + (int)rng.Next((uint)(rec.W * 23));
            int y = rec.Y * 23 + (int)rng.Next((uint)(rec.H * 23));
            int tx = x / 23, ty = y / 23;
            if (!Owns(records, index, tx, ty)) continue;
            if (grid[tx, ty] is not { } e) continue;
            if ((TileFlags(e.Tile) & 4) == 0) continue;
            // The nearest vertex's ground.
            string? groundPath = grid.Corner(tx + (x - tx * 23 > 11 ? 1 : 0), ty + (y - ty * 23 > 11 ? 1 : 0)).Ground;
            string? ground = GroundName(groundPath);
            DdtEntry? pick;
            bool loose = ground is null || (GroundFlags(groundPath) & 4) != 0;
            // No ground, or a flag-4 one: a cell already short of open ground keeps no doodad (0x141d85260 < 5).
            if (loose && walk.Walk[y * walk.Width + x] < 5) continue;
            if (ground is null)
            {
                if (ddt.Default.Weighted.Count == 0) continue;
                if (rng.Next(0x400) < ddt.DefaultThreshold) continue;
                pick = ddt.Default.Pick(rng.Next((uint)ddt.Default.Total));
            }
            else if (ddt.Grounds.GetValueOrDefault(ground) is { } group)
            {
                if (rng.Next(0x400) < ddt.Thresholds.GetValueOrDefault(ground)) continue;
                pick = group.Pick(rng.Next((uint)group.Total));
            }
            else
            {
                // A flag-4 ground without a group takes the default list.
                if (!loose || rng.Next(0x400) < ddt.DefaultThreshold) continue;
                pick = ddt.Default.Pick(rng.Next((uint)ddt.Default.Total));
            }
            if (pick is null) continue;
            if (Near(sameModel.GetValueOrDefault(pick.Model), x, y)) continue;
            string groundKey = ground ?? "";
            if (Near(sameGround.GetValueOrDefault(groundKey), x, y)) continue;
            if (!Place(pick, x, y, flip: true)) continue;
            if (pick.Radius != 0)
            {
                if (!sameModel.TryGetValue(pick.Model, out var l)) sameModel[pick.Model] = l = [];
                l.Add((x, y, pick.Radius * pick.Radius));
            }
            if (pick.GroundRadius != 0)
            {
                if (!sameGround.TryGetValue(groundKey, out var l)) sameGround[groundKey] = l = [];
                l.Add((x, y, pick.GroundRadius * pick.GroundRadius));
            }
        }
    }

    static bool Near(List<(int X, int Y, int R2)>? points, int x, int y) =>
        points is not null && points.Any(p => (uint)((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y)) <= (uint)p.R2);

    /// <summary>The 1020-line loop's second half: every inner vertex of the record it owns takes its ground's fixed lines
    /// (or the default ones) and one pick of its ground's "All" group.</summary>
    void VertexDoodads(IReadOnlyList<DoodadRecord> records, int index, DoodadTable ddt)
    {
        var rec = records[index];
        for (int v = 1; v < rec.H; v++)
            for (int u = 1; u < rec.W; u++)
            {
                int vx = rec.X + u, vy = rec.Y + v;
                if (!Owns(records, index, vx, vy)) continue;
                string? ground = GroundName(grid.Corner(vx, vy).Ground);
                var fixedLines = ground is null ? ddt.DefaultFixed : ddt.Fixed.GetValueOrDefault(ground) ?? [];
                foreach (var f in fixedLines) Place(f, vx * 23, vy * 23, flip: false);
                if (ddt.AllGroups.TryGetValue(ground ?? "Default", out var all) && all.Total != 0)
                    if (all.Pick(rng.Next((uint)all.Total)) is { } pick) Place(pick, vx * 23, vy * 23, flip: true);
            }
    }

    /// <summary><c>0x142044180</c>.</summary>
    bool Place(DdtEntry entry, int x, int y, bool flip)
    {
        var text = files.Text(entry.Model);
        if (text is null) return false;
        if (text.Contains("AOSet", StringComparison.Ordinal))
        {
            notes.Add($"doodad {entry.Model}: AOSet (0x14202ede0) is not ported");
            return false;
        }
        // 0x14202ede0 without an AOSet: a random angle when the line has none.
        float defAngle = entry.RandomAngle ? rng.Next(0x200) * (1f / 512) * (MathF.PI * 2) : 0;
        bool mirrored = false;
        float angle;
        if (entry.RandomAngle && flip && rng.Next(2) != 0) mirrored = true;
        if (entry.RandomAngle) angle = rng.Next(0x200) * (1f / 512) * (MathF.PI * 2);
        else
        {
            angle = entry.Angle;
            while (angle < 0) angle += MathF.PI * 2;
            while (angle > MathF.PI * 2) angle -= MathF.PI * 2;
        }
        Create(new PlacedDoodad(entry.Model, x, y, defAngle + angle, 1, mirrored));
        return true;
    }

    /// <summary><c>0x142040770</c>: a room's doodad lines in order. Each resolves its .ao (<c>0x14202ede0</c> without a
    /// random angle: no draw for an .ao without AOSet) and is placed by <c>0x14202f1c0</c>: the cell turned with the
    /// room's code inside its untouched w·23 × h·23 cells, plus the room's origin; the angle mirrored (2π − a) for codes
    /// 4–7, plus (code &amp; 3)·π/2; the scale; mirrored when the code mirrors xor the line's flip.</summary>
    void RoomDoodads(PlacedRoom room)
    {
        int code = room.Orientation;
        foreach (var d in room.Room.Doodads)
        {
            if (!d.Type.Equals(DoodadType, StringComparison.OrdinalIgnoreCase)) continue;
            var text = files.Text(d.Model);
            if (text is null) continue;
            if (text.Contains("AOSet", StringComparison.Ordinal))
            {
                notes.Add($"room doodad {d.Model}: AOSet (0x14202ede0) is not ported");
                continue;
            }
            var (cx, cy) = Orientation.Map(code, (int)d.X, (int)d.Y, room.Room.Width * 23, room.Room.Height * 23);
            float angle = d.Angle;
            bool mirrors = code is >= 4 and < 8;
            if (mirrors) angle = MathF.PI * 2 - angle;
            angle += (code & 3) * (MathF.PI / 2);
            while (angle < 0) angle += MathF.PI * 2;
            while (angle > MathF.PI * 2) angle -= MathF.PI * 2;
            Create(new PlacedDoodad(d.Model, room.X * 23 + cx, room.Y * 23 + cy, angle, d.Scale, mirrors ^ d.Flip));
        }
    }

    /// <summary><c>0x142042360</c> (nothing outside the map) and the world add: the doodad and its collision
    /// (<c>0x141d876e0</c>).</summary>
    void Create(PlacedDoodad d)
    {
        if (d.X < 0 || d.Y < 0 || d.X >= grid.Width * 23 || d.Y >= grid.Height * 23) return;
        Placed.Add(d);
        if (!_hulls.TryGetValue(d.Model, out var hull)) _hulls[d.Model] = hull = DoodadHull.Load(files.Text, d.Model);
        if (hull is { IsEmpty: false }) walk.Stamp(hull, d);
    }

    /// <summary>Tile data +0x16c: the low byte of the .tdt's flags word (bit 4: doodads may go on the tile).</summary>
    int TileFlags(TileDef tile) => files.TileFlags(tile.Path);
}
