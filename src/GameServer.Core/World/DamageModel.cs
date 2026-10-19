namespace GameServer.Core.World;

/// <summary>The damage types the server can mitigate.</summary>
public enum DamageType { Physical, Fire, Cold, Lightning, Chaos }

/// <summary>
/// Path of Exile's resistance rule for the simple cases. The floor and absolute cap are taken from Path of Building
/// (Tools/PathOfBuilding, commit 16de4b8): <c>ResistFloor = -200</c> and <c>MaxResistCap = 90</c> in
/// <c>src/Modules/Data.lua</c>; the default per-type maximum is 75 (<c>src/Data/Misc.lua</c>, Character.ot
/// <c>base_maximum_all_resistances_%</c>), and the final value is
/// <c>clamp(base × increased, ResistFloor, max)</c> in <c>src/Modules/CalcDefence.lua</c> (§ resistances).
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
