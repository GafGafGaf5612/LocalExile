/// <summary>
/// The local defences of one equipped armour piece or shield (13-items.md §2c): its base ArmourTypes roll plus the item's
/// own flat and increased armour/evasion/energy shield mods, and item quality (+1% per quality, a separate multiplier as
/// since 3.25). Applied by Program.Equipment's equipped-defence sum; the character panel and the pools see the result.
/// </summary>
static class ItemDefences
{
    public static (int Armour, int Evasion, int EnergyShield) Of(
        ItemCatalog.BaseDefences baseDefences, IReadOnlyDictionary<string, int> local, int quality)
    {
        int armourPct = local.GetValueOrDefault("local_physical_damage_reduction_rating_+%")
            + local.GetValueOrDefault("local_armour_and_evasion_+%")
            + local.GetValueOrDefault("local_armour_and_energy_shield_+%")
            + local.GetValueOrDefault("local_armour_and_evasion_and_energy_shield_+%");
        int evasionPct = local.GetValueOrDefault("local_evasion_rating_+%")
            + local.GetValueOrDefault("local_armour_and_evasion_+%")
            + local.GetValueOrDefault("local_evasion_and_energy_shield_+%")
            + local.GetValueOrDefault("local_armour_and_evasion_and_energy_shield_+%");
        int energyShieldPct = local.GetValueOrDefault("local_energy_shield_+%")
            + local.GetValueOrDefault("local_armour_and_energy_shield_+%")
            + local.GetValueOrDefault("local_evasion_and_energy_shield_+%")
            + local.GetValueOrDefault("local_armour_and_evasion_and_energy_shield_+%");
        int flatArmour = baseDefences.ArmourMin + local.GetValueOrDefault("local_base_physical_damage_reduction_rating");
        // The combined evasion-and-energy-shield mod adds its flat value to both (e.g. "adds X Evasion and Y Energy Shield").
        int combined = local.GetValueOrDefault("local_evasion_rating_and_energy_shield");
        int flatEvasion = baseDefences.EvasionMin + local.GetValueOrDefault("local_base_evasion_rating") + combined;
        int flatEnergyShield = baseDefences.EnergyShieldMin + local.GetValueOrDefault("local_energy_shield") + combined;
        int qualityPct = Math.Clamp(quality, 0, 100);

        int Scaled(int flat, int increased) =>
            (int)((long)EnergyShield.Max(Math.Max(0, flat), increased) * (100L + qualityPct) / 100);

        return (Scaled(flatArmour, armourPct), Scaled(flatEvasion, evasionPct),
            local.GetValueOrDefault("local_no_energy_shield") != 0 ? 0 : Scaled(flatEnergyShield, energyShieldPct));
    }
}
