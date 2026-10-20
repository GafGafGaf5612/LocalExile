namespace GameServer.Core.World;

/// <summary>
/// Path of Exile's damage-type order. Damage may only be converted to a type later in this sequence:
/// physical → lightning → cold → fire → chaos. <see cref="DamageType"/> is numbered in this order; use
/// <see cref="DamageTypes.ConversionOrder"/> whenever the order matters.
/// </summary>
public static class DamageTypes
{
    public static readonly DamageType[] ConversionOrder =
        [DamageType.Physical, DamageType.Lightning, DamageType.Cold, DamageType.Fire, DamageType.Chaos];

    public static bool IsElemental(DamageType type) =>
        type is DamageType.Fire or DamageType.Cold or DamageType.Lightning;

    /// <summary>The position of a type in the conversion sequence (physical 0 … chaos 4).</summary>
    public static int OrderOf(DamageType type) => type switch
    {
        DamageType.Physical => 0,
        DamageType.Lightning => 1,
        DamageType.Cold => 2,
        DamageType.Fire => 3,
        DamageType.Chaos => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static bool CanConvert(DamageType from, DamageType to) =>
        from != to && OrderOf(from) < OrderOf(to);

    public static string Name(DamageType type) => type.ToString();
}

/// <summary>A minimum/maximum damage range for one damage type. Fractions are kept until the final roll; converting
/// a fractional range and only then rounding matches the game's conversion path.</summary>
public readonly record struct DamageRange(double Min, double Max)
{
    public static DamageRange Zero => default;

    public bool IsZero => Min <= 0 && Max <= 0;
    public double Average => (Min + Max) / 2;
    public DamageRange Normalized => new(Math.Min(Min, Max), Math.Max(Min, Max));
    public DamageRange NonNegative => new(Math.Max(0, Min), Math.Max(0, Max));

    public DamageRange Add(DamageRange other) => new(Min + other.Min, Max + other.Max);

    public DamageRange Scale(double factor)
    {
        if (factor <= 0) return Zero;
        return new(Min * factor, Max * factor);
    }

    public DamageRange Round() => new(Math.Round(Min), Math.Round(Max));

    /// <summary>Roll one whole-number hit from this range. Each damage type rolls separately, as in the game.</summary>
    public int Roll(Random random)
    {
        if (Max <= 0) return 0;
        int min = (int)Math.Floor(Math.Max(0, Min) + 1e-9);
        int max = (int)Math.Floor(Math.Max(0, Max) + 1e-9);
        if (max < min) max = min;
        return random.Next(min, max + 1);
    }

    public override string ToString() =>
        Min == Max ? $"{Min:0.##}" : $"{Min:0.##}-{Max:0.##}";
}

/// <summary>
/// A hit's damage, split by type. This is the unit the conversion pipeline, added damage, increased/more modifiers,
/// critical strikes and mitigation operate on. A packet is immutable; methods return a new packet.
/// </summary>
public sealed class DamagePacket
{
    private readonly DamageRange[] _ranges;

    public static DamagePacket Empty { get; } = new(new DamageRange[5]);

    internal DamagePacket(DamageRange[] ranges) => _ranges = ranges;

    private static int Index(DamageType type) => (int)type;

    public static DamagePacket Of(DamageType type, DamageRange range)
    {
        var ranges = new DamageRange[5];
        ranges[Index(type)] = range.NonNegative;
        return new DamagePacket(ranges);
    }

    public static DamagePacket Of(DamageType type, double min, double max) =>
        Of(type, new DamageRange(min, max));

    public static DamagePacket Of(DamageType type, int min, int max) =>
        Of(type, new DamageRange(min, max));

    public DamageRange this[DamageType type] => _ranges[Index(type)];

    public double MinTotal => _ranges.Sum(r => r.Min);
    public double MaxTotal => _ranges.Sum(r => r.Max);
    public double AverageTotal => _ranges.Sum(r => r.Average);
    public bool IsZero => _ranges.All(r => r.IsZero);

    /// <summary>The non-zero components, in the game's conversion order.</summary>
    public IEnumerable<(DamageType Type, DamageRange Range)> Components
    {
        get
        {
            foreach (var type in DamageTypes.ConversionOrder)
                if (!this[type].IsZero) yield return (type, this[type]);
        }
    }

