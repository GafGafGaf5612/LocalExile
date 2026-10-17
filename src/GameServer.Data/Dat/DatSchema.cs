using System.Text.Json;

namespace GameServer.Data.Dat;

/// <summary>Column logical type as named in the community dat-schema.</summary>
public enum DatColumnType
{
    Bool,
    String,
    I16,
    U16,
    I32,
    U32,
    F32,
    Array,
    Row,
    ForeignRow,
    EnumRow,
}

public sealed class SchemaColumn
{
    public required string Name { get; init; }
    public required DatColumnType Type { get; init; }
    public bool IsArray { get; init; }
    public bool IsInterval { get; init; }
    public bool Localized { get; init; }
    public string? ReferenceTable { get; init; }
    public string? ReferenceColumn { get; init; }
}

public sealed class SchemaTable
{
    public required string Name { get; init; }
    public required IReadOnlyList<SchemaColumn> Columns { get; init; }
    /// <summary>1 = Game1, 2 = Game2, 3 = common.</summary>
    public int ValidFor { get; init; }
}

/// <summary>Loads the community dat-schema (poe-tool-dev/dat-schema, schema.min.json).</summary>
public static class DatSchema
{
    public static IReadOnlyDictionary<string, SchemaTable> LoadFile(string path)
        => Load(File.ReadAllText(path));

    public static IReadOnlyDictionary<string, SchemaTable> Load(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var result = new Dictionary<string, SchemaTable>(StringComparer.OrdinalIgnoreCase);

        if (!root.TryGetProperty("tables", out var tables) || tables.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var t in tables.EnumerateArray())
        {
            if (!t.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                continue;

            var name = nameEl.GetString()!;
            var columns = new List<SchemaColumn>();

            int validFor = 3;
            if (t.TryGetProperty("validFor", out var vf) && vf.ValueKind == JsonValueKind.Number)
                validFor = vf.GetInt32();

            if (t.TryGetProperty("columns", out var colsEl) && colsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in colsEl.EnumerateArray())
                {
                    columns.Add(ParseColumn(c));
                }
            }

            var table = new SchemaTable { Name = name, Columns = columns, ValidFor = validFor };

            // Names may repeat across Game1/Game2; prefer the Game1 variant.
            if (result.TryGetValue(name, out var existing))
            {
                bool newIsGame1 = (validFor & 1) != 0;
                bool existingIsGame1 = (existing.ValidFor & 1) != 0;
                if (newIsGame1 && !existingIsGame1)
                    result[name] = table;
            }
            else
            {
                result[name] = table;
            }
        }

        return result;
    }

    private static SchemaColumn ParseColumn(JsonElement c)
    {
        string colName = "";
        if (c.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
            colName = n.GetString()!;

        string typeStr = "array";
        if (c.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String)
            typeStr = ty.GetString()!;

        bool GetBool(string prop) => c.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.True;

        string? refTable = null, refColumn = null;
        if (c.TryGetProperty("references", out var rf) && rf.ValueKind == JsonValueKind.Object)
        {
            if (rf.TryGetProperty("table", out var rt) && rt.ValueKind == JsonValueKind.String)
                refTable = rt.GetString();
            if (rf.TryGetProperty("column", out var rc) && rc.ValueKind == JsonValueKind.String)
                refColumn = rc.GetString();
        }

        return new SchemaColumn
        {
            Name = colName,
            Type = MapType(typeStr),
            IsArray = GetBool("array"),
            IsInterval = GetBool("interval"),
            Localized = GetBool("localized"),
            ReferenceTable = refTable,
            ReferenceColumn = refColumn,
        };
    }

    private static DatColumnType MapType(string s) => s switch
    {
        "bool" => DatColumnType.Bool,
        "string" => DatColumnType.String,
        "i16" => DatColumnType.I16,
        "u16" => DatColumnType.U16,
        "i32" => DatColumnType.I32,
        "u32" => DatColumnType.U32,
        "f32" => DatColumnType.F32,
        "row" => DatColumnType.Row,
        "foreignrow" => DatColumnType.ForeignRow,
        "enumrow" => DatColumnType.EnumRow,
        "array" => DatColumnType.Array,
        _ => DatColumnType.Array,
    };
}
