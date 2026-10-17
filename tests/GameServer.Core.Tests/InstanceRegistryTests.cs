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
}
