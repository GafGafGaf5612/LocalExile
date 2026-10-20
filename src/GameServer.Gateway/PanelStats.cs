/// <summary>
/// The character panel's "sum" stats (issue #75): virtual stats that the server works out from the base forms that
/// passives, items and auras give, because the client derives none of them (11-skills.md §4). A passive grants
/// <c>base_stun_recovery_+%</c>, the panel's Stun Recovery row reads <c>stun_recovery_+%</c>. Each display stat is its
/// own value plus every unconditional source below: the base form, and the combined stats that apply to it too (Avoid
/// Elemental Ailments counts for each avoid row, "increased Duration of Elemental Ailments" for each duration).
/// Conditional stats (while, if, per charge, against …) are not counted. Flags (cannot be, immune to) are 1 when any
/// source is set. The pools' own stats (life, mana, armour, evasion, attributes, resistances, suppression, block,
/// movement and attack speed, crit) are not here: <see cref="CharacterStats"/> computes those.
/// </summary>
static class PanelStats
{
    /// <summary>A display stat and what it sums. <paramref name="PerHundredThousand"/> sources are in 1/100 000 and are
    /// added as permyriad (÷ 10).</summary>
    public sealed record Sum(string Display, string[] Sources, bool Flag = false, string[]? PerHundredThousand = null);

    const string ElementalAilmentDuration = "base_elemental_status_ailment_duration_+%";
    const string AilmentDuration = "base_all_ailment_duration_+%";
    const string NonDamagingAilmentEffect = "non_damaging_ailment_effect_+%";
    const string AvoidElementalAilments = "avoid_all_elemental_status_%";
    const string SelfElementalAilmentDuration = "self_elemental_status_duration_-%";
    const string ChargeDuration = "charge_duration_+%", BaseChargeDuration = "base_charge_duration_+%";
    const string LeechSpeed = "life_mana_es_leech_speed_+%";

    public static readonly Sum[] Sums =
    [
        // Offence
        new("cast_speed_+%", ["base_cast_speed_+%", "attack_and_cast_speed_+%"]),
        new("global_chance_to_knockback_%", ["base_global_chance_to_knockback_%"]),
        new("virtual_knockback_distance_+%", ["knockback_distance_+%"]),
        new("stun_duration_+%", ["base_stun_duration_+%"]),
        new("virtual_ignite_duration_+%", ["ignite_duration_+%", ElementalAilmentDuration, AilmentDuration,
            "fire_ailment_duration_+%", "skill_effect_and_damaging_ailment_duration_+%"]),
        new("virtual_scorch_effect_+%", ["scorch_effect_+%", NonDamagingAilmentEffect]),
        new("virtual_chill_duration_+%", ["chill_duration_+%", "chill_and_freeze_duration_+%", ElementalAilmentDuration,
            AilmentDuration, "cold_ailment_duration_+%"]),
        new("virtual_chill_effect_+%", ["chill_effect_+%", "cold_ailment_effect_+%", NonDamagingAilmentEffect]),
        new("chance_to_freeze_%", ["base_chance_to_freeze_%", "chance_to_freeze_shock_ignite_%"]),
        new("virtual_freeze_duration_+%", ["freeze_duration_+%", "chill_and_freeze_duration_+%", ElementalAilmentDuration,
            AilmentDuration, "cold_ailment_duration_+%"]),
        new("chance_to_shock_%", ["base_chance_to_shock_%", "chance_to_freeze_shock_ignite_%"]),
        new("virtual_shock_duration_+%", ["shock_duration_+%", ElementalAilmentDuration, AilmentDuration,
            "lightning_ailment_duration_+%"]),
        new("virtual_shock_effect_+%", ["shock_effect_+%", "lightning_ailment_effect_+%", NonDamagingAilmentEffect]),
        new("reduce_enemy_fire_resistance_%", ["base_reduce_enemy_fire_resistance_%", "reduce_enemy_elemental_resistance_%"]),
        new("reduce_enemy_cold_resistance_%", ["base_reduce_enemy_cold_resistance_%", "reduce_enemy_elemental_resistance_%"]),
        new("reduce_enemy_lightning_resistance_%", ["base_reduce_enemy_lightning_resistance_%", "reduce_enemy_elemental_resistance_%"]),
        // Defence
        new("virtual_energy_shield_delay_-%", ["energy_shield_delay_-%"]),
        new("virtual_block_recovery_+%", ["block_recovery_+%"]),
        new("stun_recovery_+%", ["base_stun_recovery_+%"]),
        new("avoid_stun_%", ["base_avoid_stun_%"]),
        new("cannot_be_stunned", ["base_cannot_be_stunned"], Flag: true),
        new("avoid_bleed_%", ["base_avoid_bleed_%"]),
        new("cannot_gain_bleeding", ["base_cannot_gain_bleeding"], Flag: true),
        new("virtual_immune_to_bleeding", ["immune_to_bleeding"], Flag: true),
        .. Ailment("ignite", "ignited"), .. Ailment("chill", "chilled"), .. Ailment("freeze", "frozen"), .. Ailment("shock", "shocked"),
        new("avoid_poison_%", ["base_avoid_poison_%"]),
        new("virtual_cannot_be_poisoned", ["cannot_be_poisoned"], Flag: true),
        new("virtual_immune_to_poison", ["immune_to_poison"], Flag: true),
        // Misc
        new("ascendance", ["base_ascendance"]),
        new("skill_area_of_effect_+%", ["base_skill_area_of_effect_+%"]),
        new("virtual_curse_effect_+%", ["curse_effect_+%"]),
        new("virtual_flask_duration_+%", ["flask_duration_+%"]),
        .. Leech("life"), .. Leech("mana"),
        new("virtual_energy_shield_leech_speed_+%", ["energy_shield_leech_speed_+%", LeechSpeed]),
        new("life_gained_on_spell_hit", ["base_life_gained_on_spell_hit"]),
        new("virtual_energy_shield_gain_per_target", ["energy_shield_gain_per_target"]),
        new("life_gained_on_enemy_death", ["base_life_gained_on_enemy_death"]),
        new("mana_gained_on_enemy_death", ["base_mana_gained_on_enemy_death"]),
        new("energy_shield_gained_on_enemy_death", ["base_energy_shield_gained_on_enemy_death"]),
        new("virtual_minion_damage_+%", ["minion_damage_+%"]),
        new("projectile_speed_+%", ["base_projectile_speed_+%"]),
        new("virtual_trap_throwing_speed_+%", ["trap_throwing_speed_+%", "trap_and_mine_throwing_speed_+%"]),
        // Charges
        new("virtual_endurance_charge_duration_+%", ["endurance_charge_duration_+%", ChargeDuration, BaseChargeDuration]),
        new("virtual_power_charge_duration_+%", ["power_charge_duration_+%", ChargeDuration, BaseChargeDuration]),
        new("attack_speed_+%_per_frenzy_charge", ["base_attack_speed_+%_per_frenzy_charge"]),
        new("cast_speed_+%_per_frenzy_charge", ["base_cast_speed_+%_per_frenzy_charge"]),
    ];

