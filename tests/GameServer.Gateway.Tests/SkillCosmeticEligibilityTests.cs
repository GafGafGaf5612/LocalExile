namespace GameServer.Gateway.Tests;

public class SkillCosmeticEligibilityTests
{
    [Fact]
    public void Eligibility_respects_both_skill_groups_slot_type_and_optional_gem_and_class_restrictions()
    {
        string folder = Directory.CreateTempSubdirectory("skill-mtx-eligibility-").FullName;
        try
        {
            string tables = Directory.CreateDirectory(Path.Combine(folder, "tables")).FullName;
            void Write(string file, string json) => File.WriteAllText(Path.Combine(tables, file + ".json"), json);
            Write("MtxTypeGameSpecific", """
                [{"HASH32":101,"__col4":[27]},{"HASH32":102,"__col4":[27]},
                 {"HASH32":103,"__col4":[27]},{"HASH32":104,"__col4":[]},{"HASH32":106,"__col4":[27]}]
                """);
            Write("ItemVisualIdentity", "[]");
            Write("ItemVisualEffect", "[]");
            Write("ItemisedVisualEffect", """
                [{"Mtx":0,"__col8":[27],"EffectSlotType":0,"BaseItemTypes":[7,8],"ItemClasses":[2,3]},
                 {"Mtx":1,"__col8":[27],"EffectSlotType":1,"BaseItemTypes":[],"ItemClasses":[]},
                 {"Mtx":2,"__col8":[],"EffectSlotType":0,"BaseItemTypes":[],"ItemClasses":[]},
                 {"Mtx":3,"__col8":[27],"EffectSlotType":0,"BaseItemTypes":[],"ItemClasses":[]},
                 {"Mtx":4,"__col8":[27],"EffectSlotType":null,"BaseItemTypes":[],"ItemClasses":[]}]
                """);
            var effects = new CosmeticEffects(folder);
            var gem = new ItemCatalog.BaseType(7, "gem", "gem", 777, 1, 1, 0, [], 0) { ClassRow = 2 };
            Assert.True(effects.FitsSkill(101, gem, 0));
            Assert.True(effects.FitsSkill(101, gem with { Row = 8, ClassRow = 3 }, 0));
            Assert.False(effects.FitsSkill(101, gem with { Row = 9 }, 0));
            Assert.False(effects.FitsSkill(101, gem with { ClassRow = 4 }, 0));
            Assert.False(effects.FitsSkill(101, gem, 1));
            Assert.True(effects.FitsSkill(102, gem with { Row = 9, ClassRow = 4 }, 1));
            Assert.False(effects.FitsSkill(102, gem, 0));
            Assert.False(effects.FitsSkill(103, gem, 0));
            Assert.False(effects.FitsSkill(104, gem, 0));
            Assert.False(effects.FitsSkill(105, gem, 0));
            Assert.False(effects.FitsSkill(106, gem, 0));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
