namespace GameServer.Gateway.Tests;

public class ImpaleTests
{
    [Fact]
    public void An_impale_stores_a_tenth_of_the_hits_physical_damage()
    {
        var impale = new ImpaleState();
        Assert.True(impale.Apply(120));
        Assert.Equal(1, impale.Stacks);
        Assert.Equal(12, impale.Bonus, 6);
    }

    [Fact]
    public void A_stack_lasts_five_hits()
    {
        var impale = new ImpaleState();
        impale.Apply(100);
        for (int hit = 1; hit <= ImpaleState.HitsPerStack - 1; hit++)
        {
            impale.AfterHit();
            Assert.Equal(10, impale.Bonus, 6);
        }
        impale.AfterHit();
        Assert.Equal(0, impale.Stacks);
        Assert.Equal(0, impale.Bonus, 6);
    }

    [Fact]
    public void Five_stacks_add_half_the_impaling_hits_damage()
    {
        var impale = new ImpaleState();
        for (int i = 0; i < ImpaleState.MaxStacks; i++) Assert.True(impale.Apply(100));
        Assert.Equal(5, impale.Stacks);
        Assert.Equal(50, impale.Bonus, 6);
        // A sixth impale is refused while the five last.
        Assert.False(impale.Apply(100));
        Assert.Equal(5, impale.Stacks);
    }

    [Fact]
    public void A_hit_with_no_physical_damage_cannot_impale()
    {
        var impale = new ImpaleState();
        Assert.False(impale.Apply(0));
        Assert.Equal(0, impale.Stacks);
    }

    [Fact]
    public void A_new_impale_keeps_all_five_of_its_hits()
    {
        var impale = new ImpaleState();
        impale.Apply(100);
        // The hit that applied it has already used one of its own five hits in the attack path, so this is the second.
        for (int hit = 0; hit < ImpaleState.HitsPerStack - 1; hit++) impale.AfterHit();
        Assert.Equal(10, impale.Bonus, 6);
        impale.AfterHit();
        Assert.Equal(0, impale.Stacks);
    }

    [Fact]
    public void Clear_drops_every_stack()
    {
        var impale = new ImpaleState();
        impale.Apply(100);
        impale.Apply(100);
        impale.Clear();
        Assert.Equal(0, impale.Stacks);
        Assert.Equal(0, impale.Bonus, 6);
    }
}