    // An elemental ailment's defence rows: avoid (with Avoid Elemental Ailments), the duration on self (with the elemental
    // ailment duration on self), cannot be (flag) and immune (flag; "Immune to Ignite and Shock" counts for both).
    static Sum[] Ailment(string ailment, string past) =>
    [
        new($"avoid_{ailment}_%", [$"base_avoid_{ailment}_%", AvoidElementalAilments]),
        new($"self_{ailment}_duration_-%", [$"base_self_{ailment}_duration_-%", SelfElementalAilmentDuration]),
        new($"cannot_be_{past}", [$"base_cannot_be_{past}"], Flag: true),
        new($"immune_to_{ailment}", ailment is "ignite" or "shock"
            ? [$"base_immune_to_{ailment}", "immune_to_ignite_and_shock"] : [$"base_immune_to_{ailment}"], Flag: true),
    ];

    // Life or mana leech per damage type (fire, cold and lightning also take the elemental leech; life also has the
    // 1/100 000 form some item mods use), and the leech recovery speed.
    static Sum[] Leech(string pool) =>
    [
        .. new[] { "spell", "physical", "fire", "cold", "lightning", "chaos" }.Select(type => new Sum(
            $"{pool}_leech_from_{type}_damage_permyriad",
            type is "fire" or "cold" or "lightning"
                ? [$"base_{pool}_leech_from_{type}_damage_permyriad", $"base_{pool}_leech_from_elemental_damage_permyriad"]
                : [$"base_{pool}_leech_from_{type}_damage_permyriad"],
            PerHundredThousand: pool == "life" && type != "spell" ? [$"horrible_ugly_life_leech_from_{type}_damage_perhundredthousand"] : null)),
        new($"virtual_{pool}_leech_speed_+%", [$"{pool}_leech_speed_+%", LeechSpeed]),
    ];

    /// <summary>Every display stat with a non-zero value, from the stats as the server holds them (<paramref name="get"/>:
    /// by Stats.dat Id, 0 when absent).</summary>
    public static List<(string Stat, int Value)> Compute(Func<string, int> get)
    {
        var result = new List<(string, int)>();
        foreach (var sum in Sums)
        {
            long value = get(sum.Display) + sum.Sources.Sum(s => (long)get(s))
                + (sum.PerHundredThousand?.Sum(s => (long)get(s)) ?? 0) / 10;
            if (sum.Flag) value = value > 0 ? 1 : 0;
            if (value != 0) result.Add((sum.Display, (int)Math.Clamp(value, int.MinValue, int.MaxValue)));
        }
        return result;
    }
}
