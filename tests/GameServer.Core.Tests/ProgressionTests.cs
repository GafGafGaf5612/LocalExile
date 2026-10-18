using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Core.Tests;

public class ProgressionTests
{
    // ExperienceLevels.dat's first levels.
    static readonly uint[] Starts = [0, 525, 1760, 3781, 7184];

    [Fact]
    public void Level_from_experience()
    {
        Assert.Equal(1, Progression.LevelFor(0, Starts));
        Assert.Equal(1, Progression.LevelFor(524, Starts));
        Assert.Equal(2, Progression.LevelFor(525, Starts));
        Assert.Equal(4, Progression.LevelFor(5000, Starts));
        Assert.Equal(5, Progression.LevelFor(1_000_000, Starts));
    }

    [Fact]
    public void Experience_stops_at_the_last_level()
    {
        Assert.Equal(600u, Progression.AddExperience(500, 100, Starts));
        Assert.Equal(7184u, Progression.AddExperience(7000, 1000, Starts));
    }

    [Fact]
    public void Level_penalty_has_a_safe_zone()
    {
        // Level 10: safe zone 3 levels.
        Assert.Equal(1, Progression.LevelPenalty(10, 13));
        Assert.Equal(1, Progression.LevelPenalty(10, 7));
        // 5 levels apart: 2 past the safe zone → (15 / (15 + 2^2.5))^1.5.
        Assert.Equal(Math.Pow(15 / (15 + Math.Pow(2, 2.5)), 1.5), Progression.LevelPenalty(10, 15), 9);
        Assert.True(Progression.LevelPenalty(1, 60) >= 0.01);
    }

    [Fact]
    public void Monster_experience()
    {
        // DefaultMonsterStats level 2 (20 experience) for a level 1 character: within the safe zone.
        Assert.Equal(20u, Progression.MonsterExperience(20, 100, MonsterRarity.Normal, 1, 2));
        Assert.Equal(40u, Progression.MonsterExperience(20, 100, MonsterRarity.Magic, 1, 2));
        Assert.Equal(100u, Progression.MonsterExperience(20, 100, MonsterRarity.Rare, 1, 2));
        Assert.Equal(0u, Progression.MonsterExperience(20, 0, MonsterRarity.Normal, 1, 2));
    }

    [Fact]
    public void Passive_points_per_level()
    {
        Assert.Equal(0, Progression.PassivePoints(1));
        Assert.Equal(9, Progression.PassivePoints(10));
        Assert.Equal(99, Progression.PassivePoints(100));
    }
}
