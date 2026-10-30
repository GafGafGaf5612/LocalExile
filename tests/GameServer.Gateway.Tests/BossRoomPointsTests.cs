using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>Unique bosses at their room points: "mapboss&lt;n&gt;" takes the n-th of WorldAreas Bosses (The Upper Prison's
/// Brutus is mapboss2, The Cavern of Anger's Merveil too), and a boss room takes its actual placement instead of the doodad
/// fit, which can match a repeated room more than once (one unique boss per match).</summary>
public sealed class BossRoomPointsTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "boss-room-points-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    static PackCatalog.AreaPacks Area(params int[] bosses) => new("9_9_9", 9, [], new Dictionary<string, PackCatalog.Pack>(), bosses);

    static List<AreaPopulation.Spawn> Plan(PackCatalog.AreaPacks area, params AreaPopulation.RoomPoint[] points) =>
        AreaPopulation.Plan(area, 400, 400, (_, _) => true, (0, 0), [], points, 1, _ => true, []);

    [Theory]
    [InlineData("mapboss", null)]
    [InlineData("mapboss1", 0)]
    [InlineData("mapboss2", 1)]
    [InlineData("mapboss12", 11)]
    [InlineData("mapbossx", -1)]
    [InlineData("default", -1)]
    public void Boss_points_name_their_place_in_the_list(string name, int? index)
    {
        int? expected = index switch { null => 0, -1 => null, int i => i + 1 };
        Assert.Equal(expected, AreaPopulation.BossPoint(name));
    }

    [Fact]
    public void Numbered_boss_points_take_their_own_boss()
    {
        // The Upper Prison: Bosses [NecromancerBossPrison, BossBrute]; mapboss1 in the Necromancer's room, mapboss2 in
        // the cells before Brutus's arena. Before, "mapboss2" was not a monster point and Brutus never appeared.
        var spawns = Plan(Area(11, 22), new("mapboss2", 200, 100), new("mapboss1", 50, 60));
        Assert.Equal([(22, 200, 100), (11, 50, 60)], spawns.Select(s => (s.Variety, s.X, s.Y)));
        Assert.All(spawns, s => Assert.Equal(MonsterRarity.Unique, s.Rarity));
    }

    [Fact]
    public void A_numbered_boss_point_beyond_the_list_places_nothing()
    {
        var notes = new List<string>();
        var spawns = AreaPopulation.Plan(Area(11), 400, 400, (_, _) => true, (0, 0), [], [new("mapboss2", 10, 10)], 1, _ => true, notes);
        Assert.Empty(spawns);
        Assert.Contains(notes, n => n.Contains("mapboss2"));
    }

    [Fact]
    public void A_boss_room_the_layout_did_not_place_has_no_boss()
    {
        // broken_bridge and broken_bridge_left share their doodads; only one is placed (rooms.json), but both fit it.
        string data = Rooms(siblings: true);
        var (points, _, _) = AreaPopulation.FitRooms("9_9_9", 1, [], data);
        Assert.Equal([("mapboss1", 310, 320)], points.Where(p => p.Name.StartsWith("mapboss")).Select(p => (p.Name, p.X, p.Y)));
    }

    [Fact]
    public void A_boss_room_takes_its_placement_not_every_doodad_fit()
    {
        string data = Rooms();
        var notes = new List<string>();
        var (points, _, captured) = AreaPopulation.FitRooms("9_9_9", 1, notes, data);
        // The boss room's doodads fit twice; the capture placed it once, at (300, 300).
        Assert.Equal([("mapboss1", 310, 320)], points.Where(p => p.Name.StartsWith("mapboss")).Select(p => (p.Name, p.X, p.Y)));
        Assert.Contains(BossRoom, captured);
        // A room without a boss point keeps the fit: both matches.
        Assert.Equal(2, points.Count(p => p.Name == "%area%-nestpack"));
    }

    const string SiblingRoom = "Metadata/Terrain/Test/boss_left.arm";
    const string BossRoom = "Metadata/Terrain/Test/boss.arm", PackRoom = "Metadata/Terrain/Test/pack.arm";

    // Two rooms with the same four doodads, captured twice in the area (at (100, 100) and (300, 300)); rooms.json places
    // the boss room once. With siblings, a second boss room with the same doodads that the layout did not place.
    string Rooms(bool siblings = false)
    {
        string data = Path.Combine(folder, "data");
        Directory.CreateDirectory(Path.Combine(data, "walkability"));
        (int X, int Y)[] doodads = [(1, 2), (5, 9), (12, 3), (8, 14)];
        object Room(string tag, string point, int x, int y) => new
        {
            tag, points = new[] { new { name = point, x, y, angle = 0 } },
            doodads = doodads.Select(d => new { model = "rock.ao", x = d.X, y = d.Y }),
        };
        File.WriteAllText(Path.Combine(data, "room-points.json"), JsonSerializer.Serialize(new
        {
            note = "test",
            areas = new Dictionary<string, string[]> { ["9_9_9"] = siblings ? [BossRoom, SiblingRoom, PackRoom] : [BossRoom, PackRoom] },
            rooms = new Dictionary<string, object> { [BossRoom] = Room("boss", "mapboss1", 10, 20), [SiblingRoom] = Room("boss", "mapboss1", 10, 20), [PackRoom] = Room("pool", "%area%-nestpack", 4, 4) },
        }));
        File.WriteAllText(Path.Combine(data, "walkability", "9_9_9_seed1.doodads.json"), JsonSerializer.Serialize(new
        {
            area = "9_9_9", seed = 1, models = new[] { "rock.ao" },
            doodads = new[] { 100, 300 }.SelectMany(o => doodads.Select(d => new[] { 0, o + d.X, o + d.Y, 0 })).ToArray(),
        }));
        File.WriteAllText(Path.Combine(data, "walkability", "9_9_9_seed1.rooms.json"), JsonSerializer.Serialize(new
        {
            area = "9_9_9", seed = 1, rooms = new[] { new { path = BossRoom, x = 300, y = 300, width = 30, height = 30, code = 0 } },
        }));
        return data;
    }
}
