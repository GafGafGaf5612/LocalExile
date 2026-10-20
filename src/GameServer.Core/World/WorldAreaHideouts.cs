using System.Text.Json;

namespace GameServer.Core.World;

/// <summary>
/// The hideout areas of the game: the WorldAreas rows with <c>IsHideout</c> set (data/tables/WorldAreas.json), by
/// <c>HASH16</c>. Their layout is fixed, so their instances always use seed 1 (Agents/docs/protocol/21-hideouts.md).
/// </summary>
public static class WorldAreaHideouts
{
    /// <summary>The hideout rows' HASH16s; empty when the file is missing.</summary>
    public static IReadOnlySet<ushort> Load(string worldAreasJson)
    {
        if (!File.Exists(worldAreasJson)) return new HashSet<ushort>();
        using var doc = JsonDocument.Parse(File.ReadAllBytes(worldAreasJson));
        return doc.RootElement.EnumerateArray()
            .Where(r => r.TryGetProperty("IsHideout", out var h) && h.ValueKind == JsonValueKind.True)
            .Select(r => (ushort)r.GetProperty("HASH16").GetInt32()).ToHashSet();
    }
}
