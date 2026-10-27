/// <summary>
/// The character panel's leech limits, independently of active leech instances. The default percentages are
/// Character.ot's constants, also exported in pinned PoB 16de4b82's Data/Misc.lua and used by CalcSetup/CalcDefence.
/// The limits use maximum pools, including their reserved portions. This does not create or recover any leech.
/// </summary>
static class LeechLimits
{
    public const int BaseInstancePercent = 10;
    public const int BaseLifeManaRatePercentPerMinute = 1200;
    public const int BaseEnergyShieldRatePercentPerMinute = 600;

    /// <summary>
    /// Six absolute limits and their native permyriad companions, plus the shared cannot-leech flags that gate them.
    /// <paramref name="get"/> reads resolved sources without Character.ot's defaults; <paramref name="replacement"/>
    /// optionally supplies explicit developer replacements, preserving zero for default inputs and virtual limits.
    /// Increased maximum rate is distinct from leech recovery speed. Integer wire endpoints truncate nonnegative
    /// fractions; PoB retains fractional values, so this projection is not a measured official-server rounding rule.
    /// Conditional modifiers requiring other systems (reservation, recent savage hits, Defiance and party links),
    /// including the derived Wicked Ward virtual and unacquired legacy Life-rate doubling, are outside this calculation.
    /// <paramref name="onLowLife"/> supplies the condition that prevents Life and Mana leech; pinned PoB's ModParser
    /// leaves ES leech available.
    /// </summary>
    public static List<(string Stat, int Value)> Compute(Func<string, int> get, uint maximumLife, uint maximumMana,
        uint maximumEnergyShield, Func<string, int?>? replacement = null, bool onLowLife = false)
    {
        decimal Input(string name, int baseline = 0) => replacement?.Invoke(name) is int set ? set : (decimal)baseline + get(name);
        bool Flag(string name) => Input(name) != 0;
        bool ReplacedFlag(string name, bool derived) => replacement?.Invoke(name) is int set ? set != 0 : derived;
        bool all = ReplacedFlag("cannot_leech", Flag("base_cannot_leech") || Flag("cannot_leech")
            || Flag("map_players_cannot_leech"));
        bool lowLifeLifeMana = onLowLife && Flag("cannot_leech_when_on_low_life");
        var rows = new List<(string Stat, int Value)> { ("cannot_leech", all ? 1 : 0) };
        foreach (var (pool, maximum) in new[] { ("life", maximumLife), ("mana", maximumMana), ("energy_shield", maximumEnergyShield) })
        {
            bool blocked = ReplacedFlag($"cannot_leech_{pool}", all || Flag($"base_cannot_leech_{pool}")
                || Flag($"cannot_leech_{pool}") || pool != "energy_shield" && lowLifeLifeMana
                || pool == "mana" && Flag("cannot_leech_or_regenerate_mana"));
            rows.Add(($"cannot_leech_{pool}", blocked ? 1 : 0));
            string maximumName = pool == "energy_shield" ? "energy_shield" : pool;
            string instanceVirtual = $"virtual_maximum_{pool}_leech_amount_per_leech_permyriad_max_{maximumName}";
            string rateVirtual = $"virtual_maximum_{pool}_leech_rate_permyriad_per_minute";
            decimal instance = Math.Max(0, Input($"maximum_{pool}_leech_amount_per_leech_%_max_{maximumName}", BaseInstancePercent))
                * Math.Max(0, 100 + Input($"maximum_{pool}_leech_amount_per_leech_+%"));
            decimal rate = Math.Max(0, Input($"maximum_{pool}_leech_rate_%_per_minute",
                    pool == "energy_shield" ? BaseEnergyShieldRatePercentPerMinute : BaseLifeManaRatePercentPerMinute))
                * Math.Max(0, 100 + Input($"maximum_{pool}_leech_rate_+%") + Input("maximum_leech_rate_+%")
                    + Input("map_player_maximum_leech_rate_+%"));
            if (pool == "energy_shield" && Flag("maximum_energy_shield_leech_amount_per_leech_is_doubled")) instance *= 2;
            instance = Math.Max(0, replacement?.Invoke(instanceVirtual) ?? instance);
            rate = Math.Max(0, replacement?.Invoke(rateVirtual) ?? rate);
            if (blocked) (instance, rate) = (0, 0);
            rows.Add((instanceVirtual, Whole(instance)));
            rows.Add((rateVirtual, Whole(rate)));
            rows.Add(($"maximum_{pool}_leech_amount_per_leech", OfPool(maximum, instance)));
            rows.Add(($"maximum_total_recovery_per_minute_from_{pool}_leech", OfPool(maximum, rate)));
        }
        return rows;
    }

    static int Whole(decimal value) => (int)Math.Clamp(decimal.Truncate(value), 0, int.MaxValue);

    // Compare before multiplication: valid uint pools and extreme signed stat inputs must never overflow decimal.
    static int OfPool(uint maximum, decimal permyriad) => maximum == 0 || permyriad <= 0 ? 0
        : permyriad >= (decimal)int.MaxValue * 10_000 / maximum ? int.MaxValue : Whole(maximum * permyriad / 10_000);
}
