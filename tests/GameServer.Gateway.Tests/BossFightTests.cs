using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class BossFightTests
{
    const string Hillock = "Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal";

    [Fact]
    public void Hillock_draws_his_sword_at_half_life()
    {
        var stage = BossFights.For(Hillock);
        Assert.NotNull(stage);
        Assert.Equal((50, 2612, (byte)2), (stage.AtLifePct, stage.SkillEffect, stage.TransitionTo));
        Assert.Null(BossFights.For("Metadata/Monsters/Zombies/ZombieBite"));
    }

    [Fact]
    public void The_hit_that_reaches_the_threshold_starts_the_stage_and_cannot_kill()
    {
        var stage = BossFights.For(Hillock);
        Assert.Equal((200u, false), BossFights.Damage(311, 311, 111, stage));
        Assert.Equal((155u, true), BossFights.Damage(200, 311, 45, stage));
        // A killing blow before the stage leaves 1 life and starts it.
        Assert.Equal((1u, true), BossFights.Damage(311, 311, 1000, stage));
        // After the stage (null), hits kill as usual.
        Assert.Equal((0u, false), BossFights.Damage(311, 311, 1000, null));
    }

    [Fact]
    public void Rarity_adds_its_monster_mods_life()
    {
        // Mods.dat MonsterMagic2 / MonsterRare2 / MonsterUnique2: monster_life_+%_final_from_rarity.
        Assert.Equal(39, MonsterCatalog.LifeWithRarity(39, MonsterRarity.Normal));
        Assert.Equal(96, MonsterCatalog.LifeWithRarity(39, MonsterRarity.Magic));
        Assert.Equal(191, MonsterCatalog.LifeWithRarity(39, MonsterRarity.Rare));
        Assert.Equal(311, MonsterCatalog.LifeWithRarity(39, MonsterRarity.Unique));
    }

    [Fact]
    public void Rarity_hits_harder_and_attacks_faster()
    {
        // MonsterUnique5 monster_rarity_damage_+%_final 70, MonsterUnique8 attack speed +33% and 33% less damage.
        var (damage, speed) = MonsterCatalog.RarityAttack(MonsterRarity.Unique);
        Assert.Equal(1.7 * 0.67, damage, 3);
        Assert.Equal(1.33, speed, 3);
        Assert.Equal((1.0, 1.0), MonsterCatalog.RarityAttack(MonsterRarity.Normal));
        Assert.Equal(1.3 * 0.8, MonsterCatalog.RarityAttack(MonsterRarity.Magic).DamageFactor, 3);
    }

    [Fact]
    public void A_room_angle_follows_the_rooms_turn()
    {
        const float a = 5.2948f;   // -0.98839 + 2π: the Strand's force wall, room not turned
        Assert.Equal(a, AreaPopulation.TurnAngle(1, 0, 0, 1, -0.98839f), 3);
        Assert.Equal(MathF.PI / 2 + 1, AreaPopulation.TurnAngle(0, -1, 1, 0, 1), 3);
        Assert.Equal(MathF.PI - 1, AreaPopulation.TurnAngle(-1, 0, 0, 1, 1), 3);
    }
}
