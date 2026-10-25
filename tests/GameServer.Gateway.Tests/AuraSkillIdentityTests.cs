public class AuraSkillIdentityTests
{
    [Fact]
    public void Effect_action_hash_follows_both_references_and_keeps_the_full_hash()
    {
        string dir = Path.Combine(Path.GetTempPath(), "aura-actions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "tables"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "tables", "GrantedEffects.json"),
                """[{"ActiveSkill":1},{"ActiveSkill":null},{"ActiveSkill":0},{"ActiveSkill":99}]""");
            File.WriteAllText(Path.Combine(dir, "tables", "ActiveSkills.json"),
                """[{"ActionType":1},{"ActionType":0}]""");
            File.WriteAllText(Path.Combine(dir, "tables", "ActionTypes.json"),
                """[{"HASH16":49594},{"HASH16":65535}]""");
            var skills = new SkillCatalog(dir);
            Assert.Equal((ushort)49594, skills.ActionHash(0));
            Assert.Equal(ushort.MaxValue, skills.ActionHash(2));
            Assert.Null(skills.ActionHash(1));
            Assert.Null(skills.ActionHash(3));
            Assert.Null(skills.ActionHash(-1));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
