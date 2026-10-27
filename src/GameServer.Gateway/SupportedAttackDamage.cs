using GameServer.Core.World;

static class SupportedAttackDamage
{
    public static DamagePacket Hit(SkillCatalog.Unarmed weapon, Func<string, int> get, IReadOnlySet<int> types)
    {
        var hit = weapon.LocalDamage ?? DamagePacket.Of(DamageType.Physical, new(weapon.MinDamage, weapon.MaxDamage));
        foreach (var type in DamageTypes.ConversionOrder)
        {
            string slug = type.ToString().ToLowerInvariant();
            hit = hit.Add(type, new(get($"attack_minimum_added_{slug}_damage"), get($"attack_maximum_added_{slug}_damage")));
        }
        double manaLightning = DamageStats.ManaAddedLightning(get);
        hit = hit.Add(DamageType.Lightning, new(manaLightning, manaLightning));
        return hit.Select((type, range) =>
        {
            double increased = DamageStats.AttackIncrease(get, type, types.Contains(19), types.Contains(7), types.Contains(2));
            return DamageStats.IncreaseAttackRange(range, increased);
        });
    }
}
