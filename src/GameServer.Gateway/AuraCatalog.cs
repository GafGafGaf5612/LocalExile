/// <summary>
/// The auras the server models: their BuffDefinitions rows and their effects on the player. Reservation itself is
/// data-driven (SkillCatalog.Reservation); this table holds the small, unconditional auras. The combat auras (added
/// damage, heralds, banners) are deliberately not here yet.
/// </summary>
static class AuraCatalog
{
    /// <summary>One active reservation skill: the skill id, its GrantedEffects row and id, its buff instance, and its gem's
    /// level and quality.</summary>
    public sealed record Aura(ushort SkillId, int Effect, string EffectId, string Name, ushort Buff, ushort BuffRow,
        int ManaFlat, int ManaPercent, int Level = 1, int Quality = 0);

    /// <summary>The BuffDefinitions row that draws each aura's visual and buff icon.</summary>
    private static readonly Dictionary<string, ushort> BuffRows = new()
    {
        ["PurityOfElements"] = 144,
        ["PurityOfFire"] = 145,
        ["PurityOfIce"] = 146,
        ["PurityOfLightning"] = 147,
        ["Clarity"] = 155,
        ["Vitality"] = 153,
        ["Precision"] = 1293,
        ["Haste"] = 152,
    };

    public static bool Supported(string effectId) => BuffRows.ContainsKey(effectId);

    public static ushort BuffRow(string effectId) => BuffRows[effectId];

    /// <summary>
    /// The mana reserved by the active auras: flat reservations plus the percent reservations (permyriad) of the
    /// character's maximum mana, each reduced by mana reservation efficiency (PoB CalcPerform: divide by
    /// 1 + efficiency / 100; the final amount is rounded up to whole mana).
    /// </summary>
    public static int ReservedMana(CharacterStats.Pools pools, IEnumerable<Aura> auras)
    {
        double efficiency = Math.Max(-99, pools.ManaReservationEfficiencyPct) / 100.0 + 1;
        double flat = 0, percent = 0;
        foreach (var aura in auras)
        {
            flat += Math.Round(aura.ManaFlat / efficiency, MidpointRounding.AwayFromZero);
            // PoB rounds each skill's reserved percent to two decimals before summing it.
            percent += Math.Round(aura.ManaPercent / 100.0 / efficiency, 2, MidpointRounding.AwayFromZero);
        }
        double reserved = flat + pools.Mana * percent / 100.0;
        return Math.Clamp((int)Math.Ceiling(reserved), 0, (int)pools.Mana);
    }

    public static int UnreservedMana(CharacterStats.Pools pools, IEnumerable<Aura> auras) =>
        Math.Max(0, (int)pools.Mana - ReservedMana(pools, auras));

    /// <summary>The player stat bonuses from the active auras, scaled by generic aura effect.</summary>
    public static CharacterStats.Bonus Bonus(IEnumerable<Aura> auras, int auraEffectPct, SkillCatalog skills,
        StatCatalog stats)
    {
        int fire = 0, cold = 0, lightning = 0, chaos = 0;
        int fireMax = 0, coldMax = 0, lightningMax = 0, chaosMax = 0;
        int accuracy = 0, criticalChance = 0, attackSpeed = 0, movementSpeed = 0;
        foreach (var aura in auras)
        {
            switch (aura.EffectId)
            {
                case "PurityOfElements":
                    int all = Value(skills, stats, aura, "base_resist_all_elements_%", auraEffectPct);
                    fire += all;
                    cold += all;
                    lightning += all;
                    break;
                case "PurityOfFire":
                    fire += Value(skills, stats, aura, "base_fire_damage_resistance_%", auraEffectPct);
                    fireMax += Value(skills, stats, aura, "base_maximum_fire_damage_resistance_%", auraEffectPct);
                    break;
                case "PurityOfIce":
                    cold += Value(skills, stats, aura, "base_cold_damage_resistance_%", auraEffectPct);
                    coldMax += Value(skills, stats, aura, "base_maximum_cold_damage_resistance_%", auraEffectPct);
                    break;
                case "PurityOfLightning":
                    lightning += Value(skills, stats, aura, "base_lightning_damage_resistance_%", auraEffectPct);
                    lightningMax += Value(skills, stats, aura, "base_maximum_lightning_damage_resistance_%", auraEffectPct);
                    break;
                case "Precision":
                    accuracy += Value(skills, stats, aura, "accuracy_rating", auraEffectPct);
                    int crit = Value(skills, stats, aura, "skill_buff_grants_critical_strike_chance_+%",
                        auraEffectPct);
                    criticalChance += crit;
                    break;
                case "Haste":
                    attackSpeed += Value(skills, stats, aura, "attack_speed_+%_granted_from_skill", auraEffectPct);
                    movementSpeed += Value(skills, stats, aura, "base_movement_velocity_+%", auraEffectPct);
                    break;
            }
        }
        return new CharacterStats.Bonus(FireResistance: fire, ColdResistance: cold, LightningResistance: lightning,
            ChaosResistance: chaos, FireMaxResistance: fireMax, ColdMaxResistance: coldMax,
            LightningMaxResistance: lightningMax, ChaosMaxResistance: chaosMax,
            Accuracy: accuracy, CriticalStrikeChancePct: criticalChance, SpellCriticalStrikeChancePct: criticalChance,
            AttackSpeedPct: attackSpeed, MovementVelocityPct: movementSpeed);
    }

    /// <summary>The flat stat bonuses from the active auras (regeneration and Haste's cast speed), scaled by aura effect.</summary>
    public static IEnumerable<(uint Key, int Value)> RawStats(IEnumerable<Aura> auras, int auraEffectPct,
        SkillCatalog skills, StatCatalog stats)
    {
        var result = new Dictionary<uint, int>();
        foreach (var aura in auras)
        {
            switch (aura.EffectId)
            {
                case "Clarity":
                    Add("base_mana_regeneration_rate_per_minute");
                    break;
                case "Vitality":
                    Add("base_life_regeneration_rate_per_minute");
                    break;
                case "Haste":
                    Add("cast_speed_+%", "cast_speed_+%_granted_from_skill");
                    break;
            }

            void Add(string name, string? source = null)
            {
                if (stats.Key(name) is not uint key) return;
                result[key] = result.GetValueOrDefault(key)
                    + Value(skills, stats, aura, source ?? name, auraEffectPct);
            }
        }
        return result.Select(kv => (kv.Key, kv.Value));
    }

    private static int Value(SkillCatalog skills, StatCatalog stats, Aura aura, string name, int auraEffectPct)
    {
        if (stats.Key(name) is not uint key) return 0;
        int value = skills.EffectStats(aura.Effect, aura.Level, aura.Quality).FirstOrDefault(kv => kv.Item1 == key).Item2;
        return (int)Math.Round(value * Math.Max(0, 100 + auraEffectPct) / 100.0, MidpointRounding.AwayFromZero);
    }
}
