using System.Text.Json;

namespace GameServer.Gateway.Tests;

/// <summary>
/// The campaign towns' NPCs (TownNpcs.cs, 17-npcs.md §2): their rooms' NPC lines through the client's room placement.
/// The tests that read game data (data/tables, data/room-points.json, the Lioneye's Watch walk-map capture) return
/// early without it, as the other catalogue tests do.
/// </summary>
public class TownNpcsTests
{
    static readonly Dictionary<string, (int X, int Y)> MeasuredOnTheMinimap = new()
    {
        ["Metadata/NPC/Act1/Tarkleigh"] = (388, 182),
        ["Metadata/NPC/Act1/Nessa"] = (346, 271),
        ["Metadata/NPC/Act1/Bestel"] = (361, 246),
    };

    [Fact]
    public void R270_places_a_room_cell_at_y_and_width_minus_one_minus_x()
    {
        var room = new TownNpcs.Room("r.arm", 414, 690, 3);
        Assert.Equal((389, 183), room.Cell(230, 389));
        Assert.Equal((0, 413), room.Cell(0, 0));
        Assert.Equal(3 * MathF.PI / 2 - 0.155698f, room.Angle(-0.155698f), 4);
        // I keeps the cell (plus the room's offset) and the angle, wrapped into [0, 2π).
        var plain = new TownNpcs.Room("r.arm", 100, 100, 0, 10, 20);
        Assert.Equal((15, 27), plain.Cell(5, 7));
        Assert.Equal(2 * MathF.PI - 1, plain.Angle(-1), 4);
    }

    [Fact]
    public void Lioneyes_Watch_lands_within_a_cell_of_its_minimap_measurement()
    {
        var town = TownNpcs.Of(AreaLayouts.LioneyesWatch)!;
        var placed = town.Placements().ToList();
        Assert.Equal(MeasuredOnTheMinimap.Keys.Order(), placed.Select(p => p.Id).Order());
        foreach (var p in placed)
        {
            var (x, y) = MeasuredOnTheMinimap[p.Id];
            Assert.InRange(p.X - x, -1, 1);
            Assert.InRange(p.Y - y, -1, 1);
        }
        Assert.Same(placed[0].Id, NpcCatalog.In(AreaLayouts.LioneyesWatch)[0].Id);
    }

    [Fact]
    public void Every_campaign_town_has_its_npcs_inside_its_grid()
    {
        Assert.Equal(12, TownNpcs.All.Length);
        Assert.Equal(TownNpcs.All.Length, TownNpcs.All.Select(t => t.Area).Distinct().Count());
        foreach (var town in TownNpcs.All)
        {
            var room = town.Room;
            var (w, h) = room.Code % 2 == 1 ? (room.Height, room.Width) : (room.Width, room.Height);
            Assert.NotEmpty(town.Npcs);
            foreach (var p in town.Placements())
            {
                Assert.InRange(p.X, room.OffsetX, room.OffsetX + w - 1);
                Assert.InRange(p.Y, room.OffsetY, room.OffsetY + h - 1);
                Assert.InRange(p.Rotation, 0, 2 * MathF.PI);
            }
            Assert.Equal(town.Npcs.Length, NpcCatalog.In(town.Area).Count);
        }
    }

    [Fact]
    public void Npc_blob_carries_a_room_model_and_height()
    {
        var npc = new GameServer.Core.Protocol.Instance.NpcObject(1, 2, GameServer.Core.Protocol.Instance.NpcObject.BaseComponents,
            Model: "a.ao", Height: -335.938f).Blob();
        string hex = Convert.ToHexString(npc);
        // Positioned: flags0 0x04, flags1 0x02, then i32 −43 (z / 7.8125) and f32 z before flags1's two bytes.
        Assert.Contains("0000" + "04" + "02" + "D5FFFFFF" + Convert.ToHexString(BitConverter.GetBytes(-335.938f)) + "0001", hex);
        // Animated: bit0 + u32 length + UTF-16 path.
        Assert.Contains("01" + "04000000" + "61002E0061006F00", hex);
    }