    public DamagePacket Add(DamagePacket other)
    {
        var ranges = new DamageRange[5];
        for (int i = 0; i < ranges.Length; i++)
            ranges[i] = _ranges[i].Add(other._ranges[i]);
        return new DamagePacket(ranges);
    }

    public DamagePacket Add(DamageType type, DamageRange range)
    {
        var ranges = (DamageRange[])_ranges.Clone();
        ranges[Index(type)] = ranges[Index(type)].Add(range.NonNegative);
        return new DamagePacket(ranges);
    }

    public DamagePacket Scale(double factor)
    {
        if (factor <= 0) return Empty;
        var ranges = new DamageRange[5];
        for (int i = 0; i < ranges.Length; i++) ranges[i] = _ranges[i].Scale(factor);
        return new DamagePacket(ranges);
    }

    /// <summary>Apply an increased/more multiplier to every type: <c>(1 + increased / 100) × more</c>.</summary>
    public DamagePacket Scale(int increasedPct, double more = 1) =>
        Scale(Math.Max(0, 1 + increasedPct / 100.0) * Math.Max(0, more));

    public DamagePacket Select(Func<DamageType, DamageRange, DamageRange> change)
    {
        var ranges = new DamageRange[5];
        for (int i = 0; i < ranges.Length; i++)
            ranges[i] = change((DamageType)i, _ranges[i]).NonNegative;
        return new DamagePacket(ranges);
    }

    public DamagePacket Round() =>
        Select((_, range) => range.Round());

    public DamagePacket Roll(Random random) =>
        Select((_, range) =>
        {
            int roll = range.Roll(random);
            return new DamageRange(roll, roll);
        });

    public int RollTotal(Random random)
    {
        int total = 0;
        foreach (var (_, range) in Components) total += range.Roll(random);
        return total;
    }

    public DamagePacket Critical(int multiplier) => Scale(Math.Max(0, multiplier) / 100.0);

    public DamagePacket Convert(DamageConversion conversion) => conversion.Apply(this);

    public override string ToString() =>
        string.Join(", ", Components.Select(c => $"{DamageTypes.Name(c.Type)} {c.Range}"));
}

/// <summary>
/// Conversion and "gain as extra" percentages. Percentages are per source type; a conversion may only go forward in
/// <see cref="DamageTypes.ConversionOrder"/>. When more than 100% of one type is converted, the outgoing conversions
/// are scaled down to exactly 100%, as Path of Building does (the skill's own conversion first, then global conversion).
/// </summary>
public sealed class DamageConversion
{
    private readonly int[,] _conversion = new int[5, 5];
    private readonly int[,] _gain = new int[5, 5];

    public bool IsEmpty
    {
        get
        {
            for (int from = 0; from < 5; from++)
                for (int to = 0; to < 5; to++)
                    if (_conversion[from, to] != 0 || _gain[from, to] != 0) return false;
            return true;
        }
    }

    /// <summary>Add a conversion, e.g. 50% of physical to fire. Throws for a conversion against the sequence.</summary>
    public DamageConversion Add(DamageType from, DamageType to, int percent)
    {
        if (percent <= 0) return this;
        if (!DamageTypes.CanConvert(from, to))
            throw new ArgumentOutOfRangeException(nameof(to),
                $"{from} cannot be converted to {to}; the conversion order is physical → lightning → cold → fire → chaos");
        _conversion[(int)from, (int)to] = Math.Min(100, _conversion[(int)from, (int)to] + percent);
        return this;
    }

    /// <summary>Add "gain <paramref name="percent"/>% of <paramref name="from"/> as extra <paramref name="to"/>". The
    /// source damage is not consumed. Gains may go forward in the sequence only, as in the game.</summary>
    public DamageConversion AddGain(DamageType from, DamageType to, int percent)
    {
        if (percent <= 0) return this;
        if (!DamageTypes.CanConvert(from, to))
            throw new ArgumentOutOfRangeException(nameof(to),
                $"{from} cannot be gained as {to}; the conversion order is physical → lightning → cold → fire → chaos");
        _gain[(int)from, (int)to] = _gain[(int)from, (int)to] + percent;
        return this;
    }

    public int ConversionPercent(DamageType from, DamageType to) => _conversion[(int)from, (int)to];
    public int GainPercent(DamageType from, DamageType to) => _gain[(int)from, (int)to];

