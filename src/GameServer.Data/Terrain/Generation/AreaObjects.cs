namespace GameServer.Data.Terrain.Generation;

/// <summary>A tile object the server creates itself: an area transition or a waypoint, at a grid cell, with its
/// object type (.ot), its virtual model, its angle and its height (the record's z, negative is up).</summary>
public sealed record PlacedTileObject(string Type, string Model, int X, int Y, float Rotation, float Z);

/// <summary>The area transitions and waypoints of a generated grid, placed as <see cref="DoodadStage"/>'s tile lines
/// place tile objects (<c>0x142041200</c>): row by row, every tile origin's objects (the record master's Reskin copy
/// when there is one; per sub-tile for a tile made of them), then the .tmd the .tdt names (+0x180), turned by the
/// entry's orientation. What scripts/walkmaps captures from the client as &lt;area&gt;_seed&lt;seed&gt;.transitions.json.
/// The transitions' server-only effects (<see cref="EffectsFolder"/>: Act 5's crater glow and ground shatter) are
/// placed too.</summary>
public static class AreaObjects
{
    /// <summary>The two server-only Transitionable effects a tile places beside an entrance (GlowyFX,
    /// SlavePensIntroGround); no area transition themselves.</summary>
    public const string EffectsFolder = "Metadata/MiscellaneousObjects/AreaTransitionEffects/";

    public static bool IsEffect(string type) => type.StartsWith(EffectsFolder, StringComparison.OrdinalIgnoreCase);

    public static List<PlacedTileObject> Transitions(TerrainCache files, GenerationResult result)
    {
        var grid = result.Grid;
        var records = result.Records;
        var placed = new List<PlacedTileObject>();
        for (int y = 0; y < grid.Height; y++)
            for (int x = 0; x < grid.Width; x++)
            {
                if (grid[x, y] is not { IsOrigin: true } e) continue;
                var tile = e.Tile;
                if (!files.HasTransition(tile) && !files.TileObjects(tile).Concat(files.SecondObjects(tile)).Any(o => IsEffect(o.Type))) continue;
                int index = 0;
                for (int k = 0; k < records.Count; k++)
                    if (x >= records[k].X && x < records[k].X + records[k].W && y >= records[k].Y && y < records[k].Y + records[k].H) index = k;
                string? reskin = records.Count != 0 ? records[index].Master.GetValueOrDefault("Reskin") : null;

                void Line(TileObject o, int cellX, int cellY, float angle)
                {
                    if (!files.IsTransitionType(o.Type) && !IsEffect(o.Type)) return;
                    var (cx, cy) = Orientation.Map(e.Orientation, cellX, cellY, tile.Width * 23, tile.Height * 23);
                    string model = IsEffect(o.Type) && EffectModel(files, o.Type) is { } own ? own : o.VirtualModel(tile.Path);
                    placed.Add(new PlacedTileObject(o.Type, model, x * 23 + cx, y * 23 + cy,
                        TilePlacement.Angle(e.Orientation, angle), o.Z - e.HeightOffset * 7.8125f));
                }

                var objects = files.TileObjects(tile);
                if (objects.Count != 0 && (files.TileFlags(tile.Path) & 0x200) != 0) SubTiles(files, tile, reskin, Line);
                else if (objects.Count != 0)
                {
                    if (reskin is { Length: > 0 }) objects = files.ReskinObjects(tile, reskin) ?? objects;
                    foreach (var o in objects) Line(o, o.CellX, o.CellY, o.Angle);
                }
                foreach (var o in files.SecondObjects(tile)) Line(o, o.CellX, o.CellY, o.Angle);
            }
        return placed;
    }

    // An effect's model is its type's Animated animated_object (the record's virtual .ao is not what the client holds:
    // 1_5_1's slave_crater_v01_01_glowyFX record is crater_glow.ao).
    static string? EffectModel(TerrainCache files, string type) =>
        files.Text(type + ".ot") is { } text
            ? TerrainText.Lines(text).Select(l => l.Trim()).Where(l => l.StartsWith("animated_object", StringComparison.Ordinal))
                .Select(l => l[(l.IndexOf('"') + 1)..l.LastIndexOf('"')]).FirstOrDefault()
            : null;

    // As DoodadStage.SubTileDoodads: each sub-tile's mesh objects, turned by the slot's code inside its span.
    static void SubTiles(TerrainCache files, TileDef tile, string? reskin, Action<TileObject, int, int, float> line)
    {
        if (files.Walk(tile.Path) is not { } walkData || walkData.SubTiles.Length < tile.Width * tile.Height) return;
        SubTileWalk Slot(int u, int v) => walkData.SubTiles[v * tile.Width + u];
        for (int v = 0; v < tile.Height; v++)
            for (int u = 0; u < tile.Width; u++)
            {
                var slot = Slot(u, v);
                if ((slot.G & 0xe) != 0) continue;
                if (files.SubTileObjects(tile, slot.Mesh, reskin) is not { } objects) continue;
                int w = 1, h = 1;
                while (u + w < tile.Width && (Slot(u + w, v).G & 2) != 0) w++;
                while (v + h < tile.Height && (Slot(u, v + h).G & 4) != 0) h++;
                int code = slot.Code;
                if ((code & 1) != 0) (w, h) = (h, w);
                var (t0, t1, t2) = TilePlacement.Row(code);
                foreach (var o in objects)
                {
                    Span<float> a = [w * 250 - o.X, o.X, h * 250 - o.Y, o.Y];
                    float px = u * 250f + a[t1 + 2 * t0], py = v * 250f + a[(t0 == 0 ? 2 : 0) + t2];
                    line(o, (int)MathF.Floor(px * 0.092f), (int)MathF.Floor(py * 0.092f), TilePlacement.Angle(code, o.Angle));
                }
            }
    }
}
