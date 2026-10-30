using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// A skill's damage effectiveness (GrantedEffectStatSetsPerLevel DamageEffectiveness): the attack gem's "Deals X% of base
// Attack Damage", applied to the weapon's hit. Flicker Strike anchors it: 77.3% at level 1, 308.9% at 20 and 696% at 40
// in 3.29.3b. Reads the extracted game tables.
public class SkillEffectivenessTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly SkillCatalog Skills = new(Data());

    [Fact]
    public void An_attack_gems_effectiveness_is_its_levels_DamageEffectiveness()
    {
        int flicker = Skills.EffectRow("FlickerStrike");
        Assert.Equal(7730, Skills.DamageEffectiveness(flicker, 1));
        Assert.Equal(30890, Skills.DamageEffectiveness(flicker, 20));
        Assert.Equal(69600, Skills.DamageEffectiveness(flicker, 40));
    }

}