    /// <summary>Apply conversion and gain in the game's order. Converted paths stay fractional until the caller rolls
    /// the final packet; each type's outgoing conversion is capped at 100%.</summary>
    public DamagePacket Apply(DamagePacket packet)
    {
        var incomingMin = new double[5];
        var incomingMax = new double[5];
        var result = new DamageRange[5];
        int from = 0;
        foreach (var type in DamageTypes.ConversionOrder)
        {
            var source = packet[type];
            double min = source.Min + incomingMin[from];
            double max = source.Max + incomingMax[from];

            double conversionTotal = 0;
            for (int to = from + 1; to < 5; to++) conversionTotal += _conversion[from, to];
            double conversionScale = conversionTotal > 100 ? 100.0 / conversionTotal : 1.0;

            double kept = 1 - Math.Min(1, conversionTotal / 100.0);
            result[from] = new DamageRange(min * kept, max * kept);

            for (int to = from + 1; to < 5; to++)
            {
                double converted = _conversion[from, to] * conversionScale / 100.0;
                double gained = _gain[from, to] / 100.0;
                if (converted != 0)
                {
                    incomingMin[to] += min * converted;
                    incomingMax[to] += max * converted;
                }
                if (gained != 0)
                {
                    incomingMin[to] += min * gained;
                    incomingMax[to] += max * gained;
                }
            }
            from++;
        }
        return new DamagePacket(result);
    }
}

/// <summary>The damaging ailments modelled by the server. Non-damaging ailments (chill, freeze, shock) are separate.</summary>
public enum Ailment { Ignite, Bleed, Poison, Burning, CorruptedBlood }

/// <summary>
/// A single damage-over-time effect. Unlike a hit it has one damage type and cannot critically strike. Ticks carry the
/// fractional remainder so a 0.35 s server tick does not lose damage.
/// </summary>
public readonly record struct DamageOverTime(
    DamageType Type, double DamagePerSecond, double RemainingSeconds, Ailment? Ailment = null)
{
    public bool Expired => RemainingSeconds <= 0;
    public double TotalRemainingDamage => Math.Max(0, DamagePerSecond) * Math.Max(0, RemainingSeconds);

    public DamageOverTime Advance(double seconds) =>
        this with { RemainingSeconds = Math.Max(0, RemainingSeconds - Math.Max(0, seconds)) };

    /// <summary>Whole damage for this tick and the fraction to carry into the next one.</summary>
    public (int Damage, double Carry) Tick(double seconds, double carry = 0)
    {
        double total = Math.Max(0, carry) + Math.Max(0, DamagePerSecond) * Math.Max(0, seconds);
        int whole = (int)Math.Truncate(total);
        return (whole, total - whole);
    }
}

/// <summary>The ailment damage formulas. Ignite, bleed and poison use the base damage of the hit after conversion:
/// ignite fire (90%/s for 4 s), bleed physical (70%/s for 5 s) and poison physical + chaos (30%/s for 2 s, stacking).</summary>
public static class Ailments
{
    public const double IgniteDurationSeconds = 4;
    public const double BleedDurationSeconds = 5;
    public const double PoisonDurationSeconds = 2;

    public static DamageOverTime Ignite(DamagePacket hit, double increasedPct = 0, double more = 1) =>
        new(DamageType.Fire, hit[DamageType.Fire].Average * 0.9 * Scale(increasedPct, more),
            IgniteDurationSeconds, Ailment.Ignite);

    public static DamageOverTime Bleed(DamagePacket hit, double increasedPct = 0, double more = 1) =>
        new(DamageType.Physical, hit[DamageType.Physical].Average * 0.7 * Scale(increasedPct, more),
            BleedDurationSeconds, Ailment.Bleed);

    public static DamageOverTime Poison(DamagePacket hit, double increasedPct = 0, double more = 1) =>
        new(DamageType.Chaos,
            (hit[DamageType.Physical].Average + hit[DamageType.Chaos].Average) * 0.3 * Scale(increasedPct, more),
            PoisonDurationSeconds, Ailment.Poison);

    private static double Scale(double increasedPct, double more) =>
        Math.Max(0, 1 + increasedPct / 100.0) * Math.Max(0, more);
}
