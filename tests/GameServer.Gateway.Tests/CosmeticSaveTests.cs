namespace GameServer.Gateway.Tests;

/// <summary>
/// A character's portal cosmetic is saved with it, so /cosmetic portal survives a relog. The save database is one
/// process-wide database for the test run (TestSaves), so every test uses its own character name.
/// </summary>
public class CosmeticSaveTests
{
    public CosmeticSaveTests() => TestSaves.Ensure();

    static string Character() => $"Cosmetic{Guid.NewGuid():N}"[..20];

    [Fact]
    public void A_saved_portal_cosmetic_comes_back_for_that_character_only()
    {
        string character = Character();
        CharacterSaves.SetPortalCosmetic(character, 6);
        Assert.Equal(6, CharacterSaves.PortalCosmetic(character));
        // No field at all for another character: the base effect.
        Assert.Equal(0, CharacterSaves.PortalCosmetic(Character()));
    }

    [Fact]
    public void Turning_the_cosmetic_off_drops_the_field()
    {
        string character = Character();
        CharacterSaves.SetPortalCosmetic(character, 6);
        CharacterSaves.SetPortalCosmetic(character, 0);
        Assert.Equal(0, CharacterSaves.PortalCosmetic(character));
        Assert.Null(CharacterSaves.Load(character)!.PortalCosmetic);   // as in a save from before the field existed
    }

    [Fact]
    public void Setting_a_cosmetic_keeps_the_rest_of_the_character()
    {
        string character = Character();
        CharacterSaves.SetSkillBar(character, 0, "Action/Move");   // the character's first save, as its area entry leaves one
        CharacterSaves.SetExperience(character, 12345);
        CharacterSaves.SetPortalCosmetic(character, 10);
        var save = CharacterSaves.Load(character)!;
        Assert.Equal(12345u, save.Experience);
        Assert.Equal((ushort)10, save.PortalCosmetic);
    }
}
