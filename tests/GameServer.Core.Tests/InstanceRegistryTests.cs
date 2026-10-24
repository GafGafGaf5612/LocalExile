using GameServer.Core.World;

namespace GameServer.Core.Tests;

public class InstanceRegistryTests
{
    const ushort Town = 0x235c, Coast = 0x7ad2;

    sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void A_town_has_one_instance_for_everyone()
    {
        var reg = new InstanceRegistry();
        var a = reg.Enter(Town, "A");
        var b = reg.Enter(Town, "B");
        var c = reg.EnterNew(Town, "C");
        Assert.Same(a, b);
        Assert.Same(a, c);
        Assert.Single(reg.List(Town));
        Assert.True(a.IsTown);
        Assert.Equal(1u, a.Seed);
    }

    [Fact]
    public void Non_town_areas_can_have_many_instances_and_a_plain_entry_returns_to_the_previous_one()
    {
        var reg = new InstanceRegistry { SeedFor = _ => 7 };
        var first = reg.Enter(Coast, "A");
        reg.Enter(Town, "A");                 // leaves the Coast instance (it stays alive, empty)
        var again = reg.Enter(Coast, "A");
        Assert.Same(first, again);
        var fresh = reg.EnterNew(Coast, "A");
        Assert.NotSame(first, fresh);
        Assert.Equal(2, reg.List(Coast).Count);
        Assert.Equal(7u, fresh.Seed);
        Assert.Same(first, reg.EnterExisting(first.Id, "B"));
        Assert.Contains("B", first.Players);
        Assert.Contains("A", fresh.Players);
    }

    [Fact]
    public void A_new_instance_can_be_given_its_seed_but_a_town_keeps_seed_1()
    {
        var reg = new InstanceRegistry { SeedFor = _ => 7 };
        Assert.Equal(3117172586u, reg.EnterNew(Coast, "A", 3117172586).Seed);
        Assert.Equal(7u, reg.EnterNew(Coast, "A").Seed);
        Assert.Equal(1u, reg.EnterNew(Town, "A", 42).Seed);
    }

    [Fact]
    public void An_empty_instance_expires_after_its_lifetime()
    {
        var clock = new FakeClock();
        var reg = new InstanceRegistry(clock) { Lifetime = TimeSpan.FromMinutes(6) };
        var coast = reg.Enter(Coast, "A");
        reg.Leave(coast.Id, "A");
        clock.Now += TimeSpan.FromMinutes(5);
        Assert.Single(reg.List(Coast));                       // still kept
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.Empty(reg.List(Coast));                        // gone after 6 minutes
        Assert.Null(reg.EnterExisting(coast.Id, "A"));
        Assert.NotSame(coast, reg.Enter(Coast, "A"));         // a new one
    }

    [Fact]
    public void An_occupied_instance_never_expires()
    {
        var clock = new FakeClock();
        var reg = new InstanceRegistry(clock);
        var coast = reg.Enter(Coast, "A");
        clock.Now += TimeSpan.FromHours(1);
        Assert.Same(coast, reg.EnterExisting(coast.Id, "B"));
    }

    [Fact]
    public void Time_left_is_the_full_lifetime_while_occupied_then_counts_down()
    {
        var clock = new FakeClock();
        var reg = new InstanceRegistry(clock) { Lifetime = TimeSpan.FromMinutes(6) };
        var coast = reg.Enter(Coast, "A");
        clock.Now += TimeSpan.FromMinutes(10);
        Assert.Equal(TimeSpan.FromMinutes(6), reg.TimeLeft(coast));
        reg.Leave(coast.Id, "A");
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Equal(TimeSpan.FromMinutes(4), reg.TimeLeft(coast));
    }

    [Fact]
    public void A_restarted_gateway_empties_every_instance_and_starts_their_expiry()
    {
        // A Gateway that stopped mid-connection never sent "leave": its players stayed in their instances for good.
        var clock = new FakeClock();
        var reg = new InstanceRegistry(clock) { Lifetime = TimeSpan.FromMinutes(6) };
        var coast = reg.Enter(Coast, "A");
        var emptied = reg.Enter(Coast, "B");
        reg.Leave(emptied.Id, "B");
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.Equal(1, reg.LeaveAll());                      // only A was still inside
        Assert.Empty(coast.Players);
        Assert.Equal(TimeSpan.FromMinutes(6), reg.TimeLeft(coast));
        Assert.Equal(TimeSpan.FromMinutes(5), reg.TimeLeft(emptied)); // an empty one keeps its own expiry
        clock.Now += TimeSpan.FromMinutes(6);
        Assert.Empty(reg.List(Coast));
    }
}
