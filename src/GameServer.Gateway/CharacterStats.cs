using System.Text.Json;
using GameServer.Core.World;

/// <summary>
/// A character's level-based pools and attribute bonuses, from its class, level and allocated passives: what the server
/// sends as final stats (the client derives none, 11-skills.md §4). Path of Exile's base rules, before items and buffs,
/// with the per-level values of Metadata/Characters/Character.ot (life_per_level 12, mana_per_level 6,
/// accuracy_rating_per_level 2, base_evasion_rating 15 and no evasion per level):
/// life = (Characters.dat BaseMaxLife (38) + 12 per level + 1 per 2 strength + flat life) × (1 + increased life);
/// mana likewise with BaseMaxMana (34), 6 per level and intelligence; accuracy = 2 per level + 2 per dexterity;
/// evasion = 15 raised by 1% per 5 dexterity; 1% increased maximum energy shield per 5 intelligence and 1% increased melee
/// physical damage per 5 strength. Without these stats the client falls back to the base for level 1 (the Witch's 50
/// life and 40 mana).
/// </summary>
static class CharacterStats
{
    public sealed record Pools(int Level, uint Life, uint Mana, int Accuracy, uint Experience,
        int Evasion = BaseEvasion, int EvasionPct = 0, int EnergyShieldPct = 0, int MeleePhysicalDamagePct = 0,
        int Strength = 0, int Dexterity = 0, int Intelligence = 0,
        int FireResistance = BaseResistance, int ColdResistance = BaseResistance,
        int LightningResistance = BaseResistance, int ChaosResistance = BaseResistance,
        int FireMaxResistance = DamageModel.DefaultMaxResistance, int ColdMaxResistance = DamageModel.DefaultMaxResistance,
        int LightningMaxResistance = DamageModel.DefaultMaxResistance, int ChaosMaxResistance = DamageModel.DefaultMaxResistance,
        int SpellSuppressionChance = 0, int SpellSuppressionEffect = DamageModel.SpellSuppressionEffect);

    /// <summary>What allocated passives add to the pools (PassiveBonus.From): attributes, flat and increased life and
    /// mana, increased evasion, energy shield and melee physical damage, and the base resistances.</summary>
    public sealed record Bonus(int Strength = 0, int Dexterity = 0, int Intelligence = 0, int Life = 0, int LifePct = 0,
        int Mana = 0, int ManaPct = 0, int EvasionPct = 0, int EnergyShieldPct = 0, int MeleePhysicalDamagePct = 0,
        int FireResistance = 0, int ColdResistance = 0, int LightningResistance = 0, int ChaosResistance = 0,
        int FireMaxResistance = 0, int ColdMaxResistance = 0, int LightningMaxResistance = 0, int ChaosMaxResistance = 0,
        int SpellSuppressionChance = 0, int SpellSuppressionEffect = 0)
    {
        public static readonly Bonus None = new();
    }

    /// <summary>Character.ot base_evasion_rating.</summary>
    public const int BaseEvasion = 15;

    /// <summary>Path of Exile's resistance before the quest penalties: 0% to all four types at character level 1.
    /// Act 5 and Act 10 Kitava each take another 30% away; see <see cref="ResistancePenalty"/> in the Gateway.</summary>
    public const int BaseResistance = 0;

    public static Pools For(SkillCatalog.ClassInfo c, int level, Bonus? bonus = null, string folder = "data")
    {
        var b = bonus ?? Bonus.None;
        level = Math.Clamp(level, 1, Progression.MaxLevel);
        int str = c.Strength + b.Strength, dex = c.Dexterity + b.Dexterity, intel = c.Intelligence + b.Intelligence;
        uint life = (uint)Math.Max(1, (c.BaseMaxLife + 12 * level + str / 2 + b.Life) * (100 + b.LifePct) / 100);
        uint mana = (uint)Math.Max(0, (c.BaseMaxMana + 6 * level + intel / 2 + b.Mana) * (100 + b.ManaPct) / 100);
        int evasionPct = dex / 5 + b.EvasionPct;
        int Max(int bonus) => Math.Min(DamageModel.MaxResistCap, DamageModel.DefaultMaxResistance + bonus);
        return new(level, life, mana, 2 * level + 2 * dex, ExperienceFor(level, folder),
            Evasion: BaseEvasion * (100 + evasionPct) / 100, EvasionPct: evasionPct,
            EnergyShieldPct: intel / 5 + b.EnergyShieldPct, MeleePhysicalDamagePct: str / 5 + b.MeleePhysicalDamagePct,
            Strength: str, Dexterity: dex, Intelligence: intel,
            FireResistance: BaseResistance + b.FireResistance, ColdResistance: BaseResistance + b.ColdResistance,
            LightningResistance: BaseResistance + b.LightningResistance, ChaosResistance: BaseResistance + b.ChaosResistance,
            FireMaxResistance: Max(b.FireMaxResistance), ColdMaxResistance: Max(b.ColdMaxResistance),
            LightningMaxResistance: Max(b.LightningMaxResistance), ChaosMaxResistance: Max(b.ChaosMaxResistance),
            SpellSuppressionChance: Math.Min(DamageModel.SpellSuppressionChanceCap, b.SpellSuppressionChance),
            SpellSuppressionEffect: Math.Max(0, DamageModel.SpellSuppressionEffect + b.SpellSuppressionEffect));
    }

    private static readonly Lazy<uint[]> Experience = new(() => LoadExperience("data"));

    /// <summary>Every level's starting experience (index = level − 1), ExperienceLevels.dat.</summary>
    public static IReadOnlyList<uint> LevelStarts => Experience.Value;

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
