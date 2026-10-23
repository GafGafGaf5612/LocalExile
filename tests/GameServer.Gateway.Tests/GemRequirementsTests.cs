namespace GameServer.Gateway.Tests;

// The level requirement a gem's level carries (GrantedEffectStatSetsPerLevel PlayerLevelReq) and the stall it puts on
// the gem's experience. Anchors from the client's tooltips: Haste level 1 requires character level 24 … 70 at 20,
// Clarity 10 … 70 (poedb, 3.26 data); Fireball level 20 requires 70 and level 4 requires 7 (checked live 2026-10-23:
// a character set to level 10 in a level 20 Fireball wand shows the gem red, "Requires level 70").
public class GemRequirementsTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    [Fact]
    public void A_gems_level_requirement_is_the_PlayerLevelReq_of_its_level()
    {
        var skills = new SkillCatalog(Data());
        int haste = skills.EffectRow("Haste"), clarity = skills.EffectRow("Clarity");

        Assert.Equal(24, skills.PlayerLevelReq(haste, 1));
        Assert.Equal(70, skills.PlayerLevelReq(haste, 20));
        Assert.Equal(10, skills.PlayerLevelReq(clarity, 1));
        Assert.Equal(70, skills.PlayerLevelReq(clarity, 20));
    }

    [Fact]
    public void The_level_stalls_at_the_first_level_the_character_cannot_meet()
    {
        var skills = new SkillCatalog(Data());
        int haste = skills.EffectRow("Haste");

        // Not even level 1 (24) is met: the gem is disabled and shows level 1.
        Assert.Equal(1, skills.MaxLevelAt(haste, 1));
        Assert.Equal(1, skills.MaxLevelAt(haste, 23));
        // Level 2's requirement is 27, level 5's 36, level 9's 48, level 10's 50.
        Assert.Equal(1, skills.MaxLevelAt(haste, 24));
        Assert.Equal(1, skills.MaxLevelAt(haste, 26));
        Assert.Equal(2, skills.MaxLevelAt(haste, 27));
        Assert.Equal(5, skills.MaxLevelAt(haste, 36));
        Assert.Equal(9, skills.MaxLevelAt(haste, 49));
        Assert.Equal(20, skills.MaxLevelAt(haste, 70));
        // Above the cap (bonus levels, corruption): the data's requirements carry on to level 100.
        Assert.Equal(40, skills.MaxLevelAt(haste, 100));
    }

    [Fact]
    public void Fireball_at_character_level_10_stalls_at_level_4()
    {
        var skills = new SkillCatalog(Data());
        int fireball = skills.EffectRow("Fireball");

        Assert.Equal(70, skills.PlayerLevelReq(fireball, 20));
        Assert.Equal(7, skills.PlayerLevelReq(fireball, 4));
        Assert.Equal(20, skills.MaxLevelAt(fireball, 70));
        Assert.Equal(4, skills.MaxLevelAt(fireball, 10));
    }

    [Fact]
    public void A_skill_without_a_requirement_table_does_not_stall()
    {
        var skills = new SkillCatalog(Data());

        Assert.Equal(40, skills.MaxLevelAt(-1, 1));
        Assert.Equal(0, skills.PlayerLevelReq(-1, 1));
    }
}
