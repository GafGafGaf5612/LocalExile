using System.Collections.Concurrent;

/// <summary>
/// One connection's charges at work (issue #237): the events that may grant charges wait in a queue (the combat code
/// only enqueues, under any lock), and <see cref="Step"/>, run by the Gateway's charge ticker every
/// <see cref="StepSeconds"/> of game time, rolls them, times the periodic gains, expires and clamps the charges, and
/// takes them all on death. <see cref="Presentation"/> says which charge buffs the client must get or lose. Not
/// thread-safe: the Gateway calls it under the connection's Monsters lock.
/// </summary>
sealed class ChargeSession
{
    /// <summary>How often the ticker steps (seconds): expiry and periodic gains are this precise.</summary>
    public const double StepSeconds = 0.05;

    /// <summary>How often the limits (maximum, minimum, duration) and the periodic stats are read again when nothing
    /// happens (seconds): an item taken off clamps the charges within this.</summary>
    public const double LimitsSeconds = 0.25;

    /// <summary>PoE's "recently": the last 4 seconds.</summary>
    public const double RecentlySeconds = 4;

    public Charges.State State { get; } = new();
    readonly ConcurrentQueue<ChargeEvent> events = new();
    readonly ChargeGains.Timers timers = new();
    readonly Charges.Limits[] limits = [Default, Default, Default];
    readonly int[] applied = new int[3];
    readonly Shown[] shown = new Shown[3];
    Dictionary<string, int> periodic = [];
    double lastStep = double.NaN, limitsAt = double.NegativeInfinity;

    static readonly Charges.Limits Default = new(ChargePanelStats.BaseMaximumCharges, 0, Charges.BaseDurationMs);

    /// <summary>When a monster's hit last landed on the player (game clock seconds).</summary>
    public double LastHitAt { get; private set; } = double.NegativeInfinity;

    /// <summary>The type's limits as last read.</summary>
    public Charges.Limits Limits(ChargeType type) => limits[(int)type];

    /// <summary>The charges the character has now (the minimum counts).</summary>
    public int Effective(ChargeType type) => State.Effective(type, limits[(int)type]);

    public void Enqueue(ChargeEvent e) => events.Enqueue(e);

    public bool Pending => !events.IsEmpty;

    /// <summary>What one step did: whether the charges the stats count changed, and lines for the log.</summary>
    public sealed record StepResult(bool CountsChanged, IReadOnlyList<string> Log);

    /// <summary>
    /// One step at <paramref name="now"/> (game clock seconds). <paramref name="stats"/> reads the character's stats
    /// (called at most once, and only when needed: pending events, or every <see cref="LimitsSeconds"/>);
    /// <paramref name="weapon"/> its weapons. While dead every gained charge is lost and the events are dropped; in
    /// towns no periodic charge is gained.
    /// </summary>
    public StepResult Step(double now, Func<Func<string, int>> stats, Func<WeaponProfile> weapon, bool alive, bool moving,
        bool inTown, Random rng)
    {
        var log = new List<string>();
        double elapsed = double.IsNaN(lastStep) ? 0 : Math.Max(0, now - lastStep);
        lastStep = now;
        Func<string, int>? cached = null;
        Func<string, int> Get() => cached ??= stats();
        if (!alive)
        {
            while (events.TryDequeue(out _)) { }
            timers.Reset();
            if (State.Clear()) log.Add("charges lost on death");
        }
        else
        {
            if (!events.IsEmpty || now - limitsAt >= LimitsSeconds) ReadLimits(Get(), now);
            while (events.TryDequeue(out var e))
            {
                if (e is ChargeEvent.WhenHit) LastHitAt = now;
                if (e is ChargeEvent.Set set)
                {
                    foreach (var type in set.Type is { } only ? [only] : Charges.Types)
                        State.Set(type, set.Count, now, limits[(int)type]);
                    log.Add(set.Type is { } t ? $"{Charges.Name(t)} charges set to {set.Count}" : "charges cleared");
                    continue;
                }
                var gains = ChargeGains.Roll(e, Get(), weapon(), rng);
                foreach (var gain in gains) Apply(gain, now, Get(), rng, log);
            }
            if (!inTown)
                foreach (var gain in timers.Advance(elapsed, name => periodic.GetValueOrDefault(name),
                    now - LastHitAt <= RecentlySeconds, moving, rng))
                    Apply(gain, now, Get(), rng, log);
        }
        foreach (var type in State.Expire(now)) log.Add($"{Charges.Name(type)} charges expired");
        foreach (var type in Charges.Types)
            if (State.Clamp(type, limits[(int)type])) log.Add($"{Charges.Name(type)} charges down to the maximum {limits[(int)type].Maximum}");
        bool changed = false;
        foreach (var type in Charges.Types)
        {
            int count = Effective(type);
            if (count != applied[(int)type]) { applied[(int)type] = count; changed = true; }
        }
        return new(changed, log);
    }

