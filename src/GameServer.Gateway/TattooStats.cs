/// <summary>
/// The replacement stats of the tattoo pilot. A tattoo replaces the passive's stats; these are the first simple,
/// unconditional stats (resistances, defences, movement and a few global damage values). The remaining catalogue
/// entries need their condition/special consumers before they can be listed here.
/// </summary>
static class TattooStats
{
    static readonly Dictionary<string, (string Stat, int Value)[]> Entries = new(StringComparer.Ordinal)
    {
        ["Tattoo of the Ngamahu Firewalker"] = [("base_fire_damage_resistance_%", 6)],
        ["Tattoo of the Arohongui Moonwarden"] = [("base_cold_damage_resistance_%", 6)],
        ["Tattoo of the Valako Stormrider"] = [("base_lightning_damage_resistance_%", 6)],
        ["Tattoo of the Hinekora Storyteller"] = [("base_chaos_damage_resistance_%", 3)],
        ["Tattoo of the Ramako Fleetfoot"] = [("base_movement_velocity_+%", 2)],
        ["Tattoo of the Rongokurai Warrior"] = [("physical_damage_reduction_rating_+%", 6)],
        ["Tattoo of the Valako Shieldbearer"] = [("additional_block_%", 1)],
        ["Tattoo of the Ramako Shaman"] = [("base_spell_suppression_chance_%", 2)],
        ["Tattoo of the Hinekora Warrior"] = [("maximum_energy_shield_+%", 3)],
        ["Honoured Tattoo of the Oak"] = [("maximum_life_+%", 2)],
    };

    public static IReadOnlyCollection<string> Names => Entries.Keys;

    /// <summary>The tattoo's replacement stats as client stat keys, or null when the tattoo is not in the pilot (or
    /// its stat names are missing from the client's Stats table).</summary>
    public static IReadOnlyList<(uint Key, int Value)>? For(string name)
    {
        if (!Entries.TryGetValue(name, out var definitions)) return null;
        var stats = new List<(uint Key, int Value)>(definitions.Length);
        foreach (var (stat, value) in definitions)
            if (Program.Stats.Key(stat) is uint key) stats.Add((key, value));
        return stats.Count == 0 ? null : stats;
    }
}
