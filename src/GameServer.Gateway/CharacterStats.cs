using System.Text.Json;

/// <summary>
/// A character's level-based pools and attribute bonuses, from its class and level: what the server sends as final stats
/// (the client derives none, 11-skills.md §4). Path of Exile's base rules, before passives, items and buffs, with the
/// per-level values of Metadata/Characters/Character.ot (life_per_level 12, mana_per_level 6, accuracy_rating_per_level 2,
/// base_evasion_rating 15 and no evasion per level):
/// life = Characters.dat BaseMaxLife (38) + 12 per level + 1 per 2 strength; mana = BaseMaxMana (34) + 6 per level + 1
/// per 2 intelligence; accuracy = 2 per level + 2 per dexterity; evasion = 15 raised by 1% per 5 dexterity; 1% increased
/// maximum energy shield per 5 intelligence and 1% increased melee physical damage per 5 strength. Without these stats
/// the client falls back to the base for level 1 (the Witch's 50 life and 40 mana).
/// </summary>
static class CharacterStats
{
    public sealed record Pools(int Level, uint Life, uint Mana, int Accuracy, uint Experience,
        int Evasion = BaseEvasion, int EvasionPct = 0, int EnergyShieldPct = 0, int MeleePhysicalDamagePct = 0);

    /// <summary>Character.ot base_evasion_rating.</summary>
    public const int BaseEvasion = 15;

    public static Pools For(SkillCatalog.ClassInfo c, int level, string folder = "data")
    {
        level = Math.Clamp(level, 1, 100);
        uint life = (uint)(c.BaseMaxLife + 12 * level + c.Strength / 2);
        uint mana = (uint)(c.BaseMaxMana + 6 * level + c.Intelligence / 2);
        int evasionPct = c.Dexterity / 5;
        return new(level, life, mana, 2 * level + 2 * c.Dexterity, ExperienceFor(level, folder),
            Evasion: BaseEvasion * (100 + evasionPct) / 100, EvasionPct: evasionPct,
            EnergyShieldPct: c.Intelligence / 5, MeleePhysicalDamagePct: c.Strength / 5);
    }

    private static readonly Lazy<uint[]> Experience = new(() => LoadExperience("data"));

    /// <summary>The experience at the start of a level (ExperienceLevels.dat; level 37: 12 361 842). The client derives
    /// the level from the Player component's experience (04 §4).</summary>
    public static uint ExperienceFor(int level, string folder = "data")
    {
        var table = folder == "data" ? Experience.Value : LoadExperience(folder);
        return level >= 1 && level <= table.Length ? table[level - 1] : 0;
    }

    private static uint[] LoadExperience(string folder)
    {
        string path = Path.Combine(folder, "tables", "ExperienceLevels.json");
        if (!File.Exists(path)) return [0];
        using var d = JsonDocument.Parse(File.ReadAllBytes(path));
        return [.. d.RootElement.EnumerateArray().OrderBy(r => r.GetProperty("Level").GetInt32()).Select(r => (uint)r.GetProperty("Experience").GetInt64())];
    }
}
