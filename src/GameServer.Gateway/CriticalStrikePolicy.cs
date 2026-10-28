/// <summary>A hit's conditional luck and unarmed melee multiplier. Native 0x1427e5480 adds ordinary,
/// low-life and Focus rolls plus Sharp and Brittle, then clamps the signed total to [-1, 1].
/// Its low-life input is virtual_on_low_life; it has no attack or melee gate, so spell critical rolls qualify.</summary>
static class CriticalStrikePolicy
{
    public const string UnarmedMeleeMultiplierStat = "unarmed_melee_attack_critical_strike_multiplier_+";

    public static bool OnLowLife(uint currentLife, uint maximumLife, int thresholdOverride = 0,
        int thresholdPercent = 0, bool countsAsLowLife = false)
    {
        if (currentLife == 0 || maximumLife == 0) return false;
        if (countsAsLowLife) return true;
        int raw = thresholdOverride > 0 ? thresholdOverride : thresholdPercent;
        int threshold = Math.Clamp(raw > 0 ? raw : 50, 0, 100);
        return (long)currentLife * 100 <= (long)maximumLife * threshold;
    }

    public static int ExtraRolls(long ordinary, long lowLife, bool onLowLife, long focus = 0,
        bool focused = false, bool sharpAndBrittle = false) => (int)Math.Clamp(
            ordinary + (onLowLife ? lowLife : 0L) + (focused ? focus : 0L) + (sharpAndBrittle ? 1L : 0L), -1, 1);

    /// <summary>Retain the existing normal critical draw (chance &gt; 0, Next(10000)); a lucky/unlucky policy adds
    /// one draw after that original draw. This local ordering preserves the zero-extra-roll path, and is not a
    /// claim about the official server's RNG sequence. Both draws occur even when the first decides the result.</summary>
    public static bool Roll(int chancePerMyriad, Random random, int extraRolls)
    {
        if (chancePerMyriad <= 0) return false;
        bool first = random.Next(10_000) < chancePerMyriad;
        if (extraRolls == 0) return first;
        bool second = random.Next(10_000) < chancePerMyriad;
        return extraRolls > 0 ? first || second : first && second;
    }

    /// <summary>Expected damage multiplier of the same ordinary/lucky/unlucky critical policy, before target
    /// mitigation or attack accuracy. The caller supplies zero chance when critical strikes are prohibited.</summary>
    public static double DamageFactor(int chancePerMyriad, int multiplier, int extraRolls = 0)
    {
        double chance = Math.Clamp(chancePerMyriad, 0, 10_000) / 10_000.0;
        if (extraRolls > 0) chance = 1 - (1 - chance) * (1 - chance);
        else if (extraRolls < 0) chance *= chance;
        return 1 - chance + chance * Math.Max(0, multiplier) / 100.0;
    }

    public static int Multiplier(int ordinaryMultiplier, long rawUnarmedMeleeBonus, bool emptyActiveMainHand,
        bool meleeAttack, bool spell = false) => emptyActiveMainHand && meleeAttack && !spell
            ? (int)Math.Clamp((long)ordinaryMultiplier + rawUnarmedMeleeBonus, int.MinValue, int.MaxValue)
            : ordinaryMultiplier;
}
