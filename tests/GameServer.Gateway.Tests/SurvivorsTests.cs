namespace GameServer.Gateway.Tests;

public class SurvivorsTests
{
    static Survivors.Run NewRun(int level = 1, int seed = 7) => new(level, new Random(seed));

    [Fact]
    public void The_league_and_its_variants_are_survivors()
    {
        Assert.True(Survivors.IsLeague("Survivors"));
        Assert.True(Survivors.IsLeague("Hardcore Survivors"));
        Assert.True(Survivors.IsLeague("SSF Survivors"));
        Assert.False(Survivors.IsLeague("Standard"));
        Assert.False(Survivors.IsLeague(null));
    }

    [Fact]
    public void A_run_starts_with_fireball_and_no_pick_open()
    {
        var run = NewRun();
        Assert.Equal(1, run.Level(Survivors.Upgrade.Fireball));
        Assert.Null(run.Offer);
        Assert.Null(run.OpenPick());
        Assert.Equal("Fireball 1", run.Describe());
    }

    [Fact]
    public void A_pick_offers_three_different_upgrades_and_choosing_one_levels_it()
    {
        var run = NewRun();
        run.PendingPicks = 2;
        var offer = run.OpenPick()!;
        Assert.Equal(3, offer.Count);
        Assert.Equal(3, offer.Distinct().Count());
        Assert.DoesNotContain(Survivors.Upgrade.Mending, offer);
        Assert.Null(run.OpenPick());   // one at a time
        Assert.Equal(1, run.PendingPicks);

        var chosen = run.Choose(2);
        Assert.Equal(offer[1], chosen);
        Assert.Equal(chosen == Survivors.Upgrade.Fireball ? 2 : 1, run.Level(chosen!.Value));
        Assert.Null(run.Offer);
        Assert.Null(run.Choose(1));   // nothing open any more
    }

    [Fact]
    public void A_choice_outside_the_offer_changes_nothing()
    {
        var run = NewRun();
        run.PendingPicks = 1;
        run.OpenPick();
        Assert.Null(run.Choose(0));
        Assert.Null(run.Choose(4));
        Assert.NotNull(run.Offer);
    }

    [Fact]
    public void Maxed_upgrades_are_not_offered_and_mending_comes_when_all_are()
    {
        var run = NewRun();
        foreach (var u in Survivors.Upgrades.Where(u => u.MaxLevel > 0)) run.Levels[u.Id] = u.MaxLevel - 1;
        run.Levels[Survivors.Upgrade.Fireball] = Survivors.Info(Survivors.Upgrade.Fireball).MaxLevel;
        for (int i = 0; i < 20; i++) Assert.DoesNotContain(Survivors.Upgrade.Fireball, run.RollOffer());

        foreach (var u in Survivors.Upgrades) run.Levels[u.Id] = u.MaxLevel;
        Assert.Equal([Survivors.Upgrade.Mending], run.RollOffer());
        run.PendingPicks = 1;
        run.OpenPick();
        Assert.Equal(Survivors.Upgrade.Mending, run.Choose(1));
    }

    [Fact]
    public void Picks_come_only_from_character_levels()
    {
        var run = NewRun(level: 30);
        Assert.Equal(0, run.PendingPicks);   // no free picks at the start, none from kills
        run.LevelledUp(32, 2);
        Assert.Equal(2, run.PendingPicks);
        Assert.Equal(32, run.CharacterLevel);
        run.State = Survivors.Run.Phase.Lost;
        run.LevelledUp(33, 1);
        Assert.Equal(2, run.PendingPicks);   // a level after the run is over gives none
    }

    [Fact]
    public void The_league_brings_the_kills_per_level_down_to_its_own_curve()
    {
        // Level 10 → 11 takes 24 297 experience and a level-10 monster gives 173: 140 kills, brought to 12.
        uint[] starts = [.. Enumerable.Range(0, 12).Select(i => (uint)(i * 24_297))];
        Assert.Equal(140.4 / 12, Survivors.ExperienceMultiplier(10, starts, 173), 2);
        // Level 1 → 2: 525 experience at 20 a kill is 26 kills, brought to 3.
        Assert.Equal(26.25 / 3, Survivors.ExperienceMultiplier(1, [0, 525], 20), 2);
        Assert.Equal([3.0, 4.0, 5.0], [Survivors.KillsPerLevel(1), Survivors.KillsPerLevel(2), Survivors.KillsPerLevel(3)]);
        Assert.Equal(1, Survivors.ExperienceMultiplier(100, starts, 173));
    }

