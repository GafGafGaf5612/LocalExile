namespace GameServer.Gateway.Tests;

public class SkillAreaTests
{
    static Func<string, int> Stats(params (string Name, int Value)[] stats) =>
        name => stats.FirstOrDefault(s => s.Name == name).Value;

    [Fact]
    public void Without_area_stats_the_radius_is_the_base()
    {
        Assert.Equal(18, SkillArea.Radius(18, Stats(), spell: true));
    }

    [Fact]
    public void Increased_area_grows_the_radius_by_its_square_root()
    {
        // 100% increased area: √2 = 1.41, 18 × 1.41 = 25.38 → 25 (PoB calcRadius).
        Assert.Equal(25, SkillArea.Radius(18, Stats(("base_skill_area_of_effect_+%", 60), ("skill_area_of_effect_+%", 40)), spell: true));
        // Spell area only counts for spells.
        Assert.Equal(25, SkillArea.Radius(18, Stats(("spell_area_of_effect_+%", 100)), spell: true));
        Assert.Equal(18, SkillArea.Radius(18, Stats(("spell_area_of_effect_+%", 100)), spell: false));
    }

    [Fact]
    public void More_area_multiplies_and_flat_radius_adds_before_scaling()
    {
        // (1 + 0.44) × (1 - 0.3) = 1.008 → √ = 1.00 (floored to hundredths), on 18 + 2 = 20.
        Assert.Equal(20, SkillArea.Radius(18, Stats(("base_skill_area_of_effect_+%", 44),
            ("support_concentrated_effect_skill_area_of_effect_+%_final", -30), ("active_skill_base_radius_+", 2)), spell: true));
        // A radius "more" scales the radius itself: 18 × 1.5 = 27.
        Assert.Equal(27, SkillArea.Radius(18, Stats(("active_skill_area_of_effect_radius_+%_final", 50)), spell: true));
    }
}
