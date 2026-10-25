partial class Program
{
    static SingleUseCooldown? CooldownFor(ClientState conn, int effect, int level, int quality) =>
        Skills.CooldownMs(effect, level) <= 0 || GemSkillEffects(conn.Character).Count(g => g.Effect == effect) != 1 ? null :
            SingleUseCooldown.ForSkill(Skills, Stats, effect, level, CharacterPlayerStats(conn),
                Skills.EffectStats(effect, level, quality).ToDictionary(s => s.Item1, s => s.Item2));

    static SingleUseCooldownState CooldownStateLocked(ClientState conn, int effect, SingleUseCooldown plan, double now)
    {
        if (!conn.SingleUseCooldowns.TryGetValue(effect, out var state))
        {
            double legacyRemaining = Math.Max(0, conn.SkillReadyAt.GetValueOrDefault(effect) - now);
            state = new(now, plan.RecoveryRate, double.IsFinite(legacyRemaining)
                ? legacyRemaining * 1000 * (plan.RecoveryRate > 0 ? plan.RecoveryRate : 1) : plan.BaseMilliseconds);
            conn.SingleUseCooldowns[effect] = state;
        }
        state.Refresh(now, plan.RecoveryRate);
        return state;
    }

    // Readiness, reservation and action side effects are one short critical section, before any echo/await.
    // An approach checks readiness without spending; arrival must reserve under its original action token.
    internal static bool TryBeginCooldownUse(ClientState conn, int effect, SingleUseCooldown? plan, bool instant,
        Action sideEffects, out CooldownUseTicket? ticket, int? actionToken = null, bool reserve = true)
    {
        lock (conn.Monsters)
        {
            ticket = null;
            if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || conn.Paused
                || actionToken is int original && conn.ActionCount != original) return false;
            if (plan is { } cooldown)
            {
                double now = conn.Clock.Elapsed.TotalSeconds;
                var state = CooldownStateLocked(conn, effect, cooldown, now);
                if (reserve ? !state.TryUse(now, cooldown) : !state.Ready) return false;
                conn.SkillReadyAt[effect] = now + state.RemainingSeconds;
            }
            sideEffects();
            if (plan is not null) ticket = new(effect, conn.ActionCount, conn.ProjectileEpoch, instant, reserve);
            return true;
        }
    }

    static void RefreshSingleUseCooldowns(ClientState conn)
    {
        lock (conn.Monsters) if (conn.SingleUseCooldowns.Count == 0) return;
        var player = CharacterPlayerStats(conn);
        var plans = GemSkillEffects(conn.Character).GroupBy(g => g.Effect).ToDictionary(g => g.Key, g =>
            g.Count() != 1 ? null : SingleUseCooldown.ForSkill(Skills, Stats, g.Key, g.First().Level, player,
                Skills.EffectStats(g.Key, g.First().Level, g.First().Quality).ToDictionary(s => s.Item1, s => s.Item2)));
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            foreach (var (effect, state) in conn.SingleUseCooldowns.ToArray())
            {
                if (!plans.TryGetValue(effect, out var plan)) continue; // Removal does not reset a spent use.
                state.Refresh(now, plan?.RecoveryRate ?? state.Rate);
                conn.SkillReadyAt[effect] = now + state.RemainingSeconds;
                if (plan is null) conn.SingleUseCooldowns.Remove(effect); // Unsupported mechanics retain the legacy path.
            }
        }
    }
}
