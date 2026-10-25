using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

/// <summary>
/// Monsters that rise from the ground (MonsterEmerge.cs): which varieties are created buried, how long they take to
/// rise, and that a buried monster stays out of the connection's monsters (so nothing hits it) until the player is near.
/// </summary>
public class MonsterEmergeTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), $"localexile-emerge-{Guid.NewGuid():n}");

    const string GravelEaterType = "Metadata/Monsters/SandSpitterEmerge/SandSpitterEmerge";
    static readonly string[] EmergeComponents = [.. MonsterObject.BaseComponents, "Transitionable"];

    public MonsterEmergeTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "tables"));
        // The shapes scripts/re/ot_components.py, scripts/re/monster_takehit.py and the table extractor write.
        File.WriteAllText(Path.Combine(_folder, "object-types.json"), $$"""
            {"types": [
              {"type": "{{GravelEaterType}}", "read": 12, "basic_actions": ["Move", "Emerge", "Emerge2"],
               "components": ["Positioned", "Stats", "ObjectMagicProperties", "BaseEvents", "Pathfinding", "Buffs", "Life",
                              "Animated", "Inventories", "Actor", "Monster", "Transitionable", "Render", "Targetable"]},
              {"type": "Metadata/Monsters/NoAction", "read": 12, "basic_actions": ["Move"],
               "components": ["Positioned", "Stats", "ObjectMagicProperties", "BaseEvents", "Pathfinding", "Buffs", "Life",
                              "Animated", "Inventories", "Actor", "Monster", "Transitionable"]},
              {"type": "Metadata/Monsters/ClientOnly", "read": 11, "basic_actions": ["Emerge"],
               "components": ["Positioned", "Stats", "ObjectMagicProperties", "BaseEvents", "Pathfinding", "Buffs", "Life",
                              "Animated", "Inventories", "Actor", "Monster", "Transitionable"]}
            ]}
            """);
        File.WriteAllText(Path.Combine(_folder, "monster-takehit.json"), """
            {"Metadata/Monsters/SandSpitterEmerge/SandSpitterEmergeStandard":
               {"index": 12, "indexes": [12], "lengths": [1000], "emerge": {"clip": "emerge", "index": 10, "length": 1000}},
             "Metadata/Monsters/Slow": {"emerge": {"clip": "grasp_emerge", "index": 38, "length": 3666}}}
            """);
        File.WriteAllText(Path.Combine(_folder, "tables", "ActionTypes.json"), """
            [{"Id": "Move", "HASH16": 10505}, {"Id": "Emerge", "HASH16": 16641}, {"Id": "Emerge2", "HASH16": 9115}]
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    static MonsterCatalog.Variety Variety(string id = "Metadata/Monsters/SandSpitterEmerge/SandSpitterEmergeStandard",
        string type = GravelEaterType, IReadOnlyList<string>? components = null, byte states = 1) =>
        new(513, id, "Gravel Eater", type, null, components ?? EmergeComponents, TransitionStates: states, AggroRange: 105);

    MonsterEmerge Emerge() => new(_folder);

    [Fact]
    public void The_emerge_action_is_the_basic_actions_skill_id()
    {
        Assert.Equal((ushort)0x0101, Emerge().SkillId);
    }

    [Fact]
    public void A_type_with_the_emerge_action_and_two_transitionable_states_is_created_buried()
    {
        var e = Emerge();
        var gravelEater = Variety();
        Assert.True(e.Burrows(gravelEater));
        Assert.Equal(1, e.CreateState(gravelEater, buried: true));
        // Kept after rising: created transitioned, so the client runs create_transitioned (EnableTargetable).
        Assert.Equal(2, e.CreateState(gravelEater, buried: false));
    }

    [Fact]
    public void Types_the_server_cannot_raise_keep_their_own_state()
    {
        var e = Emerge();
        var noAction = Variety("Metadata/Monsters/X/NoActionEmerge", "Metadata/Monsters/NoAction");
        var clientOnly = Variety("Metadata/Monsters/X/ClientOnlyEmerge", "Metadata/Monsters/ClientOnly");
        var threeStates = Variety(states: 3);
        Assert.False(e.Burrows(noAction));
        Assert.False(e.Burrows(clientOnly));
        Assert.False(e.Burrows(threeStates));
        Assert.Equal(3, e.CreateState(threeStates, buried: false));
        Assert.Equal(1, e.CreateState(noAction, buried: false));
    }

    [Fact]
    public void Packs_place_emerge_monsters_only_when_they_can_rise()
    {
        var e = Emerge();
        Assert.True(e.CanPlace(Variety()));
        // An "Emerge" variety the server cannot raise would stay buried for good.
        Assert.False(e.CanPlace(Variety("Metadata/Monsters/X/NoActionEmerge", "Metadata/Monsters/NoAction")));
        // Without a Transitionable it is an ordinary monster.
        Assert.True(e.CanPlace(Variety("Metadata/Monsters/X/DwellerEmerge", "Metadata/Monsters/Dweller", MonsterObject.BaseComponents)));
    }

    [Fact]
    public void Rising_takes_the_emerge_clip()
    {
        var e = Emerge();
        Assert.Equal(1.0, e.Seconds(Variety()));
        Assert.Equal(3.666, e.Seconds(Variety("Metadata/Monsters/Slow")), 3);
        Assert.Equal(MonsterEmerge.DefaultSeconds, e.Seconds(Variety("Metadata/Monsters/Unknown")));
    }

    [Fact]
    public void Without_the_data_nothing_burrows()
    {
        var e = new MonsterEmerge(Path.Combine(_folder, "missing"));
        Assert.Null(e.SkillId);
        Assert.False(e.Burrows(Variety()));
        Assert.Equal(1, e.CreateState(Variety(), buried: false));
    }

    static SpawnedMonster Buried(int x, int y) =>
        new("Gravel Eater@2", new WalkEstimator(x, y, 20, null, 2000, 2000), aggroRange: 105, stopRange: 3, maxLife: 30)
        {
            Variety = Variety(), Level = 2,
        };

    [Fact]
    public void A_buried_monster_rises_only_when_the_player_is_near_and_then_joins_the_monsters()
    {
        var e = Emerge();
        var conn = new ClientState(new AreaLayout(1, "test", 200, 200, 10, 10, null, []), 1, 1);
        var near = Buried(100, 100);
        var far = Buried(100, 160);
        lock (conn.Monsters)
        {
            MonsterEmerge.Buried(conn)[0x3000] = near;
            MonsterEmerge.Buried(conn)[0x3001] = far;
            Assert.True(MonsterEmerge.StartWatch(conn));
            Assert.False(MonsterEmerge.StartWatch(conn));   // one watch per connection
        }
        Assert.Empty(conn.Monsters);   // nothing targets a buried monster

        var risen = e.RiseLocked(conn, (100, 100 - MonsterEmerge.Range), now: 5, out bool watching);

        var (id, m, at, seconds) = Assert.Single(risen);
        Assert.Equal((0x3000u, (100, 100), 1.0), (id, at, seconds));
        Assert.Same(near, conn.Monsters[0x3000]);
        Assert.True(m.Aggro);
        Assert.True(m.BusyUntil > 6);   // busy until the clip has played
        Assert.True(watching);
        Assert.Same(far, Assert.Single(MonsterEmerge.Buried(conn)).Value);

        Assert.Single(e.RiseLocked(conn, (100, 150), now: 7, out watching));
        Assert.False(watching);         // none left: the watch ends
        Assert.Empty(MonsterEmerge.Buried(conn));
        Assert.True(MonsterEmerge.StartWatch(conn));
    }

    [Fact]
    public void Buried_monsters_are_kept_buried_with_the_instance()
    {
        var risen = Buried(10, 10);
        var buried = Buried(50, 50);
        var saved = SavedMonster.Save([risen], now: 0, buried: [buried]);
        Assert.Equal([false, true], saved.Select(s => s.Buried));
        Assert.Equal((50, 50), (saved[1].X, saved[1].Y));
    }
}
