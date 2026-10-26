namespace GameServer.Gateway.Tests;

public class AttackSpeedTests
{
    [Fact]
    public void Pob_increased_and_more_attack_speed_combine_into_the_client_percentage()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1,
            new CharacterStats.Bonus(AttackSpeedPct: 75, AttackSpeedMorePct: 10));

        // (1 + 75 / 100) * (1 + 10 / 100) = 1.925, i.e. the equivalent 93% increased attack speed.
        Assert.Equal(93, pools.AttackSpeedPct);
    }

    [Fact]
    public void Unarmed_uses_the_final_main_hand_attack_speed()
    {
        var player = new Dictionary<uint, int>
        {
            [StatKeys.AttackSpeedPct] = 1000,          // raw passives must not be counted again
            [StatKeys.MainHandAttackSpeedPct] = 93,    // Pools.AttackSpeedPct
        };

        var attack = SkillCatalog.Unarmed.Witch;
        Assert.Equal(93, attack.IncreasedAttackSpeed(player));
        Assert.Equal(833 / 1000.0 / 1.93, attack.AttackSeconds(player), 6);
    }

    [Fact]
    public void Unarmed_falls_back_to_its_ten_percent_when_no_final_value_is_sent()
    {
        Assert.Equal(10, SkillCatalog.Unarmed.Witch.IncreasedAttackSpeed(new Dictionary<uint, int>()));
    }

    [Fact]
    public void Unarmed_sends_the_character_panels_damage_values()
    {
        var stats = SkillCatalog.Unarmed.Witch.Stats(new Dictionary<uint, int>
        {
            [StatKeys.MainHandAttackSpeedPct] = 93,
        }).ToDictionary(s => s.Item1, s => s.Item2);

        // Witch unarmed 2-5 (average 3.5), 833 ms / 1.93 = 432 ms, 2.32 attacks/s.
        Assert.Equal(1, stats[StatKeys.SkillUsesMainHand]);
        Assert.Equal(232, stats[StatKeys.HundredTimesAttacksPerSecond]);
        Assert.Equal(811, stats[StatKeys.HundredTimesDamagePerSecond]);
        Assert.Equal(350, stats[StatKeys.DisplayHundredTimesDamagePerSkillUse]);
    }

    [Fact]
    public void Off_hand_sends_the_same_values_under_the_off_hand_keys()
    {
        var player = new Dictionary<uint, int> { [StatKeys.MainHandAttackSpeedPct] = 93 };
        var attack = SkillCatalog.Unarmed.Witch;
        var main = attack.Stats(player).ToDictionary(s => s.Item1, s => s.Item2);
        var off = attack.OffStats(player).ToDictionary(s => s.Item1, s => s.Item2);

        // The same weapon in the other hand: the same hit, attack time and reach, under the off_hand_… keys the client
        // sets the off-hand strike up from (issue #167: without them the off-hand weapon had no stats at all).
        Assert.Equal(main[StatKeys.MainHandMinPhysical], off[StatKeys.OffHandMinPhysical]);
        Assert.Equal(main[StatKeys.MainHandMaxPhysical], off[StatKeys.OffHandMaxPhysical]);
        Assert.Equal(main[StatKeys.MainHandMinTotal], off[StatKeys.OffHandMinTotal]);
        Assert.Equal(main[StatKeys.MainHandMaxTotal], off[StatKeys.OffHandMaxTotal]);
        Assert.Equal(main[StatKeys.MainHandAttackDurationMs], off[StatKeys.OffHandAttackDurationMs]);
        Assert.Equal(main[StatKeys.MainHandTotalAttackDurationMs], off[StatKeys.OffHandTotalAttackDurationMs]);
        Assert.Equal(main[StatKeys.MainHandAttackSpeedPct], off[StatKeys.OffHandAttackSpeedPct]);
        Assert.Equal(main[StatKeys.MainHandBaseMaxAttackDistance], off[StatKeys.OffHandBaseMaxAttackDistance]);
        Assert.Equal(main[StatKeys.MainHandMaxAttackDistance], off[StatKeys.OffHandMaxAttackDistance]);
        Assert.Equal(9, off.Count);
        Assert.DoesNotContain(StatKeys.MainHandAttackDurationMs, off.Keys);
    }

    [Fact]
    public void Off_hand_attack_speed_leaves_out_the_main_weapons_local_attack_speed()
    {
        // 75% increased, dual wielding's 10% more and a main-hand weapon with 10% local attack speed: CharacterStats sends
        // main_hand_attack_speed_+% 112 (1.75 × 1.1 × 1.1). The off-hand weapon's own local value is in its attack time,
        // so its hand gets 1.75 × 1.1 = 93, not the main weapon's 112.
        Assert.Equal(93, SkillCatalog.Unarmed.OffHandAttackSpeedPct(112, 10));
        Assert.Equal(93, SkillCatalog.Unarmed.OffHandAttackSpeedPct(93, 0));
        Assert.Equal(-99, SkillCatalog.Unarmed.OffHandAttackSpeedPct(-99, 50));

        var player = new Dictionary<uint, int> { [StatKeys.MainHandAttackSpeedPct] = 112 };
        var off = SkillCatalog.Unarmed.Witch.OffStats(player, mainHandLocalAttackSpeedPct: 10).ToDictionary(s => s.Item1, s => s.Item2);
        Assert.Equal(93, off[StatKeys.OffHandAttackSpeedPct]);
    }
}
