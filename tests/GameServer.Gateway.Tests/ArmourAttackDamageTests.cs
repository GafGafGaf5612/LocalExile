using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class ArmourAttackDamageTests
{
    const string PerArmour = "attack_damage_+%_per_450_physical_damage_reduction_rating";
    static readonly StatCatalog Catalog = new();
    static readonly SkillCatalog.Unarmed Weapon = new(1000, 100, 100, 20);
    static uint Key(string name) => Catalog.Key(name)!.Value;
    static int Value(IReadOnlyDictionary<uint, int> stats, string name) => stats.GetValueOrDefault(Key(name));

    sealed class Character : IDisposable
    {
        public ClientState State { get; }
        public Character(int armour)
        {
            TestSaves.Ensure();
            State = new(new(0, "ArmourAttack", 128, 128, 32, 32, null, []), 0, 1)
            { Character = "ArmourAttack" + Guid.NewGuid().ToString("N"), ClassRow = 1,
                Pools = new(100, 50, 1000, 0, 0, Armour: armour,
                    Strength: 1000, Dexterity: 1000, Intelligence: 1000) };
            State.Passives.Add(2103);
        }
        public Dictionary<uint, int> Stats() => Program.CharacterPlayerStats(State);
        public void Dispose() => PlayerInventories.Forget(State.Character!);
    }

    [Fact]
    public void The_real_Steel_Willed_node_supplies_two_percent_per_complete_450_final_armour()
    {
        var node = new PassiveTree().Find(2103);
        Assert.NotNull(node);
        Assert.Equal("Steel Willed", node.Name);
        Assert.True(node.IsAscendancy);
        Assert.Equal(13019u, Key(PerArmour));
        Assert.Contains((Key(PerArmour), 2), node.Stats);
        using var character = new Character(900);
        Assert.Equal(4, Value(character.Stats(), "attack_damage_+%"));
    }

    [Theory]
    [InlineData(-450, 0)]
    [InlineData(0, 0)]
    [InlineData(449, 0)]
    [InlineData(450, 2)]
    [InlineData(899, 2)]
    [InlineData(900, 4)]
    [InlineData(4500, 20)]
    public void Complete_groups_join_the_attack_increased_bucket_for_weapon_and_added_damage(int armour, int bonus)
    {
        using var character = new Character(armour);
        character.State.PlayerStats[Key("damage_+%")] = 100;
        character.State.PlayerStats[Key("attack_damage_+%")] = 10;
        character.State.PlayerStats[Key("attack_minimum_added_chaos_damage")] = 100;
        character.State.PlayerStats[Key("attack_maximum_added_chaos_damage")] = 100;
        var stats = character.Stats();
        var hit = Program.AttackHit(Weapon, stats, melee: false);
        Assert.Equal(10 + bonus, Value(stats, "attack_damage_+%"));
        Assert.Equal(210 + bonus, hit[DamageType.Physical].Min, 6);
        Assert.Equal(210 + bonus, hit[DamageType.Chaos].Min, 6);
    }

    [Fact]
    public void Rebuilding_uses_the_current_final_rating_once_and_leaves_raw_sources_unchanged()
    {
        using var character = new Character(450);
        var conn = character.State;
        conn.PlayerStats[Key("attack_damage_+%")] = 10;
        Assert.Equal(12, Value(character.Stats(), "attack_damage_+%"));
        Assert.Equal(12, Value(character.Stats(), "attack_damage_+%"));
        Assert.Equal(10, conn.PlayerStats[Key("attack_damage_+%")]);
        conn.Pools = conn.Pools with { Armour = 900, ArmourPct = 100 };
        Assert.Equal(14, Value(character.Stats(), "attack_damage_+%"));
        conn.PlayerStats[Key("physical_damage_reduction_rating")] = 449;
        Assert.Equal(10, Value(character.Stats(), "attack_damage_+%"));
        conn.PlayerStats.Remove(Key("physical_damage_reduction_rating"));
        conn.PlayerStats[Key(PerArmour)] = 0;
        Assert.Equal(10, Value(character.Stats(), "attack_damage_+%"));
        conn.PlayerStats.Remove(Key(PerArmour));
        conn.Passives.Clear();
        Assert.Equal(10, Value(character.Stats(), "attack_damage_+%"));
        using var fresh = new Character(900);
        Assert.Equal(4, Value(fresh.Stats(), "attack_damage_+%"));
    }

    [Fact]
    public void The_rating_includes_existing_defence_increases_and_conversion_before_counting_groups()
    {
        using var character = new Character(0);
        character.State.Pools = CharacterStats.For(Program.Skills.ClassFor(1), 100,
            new(Armour: 225, ArmourPct: 100));
        Assert.Equal(450, character.State.Pools.Armour);
        Assert.Equal(2, Value(character.Stats(), "attack_damage_+%"));
        character.State.Pools = CharacterStats.For(Program.Skills.ClassFor(1), 100,
            new(Armour: 225, ArmourPct: 100, Evasion: 435, IronReflexes: true));
        Assert.True(character.State.Pools.Armour >= 900);
        Assert.Equal(character.State.Pools.Armour / 450 * 2, Value(character.Stats(), "attack_damage_+%"));
    }

    [Fact]
    public void Spell_hits_do_not_take_the_attack_increase()
    {
        using var character = new Character(4500);
        var stats = character.Stats();
        var spell = SpellDamage.Packet(DamagePacket.Of(DamageType.Fire, 100, 100),
            name => Value(stats, name));
        Assert.Equal(100, spell.MinTotal);
        Assert.Equal(20, Value(stats, "attack_damage_+%"));
        Assert.Equal(0, Value(stats, "damage_+%"));
        Assert.Equal(0, Value(stats, "spell_damage_+%"));
    }

    [Theory]
    [InlineData(10, 450, -2, 8)]
    [InlineData(int.MinValue, 900, int.MaxValue, 2147483646)]
    [InlineData(10, int.MaxValue, int.MaxValue, int.MaxValue)]
    [InlineData(10, int.MaxValue, int.MinValue, int.MinValue)]
    public void Signed_totals_are_combined_before_saturating(int attack, int armour, int perArmour, int expected)
    {
        Assert.Equal(expected, ArmourAttackDamage.Total(attack, armour, perArmour));
    }

    [Fact]
    public void Weapon_added_mana_and_support_constructors_keep_the_same_increase_before_effectiveness_and_conversion()
    {
        using var character = new Character(4500);
        character.State.PlayerStats[Key(DamageStats.MaxManaAddedLightning)] = 6;
        character.State.PlayerStats[Key("lightning_damage_%_to_convert_to_cold")] = 50;
        var stats = character.Stats();
        var hit = AttackSkillHit.Resolve(Weapon, stats, [], Catalog, 15000);
        Assert.Equal(180, hit[DamageType.Physical].Min);
        Assert.Equal(54, hit[DamageType.Lightning].Min);
        Assert.Equal(54, hit[DamageType.Cold].Min);
        var supported = SupportedAttackDamage.Hit(Weapon, name => Value(stats, name), new HashSet<int>());
        Assert.Equal(120, supported[DamageType.Physical].Min);
        Assert.Equal(72, supported[DamageType.Lightning].Min);
    }

    [Fact]
    public void Raw_attack_damage_and_other_signed_increases_do_not_overflow_before_cancellation()
    {
        var stats = new Dictionary<uint, int>
        {
            [Key("damage_+%")] = int.MaxValue,
            [Key("attack_damage_+%")] = int.MaxValue,
            [Key("physical_damage_+%")] = int.MinValue,
        };
        Assert.Equal(2655u, StatKeys.AttackDamagePct);
        Assert.Equal(StatKeys.AttackDamagePct, Key("attack_damage_+%"));
        Assert.Equal(2147483646L, Weapon.IncreasedDamage(stats));
        var ordinary = Weapon.HitDamage(stats);
        var supported = SupportedAttackDamage.Hit(Weapon, name => Value(stats, name), new HashSet<int>());
        Assert.Equal(2147483746, ordinary.MinTotal);
        Assert.Equal(ordinary.MinTotal, supported.MinTotal);
    }

    [Fact]
    public void Ordinary_bow_release_captures_the_derived_increase_without_following_later_armour_changes()
    {
        using var character = new Character(450);
        var shot = OrdinaryBowShot.Capture(Weapon, character.Stats(), [], Catalog, 100, 0, 150);
        character.State.Pools = character.State.Pools with { Armour = 900 };
        var next = OrdinaryBowShot.Capture(Weapon, character.Stats(), [], Catalog, 100, 0, 150);
        Assert.Equal(102, shot.Hit.MinTotal);
        Assert.Equal(104, next.Hit.MinTotal);
    }

    [Theory]
    [InlineData("BurningArrow", false)]
    [InlineData("Cyclone", false)]
    [InlineData("Fireball", false)]
    [InlineData("BurningArrow", true)]
    [InlineData("Cyclone", true)]
    public void Actual_socketed_skills_preserve_spell_isolation_conversion_and_support_restrictions(
        string effect, bool brutality)
    {
        using var character = new Character(4500);
        var conn = character.State;
        var gem = ItemCatalog.Create(Program.Items.Find($"Metadata/Items/Gems/SkillGem{effect}")!);
        var body = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!) with
        { Sockets = [new(SocketColour.White, gem)] };
        if (brutality)
            body = body with { Links = [2], Sockets = [new(SocketColour.White, gem),
                new(SocketColour.White, ItemCatalog.Create(Program.Items.Find("Brutality Support")!))] };
        PlayerInventories.For(conn.Character!, Program.Items).Add(PlayerInventories.BodyArmour, 0, 0, body);
        var method = typeof(Program).GetMethod("SupportedHit", BindingFlags.Static | BindingFlags.NonPublic)!;
        DamagePacket Hit() => (DamagePacket)method.Invoke(null, [conn, Program.GemSkillIdPool.Value[0]])!;
        conn.PlayerStats[Key(PerArmour)] = 0;
        var before = Hit();
        conn.PlayerStats.Remove(Key(PerArmour));
        var after = Hit();
        if (brutality && effect == "BurningArrow")
        {
            Assert.True(before.IsZero);
            Assert.True(after.IsZero);
            return;
        }
        Assert.True(before.MinTotal > 0);
        double factor = effect == "Fireball" ? 1 : 1.2;
        Assert.Equal(before.MinTotal * factor, after.MinTotal, 6);
        Assert.Equal(before.MaxTotal * factor, after.MaxTotal, 6);
        if (brutality)
        {
            Assert.Equal(0, after[DamageType.Fire].Max);
            Assert.Equal(0, after[DamageType.Lightning].Max);
        }
    }
}
