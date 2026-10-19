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
}