    [Fact]
    public void Rares_give_ten_times_a_normal_monster()
    {
        Assert.Equal(10, Survivors.RarityExperience(GameServer.Core.Protocol.Instance.MonsterRarity.Rare));
        Assert.Equal(1, Survivors.RarityExperience(GameServer.Core.Protocol.Instance.MonsterRarity.Normal));
    }

    [Fact]
    public void Waves_grow_with_time_and_level()
    {
        Assert.Equal(8, Survivors.Crowd(0));
        Assert.True(Survivors.Crowd(300) > Survivors.Crowd(60));
        Assert.Equal(70, Survivors.Crowd(Survivors.RunSeconds));
        Assert.Equal(12, Survivors.MonsterLevel(12, 0));
        Assert.Equal(22, Survivors.MonsterLevel(12, Survivors.RunSeconds));
        Assert.Equal(84, Survivors.MonsterLevel(100, 0));
    }

    [Fact]
    public void Rarity_starts_mostly_normal_and_rares_come_later()
    {
        var rng = new Random(3);
        var start = Enumerable.Range(0, 2000).Select(_ => Survivors.RollRarity(0, rng)).ToList();
        Assert.DoesNotContain(GameServer.Core.Protocol.Instance.MonsterRarity.Rare, start);
        var late = Enumerable.Range(0, 2000).Select(_ => Survivors.RollRarity(Survivors.RunSeconds, rng)).ToList();
        Assert.Contains(GameServer.Core.Protocol.Instance.MonsterRarity.Rare, late);
    }

    [Fact]
    public void The_new_passives_change_the_run()
    {
        var run = NewRun();
        run.Levels[Survivors.Upgrade.Swiftness] = 2;
        run.Levels[Survivors.Upgrade.Toughness] = 5;
        run.Levels[Survivors.Upgrade.Wisdom] = 2;
        run.Levels[Survivors.Upgrade.Multishot] = 1;
        Assert.Equal(16, run.MovementSpeedPct);
        Assert.Equal(0.6, run.DamageTaken, 3);
        Assert.Equal(1.3, run.ExperienceMultiplier, 3);
        Assert.Equal(1, run.ExtraProjectiles);
        Assert.Equal(100, run.Precise(100));   // no Precision: never doubled
        run.Levels[Survivors.Upgrade.Precision] = 5;
        var rolls = Enumerable.Range(0, 1000).Select(_ => run.Precise(100)).ToList();
        Assert.Contains(200, rolls);
        Assert.Contains(100, rolls);
    }

    [Fact]
    public void Passives_scale_the_skills()
    {
        var run = NewRun();
        Assert.Equal(1.0, run.DamageMultiplier);
        run.Levels[Survivors.Upgrade.Might] = 2;
        run.Levels[Survivors.Upgrade.Haste] = 5;
        run.Levels[Survivors.Upgrade.Reach] = 1;
        Assert.Equal(1.4, run.DamageMultiplier, 3);
        Assert.Equal(1.3 / 1.4, Survivors.FireballSeconds(1, run.Haste), 3);
        Assert.Equal(Survivors.FlameRingRadius(1, 1) * 1.15, Survivors.FlameRingRadius(1, run.Reach), 0);
        Assert.Equal(3, Survivors.FireballProjectiles(5));
    }

    [Fact]
    public void Choice_lines_say_what_the_next_level_gives()
    {
        var run = NewRun();
        Assert.Equal("1. Fireball (level 2) - 2 projectile(s), 0.66x damage", Survivors.ChoiceLine(1, Survivors.Upgrade.Fireball, run));
        Assert.StartsWith("2. Flame Ring (new) - ", Survivors.ChoiceLine(2, Survivors.Upgrade.FlameRing, run));
        // A sender-less chat line shows its text up to ": " as a speaker name: the choices must not have one.
        Assert.All(Survivors.Upgrades, u => Assert.DoesNotContain(": ", Survivors.ChoiceLine(1, u.Id, run)));
    }

    [Fact]
    public void A_survivors_map_opens_one_portal()
    {
        var beach = new MapDevice.MapArea(0x1111, "MapAtlasBeach", "Beach", 2);
        var open = MapDevice.Activate("SurvivorsTest", beach, 1);
        Assert.Equal([0], open.Left);
        Assert.Equal((open, 0), MapDevice.TakePortal("SurvivorsTest", 0));
        Assert.Null(MapDevice.OpenOf("SurvivorsTest"));   // used up: no way back in
        Assert.Equal(MapDevice.PortalCount, MapDevice.Activate("SurvivorsTest", beach).PortalsLeft);
        MapDevice.Close("SurvivorsTest");
    }

    [Fact]
    public void Time_is_minutes_and_seconds()
    {
        Assert.Equal("00:05", Survivors.Time(5.9));
        Assert.Equal("15:00", Survivors.Time(Survivors.RunSeconds));
    }
}
