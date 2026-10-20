using System.Text.Json;

/// <summary>
/// Stat names and the client's stat keys: key = Stats.dat row + 1 (Agents/docs/protocol/11-skills.md §5). Reads
/// data/tables/Stats.json (game data, loaded on first use); without it only numeric keys work.
/// </summary>
sealed class StatCatalog(string folder = "data")
{
    private readonly Lazy<List<string>> _names = new(() =>
    {
        string path = Path.Combine(folder, "tables", "Stats.json");
        if (!File.Exists(path)) return [];
        using var d = JsonDocument.Parse(File.ReadAllBytes(path));
        return [.. d.RootElement.EnumerateArray().Select(r => r.GetProperty("Id").GetString() ?? "")];
    });

    // Name → key; the stats code looks up hundreds of names per change, too many for a search of 23 000 names each.
    private readonly Lazy<Dictionary<string, uint>> _keys = new(() => new());

    /// <summary>A key from a name ("attack_speed_+%"), a decimal key or a 0x hex key.</summary>
    public uint? Key(string text)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out uint hex)) return hex;
        if (uint.TryParse(text, out uint key)) return key;
        var keys = _keys.Value;
        lock (keys)
            if (keys.Count == 0)
                for (int i = 0; i < _names.Value.Count; i++) keys.TryAdd(_names.Value[i], (uint)i + 1);
        return keys.TryGetValue(text, out uint found) ? found : null;
    }

    public string Name(uint key) => key >= 1 && key <= _names.Value.Count ? _names.Value[(int)key - 1] : $"0x{key:x}";
}
