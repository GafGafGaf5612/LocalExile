using GameServer.Core.World;

namespace GameServer.Core.Tests;

public class DamagePacketTests
{
    [Fact]
    public void A_packet_keeps_each_damage_type_separate()
    {
        var hit = DamagePacket.Of(DamageType.Physical, 10, 20)
            .Add(DamageType.Fire, new DamageRange(3, 5));

        Assert.Equal(10, hit[DamageType.Physical].Min);
        Assert.Equal(20, hit[DamageType.Physical].Max);
        Assert.Equal(3, hit[DamageType.Fire].Min);
        Assert.Equal(5, hit[DamageType.Fire].Max);
        Assert.Equal(19, hit.AverageTotal);
    }

    [Fact]
    public void Conversion_moves_damage_forward_in_the_game_order()
    {
        var conversion = new DamageConversion().Add(DamageType.Physical, DamageType.Fire, 50);
        var result = DamagePacket.Of(DamageType.Physical, 100, 100).Convert(conversion);

        Assert.Equal(50, result[DamageType.Physical].Min);
        Assert.Equal(50, result[DamageType.Fire].Min);
        Assert.Equal(100, result.MinTotal);
    }

    [Fact]
    public void Conversion_can_chain_physical_lightning_cold_fire()
    {
        var conversion = new DamageConversion()
            .Add(DamageType.Physical, DamageType.Lightning, 50)
            .Add(DamageType.Lightning, DamageType.Cold, 50)
            .Add(DamageType.Cold, DamageType.Fire, 50);

        var result = DamagePacket.Of(DamageType.Physical, 100, 100).Convert(conversion);

        Assert.Equal(50, result[DamageType.Physical].Min);
        Assert.Equal(25, result[DamageType.Lightning].Min);
        Assert.Equal(12.5, result[DamageType.Cold].Min);
        Assert.Equal(12.5, result[DamageType.Fire].Min);
        Assert.Equal(100, result.MinTotal);
    }

    [Fact]
    public void Conversion_over_100_percent_is_normalised()
    {
        var conversion = new DamageConversion()
            .Add(DamageType.Physical, DamageType.Fire, 80)
            .Add(DamageType.Physical, DamageType.Cold, 40);

        var result = DamagePacket.Of(DamageType.Physical, 100, 100).Convert(conversion);

        Assert.Equal(0, result[DamageType.Physical].Min, 6);
        Assert.Equal(66.667, result[DamageType.Fire].Min, 3);
        Assert.Equal(33.333, result[DamageType.Cold].Min, 3);
        Assert.Equal(100, result.MinTotal);
    }

    [Fact]
    public void Gained_damage_does_not_consume_the_source()
    {
        var conversion = new DamageConversion().AddGain(DamageType.Physical, DamageType.Fire, 50);
        var result = DamagePacket.Of(DamageType.Physical, 100, 100).Convert(conversion);

        Assert.Equal(100, result[DamageType.Physical].Min);
        Assert.Equal(50, result[DamageType.Fire].Min);
        Assert.Equal(150, result.MinTotal);
    }

    [Fact]
    public void Gained_damage_can_be_converted_again()
    {
        var conversion = new DamageConversion()
            .AddGain(DamageType.Physical, DamageType.Lightning, 50)
            .Add(DamageType.Lightning, DamageType.Cold, 50);

        var result = DamagePacket.Of(DamageType.Physical, 100, 100).Convert(conversion);

        Assert.Equal(100, result[DamageType.Physical].Min);
        Assert.Equal(25, result[DamageType.Lightning].Min);
        Assert.Equal(25, result[DamageType.Cold].Min);
    }

    [Fact]
    public void Conversion_cannot_go_backwards()
    {
        var conversion = new DamageConversion();
        Assert.Throws<ArgumentOutOfRangeException>(() => conversion.Add(DamageType.Fire, DamageType.Cold, 50));
    }

    [Fact]
    public void Ignite_bleed_and_poison_use_their_base_damage()
    {
        var hit = DamagePacket.Of(DamageType.Physical, 100, 100).Add(DamageType.Fire, new DamageRange(100, 100))
            .Add(DamageType.Chaos, new DamageRange(50, 50));

        var ignite = Ailments.Ignite(hit);
        var bleed = Ailments.Bleed(hit);
        var poison = Ailments.Poison(hit);

        Assert.Equal(DamageType.Fire, ignite.Type);
        Assert.Equal(90, ignite.DamagePerSecond);
        Assert.Equal(360, ignite.TotalRemainingDamage);

        Assert.Equal(DamageType.Physical, bleed.Type);
        Assert.Equal(70, bleed.DamagePerSecond);
        Assert.Equal(350, bleed.TotalRemainingDamage);

        Assert.Equal(DamageType.Chaos, poison.Type);
        Assert.Equal(45, poison.DamagePerSecond);       // (physical 100 + chaos 50) × 30%
        Assert.Equal(90, poison.TotalRemainingDamage);
    }

    [Fact]
    public void Damage_over_time_keeps_the_fraction_between_ticks()
    {
        var dot = new DamageOverTime(DamageType.Fire, 10, 10);

        var (first, carry) = dot.Tick(0.35);
        var (second, carry2) = dot.Tick(0.35, carry);

        Assert.Equal(3, first);
        Assert.Equal(0.5, carry, 3);
        Assert.Equal(4, second);
        Assert.Equal(0.0, carry2, 3);
    }

    [Fact]
    public void Mitigation_is_applied_per_damage_type()
    {
        var hit = DamagePacket.Of(DamageType.Physical, 100, 100).Add(DamageType.Fire, new DamageRange(100, 100));
        var mitigated = DamageModel.Mitigate(hit, type => type == DamageType.Fire
            ? new DamageModel.ResistanceValue(50)
            : new DamageModel.ResistanceValue(0));

        Assert.Equal(100, mitigated[DamageType.Physical].Min);
        Assert.Equal(50, mitigated[DamageType.Fire].Min);
    }

    [Fact]
    public void Rolling_a_packet_stays_inside_each_range()
    {
        var hit = DamagePacket.Of(DamageType.Physical, 2, 5).Add(DamageType.Fire, new DamageRange(9, 14));
        var random = new Random(1234);

        for (int i = 0; i < 100; i++)
        {
            int total = hit.RollTotal(random);
            Assert.InRange(total, 11, 19);
        }
    }
}
