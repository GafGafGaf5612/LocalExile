/// <summary>
/// The attack timer every attack shares (the default attack and the attack gem skills). While a button is held the client
/// keeps aiming the attack, so a use may only start a swing once the previous attack is over; the client's jitter lets
/// one arrive up to a quarter of an attack early, and the schedule stays on its cadence
/// (Agents/docs/protocol/11-skills.md §5).
/// </summary>
static class AttackTiming
{
    /// <summary>How early a use may arrive, as a fraction of the attack time.</summary>
    public const double EarlyAllowance = 0.25;

    /// <summary>Whether a use at <paramref name="now"/> may start an attack whose previous one was at <paramref name="next"/>.</summary>
    public static bool Ready(double now, double next, double seconds) => now >= next - seconds * EarlyAllowance;

    /// <summary>When the attack after this one may start: one attack time after the later of now and the previous one.</summary>
    public static double After(double now, double next, double seconds) => Math.Max(now, next) + seconds;
}
