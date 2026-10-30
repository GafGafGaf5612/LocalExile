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

    [Fact]
    public void Makanga_adjacency_fields_are_loaded_from_the_override()
    {
        var honoured = TattooCatalogue.Find("Honoured Tattoo of the Makanga");
        Assert.NotNull(honoured);
        Assert.Equal(8, honoured.RequiresAdjacent);
        Assert.Equal(0, honoured.MaxAdjacent);

        var ngamahu = TattooCatalogue.Find("Tattoo of the Ngamahu Makanga");
        Assert.NotNull(ngamahu);
        Assert.Equal(0, ngamahu.RequiresAdjacent);
        Assert.Equal(1, ngamahu.MaxAdjacent);

        var hinekora = TattooCatalogue.Find("Tattoo of the Hinekora Makanga");
        Assert.NotNull(hinekora);
        Assert.Equal(7, hinekora.RequiresAdjacent);
        Assert.Equal(0, hinekora.MaxAdjacent);
    }

    [Fact]
    public void Honoured_Makanga_is_inactive_below_eight_allocated_neighbours()
    {
        TestSaves.Ensure();
        string character = "TattooMakanga" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Tattoo", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            // 3644 is a small Strength with eight neighbours.
            var neighbours = Program.OrdinaryPassives.Adjacent(3644, AscendancyFamily.Normal).Take(8).ToArray();
            Assert.Equal(8, neighbours.Length);
            conn.Tattoos[3644] = "Honoured Tattoo of the Makanga";
            conn.TattooRevision++;

            conn.Passives = [3644, .. neighbours.Take(7)];
            var inactive = Program.PoolsFor(conn, 20);
            conn.Passives = [3644, .. neighbours];
            conn.TattooRevision++;
            var active = Program.PoolsFor(conn, 20);

            Assert.Equal(inactive.FireMaxResistance, active.FireMaxResistance - 2);
            Assert.Equal(inactive.ColdMaxResistance, active.ColdMaxResistance - 2);
            Assert.Equal(inactive.LightningMaxResistance, active.LightningMaxResistance - 2);
            Assert.Equal(inactive.ChaosMaxResistance, active.ChaosMaxResistance - 2);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void Ngamahu_Makanga_is_inactive_above_one_allocated_neighbour()
    {
        TestSaves.Ensure();
        string character = "TattooMakangaMax" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Tattoo", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Tattoos[10829] = "Tattoo of the Ngamahu Makanga";
            conn.TattooRevision++;

            conn.Passives = [10829, 16167];
            var active = Program.PoolsFor(conn, 20);
            conn.Passives = [10829, 16167, 32091];
            conn.TattooRevision++;
            var inactive = Program.PoolsFor(conn, 20);

            Assert.Equal(inactive.FireMaxResistance + 1, active.FireMaxResistance);
        }
        finally { PlayerInventories.Forget(character); }
    }
}
