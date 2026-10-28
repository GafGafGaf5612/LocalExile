/// <summary>Owner-local leech state. All callers hold the owner's combat lock and use its pausable game clock.</summary>
sealed class LeechSession
{
    public LeechRecoveryState Life { get; } = new();
    public LeechRecoveryState Mana { get; } = new();
    public LeechRecoveryState EnergyShield { get; } = new();
    LeechLimits.Snapshot? limits;
    bool keepLife, keepEnergyShield;
    double lifeInstant, manaInstant, shieldInstant;
    bool recoverLife = true, recoverEnergyShield = true;
    public bool InstantLifeChanged, InstantManaChanged;
    public double LifeInstantCarry, ManaInstantCarry;

    public void Configure(double now, LeechLimits.Snapshot value, Func<string, int> get, Func<string, int?> replacement)
    {
        limits = value;
        keepLife = get("life_leech_does_not_stop_at_full_life") != 0 || get("virtual_life_leech_does_not_stop_at_full_life") != 0;
        keepEnergyShield = get("energy_shield_leech_does_not_stop_on_full_energy_shield") != 0;
        recoverLife = get("cannot_recover_life") == 0;
        recoverEnergyShield = get("cannot_recover_energy_shield") == 0;
        lifeInstant = Instant("life"); manaInstant = Instant("mana"); shieldInstant = Instant("energy_shield");
        foreach (var resource in value.Resources)
        {
            var state = State(resource.Pool);
            double speed = replacement($"virtual_{resource.Pool}_leech_speed_+%")
                ?? (double)get($"{resource.Pool}_leech_speed_+%") + get("life_mana_es_leech_speed_+%");
            double total = Math.Max(0, 1 + speed / 100);
            if (resource.Pool == "life" && get("life_leech_speed_is_doubled") != 0) total *= 2;
            double recovery = Math.Max(0, 1 + get($"{resource.Pool}_recovery_rate_+%") / 100.0);
            if (resource.Pool == "life" && !recoverLife || resource.Pool == "energy_shield" && !recoverEnergyShield) recovery = 0;
            state.Configure(now, new(total, resource.MaximumPerSecond, recovery));
            if (resource.CannotLeech) state.Clear(now);
        }
        double Instant(string pool) => Math.Clamp(replacement($"virtual_{pool}_leech_%_is_instant")
            ?? (double)get("leech_%_is_instant") + get($"{pool}_leech_%_is_instant"), 0, 100);
    }

    public (double Life, double Mana, double EnergyShield) Hit(double now,
        (double Life, double Mana, double EnergyShield) amounts, bool lifeFull, bool manaFull, bool shieldFull)
    {
        if (limits is null) return default;
        ClearFull(now, lifeFull, manaFull, shieldFull);
        double Grant(int index, double amount, bool full, bool keep, double instant, bool recover)
        {
            var resource = limits.Resources[index];
            if (resource.CannotLeech || full && !keep) return 0;
            double value = State(resource.Pool).Grant(now, amount, resource.MaximumPool, resource.MaximumPerInstance, instant);
            return recover && !full ? value : 0;
        }
        return (Grant(0, amounts.Life, lifeFull, keepLife, lifeInstant, recoverLife),
            Grant(1, amounts.Mana, manaFull, false, manaInstant, true),
            Grant(2, amounts.EnergyShield, shieldFull, keepEnergyShield, shieldInstant, recoverEnergyShield));
    }

    public void ClearFull(double now, bool life, bool mana, bool shield)
    {
        if (life) LifeInstantCarry = 0;
        if (mana) ManaInstantCarry = 0;
        if (life && !keepLife) Life.Clear(now);
        if (mana) Mana.Clear(now);
        if (shield && !keepEnergyShield) EnergyShield.Clear(now);
    }

    public (double Life, double Mana, double EnergyShield) TakeRecovery(double now) =>
        (Life.TakeRecovery(now), Mana.TakeRecovery(now), EnergyShield.TakeRecovery(now));

    public void Clear(double now)
    {
        Life.Clear(now); Mana.Clear(now); EnergyShield.Clear(now);
        LifeInstantCarry = ManaInstantCarry = 0;
        InstantLifeChanged = InstantManaChanged = false;
    }

    LeechRecoveryState State(string pool) => pool == "life" ? Life : pool == "mana" ? Mana : EnergyShield;
}
