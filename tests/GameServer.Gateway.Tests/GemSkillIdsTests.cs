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
    public void A_new_gem_takes_an_id_no_gem_had_before_one_a_gem_left()
    {
        var ids = new GemSkillIds();
        ids.Assign([G(1, 0, Haste), G(1, 1, RighteousFire)], Pool);
        ids.Assign([G(1, 1, RighteousFire)], Pool);

        // Fireball into the socket Haste left: Haste's id waits for Haste to come back.
        Assert.Equal([(ushort?)0x103, 0x102], ids.Assign([G(1, 0, Fireball), G(1, 1, RighteousFire)], Pool));
    }

    [Fact]
    public void A_gem_swapped_into_a_socket_never_takes_the_id_its_gem_gives_up()
    {
        var ids = new GemSkillIds();
        ids.Assign([G(1, 0, Haste)], Pool);

        // Fireball swapped with Haste in one move: Haste's id must go out as removed, so its aura ends and its key does
        // not cast Fireball.
        Assert.Equal([(ushort?)0x102], ids.Assign([G(1, 0, Fireball)], Pool));
    }

    [Fact]
    public void A_gem_moved_through_the_cursor_keeps_its_id()
    {
        var ids = new GemSkillIds();
        ids.Assign([G(1, 0, Haste), G(1, 1, RighteousFire)], Pool);

        // Haste out onto the cursor, Fireball into the wand meanwhile, then Haste into the shield.
        ids.Assign([G(1, 1, RighteousFire)], Pool);
        ids.Assign([G(1, 0, Fireball), G(1, 1, RighteousFire)], Pool);
        Assert.Equal([(ushort?)0x103, 0x102, 0x101],
            ids.Assign([G(1, 0, Fireball), G(1, 1, RighteousFire), G(2, 0, Haste)], Pool));
    }

    [Fact]
    public void Without_an_id_no_gem_had_a_new_gem_takes_the_one_that_left_longest_ago()
    {
        var ids = new GemSkillIds();
        ids.Assign([G(1, 0, Haste), G(1, 1, RighteousFire)], [0x101, 0x102]);
        ids.Assign([G(1, 1, RighteousFire)], [0x101, 0x102]);

        Assert.Equal([(ushort?)0x101, 0x102], ids.Assign([G(1, 0, Fireball), G(1, 1, RighteousFire)], [0x101, 0x102]));
    }

    [Fact]
    public void A_full_pool_gives_a_swapped_in_gem_no_id_until_the_next_update()
    {
        var ids = new GemSkillIds();
        ids.Assign([G(1, 0, Haste)], [0x101]);

        Assert.Equal([(ushort?)null], ids.Assign([G(1, 0, Fireball)], [0x101]));
        Assert.Equal([(ushort?)0x101], ids.Assign([G(1, 0, Fireball)], [0x101]));
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
