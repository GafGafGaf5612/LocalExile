using GameServer.Core.World;

/// <summary>
/// A weapon's attack from its base WeaponTypes row and the item's local stats (13-items.md §2c): local physical damage
/// (flat added and increased), weapon quality (+1% local physical damage per quality, additive with the local increase),
/// local attack speed, and the local added elemental/chaos damage the weapon adds to every hit (scaled by the local
/// elemental damage increases).
/// </summary>
static class ItemWeapons
{
    public static SkillCatalog.Unarmed Of(int attackMs, int minDamage, int maxDamage, int range,
        IReadOnlyDictionary<string, int> local, int quality)
    {
        int physicalPct = local.GetValueOrDefault("local_physical_damage_+%") + Math.Clamp(quality, 0, 100);
        double scale = Math.Max(0, 100 + physicalPct) / 100.0;
        int low = (int)((minDamage + local.GetValueOrDefault("local_minimum_added_physical_damage")) * scale);
        int high = (int)((maxDamage + local.GetValueOrDefault("local_maximum_added_physical_damage")) * scale);
        int ms = (int)Math.Round(attackMs / Math.Max(0.01, 1 + local.GetValueOrDefault("local_attack_speed_+%") / 100.0));

        var added = DamagePacket.Empty;
        int elementalPct = local.GetValueOrDefault("local_elemental_damage_+%")
            + local.GetValueOrDefault("local_attacks_with_this_weapon_elemental_damage_+%");
        void AddElemental(DamageType type, string slug)
        {
            int min = local.GetValueOrDefault($"local_minimum_added_{slug}_damage");
            int max = local.GetValueOrDefault($"local_maximum_added_{slug}_damage");
            if (min <= 0 && max <= 0) return;
            double s = Math.Max(0, 100 + elementalPct) / 100.0;
            added = added.Add(type, new DamageRange((int)(min * s), (int)(Math.Max(min, max) * s)));
        }
        AddElemental(DamageType.Fire, "fire");
        AddElemental(DamageType.Cold, "cold");
        AddElemental(DamageType.Lightning, "lightning");
        int chaosMin = local.GetValueOrDefault("local_minimum_added_chaos_damage");
        int chaosMax = local.GetValueOrDefault("local_maximum_added_chaos_damage");
        if (chaosMin > 0 || chaosMax > 0)
            added = added.Add(DamageType.Chaos, new DamageRange(chaosMin, Math.Max(chaosMin, chaosMax)));

        return new SkillCatalog.Unarmed(Math.Max(1, ms), Math.Max(0, low), Math.Max(Math.Max(0, low), high), range)
        {
            LocalAdded = added,
        };
    }
}
