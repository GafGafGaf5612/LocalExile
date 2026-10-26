using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// Flask charge gains (FlaskCharges): poewiki's Flask page ("Flask charges") with the monster rarity mods of Mods.dat.
public class FlaskChargesTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly Lazy<ModCatalog> Mods = new(() => new ItemCatalog(Data()).Mods);
    static Dictionary<string, int> Local(params (string Stat, int Value)[] stats) => stats.ToDictionary(s => s.Stat, s => s.Value);
    static Func<string, int> Character(params (string Stat, int Value)[] stats) => name => stats.Where(s => s.Stat == name).Sum(s => s.Value);

    [Fact]
    public void Kills_give_1_3_5_6_and_11_charges_by_rarity_from_the_monster_mods()
    {
        // Mods.dat MonsterMagic6 / MonsterRare6 / MonsterUnique6: +250 / +500 / +1000% flask charges granted.
        Assert.Equal(1, FlaskCharges.KillCharges(MonsterRarity.Normal, Mods.Value));
        Assert.Equal(3.5, FlaskCharges.KillCharges(MonsterRarity.Magic, Mods.Value));
        Assert.Equal(6, FlaskCharges.KillCharges(MonsterRarity.Rare, Mods.Value));
        Assert.Equal(11, FlaskCharges.KillCharges(MonsterRarity.Unique, Mods.Value));
    }

    [Fact]
    public void Increased_charges_gained_and_charge_recovery_multiply()
    {
        Assert.Equal(1, FlaskCharges.GainMultiplier(Local(), Character()));
        // 20% increased flask charges gained (gear) and Perpetual's 50% increased charge recovery: 1.2 × 1.5.
        Assert.Equal(1.8, FlaskCharges.GainMultiplier(Local(("local_charges_added_+%", 50)), Character(("flask_charges_gained_+%", 20))), 6);
    }

    [Fact]
    public void Partial_charges_are_kept_to_two_decimals()
    {
        var carry = new FlaskCharges.Carry();
        // Three normal kills at 21% increased charges gained: 1.21 each.
        Assert.Equal(1, carry.Take(7, 1.21));
        Assert.Equal(1, carry.Take(7, 1.21));
        Assert.Equal(0.42, carry.Partial(7), 6);
        Assert.Equal(1, carry.Take(7, 1.21));
        Assert.Equal(0.63, carry.Partial(7), 6);
        // A magic kill's 3.5 on top: 4.13, 4 now.
        Assert.Equal(4, carry.Take(7, 3.5));
        Assert.Equal(0.13, carry.Partial(7), 6);
        // Another flask keeps its own.
        Assert.Equal(0, carry.Take(8, 0.5));
        Assert.Equal(0.5, carry.Partial(8));
    }

    [Fact]
    public void Crit_charges_share_one_cooldown_of_0_132_seconds()
    {
        var carry = new FlaskCharges.Carry();
        Assert.True(carry.TryCrit(10));
        Assert.False(carry.TryCrit(10.1));
        Assert.False(carry.TryCrit(10.131));
        Assert.True(carry.TryCrit(10.132));
        // At most ~7.5 per second.
        int given = 0;
        for (double t = 20; t < 21; t += 0.01) if (carry.TryCrit(t)) given++;
        Assert.Equal(8, given);
    }

    [Fact]
    public void A_flask_equipped_in_a_hostile_area_starts_empty()
    {
        var flask = new Item(0x1111, ["Base", "Charges", "Flask"], 0x2222) { Charges = 60 };
        Assert.Equal(0, FlaskCharges.WhenEquipped(flask, safeArea: false).Charges);
        Assert.Same(flask, FlaskCharges.WhenEquipped(flask, safeArea: true));
    }
}
