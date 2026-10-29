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

    [Fact]
    public void Converted_charges_show_the_special_buff_and_a_flag_change_swaps_it()
    {
        var s = new ChargeSession();
        var c = new Character((Charges.ConversionFlag(ChargeType.Endurance), 1), ("add_frenzy_charge_on_kill_%_chance", 100));
        s.Enqueue(new ChargeEvent.Grant(ChargeType.Endurance, 2, "test"));
        s.Enqueue(new ChargeEvent.Kill(MonsterRarity.Normal));
        var r = Step(s, 0, c);
        Assert.True(s.Converted(ChargeType.Endurance));
        Assert.False(s.MinimumConverted(ChargeType.Endurance));
        Assert.Contains(r.Log, l => l.Contains("brutal charges 0 -> 2/3"));
        Assert.Contains(r.Log, l => l.Contains("frenzy charges 0 -> 1/3"));
        var shown = s.Presentation(0, Next);
        var brutal = Assert.Single(shown, b => b.Type == ChargeType.Endurance);
        Assert.True(brutal.Converted);
        Assert.Equal(2, brutal.Count);
        Assert.False(Assert.Single(shown, b => b.Type == ChargeType.Frenzy).Converted);
        // The belt comes off: the same charges are Endurance Charges again, so the buff is swapped.
        c.Stats.Remove(Charges.ConversionFlag(ChargeType.Endurance));
        Assert.False(Step(s, 0.05, c).CountsChanged);   // the held count did not change
        var swapped = Assert.Single(s.Presentation(0.05, Next));
        Assert.Equal(ChargeType.Endurance, swapped.Type);
        Assert.False(swapped.Converted);
        Assert.Equal(brutal.Add, swapped.Remove);
        Assert.Equal(2, swapped.Count);
        Assert.Empty(s.Presentation(0.1, Next));
    }

}
