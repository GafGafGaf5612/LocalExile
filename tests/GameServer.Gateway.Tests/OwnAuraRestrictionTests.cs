using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class OwnAuraRestrictionTests
{
    internal static readonly string[] Ordinary = ["PurityOfElements", "PurityOfFire", "PurityOfIce",
        "PurityOfLightning", "Clarity", "Vitality", "Precision", "Haste", "Determination", "Grace",
        "Discipline", "Anger", "Hatred", "Wrath", "Zealotry", "Malevolence", "Envy"];

    [Fact]
    public void Actual_Aura_metadata_and_native_flags_do_not_classify_reservation_or_buff_names()
    {
        foreach (string id in Ordinary.Concat(["Pride", "FleshAndStone", "VaalHaste", "WarBanner"]))
        {
            int effect = Program.Skills.EffectRow(id);
            Assert.True(Program.Skills.IsAura(effect), id);
            var stats = Program.Skills.EffectStats(effect, 1).ToDictionary(s => s.Item1, s => s.Item2);
            Assert.Equal(1, stats[OwnAuraRestriction.BaseAura]);
            Assert.Equal(1, stats[OwnAuraRestriction.Aura]);
        }
        foreach (string id in new[] { "HeraldOfAsh", "HeraldOfIce", "HeraldOfThunder", "ArcticArmour",
            "TempestShield", "PetrifiedBlood", "BloodAndSand", "RighteousFire", "MoltenShell", "EnduringCry",
            "Fireball", "PlayerMelee" })
        {
            int effect = Program.Skills.EffectRow(id);
            Assert.False(Program.Skills.IsAura(effect), id);
            Assert.DoesNotContain(Program.Skills.EffectStats(effect, 1),
                s => s.Item1 is OwnAuraRestriction.BaseAura or OwnAuraRestriction.Aura && s.Item2 != 0);
        }
        Assert.Null(Program.Skills.Reservation(Program.Skills.EffectRow("VaalHaste"), 1));
        Assert.NotNull(Program.Skills.Reservation(Program.Skills.EffectRow("BloodAndSand"), 1));
        Assert.False(Program.Skills.IsAura(-1));
        Assert.False(Program.Skills.IsAura(int.MaxValue));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(-1, 0, 1)]
    [InlineData(0, 1, 1)]
    [InlineData(0, -1, 1)]
    [InlineData(-1, 1, 1)]
    public void Native_actor_OR_uses_signed_nonzero_inputs(int basis, int keystone, int expected)
    {
        var source = new Dictionary<uint, int>
            { [OwnAuraRestriction.BaseDisabled] = basis, [OwnAuraRestriction.HeraldOfDoom] = keystone };
        Assert.Equal(expected, OwnAuraRestriction.Value(source, new Dictionary<uint, int>()));
        Assert.Equal(0, OwnAuraRestriction.Value(source,
            new Dictionary<uint, int> { [OwnAuraRestriction.BaseDisabled] = 0,
                [OwnAuraRestriction.HeraldOfDoom] = 0 }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    public void Direct_final_virtual_developer_replacement_deliberately_wins_including_zero(int final)
    {
        var source = new Dictionary<uint, int>
            { [OwnAuraRestriction.BaseDisabled] = 1, [OwnAuraRestriction.HeraldOfDoom] = -1 };
        Assert.Equal(final, OwnAuraRestriction.Value(source,
            new Dictionary<uint, int> { [OwnAuraRestriction.Disabled] = final }));
    }

    [Theory]
    [InlineData(1, 0, 0, true)]
    [InlineData(-1, 0, 0, true)]
    [InlineData(0, -1, 1, true)]
    [InlineData(0, 1, 0, false)]
    [InlineData(0, 0, 1, false)]
    [InlineData(0, 0, 0, false)]
    public void Native_skill_classifier_keeps_the_curse_conjunction(int aura, int apply, int curse, bool expected)
    {
        var skill = new Dictionary<uint, int> { [3004] = aura, [2730] = apply, [2532] = curse, [15798] = -3 };
        Assert.Equal(expected, OwnAuraRestriction.IsAura(skill));
        OwnAuraRestriction.PublishSkill(skill, -1);
        Assert.Equal(expected ? 1 : 0, skill[15801]);
        Assert.Equal(expected ? 1 : -3, skill[15798]);
        OwnAuraRestriction.PublishSkill(skill, 0);
        Assert.Equal(expected ? 1 : -3, skill[15798]); // Never erase an independently supplied disable reason.
    }

    [Fact]
    public void Type_joins_ignore_misleading_names_invalid_references_and_missing_metadata()
    {
        string folder = Path.Combine(Path.GetTempPath(), "own-aura-types-" + Guid.NewGuid().ToString("N"));
        string tables = Path.Combine(folder, "tables");
        Directory.CreateDirectory(tables);
        void Write(string name, string value) => File.WriteAllText(Path.Combine(tables, name + ".json"), value);
        try
        {
            Write("ActiveSkillType", """[{"Id":"Buff"},{"Id":"Aura"}]""");
            Write("ActiveSkills", """
                [{"ActiveSkillTypes":[1]},{"Id":"Haste","ActiveSkillTypes":[0]},
                 {"ActiveSkillTypes":[null,"1",-1,99]},{"ActiveSkillTypes":null},{}]
                """);
            Write("GrantedEffects", """
                [{"Id":"Arbitrary","ActiveSkill":0},{"Id":"Haste","ActiveSkill":1},
                 {"ActiveSkill":2},{"ActiveSkill":3},{"ActiveSkill":4},{"ActiveSkill":null},
                 {"ActiveSkill":-1},{"ActiveSkill":99},{"ActiveSkill":"0"},{}]
                """);
            Assert.True(new SkillCatalog(folder).IsAura(0));
            for (int i = 1; i < 11; i++) Assert.False(new SkillCatalog(folder).IsAura(i));
            Write("ActiveSkillType", """[{"Id":"Buff"}]""");
            Assert.False(new SkillCatalog(folder).IsAura(0));
            File.Delete(Path.Combine(tables, "ActiveSkillType.json"));
            Assert.False(new SkillCatalog(folder).IsAura(0));
        }
        finally
        {
            foreach (string name in new[] { "ActiveSkillType", "ActiveSkills", "GrantedEffects" })
                File.Delete(Path.Combine(tables, name + ".json"));
            Directory.Delete(tables);
            Directory.Delete(folder);
        }
    }

    [Fact]
    public void Synthetic_equipment_sources_obey_active_requirements_and_per_key_replacement()
    {
        // No real Mods row supplies 21148. Isolate a single synthetic modifier, never changing production tables.
        string folder = Path.Combine(Path.GetTempPath(), "own-aura-mod-" + Guid.NewGuid().ToString("N"));
        string tables = Path.Combine(folder, "tables");
        Directory.CreateDirectory(tables);
        try
        {
            File.Copy("data/tables/Stats.json", Path.Combine(tables, "Stats.json"));
            var mod = JsonNode.Parse(File.ReadAllText("data/tables/Mods.json"))!.AsArray()[0]!.DeepClone();
            mod["Id"] = "OwnAuraRestrictionFixture";
            mod["StatsKey1"] = 21147;
            mod["StatsKey2"] = null;
            File.WriteAllText(Path.Combine(tables, "Mods.json"), new JsonArray(mod).ToJsonString());
            var mods = new ModCatalog(folder);
            var entry = mods.All.Single();
            var item = ItemCatalog.Create(Program.Items.Find("Simple Robe")!) with
                { Mods = [new(ModSection.Explicit, entry.Hash, [-1])] };
            var worn = new[] { new ItemModStats.Worn(1, item, 50, 0, 0, 1) };
            var inactive = ItemModStats.ResolveEquipment(worn, (14, 14, 32), 100, mods);
            Assert.Contains(1u, inactive.Inactive);
            var none = ItemModStats.Global(inactive.Active, mods, Program.Stats);
            Assert.Equal(0, OwnAuraRestriction.Value(none, new Dictionary<uint, int>()));
            var active = ItemModStats.ResolveEquipment(worn, (100, 14, 32), 100, mods);
            Assert.Empty(active.Inactive);
            var source = ItemModStats.Global(active.Active, mods, Program.Stats);
            Assert.Equal(-1, source[21148]);
            Assert.Equal(1, OwnAuraRestriction.Value(source, new Dictionary<uint, int>()));
            Assert.Equal(0, OwnAuraRestriction.Value(source, new Dictionary<uint, int> { [21148] = 0 }));
        }
        finally
        {
            File.Delete(Path.Combine(tables, "Stats.json"));
            File.Delete(Path.Combine(tables, "Mods.json"));
            Directory.Delete(tables);
            Directory.Delete(folder);
        }
    }
}
