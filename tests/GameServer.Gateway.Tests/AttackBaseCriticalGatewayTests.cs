using System.Reflection;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class AttackBaseCriticalGatewayTests : IDisposable
{
    readonly string _name = "CritBase" + Guid.NewGuid().ToString("N");
    static MethodInfo Method(string name) => typeof(Program).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    public AttackBaseCriticalGatewayTests() => TestSaves.Ensure();

    ClientState Character(bool armed)
    {
        var conn = new ClientState(new(0, "CritTest", 128, 128, 32, 32, null, []), 0, 1)
        { Character = _name, ClassRow = 1 };
        if (armed)
            PlayerInventories.For(_name, Program.Items).Add(PlayerInventories.Weapon1, 0, 0,
                ItemCatalog.Create(Program.Items.Row(384)!));
        return conn;
    }

    static void Set(ClientState conn, string name, int value) => conn.PlayerStats[Program.Stats.Key(name)!.Value] = value;
    static CharacterStats.Pools Pools(ClientState conn) =>
        (CharacterStats.Pools)Method("PoolsFor").Invoke(null, [conn, 20])!;

    [Theory]
    [InlineData(true, 800)]
    [InlineData(false, 1000)]
    public void Actual_pools_apply_grants_and_raw_increases_then_clear_them(bool armed, int chance)
    {
        var conn = Character(armed);
        Set(conn, armed ? AttackBaseCriticalStrike.WeaponOverrideStat : AttackBaseCriticalStrike.UnarmedBaseStat, chance);
        Set(conn, "critical_strike_chance_+%", 100);
        conn.Pools = Pools(conn);
        Assert.Equal(chance * 2, conn.Pools.AttackCriticalStrikeChance);
        Assert.Equal(100, conn.Pools.CriticalStrikeChancePct);
        Assert.Equal(100, conn.Pools.SpellCriticalStrikeChancePct);
        Assert.Equal(1000, conn.Pools.SpellCriticalStrikeChance);
        var shown = (Dictionary<uint, int>)Method("CharacterPlayerStats").Invoke(null, [conn])!;
        Assert.Equal(chance * 2, shown[Program.Stats.Key("main_hand_critical_strike_chance")!.Value]);
        Assert.Equal(100, shown[Program.Stats.Key("critical_strike_chance_+%")!.Value]);
        Assert.Equal(conn.Pools.SpellCriticalStrikeChance,
            SpellCriticalStrike.Chance(500, Program.Stats, shown, new Dictionary<uint, int>()));
        Set(conn, armed ? AttackBaseCriticalStrike.WeaponOverrideStat : AttackBaseCriticalStrike.UnarmedBaseStat, 0);
        Assert.Equal(armed ? 1660 : 0, Pools(conn).AttackCriticalStrikeChance);
    }

    [Theory]
    [InlineData("global_cannot_crit")]
    [InlineData("virtual_global_cannot_crit")]
    [InlineData("cannot_critical_strike_with_attacks")]
    public void Actual_pools_enforce_cannot_crit_even_with_a_positive_weapon_override(string prohibition)
    {
        var conn = Character(true);
        Set(conn, AttackBaseCriticalStrike.WeaponOverrideStat, 900);
        Set(conn, prohibition, 1);
        Assert.Equal(0, Pools(conn).AttackCriticalStrikeChance);
        Set(conn, prohibition, 0);
        Assert.Equal(900, Pools(conn).AttackCriticalStrikeChance);
    }

    [Fact]
    public void A_debug_base_override_replaces_a_passive_value_instead_of_adding_to_it()
    {
        var conn = Character(true);
        conn.Passives.Add(17315); // Overwhelm: supplies 800 through the real passive stat catalog.
        Assert.Equal(800, Pools(conn).AttackCriticalStrikeChance);
        Set(conn, AttackBaseCriticalStrike.WeaponOverrideStat, 900);
        Assert.Equal(900, Pools(conn).AttackCriticalStrikeChance);
        Set(conn, AttackBaseCriticalStrike.WeaponOverrideStat, 0);
        Assert.Equal(830, Pools(conn).AttackCriticalStrikeChance);
        conn.PlayerStats.Clear();
        Assert.Equal(800, Pools(conn).AttackCriticalStrikeChance);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 40)]
    public void Debug_increases_replace_aura_inputs_for_the_pool_display_and_spell_roll(int generic, int spell)
    {
        var conn = Character(true);
        var skills = Program.Skills;
        conn.Auras[0x103] = new(0x103, skills.EffectRow("Precision"), "Precision", "Precision", 1, 1293, 22, 0);
        conn.Auras[0x104] = new(0x104, skills.EffectRow("Zealotry"), "Zealotry", "Zealotry", 2, 0, 0, 5000);
        var baseline = Pools(conn);
        Assert.True(baseline.CriticalStrikeChancePct > 0);
        Assert.True(baseline.SpellCriticalStrikeChancePct > baseline.CriticalStrikeChancePct);

        Set(conn, "critical_strike_chance_+%", generic);
        Set(conn, "spell_critical_strike_chance_+%", spell);
        conn.Pools = Pools(conn);
        Assert.Equal(generic, conn.Pools.CriticalStrikeChancePct);
        Assert.Equal(generic + spell, conn.Pools.SpellCriticalStrikeChancePct);
        Assert.Equal(830 * (100 + generic) / 100, conn.Pools.AttackCriticalStrikeChance);
        Assert.Equal(5 * (100 + generic + spell), conn.Pools.SpellCriticalStrikeChance);
        var shown = (Dictionary<uint, int>)Method("CharacterPlayerStats").Invoke(null, [conn])!;
        Assert.Equal(conn.Pools.SpellCriticalStrikeChance,
            SpellCriticalStrike.Chance(500, Program.Stats, shown, new Dictionary<uint, int>()));
    }

    [Theory]
    [InlineData(int.MaxValue, 0, 0, 50, int.MaxValue, (long)int.MaxValue + 50, 10000, 10000)]
    [InlineData(int.MaxValue, 0, 50, 0, (long)int.MaxValue + 50, int.MaxValue, 10000, 10000)]
    [InlineData(int.MinValue, 0, -50, -50, (long)int.MinValue - 50, (long)int.MinValue - 50, 0, 0)]
    [InlineData(int.MaxValue, 50, -int.MaxValue, int.MinValue, 50, -1, 1350, 495)]
    [InlineData(int.MinValue, -50, int.MaxValue, int.MaxValue, -51, -1, 441, 495)]
    [InlineData(100, 0, 50, 50, 150, 150, 2250, 1250)]
    public void Actual_pools_sum_signed_critical_increases_before_clamping(int generic, int attack, int wand, int spell,
        long attackIncrease, long spellIncrease, int attackChance, int spellChance)
    {
        var conn = Character(true);
        Set(conn, AttackBaseCriticalStrike.WeaponOverrideStat, 900);
        Set(conn, "critical_strike_chance_+%", generic);
        Set(conn, "attack_critical_strike_chance_+%", attack);
        Set(conn, "wand_critical_strike_chance_+%", wand);
        Set(conn, "spell_critical_strike_chance_+%", spell);
        conn.Pools = Pools(conn);
        Assert.Equal(attackIncrease, conn.Pools.CriticalStrikeChancePct);
        Assert.Equal(spellIncrease, conn.Pools.SpellCriticalStrikeChancePct);
        Assert.Equal(attackChance, conn.Pools.AttackCriticalStrikeChance);
        Assert.Equal(spellChance, conn.Pools.SpellCriticalStrikeChance);
        var shown = (Dictionary<uint, int>)Method("CharacterPlayerStats").Invoke(null, [conn])!;
        Assert.Equal(attackChance, shown[Program.Stats.Key("main_hand_critical_strike_chance")!.Value]);
        Assert.Equal(spellChance,
            SpellCriticalStrike.Chance(500, Program.Stats, shown, new Dictionary<uint, int>()));
    }

    [Fact]
    public void Midpoint_pool_panel_and_supported_attack_use_the_same_half_up_chance()
    {
        var conn = Character(true);
        Set(conn, AttackBaseCriticalStrike.WeaponOverrideStat, 650);
        Set(conn, "critical_strike_chance_+%", 1);
        conn.Pools = Pools(conn);
        var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemCyclone")!);
        var body = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!) with
        { Sockets = [new(SocketColour.White, gem)] };
        PlayerInventories.For(_name, Program.Items).Add(PlayerInventories.BodyArmour, 0, 0, body);
        Assert.Equal(657, conn.Pools.AttackCriticalStrikeChance);
        var shown = (Dictionary<uint, int>)Method("CharacterPlayerStats").Invoke(null, [conn])!;
        Assert.Equal(657, shown[Program.Stats.Key("main_hand_critical_strike_chance")!.Value]);
        Assert.Equal(657, (int)Method("SupportedCriticalChance").Invoke(null, [conn, Program.GemSkillIdPool.Value[0]])!);
        Assert.Equal(657, SpellCriticalStrike.Chance(650, name => name == "critical_strike_chance_+%" ? 1 : 0));
        Assert.True(SpellCriticalStrike.Hits(657, 656));
        Assert.False(SpellCriticalStrike.Hits(657, 657));
    }

    [Fact]
    public void Armed_overcap_is_added_once_and_explicit_zero_clears_it()
    {
        var conn = Character(true);
        Set(conn, "base_lightning_damage_resistance_%", 120);
        Set(conn, ResistanceOvercap.LightningCritFlag, 1);
        conn.Pools = Pools(conn);
        Assert.Equal(45, conn.Pools.LightningOvercapCritPct);
        Assert.Equal(1204, conn.Pools.AttackCriticalStrikeChance); // 830 * 1.45 = 1203.5, half up.
        Assert.Equal(725, conn.Pools.SpellCriticalStrikeChance);
        var raw = Program.CharacterPlayerStats(conn);
        Assert.Equal(45, raw[Program.Stats.Key("critical_strike_chance_+%")!.Value]);
        Assert.Equal(725, SpellCriticalStrike.Chance(500, Program.Stats, raw, new Dictionary<uint, int>()));
        Set(conn, ResistanceOvercap.LightningCritFlag, 0);
        conn.Pools = Pools(conn);
        Assert.Equal(830, conn.Pools.AttackCriticalStrikeChance);
        Assert.Equal(500, conn.Pools.SpellCriticalStrikeChance);
    }

    [Fact]
    public void Overcap_and_signed_extremes_cancel_before_final_chance_and_keep_raw_spell_source_once()
    {
        var conn = Character(true);
        Set(conn, "base_lightning_damage_resistance_%", 120);
        Set(conn, ResistanceOvercap.LightningCritFlag, 1);
        Set(conn, "critical_strike_chance_+%", int.MaxValue);
        Set(conn, "wand_critical_strike_chance_+%", int.MinValue);
        Set(conn, "spell_critical_strike_chance_+%", int.MinValue);
        conn.Pools = Pools(conn);
        Assert.Equal(44, conn.Pools.CriticalStrikeChancePct);
        Assert.Equal(44, conn.Pools.SpellCriticalStrikeChancePct);
        Assert.Equal(1195, conn.Pools.AttackCriticalStrikeChance);
        var raw = Program.CharacterPlayerStats(conn);
        Assert.Equal(int.MaxValue, raw[Program.Stats.Key("critical_strike_chance_+%")!.Value]);
        Assert.Equal(720, SpellCriticalStrike.Chance(500, Program.Stats, raw, new Dictionary<uint, int>(),
            conn.Pools.SpellCriticalStrikeChancePct));
        Set(conn, "global_cannot_crit", 1);
        Assert.Equal(0, Pools(conn).AttackCriticalStrikeChance);
        Assert.Equal(0, Pools(conn).SpellCriticalStrikeChance);
    }

    [Fact]
    public void A_weapon_in_the_inactive_set_does_not_make_the_active_empty_hand_armed()
    {
        var conn = Character(false);
        var items = PlayerInventories.For(_name, Program.Items);
        items.Add(PlayerInventories.Weapon2, 0, 0, ItemCatalog.Create(Program.Items.Row(384)!));
        Set(conn, AttackBaseCriticalStrike.WeaponOverrideStat, 900);
        Set(conn, "wand_critical_strike_chance_+%", 100);
        Assert.Equal(0, Pools(conn).AttackCriticalStrikeChance);
        Set(conn, AttackBaseCriticalStrike.UnarmedBaseStat, 1000);
        var unarmed = Pools(conn);
        Assert.Equal(1000, unarmed.AttackCriticalStrikeChance);
        Assert.Equal(0, unarmed.CriticalStrikeChancePct);
        items.SwapWeaponSets();
        var armed = Pools(conn);
        Assert.Equal(1800, armed.AttackCriticalStrikeChance);
        Assert.Equal(100, armed.CriticalStrikeChancePct);
    }

    public void Dispose() => PlayerInventories.Forget(_name);
}