    void ReadLimits(Func<string, int> get, double now)
    {
        foreach (var type in Charges.Types) limits[(int)type] = Charges.LimitsOf(type, get, State.Held(type));
        periodic = ChargeGains.Periodics.Select(p => p.Stat).Distinct().ToDictionary(s => s, get);
        limitsAt = now;
    }

    void Apply(ChargeGains.Gain gain, double now, Func<string, int> get, Random rng, List<string> log)
    {
        var current = limits[(int)gain.Type];
        int after = Math.Min(current.Maximum, State.Effective(gain.Type, current) + gain.Count);
        // The duration of charges counted per Frenzy Charge (frenzy_charge_duration_+%_per_frenzy_charge) uses the count
        // the gain leaves.
        var l = Charges.LimitsOf(gain.Type, get, after);
        limits[(int)gain.Type] = l;
        bool toMaximum = ChargeGains.Rolls(get(ChargeGains.ToMaximumStat(gain.Type)), rng);
        var (before, now2) = State.Gain(gain.Type, gain.Count, now, l, toMaximum);
        log.Add($"{gain.Source}: {Charges.Name(gain.Type)} charges {before} → {now2}/{l.Maximum}" +
            (toMaximum ? " (to maximum)" : "") + $", {l.DurationMs / 1000.0:0.###} s");
    }

    /// <summary>A charge buff as the client has it: the count and expiry it was sent with and its buff instance.</summary>
    public readonly record struct Shown(int Count, double ExpiresAt, ushort Buff);

    /// <summary>A charge buff to send: the previous instance to remove (0: none) and, when <paramref name="Count"/> is
    /// above 0, the new one to add with its remaining seconds (null: no duration, the minimum charges).</summary>
    public readonly record struct BuffChange(ChargeType Type, ushort Remove, ushort Add, int Count, double? Remaining);

    /// <summary>
    /// The charge buffs the client must get: for each type whose count or expiry changed since the last call, its old
    /// buff comes off and a new one goes on (<paramref name="nextBuff"/> numbers it) with the count and the duration
    /// left. Charges at the minimum do not expire, so they go without a duration. <paramref name="resend"/>: send every
    /// held type again (a new player object after an area change).
    /// </summary>
    public List<BuffChange> Presentation(double now, Func<ushort> nextBuff, bool resend = false)
    {
        var changes = new List<BuffChange>();
        foreach (var type in Charges.Types)
        {
            int i = (int)type;
            int count = Effective(type);
            bool timed = State.Held(type) > limits[i].Minimum;
            double expires = timed ? State.ExpiresAt(type) : double.PositiveInfinity;
            var old = shown[i];
            if (!resend && old.Count == count && (count == 0 || old.ExpiresAt == expires)) continue;
            if (count == 0 && old.Buff == 0) continue;
            ushort add = count > 0 ? nextBuff() : (ushort)0;
            changes.Add(new(type, old.Buff, add, count, timed ? Math.Max(0, expires - now) : null));
            shown[i] = new(count, expires, add);
        }
        return changes;
    }

    /// <summary>Forgets the buffs sent (a new player object has none).</summary>
    public void ForgetShown() => Array.Clear(shown);
}
