namespace GameServer.Gateway.Tests;

public class AuraTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static AuraCatalog.Aura Aura(SkillCatalog skills, string effect, ushort buffRow, int flat, int percent) =>
        new(0x0103, skills.EffectRow(effect), effect, effect, 1, buffRow, flat, percent);

    [Fact]
    public void Simple_aura_reservations_come_from_GrantedEffectsPerLevel()
    {
        var skills = new SkillCatalog(Data());

        Assert.Equal((0, 5000), skills.Reservation("PurityOfElements", 1));
        Assert.Equal((0, 3500), skills.Reservation("PurityOfFire", 1));
        Assert.Equal((34, 0), skills.Reservation("Clarity", 1));
        Assert.Equal((28, 0), skills.Reservation("Vitality", 1));
        Assert.Equal((22, 0), skills.Reservation("Precision", 1));
        Assert.Equal((0, 5000), skills.Reservation("Haste", 1));
    }

    [Fact]
    public void Instant_skills_come_from_the_ActiveSkillType_Instant()
    {
        var skills = new SkillCatalog(Data());

        Assert.True(skills.IsInstant(skills.EffectRow("Haste")));
        Assert.True(skills.IsInstant(skills.EffectRow("RighteousFire")));
        Assert.False(skills.IsInstant(skills.EffectRow("Fireball")));
        Assert.False(skills.IsInstant(-1));

        uint instant = new StatCatalog(Data()).Key("skill_is_instant")!.Value;
        Assert.Contains((instant, 1), skills.EffectStats(skills.EffectRow("Haste"), 1));
        Assert.DoesNotContain(skills.EffectStats(skills.EffectRow("Fireball"), 1), s => s.Item1 == instant);
    }

    [Fact]
    public void A_skill_with_a_cooldown_carries_its_uses()
    {
        var skills = new SkillCatalog(Data());
        var stats = new StatCatalog(Data());
        uint uses = stats.Key("virtual_total_cooldown_count")!.Value;

        Assert.Contains((uses, 1), skills.EffectStats(skills.EffectRow("Haste"), 1));
        Assert.Contains((uses, 1), skills.EffectStats(skills.EffectRow("PurityOfElements"), 1));
        Assert.DoesNotContain(skills.EffectStats(skills.EffectRow("Fireball"), 1), s => s.Item1 == uses);

        Assert.Equal(1200, skills.CooldownMs(skills.EffectRow("Haste"), 1));
        Assert.Equal(300, skills.CooldownMs(skills.EffectRow("RighteousFire"), 1));
        Assert.Equal(0, skills.CooldownMs(skills.EffectRow("Fireball"), 1));
    }

    [Fact]
    public void Reservation_efficiency_divides_percent_and_flat_reservations()
    {
        var pools = new CharacterStats.Pools(1, 50, 100, 2, 0, ManaReservationEfficiencyPct: 50);
        var skills = new SkillCatalog(Data());
        var auras = new[]
        {
            Aura(skills, "PurityOfElements", 144, 0, 5000),
            Aura(skills, "Clarity", 155, 34, 0),
        };

        // 50% / 1.5 = 33.33%, rounded up to 34 mana; 34 flat / 1.5 = 23.
        Assert.Equal(57, AuraCatalog.ReservedMana(pools, auras));
        Assert.Equal(43, AuraCatalog.UnreservedMana(pools, auras));
    }

    [Fact]
    public void Purity_and_precision_effects_reach_the_player_bonus()
    {
        var skills = new SkillCatalog(Data());
        var stats = new StatCatalog(Data());
        var auras = new[]
        {
            Aura(skills, "PurityOfElements", 144, 0, 5000),
            Aura(skills, "Precision", 1293, 22, 0),
        };

        var bonus = AuraCatalog.Bonus(auras, 50, skills, stats);

        Assert.Equal(30, bonus.FireResistance);
        Assert.Equal(30, bonus.ColdResistance);
        Assert.Equal(30, bonus.LightningResistance);
        Assert.Equal(140, bonus.Accuracy);       // 93 * 1.5
        Assert.Equal(30, bonus.CriticalStrikeChancePct);
        Assert.Equal(30, bonus.SpellCriticalStrikeChancePct);
    }

    [Fact]
    public void Clarity_and_vitality_regeneration_are_aura_stats()
    {
        var skills = new SkillCatalog(Data());
        var stats = new StatCatalog(Data());
        var auras = new[] { Aura(skills, "Clarity", 155, 34, 0), Aura(skills, "Vitality", 153, 28, 0) };
        var raw = AuraCatalog.RawStats(auras, 0, skills, stats).ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.Equal(176, raw[stats.Key("base_mana_regeneration_rate_per_minute")!.Value]);
        Assert.Equal(600, raw[stats.Key("base_life_regeneration_rate_per_minute")!.Value]);
    }

    [Fact]
    public void Haste_grants_attack_movement_and_cast_speed()
    {
        var skills = new SkillCatalog(Data());
        var stats = new StatCatalog(Data());
        var auras = new[] { Aura(skills, "Haste", 152, 0, 5000) };

        var bonus = AuraCatalog.Bonus(auras, 50, skills, stats);
        var raw = AuraCatalog.RawStats(auras, 50, skills, stats).ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.Equal(23, bonus.AttackSpeedPct);       // 15% increased * 1.5
        Assert.Equal(15, bonus.MovementVelocityPct);  // 10% increased * 1.5
        Assert.Equal(23, raw[stats.Key("cast_speed_+%")!.Value]);
    }

    [Fact]
    public void Only_the_implemented_auras_have_a_buff_row()
    {
        Assert.True(AuraCatalog.Supported("PurityOfElements"));
        Assert.True(AuraCatalog.Supported("Precision"));
        Assert.True(AuraCatalog.Supported("Haste"));
    }
}
