namespace GameServer.Gateway.Tests;

public class TattooTests
{
    [Fact]
    public void The_catalogue_has_all_100_tattoos_and_the_expected_targets()
    {
        Assert.Equal(100, TattooCatalogue.All.Count);
        var fire = TattooCatalogue.Find("Tattoo of the Ngamahu Firewalker");
        Assert.NotNull(fire);
        Assert.Equal("Small Strength", fire.Target);
        Assert.Equal(0, fire.Override);
        Assert.Equal(0x5C2A, fire.OverrideHash);
        Assert.False(fire.Forbidden);
        Assert.False(fire.EventOnly);

        var forbidden = TattooCatalogue.Find("Forbidden Tattoo of the Ranger");
        Assert.NotNull(forbidden);
        Assert.Equal("Ascendancy Notable", forbidden.Target);
        Assert.True(forbidden.Forbidden);
        Assert.True(forbidden.EventOnly);

        var honourable = TattooCatalogue.Find("Honoured Tattoo of the Warlord");
        Assert.NotNull(honourable);
        Assert.Equal("Notable (+30 to Strength)", honourable.Target);
        Assert.True(honourable.DropDisabled);
    }

    [Fact]
    public void Every_pilot_tattoo_maps_to_at_least_one_client_stat()
    {
        Assert.NotEmpty(TattooStats.Names);
        foreach (string name in TattooStats.Names)
        {
            Assert.NotNull(TattooCatalogue.Find(name));
            Assert.NotNull(TattooStats.For(name));
        }
    }

    [Fact]
    public void A_fire_tattoo_replaces_the_small_strength_node_in_the_pools()
    {
        TestSaves.Ensure();
        string character = "TattooFire" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Tattoo", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            // 10221 is a 10-Strength small passive.
            conn.Passives = [10221];
            var without = Program.PoolsFor(conn, 20);
            conn.Tattoos[10221] = "Tattoo of the Ngamahu Firewalker";
            conn.TattooRevision++;
            var with = Program.PoolsFor(conn, 20);

            Assert.Equal(without.Strength - 10, with.Strength);
            Assert.Equal(without.FireResistance + 6, with.FireResistance);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void A_tattoo_only_replaces_its_target_type()
    {
        var strength = Program.Passives.Find(10221);
        Assert.NotNull(strength);
        Assert.True(Program.TargetMatches(strength, "Small Strength"));
        Assert.True(Program.TargetMatches(strength, "Small Attribute"));
        Assert.False(Program.TargetMatches(strength, "Small Dexterity"));
        Assert.False(Program.TargetMatches(strength, "Small Intelligence"));
    }
}
