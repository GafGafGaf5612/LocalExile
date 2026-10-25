namespace GameServer.Gateway.Tests;

public class AuraSaveTests
{
    public AuraSaveTests() => TestSaves.Ensure();

    [Fact]
    public void Aura_toggles_persist_by_effect_without_losing_other_character_data()
    {
        string character = "Auras" + Guid.NewGuid().ToString("N");
        CharacterSaves.Write(character, new CharacterSaves.Save([], SkillBar: new() { [0] = "Grace" }, Experience: 12345));
        CharacterSaves.SetAuraActive(character, "Grace", true);
        CharacterSaves.SetAuraActive(character, "Determination", true);
        CharacterSaves.SetAuraActive(character, "Grace", true);
        var saved = CharacterSaves.Load(character)!;
        Assert.Equal(new[] { "Determination", "Grace" }, saved.ActiveAuras);
        Assert.Equal(12345u, saved.Experience);
        Assert.Equal("Grace", saved.SkillBar![0]);
        CharacterSaves.SetAuraActive(character, "Grace", false);
        Assert.Equal(new[] { "Determination" }, CharacterSaves.Load(character)!.ActiveAuras);
        CharacterSaves.SetAuraActive(character, "Determination", false);
        Assert.Empty(CharacterSaves.Load(character)!.ActiveAuras!);
        Assert.Null(CharacterSaves.Load(character + "other"));
    }

    [Fact]
    public void Restoring_requires_a_usable_gem_and_enough_mana_and_rebuilds_runtime_ids()
    {
        var pools = new CharacterStats.Pools(1, 50, 100, 2, 0);
        var grace = new AuraCatalog.Aura(0x201, 0, "Grace", "Grace", 10, 143, 0, 5000, SourceBuff: 11);
        var determination = grace with { SkillId = 0x202, EffectId = "Determination", Buff = 12, SourceBuff = 13 };
        var anger = grace with { SkillId = 0x203, EffectId = "Anger", Buff = 14, SourceBuff = 15 };
        var restored = AuraCatalog.Restore(["Missing", "Grace", "Grace", "Determination", "Anger"],
            [grace, determination, anger], pools);
        Assert.Equal(new[] { grace, determination }, restored);
        Assert.Empty(AuraCatalog.Restore(["Grace"], [], pools));
        Assert.Empty(AuraCatalog.Restore([], [grace], pools));
        Assert.Equal(4, restored.Sum(a => AuraCatalog.StartPackets(a, 0x1000, 1).Count()));
        Assert.Equal(100, AuraCatalog.RequiredMana(pools, restored));
    }
}
