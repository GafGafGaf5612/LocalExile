/// <summary>
/// A spell's hit before crits and the target's mitigation (Agents/docs/protocol/11-skills.md §9): per damage type, the
/// skill's spell_minimum/maximum_base_{type}_damage scaled by 1 + the summed increases that apply: damage_+%,
/// spell_damage_+%, elemental_damage_+% (fire, cold, lightning), the type's own {type}_damage_+%, and
/// projectile_damage_+% for projectile skills (base_is_projectile). The skill's stats include the player's (passives,
/// items, auras), so item mods count. More multipliers, conversion and added damage are not modelled.
/// </summary>
static class SpellDamage
{
    static readonly string[] Types = ["physical", "fire", "cold", "lightning", "chaos"];

    /// <summary>The summed minimum and maximum hit, unrounded (the tooltip averages these; a real hit rolls between them
    /// and drops the fraction, <see cref="Roll"/>); (0, 0) for a skill with no base spell damage.</summary>
    public static (double Min, double Max) Hit(Func<string, int> get)
    {
        int common = get("damage_+%") + get("spell_damage_+%") + (get("base_is_projectile") != 0 ? get("projectile_damage_+%") : 0);
        double min = 0, max = 0;
        foreach (string type in Types)
        {
            int baseMin = get($"spell_minimum_base_{type}_damage"), baseMax = get($"spell_maximum_base_{type}_damage");
            if (baseMin == 0 && baseMax == 0) continue;
            bool elemental = type is "fire" or "cold" or "lightning";
            double factor = Math.Max(0, 1 + (common + (elemental ? get("elemental_damage_+%") : 0) + get($"{type}_damage_+%")) / 100.0);
            min += baseMin * factor;
            max += baseMax * factor;
        }
        return (min, Math.Max(min, max));
    }

    /// <summary>One hit: uniform between the minimum and the maximum, fraction dropped.</summary>
    public static int Roll((double Min, double Max) hit, Random rng) => (int)Math.Floor(hit.Min + rng.NextDouble() * (hit.Max - hit.Min));

    /// <summary>The panel's Damage per Use and Damage per Second (both × 100) for a spell cast this often.</summary>
    public static (int PerUse, int PerSecond) Display((double Min, double Max) hit, double castsPerSecond)
    {
        double average = (hit.Min + hit.Max) / 2.0;
        return ((int)Math.Round(100 * average), (int)Math.Round(100 * average * castsPerSecond));
    }
}
