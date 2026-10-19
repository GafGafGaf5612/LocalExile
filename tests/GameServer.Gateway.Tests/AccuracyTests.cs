namespace GameServer.Gateway.Tests;

public class AccuracyTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    [Fact]
    public void Base_accuracy_is_two_per_level_and_dexterity()
    {
        var level1 = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1);
        var level10 = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 10);

        // Witch dexterity 14: 2 * level + 2 * 14.
        Assert.Equal(30, level1.Accuracy);
        Assert.Equal(48, level10.Accuracy);
    }

    [Fact]
    public void Flat_and_increased_accuracy_are_applied()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 10,
            new CharacterStats.Bonus(Accuracy: 100, AccuracyPct: 50));

        // (2 * 10 + 2 * 14 + 100) * 1.50 = 222.
        Assert.Equal(222, pools.Accuracy);
        Assert.Equal(50, pools.AccuracyPct);
    }

    [Fact]
    public void Accuracy_mastery_conversions_are_applied()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 10,
            new CharacterStats.Bonus(Accuracy: 500, AccuracyPerLevel: -2, DexterityAccuracyTriple: true,
                AccuracyEqualsIntelligence: true));

        // 0 per level, 3 per dexterity (42), +500 flat, +32 intelligence = 574.
        Assert.Equal(574, pools.Accuracy);
    }

    [Fact]
    public void Accuracy_mastery_effect_has_its_flat_and_per_level_stats()
    {
        var tree = new PassiveTree(Data());
        var catalog = new StatCatalog(Data());
        var stats = tree.Stats([42533], new Dictionary<ushort, ushort> { [42533] = 9755 });

        Assert.Equal(500, stats[catalog.Key("accuracy_rating")!.Value]);
        Assert.Equal(-2, stats[catalog.Key("accuracy_rating_per_level")!.Value]);
    }

    [Fact]
    public void Monster_evasion_matches_the_PoB_table()
    {
        var monsters = new MonsterCatalog(Data());

        Assert.Equal(67, monsters.BaseEvasion(1));
        Assert.Equal(13445, monsters.BaseEvasion(100));
        Assert.Equal(76, monsters.BaseEvasiveEvasion(1));
        Assert.Equal(19964, monsters.BaseEvasiveEvasion(100));
    }
}
