using System.Text.Json;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class AttackPanelStatsTests
{
    static readonly Lazy<SkillCatalog> Skills = new(() => new(BenchCraftingTests.Data()));
    static readonly Lazy<StatCatalog> Stats = new(() => new(BenchCraftingTests.Data()));
    static Dictionary<uint, int> Values(params (string Name, int Value)[] values) =>
        values.ToDictionary(v => Stats.Value.Key(v.Name)!.Value, v => v.Value);
    static SkillCatalog.Unarmed Weapon(int min = 10, int max = 20) => new(714, min, max, 8);
    static DamagePacket Resolve(string effect, IReadOnlyDictionary<uint, int> character, SkillCatalog.Unarmed? weapon = null)
    {
        int row = Skills.Value.EffectRow(effect);
        return AttackSkillHit.Resolve(weapon ?? Weapon(), character, Skills.Value.EffectStats(row, 1),
            Stats.Value, Skills.Value.DamageEffectiveness(row, 1));
    }
    static Dictionary<string, int> Rows(string effect, DamagePacket hit, int aps = 140) =>
        AttackPanelStats.Compute(SkillHitPlan.ForSkill(Skills.Value, Skills.Value.EffectRow(effect), 1), hit, aps)
            .ToDictionary(r => r.Stat, r => r.Value);

    [Fact]
    public void Real_Burning_Arrow_uses_180_percent_effectiveness_and_full_skill_fire_conversion()
    {
        int effect = Skills.Value.EffectRow("BurningArrow");
        Assert.Equal(18000, Skills.Value.DamageEffectiveness(effect, 1));
        Assert.Contains(Skills.Value.EffectStats(effect, 1), s =>
            s.Item1 == Stats.Value.Key("skill_physical_damage_%_to_convert_to_fire") && s.Item2 == 100);
        var hit = Resolve("BurningArrow", Values());
        Assert.Equal(new DamageRange(18, 36), hit[DamageType.Fire]);
        Assert.Equal(new DamageRange(0, 0), hit[DamageType.Physical]);
        var rows = Rows("BurningArrow", hit);
        Assert.Equal(18, rows["main_hand_minimum_total_damage"]);
        Assert.Equal(36, rows["main_hand_maximum_fire_damage"]);
        Assert.Equal(0, rows["main_hand_maximum_physical_damage"]);
        Assert.Equal(2700, rows["display_hundred_times_damage_per_skill_use"]);
        Assert.Equal(3780, rows["hundred_times_damage_per_second"]);
        // A separately corrected APS value is consumed directly, without recomputing skill speed here.
        Assert.Equal(2646, Rows("BurningArrow", hit, 98)["hundred_times_damage_per_second"]);
    }

    [Fact]
    public void Real_Heavy_Strike_keeps_fractional_physical_damage_until_display_flooring()
    {
        Assert.Equal(12430, Skills.Value.DamageEffectiveness(Skills.Value.EffectRow("HeavyStrike"), 1));
        var hit = Resolve("HeavyStrike", Values(), Weapon(10, 10));
        Assert.Equal(12.43, hit.MinTotal, 6);
        Assert.Equal(12.43, hit.MaxTotal, 6);
        var rows = Rows("HeavyStrike", hit);
        Assert.Equal(12, rows["main_hand_minimum_physical_damage"]);
        Assert.Equal(12, rows["main_hand_maximum_total_damage"]);
        Assert.Equal(1243, rows["display_hundred_times_damage_per_skill_use"]);
        Assert.Equal(1740, rows["hundred_times_damage_per_second"]);
    }

    [Fact]
    public void Added_attack_damage_and_current_character_increases_are_scaled_before_conversion()
    {
        var player = Values(("damage_+%", 10), ("cold_damage_+%", 100),
            ("attack_minimum_added_cold_damage", 2), ("attack_maximum_added_cold_damage", 4),
            ("spell_minimum_added_fire_damage", 1000), ("spell_maximum_added_fire_damage", 2000));
        var hit = Resolve("BurningArrow", player);
        Assert.Equal(19.8, hit[DamageType.Fire].Min, 6);
        Assert.Equal(39.6, hit[DamageType.Fire].Max, 6);
        Assert.Equal(7.56, hit[DamageType.Cold].Min, 6);
        Assert.Equal(15.12, hit[DamageType.Cold].Max, 6);
        var rows = Rows("BurningArrow", hit);
        Assert.Equal(27, rows["main_hand_minimum_total_damage"]);
        Assert.Equal(54, rows["main_hand_maximum_total_damage"]);
        Assert.Equal(4104, rows["display_hundred_times_damage_per_skill_use"]);
    }

    [Fact]
    public void Character_conversion_wins_and_skill_damage_modifiers_do_not_become_panel_only_effects()
    {
        var character = Values(("skill_physical_damage_%_to_convert_to_fire", 25));
        var skill = Values(("skill_physical_damage_%_to_convert_to_fire", 100),
            ("damage_+%", 1000), ("attack_minimum_added_fire_damage", 1000), ("attack_maximum_added_fire_damage", 2000));
        var hit = AttackSkillHit.Resolve(Weapon(), character, skill.Select(kv => (kv.Key, kv.Value)), Stats.Value, 18000);
        Assert.Equal(new DamageRange(13.5, 27), hit[DamageType.Physical]);
        Assert.Equal(new DamageRange(4.5, 9), hit[DamageType.Fire]);
        Assert.Equal(27, hit.AverageTotal);
    }

    [Fact]
    public void Gain_as_extra_uses_the_same_converted_combat_packet()
    {
        var hit = Resolve("BurningArrow", Values(("physical_damage_%_to_add_as_chaos", 20)));
        Assert.Equal(new DamageRange(18, 36), hit[DamageType.Fire]);
        Assert.Equal(3.6, hit[DamageType.Chaos].Min, 6);
        Assert.Equal(7.2, hit[DamageType.Chaos].Max, 6);
        var rows = Rows("BurningArrow", hit);
        Assert.Equal(21, rows["main_hand_minimum_total_damage"]);
        Assert.Equal(43, rows["main_hand_maximum_total_damage"]);
        Assert.Equal(3240, rows["display_hundred_times_damage_per_skill_use"]);
    }

    [Fact]
    public void Creation_and_refresh_use_raw_character_inputs_instead_of_derived_weapon_ranges()
    {
        var character = Values(("melee_physical_damage_+%", 2));
        var rawBefore = new Dictionary<uint, int>(character);
        var weapon = Weapon(5, 9);
        // WeaponStats supplies the unrounded packet to the ordinary attack; its legacy fallback floors first.
        var ordinary = weapon.Stats(character, weapon.HitDamage(character)).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        Dictionary<uint, int> Panel(int staleDamage)
        {
            var final = new Dictionary<uint, int>(ordinary);
            final[StatKeys.MainHandMinTotal] = staleDamage;
            final[StatKeys.MainHandMaxTotal] = staleDamage;
            final[StatKeys.DisplayHundredTimesDamagePerSkillUse] = staleDamage;
            foreach (var (name, value) in Rows("BurningArrow", Resolve("BurningArrow", character, weapon), 98))
                final[Stats.Value.Key(name)!.Value] = value;
            return final;
        }
        var created = Panel(999);
        var refreshed = Panel(99999);
        Assert.Equal(created.OrderBy(kv => kv.Key), refreshed.OrderBy(kv => kv.Key));
        Assert.Equal(9, created[StatKeys.MainHandMinTotal]);
        Assert.Equal(16, created[StatKeys.MainHandMaxTotal]);
        Assert.Equal(1285, created[StatKeys.DisplayHundredTimesDamagePerSkillUse]);
        Assert.Equal(1259, created[StatKeys.HundredTimesDamagePerSecond]);
        Assert.Equal(rawBefore.OrderBy(kv => kv.Key), character.OrderBy(kv => kv.Key));
        Assert.Equal(714, ordinary[StatKeys.DisplayHundredTimesDamagePerSkillUse]); // Existing pre-crit 7 * 1.02.
    }

    [Fact]
    public void Absent_types_and_zero_hits_clear_all_inherited_mainhand_hit_fields()
    {
        var rows = Rows("BurningArrow", Resolve("BurningArrow", Values()));
        Assert.Equal(14, rows.Count);
        foreach (string type in new[] { "physical", "cold", "lightning", "chaos" })
        {
            Assert.Equal(0, rows[$"main_hand_minimum_{type}_damage"]);
            Assert.Equal(0, rows[$"main_hand_maximum_{type}_damage"]);
        }
        Assert.All(Rows("BurningArrow", DamagePacket.Empty), row => Assert.Equal(0, row.Value));
        var noRate = Rows("BurningArrow", Resolve("BurningArrow", Values()), -1);
        Assert.Equal(2700, noRate["display_hundred_times_damage_per_skill_use"]);
        Assert.Equal(0, noRate["hundred_times_damage_per_second"]);
    }

    [Theory]
    [InlineData("Fireball")]
    [InlineData("ScorchingRay")]
    [InlineData("RighteousFire")]
    [InlineData("EnduringCry")]
    public void Spells_and_nonattack_actions_keep_their_display_path(string effect) =>
        Assert.Empty(Rows(effect, DamagePacket.Of(DamageType.Fire, 100, 200)));

    [Fact]
    public void Critical_and_accuracy_inputs_do_not_weight_the_precrit_average()
    {
        var baseline = Rows("BurningArrow", Resolve("BurningArrow", Values()));
        var critical = Rows("BurningArrow", Resolve("BurningArrow", Values(
            ("main_hand_critical_strike_chance", 10000), ("main_hand_critical_strike_multiplier", 300),
            ("main_hand_chance_to_hit_%", 50))));
        Assert.Equal(baseline.OrderBy(kv => kv.Key), critical.OrderBy(kv => kv.Key));
    }

    [Theory]
    [InlineData("MH_TotalDamage", "total", 670, 671)]
    [InlineData("MH_PhysicalDamage", "physical", 279, 280)]
    [InlineData("MH_FireDamage", "fire", 267, 268)]
    [InlineData("MH_ColdDamage", "cold", 263, 264)]
    [InlineData("MH_LightningDamage", "lightning", 271, 272)]
    [InlineData("MH_ChaosDamage", "chaos", 275, 276)]
    public void Native_mainhand_range_fields_use_whole_numbers_and_existing_hand_spell_gates(
        string id, string type, uint minKey, uint maxKey)
    {
        using var table = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(BenchCraftingTests.Data(), "tables", "CharacterPanelStats.json")));
        var row = table.RootElement.EnumerateArray().Single(r => r.GetProperty("Id").GetString() == id);
        Assert.Equal(minKey, Stats.Value.Key($"main_hand_minimum_{type}_damage"));
        Assert.Equal(maxKey, Stats.Value.Key($"main_hand_maximum_{type}_damage"));
        Assert.Equal(new[] { minKey - 1, maxKey - 1 }, row.GetProperty("StatsKeys1").EnumerateArray().Select(v => v.GetUInt32()));
        Assert.Contains(7791, row.GetProperty("StatsKeys2").EnumerateArray().Select(v => v.GetInt32()));
        Assert.Equal(new[] { 437, 1674 }, row.GetProperty("StatsKeys3").EnumerateArray().Select(v => v.GetInt32()));
        Assert.Empty(row.GetProperty("__col8").EnumerateArray());
    }
}
