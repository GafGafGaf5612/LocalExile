using System.Text.Json;

namespace GameServer.Data;

/// <summary>
/// Loads the tables produced by GameDataExtractor (<c>data/tables/&lt;Table&gt;.json</c>,
/// each a JSON array of row objects) and exposes them for simulation/protocol use.
/// </summary>
public sealed class GameData : IDisposable
{
    private readonly Dictionary<string, JsonDocument> _tables;

    private GameData(Dictionary<string, JsonDocument> tables) => _tables = tables;

    public int TableCount => _tables.Count;

    public IEnumerable<string> TableNames => _tables.Keys;

    public static GameData Load(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"game data directory not found: {directory}");

        var tables = new Dictionary<string, JsonDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            try
            {
                tables[name] = JsonDocument.Parse(File.ReadAllBytes(file));
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"table '{name}' is not valid JSON: {ex.Message}", ex);
            }
        }
        return new GameData(tables);
    }

    public bool TryGetTable(string table, out IReadOnlyList<JsonElement> rows)
    {
        if (_tables.TryGetValue(table, out var doc) && doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            rows = doc.RootElement.EnumerateArray().ToList();
            return true;
        }
        rows = Array.Empty<JsonElement>();
        return false;
    }

    public IReadOnlyList<JsonElement> Rows(string table)
    {
        if (!TryGetTable(table, out var rows))
            throw new KeyNotFoundException($"table not loaded: {table}");
        return rows;
    }

    public void Dispose()
    {
        foreach (var doc in _tables.Values)
            doc.Dispose();
        _tables.Clear();
    }
}
