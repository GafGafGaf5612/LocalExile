using System.Text.Json;

namespace GameServer.Storage;

/// <summary>Shared, uncached read of the Gateway's character home for Auth's next login assignment.</summary>
public static class CharacterHome
{
    public static string? SelectedHideout(SaveDatabase database, string character)
    {
        if (database.Get(SaveDatabase.Table.Characters, character) is not { } json) return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("SelectedHideout", out var area) && area.ValueKind == JsonValueKind.String
            ? area.GetString() : null;
    }
}
