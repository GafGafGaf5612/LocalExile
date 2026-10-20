namespace GameServer.Gateway.Tests;

// The skill id of each socketed gem stays the same while the gem stays socketed: the client binds skill bar keys by skill
// id, so a gem leaving or arriving must not move another gem's skill (live 2026-10-20: taking Haste out of a wand moved
// Righteous Fire's keys).
public class GemSkillIdsTests
{
    static readonly ushort[] Pool = [0x101, 0x102, 0x103, 0x104, 0x105];
    const int Haste = 10, RighteousFire = 20, Fireball = 30;

    static GemSkillIds.Gem G(uint host, int socket, int effect) => new(host, socket, effect);

    [Fact]
    public void Gems_get_the_lowest_free_ids_in_order()
    {
        var ids = new GemSkillIds();

        Assert.Equal([(ushort)0x101, 0x102], ids.Assign([G(1, 0, Haste), G(1, 1, RighteousFire)], Pool));
    }

    [Fact]
    public void A_gem_leaving_keeps_the_other_gems_ids()
    {
        var ids = new GemSkillIds();
        ids.Assign([G(1, 0, Haste), G(1, 1, RighteousFire)], Pool);

        Assert.Equal([(ushort?)0x102], ids.Assign([G(1, 1, RighteousFire)], Pool));
    }

    [Fact]
    public void A_gem_moved_to_another_socket_keeps_its_id()
    {
        var ids = new GemSkillIds();
        ids.Assign([G(1, 0, Haste), G(1, 1, RighteousFire)], Pool);

        // Righteous Fire out of the wand and into the shield, Haste stays.
        Assert.Equal([(ushort?)0x101, 0x102], ids.Assign([G(1, 0, Haste), G(2, 0, RighteousFire)], Pool));
    }

    [Fact]
    public void A_new_gem_takes_a_free_id_and_never_another_gems()
    {
        var ids = new GemSkillIds();
        ids.Assign([G(1, 0, Haste), G(1, 1, RighteousFire)], Pool);
        ids.Assign([G(1, 1, RighteousFire)], Pool);

        // Fireball into the socket Haste left: Haste's old id is free again.
        Assert.Equal([(ushort?)0x101, 0x102], ids.Assign([G(1, 0, Fireball), G(1, 1, RighteousFire)], Pool));
    }

    [Fact]
    public void Two_gems_of_one_skill_have_their_own_ids_and_keep_them()
    {
        var ids = new GemSkillIds();
        Assert.Equal([(ushort?)0x101, 0x102], ids.Assign([G(1, 0, Haste), G(2, 0, Haste)], Pool));

        // The first one leaves: the second keeps its id.
        Assert.Equal([(ushort?)0x102], ids.Assign([G(2, 0, Haste)], Pool));
    }

    [Fact]
    public void A_gem_without_a_free_id_gets_none()
    {
        var ids = new GemSkillIds();

        Assert.Equal([(ushort?)0x101, null], ids.Assign([G(1, 0, Haste), G(1, 1, Fireball)], [0x101]));
    }
}
