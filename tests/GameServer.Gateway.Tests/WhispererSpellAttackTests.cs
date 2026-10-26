using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class WhispererSpellAttackTests
{
    const string Flag = "additive_spell_damage_modifiers_apply_to_attack_damage_at_150%_value";
    static readonly StatCatalog Catalog = new();
    static readonly SkillCatalog.Unarmed Weapon = new(1000, 100, 100, 20);

    static Dictionary<uint, int> Stats(params (string Name, int Value)[] values) =>
        values.ToDictionary(x => Catalog.Key(x.Name)!.Value, x => x.Value);

    static Func<string, int> Reader(IReadOnlyDictionary<uint, int> stats) =>
        name => Catalog.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;

    [Fact]
    public void The_real_Whisperer_node_enables_the_fixed_transfer_stat()
    {
        var node = new PassiveTree().Find(50932);
        Assert.NotNull(node);
        Assert.Equal("your mind is destroyed", node.Name);
        Assert.True(node.IsAscendancy);
        Assert.Equal(11811u,
            Catalog.Key(Flag));
        Assert.Equal(26u, Catalog.Key("spell_damage_+%"));
        Assert.Contains((Catalog.Key(Flag)!.Value, 1), node.Stats);
        var sources = new PassiveTree().Stats([50932]);
        sources[Catalog.Key("spell_damage_+%")!.Value] = 20;
        Assert.Equal(130, Weapon.HitDamage(sources)[DamageType.Physical].Min);
    }

    [Theory]
    [InlineData(0, 20, 0, 100)]
    [InlineData(1, 20, 0, 130)]
    [InlineData(2, 20, 0, 130)]
    [InlineData(1, 1, 0, 101.5)]
    [InlineData(1, -1, 0, 98.5)]
    [InlineData(1, -100, 0, 0)]
    [InlineData(1, 20, 100, 230)]
    [InlineData(1, -50, 100, 125)]
    public void Weapon_and_added_damage_share_an_additive_fractional_increase(
        int enabled, int spell, int generic, double expected)
    {
        var stats = Stats((Flag, enabled),
            ("spell_damage_+%", spell), ("damage_+%", generic),
            ("attack_minimum_added_chaos_damage", 100), ("attack_maximum_added_chaos_damage", 100));
        var hit = Program.AttackHit(Weapon, stats, melee: false);

        Assert.Equal(expected, hit[DamageType.Physical].Min, 7);
        Assert.Equal(expected, hit[DamageType.Chaos].Min, 7);
        Assert.Equal(expected, hit[DamageType.Physical].Max, 7);
        Assert.Equal(expected, hit[DamageType.Chaos].Max, 7);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Supported_attacks_apply_the_transfer_to_the_mana_grant_and_other_damage_types(bool melee)
    {
        var stats = Stats((Flag, 1), ("spell_damage_+%", 20),
            ("damage_+%", 100), ("maximum_mana", 1000), (DamageStats.MaxManaAddedLightning, 6));
        var types = new HashSet<int> { 0, melee ? 19 : 2 };
        var hit = SupportedAttackDamage.Hit(Weapon, Reader(stats), types);

        Assert.Equal(230, hit[DamageType.Physical].Min, 7);
        Assert.Equal(138, hit[DamageType.Lightning].Min, 7);
    }

    [Theory]
    [InlineData(1, 203)]
    [InlineData(-1, 197)]
    public void Whole_damage_endpoints_remain_exact_at_half_percent_transfer_boundaries(int spell, int expected)
    {
        var weapon = new SkillCatalog.Unarmed(1000, 200, 200, 20);
        var stats = Stats((Flag, 1), ("spell_damage_+%", spell),
            ("attack_minimum_added_chaos_damage", 200), ("attack_maximum_added_chaos_damage", 200));
        var actual = Program.AttackHit(weapon, stats, melee: false);
        var supported = SupportedAttackDamage.Hit(weapon, Reader(stats), new HashSet<int> { 0, 2 });

        Assert.Equal(expected, weapon.Damage(stats).Min);
        Assert.Equal(expected, actual[DamageType.Physical].Min);
        Assert.Equal(expected, actual[DamageType.Chaos].Min);
        Assert.Equal(expected, supported[DamageType.Physical].Min);
        Assert.Equal(expected, supported[DamageType.Chaos].Min);
    }

    [Fact]
    public void Effectiveness_and_conversion_follow_the_transfer_without_scaling_it_twice()
    {
        var stats = Stats((Flag, 1), ("spell_damage_+%", 20),
            ("maximum_mana", 1000), (DamageStats.MaxManaAddedLightning, 6));
        var hit = AttackSkillHit.Resolve(Weapon, stats,
            [(Catalog.Key("skill_physical_damage_%_to_convert_to_fire")!.Value, 50)], Catalog, 20_000);

        Assert.Equal(130, hit[DamageType.Physical].Min, 7);
        Assert.Equal(130, hit[DamageType.Fire].Min, 7);
        Assert.Equal(156, hit[DamageType.Lightning].Min, 7);
    }

    [Fact]
    public void Ordinary_bows_keep_the_release_time_damage_when_the_flag_is_removed_later()
    {
        var stats = Stats((Flag, 1), ("spell_damage_+%", 20));
        var released = OrdinaryBowShot.Capture(Weapon, stats, [], Catalog, 1000, 0, 150);
        stats.Remove(Catalog.Key(Flag)!.Value);

        Assert.Equal(130, released.Hit[DamageType.Physical].Min, 7);
        Assert.Equal(100, Weapon.HitDamage(stats)[DamageType.Physical].Min, 7);
    }

    [Fact]
    public void More_spell_damage_and_cast_speed_are_not_transferred_and_spells_do_not_take_the_flag()
    {
        var stats = Stats(("spell_damage_+%", 20), ("spell_damage_aura_spell_damage_+%_final", 100),
            ("cast_speed_+%", 100));
        var spell = SpellDamage.Packet(DamagePacket.Of(DamageType.Fire, 100, 100), Reader(stats));
        stats[Catalog.Key(Flag)!.Value] = 1;

        Assert.Equal(130, Weapon.HitDamage(stats)[DamageType.Physical].Min, 7);
        Assert.Equal(240, spell[DamageType.Fire].Min, 7);
        Assert.Equal(spell.MinTotal,
            SpellDamage.Packet(DamagePacket.Of(DamageType.Fire, 100, 100), Reader(stats)).MinTotal);
    }

    [Theory]
    [InlineData("BurningArrow", false)]
    [InlineData("Cyclone", false)]
    [InlineData("Fireball", false)]
    [InlineData("BurningArrow", true)]
    [InlineData("Cyclone", true)]
    public void Actual_socketed_skill_consumers_preserve_spell_isolation_and_support_restrictions(
        string effect, bool brutality)
    {
        TestSaves.Ensure();
        string character = "WhispererTransfer" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(0, "WhispererTransfer", 128, 128, 32, 32, null, []), 0, 1)
            { Character = character, ClassRow = 1,
                Pools = new(100, 50, 1000, 0, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000) };
            conn.PlayerStats[Program.Stats.Key("spell_damage_+%")!.Value] = 20;
            var gem = ItemCatalog.Create(Program.Items.Find($"Metadata/Items/Gems/SkillGem{effect}")!);
            var body = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!) with
            { Sockets = [new(SocketColour.White, gem)] };
            if (brutality)
                body = body with { Links = [2], Sockets = [new(SocketColour.White, gem),
                    new(SocketColour.White, ItemCatalog.Create(Program.Items.Find("Brutality Support")!))] };
            PlayerInventories.For(character, Program.Items).Add(PlayerInventories.BodyArmour, 0, 0, body);
            var method = typeof(Program).GetMethod("SupportedHit", BindingFlags.Static | BindingFlags.NonPublic)!;
            DamagePacket Hit() => (DamagePacket)method.Invoke(null, [conn, Program.GemSkillIdPool.Value[0]])!;
            var before = Hit();
            conn.PlayerStats[Catalog.Key(Flag)!.Value] = 1;
            var after = Hit();

            if (brutality && effect == "BurningArrow")
            {
                // Burning Arrow converts its physical hit to fire; Brutality refuses that damage entirely.
                Assert.True(before.IsZero);
                Assert.True(after.IsZero);
                return;
            }
            Assert.True(before.MinTotal > 0);
            double factor = effect == "Fireball" ? 1 : 1.3;
            Assert.Equal(before.MinTotal * factor, after.MinTotal, 6);
            Assert.Equal(before.MaxTotal * factor, after.MaxTotal, 6);
            if (brutality)
            {
                Assert.Equal(0, after[DamageType.Fire].Max);
                Assert.Equal(0, after[DamageType.Lightning].Max);
            }
        }
        finally { PlayerInventories.Forget(character); }
    }
}
