using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class RageAttackScalingTests
{
    const string RageDamage = "attack_damage_+%_final_from_rage";
    static Dictionary<uint, int> Values(params (string Name, int Value)[] values) =>
        values.ToDictionary(v => Program.Stats.Key(v.Name)!.Value, v => v.Value);

    public static IEnumerable<object[]> Percentages() => Enumerable.Range(0, 46).Select(value => new object[] { value });

    [Theory]
    [MemberData(nameof(Percentages))]
    public void Default_skill_and_bow_packets_keep_fixed_hundred_bounds_and_fractional_mana_damage(int percent)
    {
        var stats = Values((RageDamage, percent), ("attack_minimum_added_chaos_damage", 100),
            ("attack_maximum_added_chaos_damage", 100), ("attack_minimum_added_lightning_damage", 1),
            ("attack_maximum_added_lightning_damage", 2), ("maximum_mana", 1350), (DamageStats.MaxManaAddedLightning, 5));
        var weapon = new SkillCatalog.Unarmed(1000, 100, 100, 8);
        var packets = new[]
        {
            Program.AttackHit(weapon, stats, melee: true),
            AttackSkillHit.Resolve(weapon, stats, [], Program.Stats, 10000, melee: true),
            OrdinaryBowShot.Capture(weapon, stats, [], Program.Stats, 1000, 0, 150).Hit
        };
        foreach (var hit in packets)
        {
            foreach (var type in new[] { DamageType.Physical, DamageType.Chaos })
            {
                Assert.Equal(new DamageRange(100 + percent, 100 + percent), hit[type]);
                Assert.Equal(100 + percent, hit[type].Roll(new Random(1)));
            }
            Assert.Equal((double)(68.5m * (100 + percent) / 100), hit[DamageType.Lightning].Min, 12);
            Assert.Equal((double)(69.5m * (100 + percent) / 100), hit[DamageType.Lightning].Max, 12);
            var panel = PanelStats.HandDamage(hit, "main_hand").ToDictionary(s => s.Stat, s => s.Value);
            Assert.Equal(100 + percent, panel["main_hand_minimum_chaos_damage"]);
            Assert.Equal(100 + percent, panel["main_hand_maximum_physical_damage"]);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(15, false)]
    [InlineData(25, false)]
    [InlineData(29, false)]
    [InlineData(45, false)]
    [InlineData(0, true)]
    [InlineData(15, true)]
    [InlineData(25, true)]
    [InlineData(29, true)]
    [InlineData(45, true)]
    public void Rage_follows_full_added_damage_and_charge_composition_with_the_original_melee_context(int percent, bool melee)
    {
        var stats = Values((RageDamage, percent), ("attack_minimum_added_chaos_damage", 100),
            ("attack_maximum_added_chaos_damage", 100), ("maximum_mana", 1350), (DamageStats.MaxManaAddedLightning, 5),
            ("melee_damage_+%", 50), ("object_inherent_damage_+%_final_per_frenzy_charge", 4),
            ("current_frenzy_charges", 3));
        var hit = Program.AttackHit(new(1000, 0, 0, 8), stats, melee);
        decimal meleeFactor = melee ? 1.5m : 1m;
        decimal chaos = 100 * meleeFactor * 1.12m * (100 + percent) / 100;
        decimal lightning = 67.5m * meleeFactor * 1.12m * (100 + percent) / 100;
        Assert.Equal((double)chaos, hit[DamageType.Chaos].Min, 11);
        Assert.Equal((double)lightning, hit[DamageType.Lightning].Max, 11);
        Assert.Equal((int)decimal.Floor(chaos), PanelStats.HandDamage(hit, "main_hand")
            .Single(s => s.Stat == "main_hand_minimum_chaos_damage").Value);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(29)]
    public void Converted_skill_and_bow_packets_keep_added_gain_as_extra_and_fractional_bounds(int percent)
    {
        var stats = Values((RageDamage, percent), ("attack_minimum_added_chaos_damage", 100),
            ("attack_maximum_added_chaos_damage", 100), ("maximum_mana", 1350), (DamageStats.MaxManaAddedLightning, 5),
            ("physical_damage_%_to_convert_to_fire", 25), ("non_chaos_damage_to_add_as_chaos_damage_%", 10));
        var weapon = new SkillCatalog.Unarmed(1000, 100, 100, 8);
        var hits = new[]
        {
            AttackSkillHit.Resolve(weapon, stats, [], Program.Stats, 10000, melee: false),
            OrdinaryBowShot.Capture(weapon, stats, [], Program.Stats, 1000, 0, 150).Hit
        };
        foreach (var hit in hits)
        {
            Assert.Equal((double)(75m * (100 + percent) / 100), hit[DamageType.Physical].Min, 12);
            Assert.Equal((double)(25m * (100 + percent) / 100), hit[DamageType.Fire].Min, 12);
            Assert.Equal((double)(67.5m * (100 + percent) / 100), hit[DamageType.Lightning].Min, 12);
            // Existing staged gain-as-extra: 100 added + 10 physical + 2.5 converted fire + 6.75 lightning.
            Assert.Equal((double)(119.25m * (100 + percent) / 100), hit[DamageType.Chaos].Min, 12);
        }
    }

    [Theory]
    [InlineData(int.MinValue, 0d)]
    [InlineData(-101, 0d)]
    [InlineData(-100, 0d)]
    [InlineData(-99, 1d)]
    [InlineData(-1, 99d)]
    [InlineData(0, 100d)]
    [InlineData(15, 115d)]
    [InlineData(29, 129d)]
    [InlineData(int.MaxValue, 2147483747d)]
    public void Scaling_promotes_signed_percent_addition_and_never_rounds_or_caps_a_damage_range(int percent, double expected)
    {
        var original = DamagePacket.Of(DamageType.Chaos, 100, 100);
        var result = RageStats.ApplyAttack(original, name => name == RageDamage ? percent : 0);
        Assert.Equal(new DamageRange(expected, expected), result[DamageType.Chaos]);
        Assert.Equal(new DamageRange(100, 100), original[DamageType.Chaos]);
    }

    [Theory]
    [InlineData(-99)]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(29)]
    [InlineData(67)]
    public void Scaling_keeps_subinteger_ranges(int percent)
    {
        var result = RageStats.ApplyAttack(DamagePacket.Of(DamageType.Cold, .125, .375), _ => percent);
        Assert.Equal((double)(.125m * (100 + percent) / 100), result[DamageType.Cold].Min, 14);
        Assert.Equal((double)(.375m * (100 + percent) / 100), result[DamageType.Cold].Max, 14);
    }

    [Fact]
    public void Saturated_effective_Rage_is_consumed_without_overflowing_the_percentage_numerator()
    {
        var rage = new RageState();
        var inputs = new RageState.Inputs(MaximumBonus: int.MaxValue, EffectPercent: int.MaxValue, NoInherentLoss: true);
        rage.Gain(long.MaxValue, 0, inputs);
        var value = rage.Read(0, inputs);
        Assert.Equal(int.MaxValue, value.Effective);
        var stats = RageStats.Display(value, inputs).ToDictionary(s => s.Name, s => s.Value);
        var result = RageStats.ApplyAttack(DamagePacket.Of(DamageType.Chaos, 100, 100), stats.GetValueOrDefault);
        Assert.Equal(2147483747d, result[DamageType.Chaos].Min);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Zero_Rage_preserves_the_merged_main_packet_bits_through_derived_increases_charges_and_conversion(bool melee)
    {
        var stats = Values((RageDamage, 0), (DamageStats.SpellDamageIncreasesApplyToAttacks150, 1),
            ("spell_damage_+%", 1), ("attack_damage_+%", 4), ("melee_damage_+%", 50),
            ("attack_minimum_added_chaos_damage", 100), ("attack_maximum_added_chaos_damage", 103),
            ("maximum_mana", 1350), (DamageStats.MaxManaAddedLightning, 5),
            ("physical_damage_%_to_convert_to_fire", 25), ("non_chaos_damage_to_add_as_chaos_damage_%", 10),
            ("object_inherent_damage_+%_final_per_frenzy_charge", 4), ("current_frenzy_charges", 3));
        var weapon = new SkillCatalog.Unarmed(1000, 100, 101, 8);
        int Get(string name) => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        var conversion = DamageStats.Conversion(stats, Program.Stats, attack: true);
        var baseline = DamageStats.Added(weapon.HitDamage(stats), Get, spell: false, melee)
            .Scale(ChargeEffects.DamageMore(Get));
        var converted = baseline.Convert(conversion);
        var actual = Program.AttackHitConverted(weapon, stats, melee, conversion);
        var unconverted = Program.AttackHit(weapon, stats, melee);
        foreach (var type in DamageTypes.ConversionOrder)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(converted[type].Min), BitConverter.DoubleToInt64Bits(actual[type].Min));
            Assert.Equal(BitConverter.DoubleToInt64Bits(converted[type].Max), BitConverter.DoubleToInt64Bits(actual[type].Max));
            Assert.Equal(baseline[type], unconverted[type]);
        }
    }
}
