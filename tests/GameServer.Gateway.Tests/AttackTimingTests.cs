namespace GameServer.Gateway.Tests;

// The attack timer the default attack and the attack gem skills share. While the button is held the client re-aims an
// attack every frame; the timer makes at most one of those a swing (live 2026-10-25: Holy Strike on the right mouse
// button cast 12 times a second, 6 mana each, while the cursor moved).
public class AttackTimingTests
{
    [Fact]
    public void The_first_attack_is_ready()
    {
        Assert.True(AttackTiming.Ready(now: 0, next: 0, seconds: 0.8));
    }

    [Fact]
    public void A_use_may_arrive_a_quarter_of_an_attack_early()
    {
        // The client's jitter (11-skills.md §5): 0.2 s early on a 0.8 s attack is accepted, 0.3 s is not.
        Assert.True(AttackTiming.Ready(now: 0.8, next: 1.0, seconds: 0.8));
        Assert.False(AttackTiming.Ready(now: 0.7, next: 1.0, seconds: 0.8));
    }

    [Fact]
    public void The_next_attack_follows_the_previous_one()
    {
        // An accepted use sets the next one a full attack time later; a late use (the server fell behind) counts from now.
        Assert.Equal(1.8, AttackTiming.After(now: 1.0, next: 1.0, seconds: 0.8), 6);
        Assert.Equal(2.2, AttackTiming.After(now: 1.4, next: 1.0, seconds: 0.8), 6);
    }

    [Fact]
    public void A_held_attack_swings_once_per_attack_time()
    {
        double next = 0;
        int swings = 0;
        // The client aims 20 times a second for two seconds; only the accepted uses set the timer and count as swings.
        for (int i = 0; i <= 40; i++)
        {
            double now = i * 0.05;
            if (!AttackTiming.Ready(now, next, 0.8)) continue;
            next = AttackTiming.After(now, next, 0.8);
            swings++;
        }
        Assert.Equal(3, swings);
    }

    [Fact]
    public void A_hit_lands_at_the_contact_keyframe_plus_the_offset()
    {
        // attack1_unarmed: contact 0.4 of a 1 s attack, plus the 150 ms walk-in offset.
        Assert.Equal(0.55, AttackTiming.ContactDelay(seconds: 1.0, contact: 0.4, offset: 0.15), 6);
    }

    [Fact]
    public void A_hit_lands_before_the_next_held_attack_may_cancel_it()
    {
        // A fast attack (0.3 s) with a late contact (Marauder attack1_staff, 0.72): the next use may arrive at 0.225 s, so
        // the hit lands by 0.175 s; a held attack loop never cancels its own hits.
        double delay = AttackTiming.ContactDelay(seconds: 0.3, contact: 0.72, offset: 0.15);
        Assert.Equal(0.3 * (1 - AttackTiming.EarlyAllowance) - AttackTiming.LandMargin, delay, 6);
        for (double seconds = 0.05; seconds < 3; seconds += 0.05)
            Assert.True(AttackTiming.ContactDelay(seconds, 0.76, 0.15) < seconds * (1 - AttackTiming.EarlyAllowance));
    }

    [Fact]
    public void A_bad_offset_never_gives_a_negative_or_undefined_delay()
    {
        Assert.Equal(0, AttackTiming.ContactDelay(seconds: 0.8, contact: 0.4, offset: -5));
        Assert.Equal(0.4, AttackTiming.ContactDelay(seconds: 0.8, contact: 0.4, offset: double.NaN), 6);
        Assert.Equal(0.4, AttackTiming.ContactDelay(seconds: 0.8, contact: 0.4, offset: double.PositiveInfinity), 6);
    }
}
