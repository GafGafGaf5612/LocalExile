using System.Text;
using System.Text.Json;

namespace Atlas;

/// <summary>
/// data/hideouts.json: every Hideouts.dat row with its area and the doodads of its default decoration (the row's
/// HideoutFile, e.g. Metadata/Terrain/Missions/Hideouts/Forest/decoration.hideout). A .hideout file is JSON whose
/// "doodads" object repeats keys (one per placed doodad, named after the doodad), so it is read as a token stream.
/// Each doodad: hash (MtxTypeGameSpecific HASH32), grid x / y, r (rotation, 65536 = a full turn), fv (variation: index
/// into HideoutDoodads Variation_AOFiles). The Gateway resolves the hashes (HideoutCatalog).
/// Agents/docs/protocol/21-hideouts.md §2.
/// </summary>
static class HideoutFiles
{
    public static void Run(GameFiles files, List<AreaRow> areas, string root, string output)
    {
        using var table = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "data", "tables", "Hideouts.json")));
        var list = new List<object>();
        int row = -1, doodads = 0;
        foreach (var h in table.RootElement.EnumerateArray())
        {
            row++;
            string file = h.GetProperty("HideoutFile").GetString() ?? "";
            int areaRow = h.GetProperty("HideoutArea").ValueKind == JsonValueKind.Number ? h.GetProperty("HideoutArea").GetInt32() : -1;
            if (areaRow < 0 || areaRow >= areas.Count) continue;
            var area = areas[areaRow];
            var placed = files.Bytes(file) is { } bytes ? Parse(bytes) : null;
            if (placed is null) Console.WriteLine($"hideout {row} {area.Id}: {file} missing or unreadable");
            doodads += placed?.Count ?? 0;
            int hash = h.GetProperty("HASH16").GetInt32();
            list.Add(new { row, hash16 = hash, area = area.Id, areaHash16 = area.Hash16, name = area.Name, file, doodads = placed ?? [] });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            note = "Hideouts.dat rows with their area and default decoration (HideoutFile): doodads with MtxTypeGameSpecific hash, grid x/y, rotation r (65536 = a turn), variation fv. scripts/re/atlas hideouts.",
            hideouts = list,
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Console.WriteLine($"{output}: {list.Count} hideouts, {doodads} doodads");
        if (files.Missing.Count > 0) Console.WriteLine($"missing: {string.Join(", ", files.Missing.Keys)}");
    }

    public sealed record Doodad(string Name, uint Hash, int X, int Y, int R, int Fv);

    /// <summary>The "doodads" of a .hideout file, in file order (repeated keys kept).</summary>
    public static List<Doodad>? Parse(byte[] bytes)
    {
        try
        {
            var span = bytes.AsSpan();
            if (span.StartsWith(Encoding.UTF8.Preamble)) span = span[3..];
            var reader = new Utf8JsonReader(span, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var result = new List<Doodad>();
            // Find "doodads" at depth 1, then each property (name) → object of numbers.
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || reader.GetString() != "doodads") continue;
                reader.Read();   // StartObject
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    string name = reader.GetString() ?? "";
                    reader.Read();   // StartObject
                    var values = new Dictionary<string, long>();
                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        string key = reader.GetString() ?? "";
                        reader.Read();
                        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long v)) values[key] = v;
                        else reader.Skip();
                    }
                    result.Add(new Doodad(name, (uint)values.GetValueOrDefault("hash"), (int)values.GetValueOrDefault("x"),
                        (int)values.GetValueOrDefault("y"), (int)values.GetValueOrDefault("r"), (int)values.GetValueOrDefault("fv")));
                }
                break;
            }
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