    /// <summary>Each row copies a line of its room: the same NPC type at the same room cell in data/room-points.json
    /// (scripts/re/atlas room-points, which keeps every town room with its server objects).</summary>
    [Fact]
    public void Every_row_is_a_line_of_its_room()
    {
        if (FindUp("data/room-points.json") is not { } file) return;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        var rooms = doc.RootElement.GetProperty("rooms");
        var areas = doc.RootElement.GetProperty("areas");
        foreach (var town in TownNpcs.All)
        {
            Assert.Contains(town.Room.Arm, areas.GetProperty(town.AreaId).EnumerateArray().Select(a => a.GetString()));
            var lines = rooms.GetProperty(town.Room.Arm).GetProperty("doodads").EnumerateArray()
                .Where(d => d.TryGetProperty("type", out var t) && t.GetString() is not null)
                .Select(d => (d.GetProperty("type").GetString(), (int)d.GetProperty("x").GetDouble(), (int)d.GetProperty("y").GetDouble()))
                .ToHashSet();
            foreach (var n in town.Npcs) Assert.Contains((n.Id, n.X, n.Y), lines);
        }
    }

    /// <summary>The client's own placement of Lioneye's Watch's room (its walk-map capture): every plain doodad of
    /// beach_town_unique_normal.arm stands where <see cref="TownNpcs.Room.Cell"/> puts its line.</summary>
    [Fact]
    public void The_room_placement_is_the_clients_in_Lioneyes_Watch()
    {
        if (FindUp("data/room-points.json") is not { } file || FindUp("data/walkability/1_1_town_seed1.doodads.json") is not { } capture) return;
        var town = TownNpcs.Of(AreaLayouts.LioneyesWatch)!;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        using var cap = JsonDocument.Parse(File.ReadAllBytes(capture));
        var models = cap.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetString()!.ToLowerInvariant()).ToArray();
        var placed = cap.RootElement.GetProperty("doodads").EnumerateArray()
            .Select(d => (models[d[0].GetInt32()], d[1].GetInt32(), d[2].GetInt32())).ToHashSet();
        var plain = doc.RootElement.GetProperty("rooms").GetProperty(town.Room.Arm).GetProperty("doodads").EnumerateArray()
            .Where(d => !d.TryGetProperty("type", out var t) || t.GetString() is null).ToList();
        Assert.True(plain.Count > 20);
        int found = plain.Count(d =>
        {
            var (x, y) = town.Room.Cell((int)d.GetProperty("x").GetDouble(), (int)d.GetProperty("y").GetDouble());
            return placed.Contains((d.GetProperty("model").GetString()!.ToLowerInvariant(), x, y));
        });
        Assert.Equal(plain.Count, found);
    }

    /// <summary>Every placed NPC resolves to a row the Gateway can create, with dialogue: an Id without its own .ot takes
    /// its Metadata's components (Act 6's Tarkleigh on Act 1's).</summary>
    [Fact]
    public void Every_placed_npc_can_be_created_and_talks()
    {
        if (FindUp("data/tables/NPCs.json") is not { } table || FindUp("data/object-types.json") is null) return;
        var catalog = NpcCatalog.Load(Path.GetDirectoryName(Path.GetDirectoryName(table))!);
        Assert.Null(catalog.Error);
        foreach (var town in TownNpcs.All)
            foreach (var p in town.Placements())
            {
                var npc = catalog.Place(p);
                Assert.True(npc is { Supported: true }, $"{town.AreaId}: {p.Id}");
                Assert.Equal(p.Model, npc!.Model);
                Assert.Equal(p.Height, npc.Height);
                Assert.True(catalog.TalkOf(npc, 0) is not null, $"{town.AreaId}: {p.Id} has no NPCTalk rows");
            }
        Assert.Equal("Tarkleigh", catalog.Find("Metadata/NPC/Act6/Tarkleigh")!.Name);
    }

    static string? FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        return null;
    }
}
