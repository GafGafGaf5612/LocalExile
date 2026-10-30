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
        // The next plain-stat batch (stats from PassiveSkillOverrides; no conditions).
        ["Tattoo of the Arohongui Warrior"] = [("cold_damage_+%", 5)],
        ["Tattoo of the Hinekora Deathwarden"] = [("curse_effect_on_self_+%", -6)],
        ["Tattoo of the Hinekora Shaman"] = [("mana_regeneration_rate_+%", 8)],
        ["Tattoo of the Kitava Warrior"] = [("physical_damage_+%", 5)],
        ["Tattoo of the Ngamahu Shaman"] = [("base_self_ignite_duration_-%", 10)],
        ["Tattoo of the Ngamahu Warrior"] = [("fire_damage_+%", 5)],
        ["Tattoo of the Ramako Archer"] = [("accuracy_rating_+%", 5)],
        ["Tattoo of the Ramako Scout"] = [("evasion_rating_+%", 6)],
        ["Tattoo of the Ramako Sniper"] = [("base_projectile_speed_+%", 5)],
        ["Tattoo of the Rongokurai Brute"] = [("stun_threshold_+%", 10)],
        ["Tattoo of the Rongokurai Goliath"] = [("base_stun_duration_+%", 10)],
        ["Tattoo of the Rongokurai Turtle"] = [("base_self_critical_strike_multiplier_-%", 5)],
        ["Tattoo of the Tawhoa Warrior"] = [("chaos_damage_+%", 5)],
        ["Tattoo of the Tukohama Shaman"] = [("life_regeneration_rate_per_minute_%", 18)],
        ["Tattoo of the Tukohama Warmonger"] = [("melee_damage_+%", 5)],
        ["Tattoo of the Valako Scout"] = [("shocked_effect_on_self_+%", -10)],
        ["Tattoo of the Valako Warrior"] = [("lightning_damage_+%", 5)],
        ["Honoured Tattoo of the Dove"] = [("base_chaos_damage_resistance_%", 4)],
        ["Honoured Tattoo of the Makanga"] = [("additional_maximum_all_resistances_%", 2)],
        ["Honoured Tattoo of the Pillager"] = [("base_item_found_rarity_+%", 3)],
        ["Honoured Tattoo of the Sky"] = [("base_resist_all_elements_%", 3)],
        ["Honoured Tattoo of the Tuatara"] = [("additional_all_attributes", 4)],
        ["Honoured Tattoo of the Turtle"] = [("global_defences_+%", 3)],
        // The four plain Makanga: their effect is gated by RequiresAdjacent/MaxAdjacent.
        ["Tattoo of the Arohongui Makanga"] = [("base_maximum_cold_damage_resistance_%", 1)],
        ["Tattoo of the Ngamahu Makanga"] = [("base_maximum_fire_damage_resistance_%", 1)],
        ["Tattoo of the Tukohama Makanga"] = [("life_regeneration_rate_+%", 16)],
        ["Tattoo of the Valako Makanga"] = [("base_maximum_lightning_damage_resistance_%", 1)],
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
