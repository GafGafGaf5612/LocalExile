using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ChargeSessionTests
{
    static readonly WeaponProfile Unarmed = new(false, false, false, false, false, Unarmed: true, Melee: true);

    sealed class Character(params (string Name, int Value)[] stats)
    {
        public Dictionary<string, int> Stats { get; } = stats.ToDictionary(s => s.Name, s => s.Value);
        public int Reads { get; private set; }
        public int Get(string name) => Stats.GetValueOrDefault(name);
        // The ticker's stats for waiting events: worked out (counted) only then.
        public Func<string, int> Read() { Reads++; return Get; }
    }

    // A ticker step: the character's stats observed (as every stats change does through WithCharges, unless
    // observe is false), and passed in only while events wait.
    static ChargeSession.StepResult Step(ChargeSession s, double now, Character c, Random? rng = null, bool alive = true,
        bool moving = false, bool inTown = false, bool observe = true)
    {
        if (observe) s.Observe(c.Get);
        return s.Step(now, s.Pending ? c.Read() : null, () => Unarmed, alive, moving, inTown, rng ?? new ScriptedRandom());
    }

    static ushort counter;
    static ushort Next() => ++counter;

    [Fact]
    public void A_kill_with_a_full_chance_gains_a_charge_and_shows_its_buff()
    {
        var s = new ChargeSession();
        var c = new Character(("add_frenzy_charge_on_kill_%_chance", 100));
        Step(s, 0, c);
        s.Enqueue(new ChargeEvent.Kill(MonsterRarity.Normal));
        var r = Step(s, 1, c);
        Assert.True(r.CountsChanged);
        Assert.Equal(1, s.Effective(ChargeType.Frenzy));
        var shown = Assert.Single(s.Presentation(1, Next));
        Assert.Equal(ChargeType.Frenzy, shown.Type);
        Assert.Equal(0, shown.Remove);
        Assert.Equal(1, shown.Count);
        Assert.Equal(10, shown.Remaining!.Value, 6);
        // Nothing changed: nothing to send.
        Assert.False(Step(s, 1.05, c).CountsChanged);
        Assert.Empty(s.Presentation(1.05, Next));
    }

}
