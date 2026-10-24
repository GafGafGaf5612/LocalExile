using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class DamageImmunitiesTests
{
    [Fact]
    public void Elemental_immunity_covers_fire_cold_and_lightning()
    {
        var immune = DamageImmunities.From(n => n == "elemental_damage_immunity" ? 1 : 0);
        Assert.Equal(3, immune.Count);
        Assert.Contains(DamageType.Fire, immune);
        Assert.Contains(DamageType.Cold, immune);
        Assert.Contains(DamageType.Lightning, immune);
        Assert.DoesNotContain(DamageType.Chaos, immune);
    }

    [Fact]
    public void Base_forms_map_to_their_damage_type()
    {
        var immune = DamageImmunities.From(n => n is "base_fire_immunity" or "chaos_immunity" or "physical_immunity" ? 1 : 0);
        Assert.Contains(DamageType.Fire, immune);
        Assert.Contains(DamageType.Chaos, immune);
        Assert.Contains(DamageType.Physical, immune);
        Assert.DoesNotContain(DamageType.Cold, immune);
    }

    [Fact]
    public void Display_stat_counts_too()
    {
        var immune = DamageImmunities.From(n => n == "cold_damage_immunity" ? 1 : 0);
        Assert.Equal([DamageType.Cold], immune);
    }

    [Fact]
    public void No_source_means_no_immunity() => Assert.Empty(DamageImmunities.From(_ => 0));
}
