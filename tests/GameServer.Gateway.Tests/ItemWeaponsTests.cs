using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// A weapon's attack from its base row and the item's local stats (ItemWeapons, 13-items.md §2c).
public class ItemWeaponsTests
{
    static Dictionary<string, int> Local(params (string Name, int Value)[] stats) =>
        stats.ToDictionary(s => s.Name, s => s.Value);

    [Fact]
    public void Physical_damage_adds_local_flat_and_increase_and_quality()
    {
        var weapon = ItemWeapons.Of(1000, 10, 20, 8,
            Local(("local_minimum_added_physical_damage", 5), ("local_maximum_added_physical_damage", 10),
                ("local_physical_damage_+%", 50)), 20);

        // (10 + 5) * 150% * 20% quality = 27; (20 + 10) * 150% * 20% = 54. Quality is a separate factor since 3.25.
        Assert.Equal(27, weapon.MinDamage);
        Assert.Equal(54, weapon.MaxDamage);

        // The post-3.25 rule directly: base 100, 100% increased physical and 20% quality -> 100 * 2 * 1.2 = 240.
        var reviewed = ItemWeapons.Of(1000, 100, 100, 8, Local(("local_physical_damage_+%", 100)), 20);
        Assert.Equal(240, reviewed.MinDamage);
        Assert.Equal(240, reviewed.MaxDamage);
    }

    [Fact]
    public void Local_attack_speed_shortens_the_attack_time()
    {
        Assert.Equal(800, ItemWeapons.Of(1000, 10, 20, 8, Local(("local_attack_speed_+%", 25)), 0).AttackMs);
    }

    [Fact]
    public void Added_elemental_damage_is_scaled_by_the_local_elemental_increase_but_not_by_quality()
    {
        var weapon = ItemWeapons.Of(1000, 10, 20, 8,
            Local(("local_minimum_added_fire_damage", 4), ("local_maximum_added_fire_damage", 8),
                ("local_minimum_added_chaos_damage", 2), ("local_maximum_added_chaos_damage", 4),
                ("local_elemental_damage_+%", 50)), 20);
        var hit = weapon.HitDamage(null);

        Assert.Equal(new DamageRange(6, 12), hit[DamageType.Fire]);
        Assert.Equal(new DamageRange(2, 4), hit[DamageType.Chaos]); // the elemental increase does not touch chaos
        Assert.True(hit[DamageType.Cold].IsZero);
    }
}
