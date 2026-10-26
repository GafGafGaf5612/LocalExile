/// <summary>Steel Willed's increased attack damage from complete groups of the final armour rating.</summary>
static class ArmourAttackDamage
{
    // 3.29.3b Stats.dat row 13018; Behemoth graph node 2103 grants 2.
    public const string Per450Armour = "attack_damage_+%_per_450_physical_damage_reduction_rating";

    public static int Total(int increasedAttackDamage, int finalArmour, int per450Armour)
    {
        // PoB 16de4b82 ModParser PerStat Armour/div and ModStore EvalMod floor the complete groups first.
        // Hit-specific armour doubling is not a change to the character's rating.
        long bonus = Math.Max(0, finalArmour) / 450 * (long)per450Armour;
        return (int)Math.Clamp(increasedAttackDamage + bonus, int.MinValue, int.MaxValue);
    }
}
