using GameServer.Core.World;

/// <summary>An ordinary bow's offensive inputs frozen at release. The actual target supplies evasion and impales
/// at contact. This preserves the ordinary attack's existing damage/crit/impale arithmetic.</summary>
sealed record OrdinaryBowShot(DamagePacket Hit, int Accuracy, int CriticalChance, int CriticalMultiplier, int ImpaleChance)
{
    public static OrdinaryBowShot Capture(SkillCatalog.Unarmed weapon, IReadOnlyDictionary<uint, int> character,
        IEnumerable<(uint, int)> skillConversion, StatCatalog catalog, int accuracy, int criticalChance, int criticalMultiplier)
    {
        int Get(string name) => catalog.Key(name) is uint key ? character.GetValueOrDefault(key) : 0;
        var conversion = new Dictionary<uint, int>(character);
        foreach (var (key, value) in skillConversion) conversion.TryAdd(key, value);
        var hit = DamageStats.Added(weapon.HitDamage(character), Get, spell: false, melee: false)
            .Convert(DamageStats.Conversion(conversion, catalog, attack: true));
        return new(hit, accuracy, criticalChance, criticalMultiplier, Get("impale_on_hit_%_chance"));
    }

    public bool Hits(int evasion, Random random) =>
        DamageModel.Hits(DamageModel.AccuracyHitChance(evasion, Accuracy), random.Next(100));

    public (int Damage, bool Critical) Roll(ImpaleState impale, Random random)
    {
        var hit = Hit;
        // Keep the existing ordinary attack convention: store the converted physical maximum before the bonus/crit.
        double physical = hit[DamageType.Physical].Max;
        double stored = impale.Bonus;
        if (stored > 0) hit = hit.Add(DamageType.Physical, new DamageRange(stored, stored));
        impale.AfterHit();
        if (ImpaleChance > 0 && random.Next(100) < ImpaleChance) impale.Apply(physical);
        int damage = hit.RollTotal(random);
        bool critical = CriticalChance > 0 && random.Next(10_000) < CriticalChance;
        if (critical) damage = DamageModel.CriticalDamage(damage, CriticalMultiplier);
        return (damage, critical);
    }
}
