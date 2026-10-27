using GameServer.Core.World;

/// <summary>
/// Resolves raw player hit damage through conversion before applying modifiers. A converted portion inherits each
/// visited damage type, so its increased bucket and typed more modifiers cannot be reconstructed from merged totals.
/// Pinned PoB CalcOffence.lua (16de4b82), calcDamage:35-140, uses the same visited-type set.
/// </summary>
static class PlayerHitDamage
{
    public static DamagePacket Resolve(DamagePacket raw, Func<string, int> get, DamageConversion conversion,
        bool spell, bool melee = false, bool area = false, bool projectile = false,
        IEnumerable<(string Name, int Value)>? supportModifiers = null, IReadOnlySet<int>? skillTypes = null)
    {
        melee = !spell && melee;
        var modifiers = supportModifiers?.ToArray() ?? [];
        // Preserve native area/projectile support classification, but do not let contradictory supplied flags turn
        // an attack into a spell or admit melee-only modifiers on a spell. The explicit hit context owns that family.
        var supportTypes = new HashSet<int>(skillTypes ?? ContextTypes(spell, melee, area, projectile));
        supportTypes.Remove(0);
        supportTypes.Remove(1);
        supportTypes.Remove(19);
        supportTypes.Add(spell ? 1 : 0);
        if (melee) supportTypes.Add(19);
        double common = (double)get("damage_+%") + get(spell ? "spell_damage_+%" : "attack_damage_+%")
            + (melee ? get("melee_damage_+%") : 0) + (area ? get("area_damage_+%") : 0)
            + (projectile ? get("projectile_damage_+%") : 0) + (spell ? 0 : DamageStats.SpellAttackIncrease(get));
        var typed = DamageTypes.ConversionOrder.Select(type =>
            (History: DamageTypes.HistoryOf(type), Increased: get($"{type.ToString().ToLowerInvariant()}_damage_+%"))).ToArray();
        int elemental = get("elemental_damage_+%"), meleePhysical = melee ? get("melee_physical_damage_+%") : 0;
        double charges = ChargeEffects.DamageMore(get, spell);
        var result = DamagePacket.Empty;
        foreach (var path in conversion.Trace(raw))
        {
            // Prohibitions constrain the final damage dealt, not a type the portion used to have.
            if (get("base_deal_no_damage") != 0
                || DamageTypes.IsElemental(path.Type) && get("deal_no_elemental_damage") != 0
                || path.Type == DamageType.Chaos && get("base_deal_no_chaos_damage") != 0
                || GemSupportDamage.Prohibited(path.Type, modifiers)) continue;
            double increased = common;
            foreach (var source in typed)
                if ((path.History & source.History) != 0) increased += source.Increased;
            if ((path.History & DamageTypeHistory.Elemental) != 0) increased += elemental;
            if ((path.History & DamageTypeHistory.Physical) != 0) increased += meleePhysical;

            // Preserve the existing attack multiply-before-divide projection and spell factor construction.
            // Clamp the complete increased bucket once: a negative original-type subtotal must survive until here.
            var range = spell
                ? path.Range.Scale(Math.Max(0, 1 + increased / 100.0) * (SpellMore(get, path.History) * charges))
                : DamageStats.IncreaseAttackRange(path.Range, increased);
            var support = GemSupportDamage.Multipliers(path.History, modifiers, supportTypes);
            range = range.Scale(support.GeneralOrPhysical).Scale(support.ElementalOrChaos);
            result = result.Add(path.Type, range);
        }
        // WithCharges expands increases and additions, but leaves more multipliers separate. Spell portions retain
        // their existing aura/charge factor grouping before support scaling; attack packets receive the common
        // multiplier here. Both routes apply it once. Rage remains outside this resolver in the attack caller.
        return spell || charges == 1 ? result : result.Scale(charges);
    }

    static double SpellMore(Func<string, int> get, DamageTypeHistory history) =>
        Math.Max(0, 1 + get("spell_damage_aura_spell_damage_+%_final") / 100.0)
        * ((history & DamageTypeHistory.Lightning) != 0
            ? Math.Max(0, 1 + get("wrath_aura_spell_lightning_damage_+%_final") / 100.0) : 1);

    static IReadOnlySet<int> ContextTypes(bool spell, bool melee, bool area, bool projectile)
    {
        var types = new HashSet<int> { spell ? 1 : 0 };
        if (melee) types.Add(19);
        if (area) types.Add(7);
        if (projectile) types.Add(2);
        return types;
    }
}
