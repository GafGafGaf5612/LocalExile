namespace GameServer.Gateway.Tests;

public class FortificationSessionTests
{
    [Fact]
    public void One_presentation_snapshot_cannot_expire_halfway_through_its_buff_and_stat_records()
    {
        var session = new FortificationSession();
        session.State.GrantExplicitStacks(0, 20);
        using (session.Capture(5))
        {
            Assert.Equal(20, session.Read(5).Current);
            Assert.Equal(20, session.Read(7).Current); // Later wall-clock sampling during the same send construction.
        }
        Assert.Equal(0, session.Read(7).Current);
    }
    [Fact]
    public void Recovery_integrates_independent_expiries_even_if_a_panel_read_prunes_them_first()
    {
        var session = new FortificationSession();
        session.Observe(0, name => name == FortificationState.LifeRegenPerStack ? 12 : 0);
        session.ConfigureRecovery(0, 1000, false);
        session.State.GrantExplicitStacks(0, 10);
        session.State.GrantExplicitStacks(1, 10);
        session.Read(8); // A panel update can run before the recovery writer.
        Assert.Equal(0, session.Value.Current);
        Assert.Equal(240, session.TakeRecovery(8), 9); // 120 stack-seconds * 2 life/stack/second.
        Assert.Equal(0, session.TakeRecovery(8));
    }

    [Fact]
    public void Cap_and_regeneration_changes_settle_previous_inputs_once_without_compounding()
    {
        var session = new FortificationSession();
        session.Observe(0, name => name == FortificationState.LifeRegenPerStack ? 12 : 0);
        session.ConfigureRecovery(0, 1000, false);
        session.State.GrantExplicitStacks(0, 30);
        session.Read(1);
        session.Observe(1, name => name == FortificationState.MaximumStat ? 10
            : name == FortificationState.LifeRegenPerStack ? 12 : 0);
        session.ConfigureRecovery(1, 2000, false);
        Assert.Equal(160, session.TakeRecovery(2), 9); // 20*2 for 1s, then 30*4 for 1s.
        session.ConfigureRecovery(2, 2000, true);
        Assert.Equal(0, session.TakeRecovery(3)); // An explicit final regeneration stat replaces the derived source.
    }

    [Fact]
    public void Transfer_preserves_remaining_lifetimes_and_shared_gain_delay_on_a_new_clock()
    {
        var first = new FortificationSession();
        int Get(string name) => name == FortificationSession.OnMeleeHit ? 1 : 0;
        Assert.True(first.Hit(10, 100, 100, GameServer.Core.Protocol.Instance.MonsterRarity.Normal, Get, new Random(1)));
        var transfer = first.State.Carry(10.05);
        first.Clear();
        var second = new FortificationSession();
        second.Restore(transfer, 0);
        Assert.Equal(20, second.Value.Current);
        Assert.False(second.Hit(.049, 100, 100, GameServer.Core.Protocol.Instance.MonsterRarity.Normal, Get, new Random(1)));
        Assert.True(second.Hit(.051, 100, 100, GameServer.Core.Protocol.Instance.MonsterRarity.Normal, Get, new Random(1)));
        Assert.Equal(0, first.Read(11).Current);
        Assert.Equal(20, second.Read(5.951).Current);
        Assert.Equal(0, second.Read(6.052).Current);
    }
}
