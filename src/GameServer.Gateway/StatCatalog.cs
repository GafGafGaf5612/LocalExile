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

    /// <summary>A key from a name ("attack_speed_+%"), a decimal key or a 0x hex key.</summary>
    public uint? Key(string text)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out uint hex)) return hex;
        if (uint.TryParse(text, out uint key)) return key;
        int row = _names.Value.IndexOf(text);
        return row < 0 ? null : (uint)row + 1;
    }

    public string Name(uint key) => key >= 1 && key <= _names.Value.Count ? _names.Value[(int)key - 1] : $"0x{key:x}";
}
