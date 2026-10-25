namespace GameServer.Gateway.Tests;

// The attack1 contact keyframe the melee hit waits for (AnimationContacts.AttackContact): the weapon's clip, its shield or
// dual-wield variant, any attack1 of that weapon, else the default.
public class AnimationContactsTests : IDisposable
{
    private const string Ao = "Metadata/Characters/Str/Str.ao";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"anim-contacts-{Guid.NewGuid():N}");

    public AnimationContactsTests()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "animation-contacts.json"), """
            { "Metadata/Characters/Str/Str.ao": { "amd": "rig.amd", "animations": {
                "attack1_unarmed": [833, 333],
                "attack1_sword": [1000, 500],
                "attack1_sword_shield": [1000, 300],
                "attack1_claw_claw": [833, 299],
                "attack1_staff": [1000, 719],
                "attack1_dagger_alternate": [1000, 250]
            } } }
            """);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private double Contact(WeaponProfile weapon) => new AnimationContacts(_folder).AttackContact(Ao, weapon);

    [Fact]
    public void The_weapon_picks_its_attack1_clip()
    {
        Assert.Equal(333.0 / 833, Contact(new WeaponProfile(false, false, false, false, false, Unarmed: true)), 6);
        Assert.Equal(0.5, Contact(new WeaponProfile(false, false, false, false, false, Sword: true)), 6);
        Assert.Equal(0.719, Contact(new WeaponProfile(false, false, true, false, false, TwoHandMelee: true)), 6);
    }

    [Fact]
    public void A_shield_or_a_pair_picks_its_variant()
    {
        Assert.Equal(0.3, Contact(new WeaponProfile(true, false, false, false, false, Sword: true)), 6);
        Assert.Equal(299.0 / 833, Contact(new WeaponProfile(false, true, false, false, true, Claw: true)), 6);
    }

    [Fact]
    public void Another_attack1_of_the_weapon_then_the_default()
    {
        Assert.Equal(0.25, Contact(new WeaponProfile(false, false, false, false, false, Dagger: true)), 6);
        // No clip for axes in the rigs, and no such animated object: the default.
        Assert.Equal(AnimationContacts.DefaultAttackContact, Contact(new WeaponProfile(false, false, false, false, false, Axe: true)));
        Assert.Equal(AnimationContacts.DefaultAttackContact,
            new AnimationContacts(_folder).AttackContact("Metadata/None.ao", new WeaponProfile(false, false, false, false, false, Unarmed: true)));
    }
}
