/// <summary>Owner skill-duration increases derived from final attributes. Duration consumers must choose their own
/// inputs; this does not give a minion's own skills or buffs the owner's skill duration.</summary>
static class SkillDurationModifiers
{
    public const string PerTenStrength = "skill_effect_duration_+%_per_10_strength";
    public const string IncreasedDuration = "skill_effect_duration_+%";

    public static int Total(int rawIncreasedDuration, int finalStrength, int increasedPerTenStrength) =>
        (int)Math.Clamp(rawIncreasedDuration + (long)(Math.Max(0, finalStrength) / 10) * increasedPerTenStrength,
            int.MinValue, int.MaxValue);
}
