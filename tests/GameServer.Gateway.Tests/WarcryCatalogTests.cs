using System.Text.Json;

namespace GameServer.Gateway.Tests;

public class WarcryCatalogTests
{
    [Fact]
    public void Actual_type_62_classifies_warcries_independently_of_spells_attacks_and_names()
    {
        using var types = JsonDocument.Parse(File.ReadAllBytes("data/tables/ActiveSkillType.json"));
        Assert.Equal("Warcry", types.RootElement[62].GetProperty("Id").GetString());
        foreach (string id in new[] { "EnduringCry", "RallyingCry", "IntimidatingCry", "InfernalCry" })
            Assert.True(Program.Skills.IsWarcry(Program.Skills.EffectRow(id)), id);
        foreach (string id in new[] { "Fireball", "PlayerMelee", "Haste", "RighteousFire" })
            Assert.False(Program.Skills.IsWarcry(Program.Skills.EffectRow(id)), id);
        Assert.False(Program.Skills.IsSpell(Program.Skills.EffectRow("EnduringCry")));
        Assert.False(Program.Skills.IsWarcry(-1));
        Assert.False(Program.Skills.IsWarcry(int.MaxValue));
    }

    [Fact]
    public void Typed_links_ignore_misleading_names_missing_types_and_invalid_references()
    {
        string folder = Path.Combine(Path.GetTempPath(), "warcry-types-" + Guid.NewGuid().ToString("N"));
        string tables = Path.Combine(folder, "tables");
        Directory.CreateDirectory(tables);
        void Write(string name, string json) => File.WriteAllText(Path.Combine(tables, name + ".json"), json);
        try
        {
            // Deliberately put Warcry at a different row: classification follows the actual type's identity.
            Write("ActiveSkillType", """[{"Id":"Spell"},{"Id":"Warcry"}]""");
            Write("ActiveSkills", """
                [{"Id":"ArbitraryTypedSkill","ActiveSkillTypes":[1]},
                 {"Id":"EnduringCry","ActiveSkillTypes":[0]},
                 {"ActiveSkillTypes":[null,"1",-1,99]},
                 {"ActiveSkillTypes":null},{}]
                """);
            Write("GrantedEffects", """
                [{"Id":"UnrelatedName","ActiveSkill":0},{"Id":"EnduringCry","ActiveSkill":1},
                 {"ActiveSkill":2},{"ActiveSkill":3},{"ActiveSkill":4},{"ActiveSkill":null},
                 {"ActiveSkill":-1},{"ActiveSkill":99},{"ActiveSkill":"0"},{}]
                """);
            var skills = new SkillCatalog(folder);
            Assert.True(skills.IsWarcry(0));
            for (int row = 1; row < 11; row++) Assert.False(skills.IsWarcry(row));
            Write("ActiveSkillType", """[{"Id":"Spell"}]""");
            Assert.False(new SkillCatalog(folder).IsWarcry(0));
            File.Delete(Path.Combine(tables, "ActiveSkillType.json"));
            Assert.False(new SkillCatalog(folder).IsWarcry(0));
        }
        finally
        {
            foreach (string name in new[] { "ActiveSkillType", "ActiveSkills", "GrantedEffects" })
                File.Delete(Path.Combine(tables, name + ".json"));
            Directory.Delete(tables);
            Directory.Delete(folder);
        }
    }
}
