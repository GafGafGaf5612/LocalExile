using System.Text.Json;

/// <summary>
/// The quest engine (Agents/docs/protocol/12-quests.md §5). A character's quest progress is the set of its quest flags
/// (QuestFlags.dat Ids, saved by name in data/saves/). Each quest is a script: it turns game events into flags and says
/// what its flags gate (an exit). The Gateway asks every script.
/// </summary>
static class Quests
{
    public abstract class Script
    {
        /// <summary>Flags set when a monster of this MonsterVarieties Id dies in this area.</summary>
        public virtual IEnumerable<string> OnMonsterKilled(string areaId, string varietyId) => [];

        /// <summary>The flag a character needs to take the exit from one area to the other, or null.</summary>
        public virtual string? FlagToEnter(string fromArea, string toArea) => null;
    }

    /// <summary>a1q1 "Enemy at the Gate": Hillock stands before Lioneye's Watch at the end of The Twilight Strand; the way
    /// into the town opens once he is dead (12-quests.md §6).</summary>
    public sealed class EnemyAtTheGate : Script
    {
        public const string KilledHillock = "a1q1-KilledHillock";
        const string Strand = "1_1_1", Town = "1_1_town";
        const string Hillock = "Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal";

        public override IEnumerable<string> OnMonsterKilled(string areaId, string varietyId) =>
            areaId == Strand && varietyId == Hillock ? [KilledHillock] : [];

        public override string? FlagToEnter(string fromArea, string toArea) => fromArea == Strand && toArea == Town ? KilledHillock : null;
    }

    static readonly Script[] All = [new EnemyAtTheGate()];

    /// <summary>Flags every character has from the start (added on login, not a quest's), all for the hideout
    /// (21-hideouts.md §1): HideoutUnlocked; HASH32 0x2f1b9c25 (row 718, the schema's "HideoutJukeboxEnabled"), which the
    /// HUD's hideout panel checks (0x14216c840); CanEditHideout, tested before the Edit button shows (live trace
    /// 2026-10-20); MapDeviceActive, the map device's per-player "active" state (its .ot:
    /// per_player_quest_flag_state_active_1).</summary>
    public static readonly string[] EveryCharacter = ["HideoutUnlocked", "HideoutJukeboxEnabled", "CanEditHideout", "MapDeviceActive"];

    /// <summary>
    /// League quest flags: a set of their own, kept in the Player component's second flag set (+0x1d0). They are not a
    /// table: the client has the names built in, sorted, with their ids (0x142fca900, 0x776 entries; lookup
    /// 0x1423f1fb0). Every character has the hideout ones (21-hideouts.md §1).
    /// </summary>
    public static class League
    {
        public const int UnlockedHideouts = 3, HaveHideout = 200, UnlockedMapDevice = 350;

        public static readonly int[] EveryCharacter = [UnlockedHideouts, HaveHideout, UnlockedMapDevice];
    }

    public static IReadOnlyList<string> OnMonsterKilled(string areaId, string varietyId) =>
        [.. All.SelectMany(s => s.OnMonsterKilled(areaId, varietyId))];

    public static string? FlagToEnter(string fromArea, string toArea) =>
        All.Select(s => s.FlagToEnter(fromArea, toArea)).FirstOrDefault(f => f is not null);

    /// <summary>QuestFlags.dat rows by Id, from an extracted QuestFlags.json (the row is what the client keeps).</summary>
    public static Dictionary<string, int> ParseRows(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var rows = new Dictionary<string, int>();
        int i = 0;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("Id", out var id) && id.GetString() is { } name) rows.TryAdd(name, i);
            i++;
        }
        return rows;
    }

    static readonly Lazy<Dictionary<string, int>> TableRows = new(() =>
    {
        const string file = "data/tables/QuestFlags.json";
        try { return File.Exists(file) ? ParseRows(File.ReadAllText(file)) : []; }
        catch (Exception ex) when (ex is IOException or JsonException) { Console.WriteLine($"[gateway] {file}: {ex.Message}"); return []; }
    });

    /// <summary>The QuestFlags rows of these flag Ids, ascending; unknown Ids are skipped.</summary>
    public static IReadOnlyList<int> Rows(IEnumerable<string> ids, IReadOnlyDictionary<string, int>? rows = null) =>
        [.. ids.Select(id => (rows ?? TableRows.Value).TryGetValue(id, out int r) ? r : -1).Where(r => r >= 0).Order()];
}
