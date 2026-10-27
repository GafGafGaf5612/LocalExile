using GameServer.Core.World;

static class SupportedAttackDamage
{
    public static DamagePacket Hit(SkillCatalog.Unarmed weapon, Func<string, int> get, IReadOnlySet<int> types)
    {
        return PlayerHitDamage.Resolve(Raw(weapon, get), get, new DamageConversion(), spell: false,
            melee: types.Contains(19), area: types.Contains(7), projectile: types.Contains(2), skillTypes: types);
    }

    public static DamagePacket Raw(SkillCatalog.Unarmed weapon, Func<string, int> get) =>
        DamageStats.RawAdded(weapon.LocalDamage ?? DamagePacket.Of(DamageType.Physical,
            new(weapon.MinDamage, weapon.MaxDamage)), get, spell: false);
}
