namespace GameServer.Core.World;

/// <summary>The damage types the server can mitigate.</summary>
public enum DamageType { Physical, Fire, Cold, Lightning, Chaos }

/// <summary>
/// Path of Exile's resistance rule for the simple cases. The floor and absolute cap are taken from Path of Building
/// (Tools/PathOfBuilding, commit 16de4b8): <c>ResistFloor = -200</c> and <c>MaxResistCap = 90</c> in
/// <c>src/Modules/Data.lua</c>; the default per-type maximum is 75 (<c>src/Data/Misc.lua</c>, Character.ot
/// <c>base_maximum_all_resistances_%</c>), and the final value is
/// <c>clamp(base × increased, ResistFloor, max)</c> in <c>src/Modules/CalcDefence.lua</c> (§ resistances).
/// Spell Suppression's 100% chance cap and 40% effect are from the same PoB data (<c>SuppressionChanceCap</c>,
/// <c>SuppressionEffect</c>); a suppressed spell hit takes 40% less damage.
/// The per-type maximum-resistance stats, penetration and increased resistance are not modelled yet.
/// </summary>
public static class DamageModel
{
    /// <summary>Path of Building's <c>data.misc.ResistFloor</c>.</summary>
    public const int ResistFloor = -200;

    /// <summary>Path of Building's <c>data.misc.MaxResistCap</c>: no resistance can exceed this even with max-res mods.</summary>
    public const int MaxResistCap = 90;

    /// <summary>Character.ot's <c>base_maximum_all_resistances_%</c>: the default maximum resistance.</summary>
    public const int DefaultMaxResistance = 75;

    /// <summary>Path of Building's <c>SuppressionChanceCap</c>: Spell Suppression chance cannot exceed 100%.</summary>
    public const int SpellSuppressionChanceCap = 100;

    /// <summary>Path of Building's <c>SuppressionEffect</c>: a suppressed spell hit takes 40% less damage.</summary>
    public const int SpellSuppressionEffect = 40;

    /// <summary>PoE's base critical strike multiplier: 150%, before <c>base_critical_strike_multiplier_+</c>.</summary>
    public const int BaseCriticalStrikeMultiplier = 150;

    /// <summary>Fireball's GrantEffectStatSetsPerLevel SpellCritChance (500 = 5.00%).</summary>
    public const int BaseSpellCriticalStrikeChancePerMyriad = 500;

    /// <summary>A hit's critical strike chance: the base chance (permyriad: 500 = 5%) raised by the increased
    /// critical strike chance stat, capped at 100%.</summary>
    public static double CriticalStrikeChance(int basePerMyriad, int increasedPct) =>
        Math.Clamp(basePerMyriad / 100.0 * (100 + increasedPct) / 100.0, 0, 100);

    /// <summary>A critical hit's damage, rounded to the nearest point; <paramref name="multiplier"/> is the final
    /// percentage (150 by default).</summary>
    public static int CriticalDamage(int damage, int multiplier)
    {
        if (damage <= 0) return 0;
        return (int)Math.Max(0, Math.Round(damage * Math.Max(0, multiplier) / 100.0));
    }

    /// <summary>Whether a Spell Suppression roll succeeds; <paramref name="roll"/> is 0..99. Lucky and unlucky roll
    /// twice: lucky keeps the better roll (1 − (1 − p)²), unlucky the worse (p²), as PoB's suppressRolls.</summary>
    public static bool Suppresses(int chance, int roll, bool lucky = false, bool unlucky = false) =>
        roll < EffectiveSuppressionChance(chance, lucky, unlucky);

    /// <summary>The chance after a lucky or unlucky suppression roll, capped by <see cref="SpellSuppressionChanceCap"/>.</summary>
    public static double EffectiveSuppressionChance(int chance, bool lucky = false, bool unlucky = false)
    {
        double p = Math.Clamp(chance, 0, SpellSuppressionChanceCap) / 100.0;
        if (lucky) p = 1 - (1 - p) * (1 - p);
        else if (unlucky) p *= p;
        return p * 100;
    }

    /// <summary>A successful suppression's damage after the suppression effect, rounded to the nearest point. PoB does
    /// not cap the effect itself (passives can raise it above 40); damage cannot go below zero.</summary>
    public static int Suppress(int damage, int effect = SpellSuppressionEffect)
    {
        if (damage <= 0) return 0;
        return (int)Math.Max(0, Math.Round(damage * (100.0 - Math.Max(0, effect)) / 100.0));
    }

    /// <summary>The resistance used for a hit: capped at the per-type maximum (75 by default, 90 absolute) and floored
    /// at Path of Building's -200.</summary>
    public static int Capped(int resistance, int maxResistance = DefaultMaxResistance) =>
        Math.Clamp(resistance, ResistFloor, Math.Min(maxResistance, MaxResistCap));

    /// <summary>The damage left after a resistance, rounded to the nearest point.</summary>
    public static int Mitigate(int damage, int resistance, int maxResistance = DefaultMaxResistance)
    {
        if (damage <= 0) return 0;
        return (int)Math.Max(0, Math.Round(damage * (100.0 - Capped(resistance, maxResistance)) / 100.0));
    }

    /// <summary>The damage left after a resistance, for damage over time where fractions are carried between ticks.</summary>
    public static double Mitigate(double damage, int resistance, int maxResistance = DefaultMaxResistance)
    {
        if (damage <= 0) return 0;
        return damage * (100.0 - Capped(resistance, maxResistance)) / 100.0;
    }

    /// <summary>Path of Building's <c>BlockChanceCap</c>: block chance cannot exceed 90%.</summary>
    public const int BlockChanceCap = 90;

    /// <summary>Character.ot's <c>maximum_block_%</c> and <c>base_maximum_spell_block_%</c>.</summary>
    public const int DefaultMaxBlockChance = 75, DefaultMaxSpellBlockChance = 75;

    /// <summary>Whether a block roll succeeds; <paramref name="roll"/> is 0..100. Lucky block rolls twice and keeps the
    /// better roll, so its effective chance is <c>1 - (1 - p)^2</c> (PoB CalcDefence's lucky block branch).</summary>
    public static bool Blocks(int chance, double roll, bool lucky = false) =>
        roll < EffectiveBlockChance(chance, lucky);

    /// <summary>The chance after lucky/unlucky block (one extra roll each); capped by <see cref="BlockChanceCap"/>.</summary>
    public static double EffectiveBlockChance(int chance, bool lucky = false)
    {
        double p = Math.Clamp(chance, 0, BlockChanceCap) / 100.0;
        return lucky ? (1 - (1 - p) * (1 - p)) * 100 : p * 100;
    }
}
