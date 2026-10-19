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

    /// <summary>Whether a Spell Suppression roll succeeds; <paramref name="roll"/> is 0..99.</summary>
    public static bool Suppresses(int chance, int roll) => roll < Math.Clamp(chance, 0, SpellSuppressionChanceCap);

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
}
