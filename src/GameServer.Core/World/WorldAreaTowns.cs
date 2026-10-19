using System.Text.Json;

namespace GameServer.Core.World;

/// <summary>
/// The towns of the game: the WorldAreas rows with <c>IsTown</c> set (data/tables/WorldAreas.json, from
/// Tools/PoEDataExtractor), as WorldAreas <c>Id</c> and <c>HASH16</c>. 3.29.3b has 12 act towns, Lioneye's Watch
/// (1_1_town) to Karui Shores (2_11_endgame_town). Without the table only Lioneye's Watch is known.
/// </summary>
public static class WorldAreaTowns
{
    public static readonly IReadOnlyList<(string Id, ushort Hash16)> Default = [("1_1_town", 0x235c)];

    // pvpTown is IsTown too, but PvP is not implemented: entering it would give a town nobody can use.
    static readonly HashSet<string> Excluded = ["pvpTown"];

    /// <summary>The town rows of a WorldAreas.json, in table order; <see cref="Default"/> when the file is missing.</summary>
    public static IReadOnlyList<(string Id, ushort Hash16)> Load(string worldAreasJson)
    {
        if (!File.Exists(worldAreasJson)) return Default;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(worldAreasJson));
        return [.. doc.RootElement.EnumerateArray()
            .Where(r => r.GetProperty("IsTown").GetBoolean() && !Excluded.Contains(r.GetProperty("Id").GetString() ?? ""))
            .Select(r => (r.GetProperty("Id").GetString()!, (ushort)r.GetProperty("HASH16").GetInt32()))];
    }
}
