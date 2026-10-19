namespace GameServer.Gateway.Tests;

public class QuestTests
{
    const string Strand = "1_1_1", Town = "1_1_town", Coast = "1_1_2";
    const string Hillock = "Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal";

    [Fact]
    public void Killing_Hillock_on_the_Strand_sets_KilledHillock()
    {
        Assert.Equal(["a1q1-KilledHillock"], Quests.OnMonsterKilled(Strand, Hillock));
        Assert.Empty(Quests.OnMonsterKilled(Strand, "Metadata/Monsters/Zombies/ZombieBite"));
        Assert.Empty(Quests.OnMonsterKilled(Coast, Hillock));
    }

    [Fact]
    public void The_Strands_way_into_Lioneyes_Watch_needs_Hillock_dead()
    {
        Assert.Equal("a1q1-KilledHillock", Quests.FlagToEnter(Strand, Town));
        Assert.Null(Quests.FlagToEnter(Coast, Town));
        Assert.Null(Quests.FlagToEnter(Town, Strand));
    }


    [Fact]
    public void Quest_flags_go_to_the_client_as_their_QuestFlags_rows()
    {
        // data/tables/QuestFlags.json: row index = the flag the client's Player component keeps (16-waypoints.md §3).
        var rows = Quests.ParseRows("""[{"Id":"a1q1-KilledHillock"},{"Id":"a1q1-SeenHillock"},{"Id":"a1q1-QuestRewardTaken"}]""");

        Assert.Equal([0, 2], Quests.Rows(["a1q1-QuestRewardTaken", "a1q1-KilledHillock", "no-such-flag"], rows));
    }
}
