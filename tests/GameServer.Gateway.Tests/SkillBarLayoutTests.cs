namespace GameServer.Gateway.Tests;

public class SkillBarLayoutTests
{
    const string Attack = "PlayerMelee";
    static Func<string, bool> Having(params string[] skills) => e => e == Attack || e.StartsWith("Action/") || skills.Contains(e);

    [Fact]
    public void A_new_bar_gets_the_default_attack_on_the_left_mouse_button_and_the_gem_skill_on_the_right()
    {
        var added = SkillBarLayout.Fill(new Dictionary<int, string>(), Attack, ["HolyStrike"], Having("HolyStrike"));

        Assert.Equal([(0, Attack), (2, "HolyStrike")], added);
    }

    [Fact]
    public void A_new_survivors_bar_gets_move_on_the_left_mouse_button_the_gem_skill_on_the_right_and_the_attack_next()
    {
        var added = SkillBarLayout.Fill(new Dictionary<int, string>(), Attack, ["HolyStrike"], Having("HolyStrike"), moveOnLeft: true);

        Assert.Equal([(0, SkillBarLayout.Move), (2, "HolyStrike"), (3, Attack)], added);
    }

    [Fact]
    public void A_new_survivors_bar_without_gems_gets_the_attack_on_the_right_mouse_button()
    {
        var added = SkillBarLayout.Fill(new Dictionary<int, string>(), Attack, [], Having(), moveOnLeft: true);

        Assert.Equal([(0, SkillBarLayout.Move), (2, Attack)], added);
    }

    [Fact]
    public void Slots_the_player_set_stay_and_new_skills_take_the_next_empty_ones()
    {
        // Fireball was moved to Q; "Move only" is on the left mouse button.
        var bar = new Dictionary<int, string> { [0] = "Action/Move", [3] = "Fireball" };

        var added = SkillBarLayout.Fill(bar, Attack, ["Fireball", "RighteousFire"], Having("Fireball", "RighteousFire"));

        Assert.Equal([(2, "RighteousFire")], added);
    }

    [Fact]
    public void Skills_already_on_the_bar_or_a_full_fill_order_add_nothing()
    {
        var bar = Enumerable.Range(0, 13).ToDictionary(i => i, i => i == 0 ? Attack : $"Skill{i}");
        var have = Having([.. bar.Values]);

        Assert.Empty(SkillBarLayout.Fill(bar, Attack, ["Skill2", "Skill5"], have));
        Assert.Empty(SkillBarLayout.Fill(bar, Attack, ["NewSkill"], have));
    }

    [Fact]
    public void A_cleared_slot_or_one_whose_gem_left_counts_as_empty()
    {
        // The right mouse button held Heavy Strike, whose gem is gone; Q was cleared.
        var bar = new Dictionary<int, string> { [0] = Attack, [2] = "HeavyStrike", [3] = "" };

        var added = SkillBarLayout.Fill(bar, Attack, ["Cleave", "GroundSlam"], Having("Cleave", "GroundSlam"));

        Assert.Equal([(2, "Cleave"), (3, "GroundSlam")], added);
    }

    [Fact]
    public void A_cleared_left_mouse_button_stays_cleared()
    {
        var added = SkillBarLayout.Fill(new Dictionary<int, string> { [0] = "" }, Attack, [], Having());

        Assert.Empty(added);
    }
}
