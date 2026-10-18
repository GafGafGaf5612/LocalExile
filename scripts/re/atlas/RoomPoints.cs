using System.Text.Json;
using GameServer.Data.Terrain;

namespace Atlas;

/// <summary>
/// data/room-points.json: per area, the rooms of its room sets and fill tiles that carry monster points ("mapboss", the area's
/// unique boss; "%area%-…", a MonsterPacks row named after the area), with those points and the room's doodads in
/// room cells. The Gateway finds where the client placed such a room by matching its doodads against the doodads the
/// walk-map recorder captured (AreaPopulation.FitRooms). Agents/docs/protocol/14-monster-packs.md §3.
/// </summary>
static class RoomPoints
{
    public static bool IsMonsterPoint(string name) =>
        name.Equals("mapboss", StringComparison.OrdinalIgnoreCase) || name.StartsWith("%area%-", StringComparison.OrdinalIgnoreCase);

    public static void Run(GameFiles files, List<AreaRow> areas, List<TopologyRow> topologies, string output)
    {
        var byId = topologies.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var areaRooms = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var rooms = new SortedDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var area in areas)
        {
            var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var topo in area.Topologies)
            {
                if (!byId.TryGetValue(topo, out var t) || files.Graph(t.Graph) is not { } graph) continue;
                // The room set (.rs) and the fill tiles (.gft: rooms the generator scatters, e.g. The Coast's bonfires).
                var tsi = files.Tsi(graph.MasterFile);
                var lists = new[] { "RoomSet", "FillTiles" }.Select(k => tsi?.GetValueOrDefault(k)).OfType<string>();
                foreach (var arm in lists.SelectMany(l => files.List(TerrainText.Resolve(graph.MasterFile, l.Trim()), ".arm")))
                {
                    if (files.Room(arm) is not { } room || !room.Points.Any(p => IsMonsterPoint(p.Name))) continue;
                    found.Add(arm);
                    rooms.TryAdd(arm, new
                    {
                        tag = room.Tag,
                        points = room.Points.Where(p => IsMonsterPoint(p.Name)).Select(p => new { name = p.Name, x = p.X, y = p.Y }),
                        doodads = room.Doodads.Select(d => new { model = d.Model, x = d.X, y = d.Y }),
                    });
                }
            }
            if (found.Count > 0) areaRooms[area.Id] = [.. found];
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            note = "Rooms with monster points (mapboss, %area%-<pack>) per area: points and doodads in room cells. scripts/re/atlas room-points.",
            areas = areaRooms,
            rooms,
        }, new JsonSerializerOptions { WriteIndented = false }));
        Console.WriteLine($"{output}: {areaRooms.Count} areas, {rooms.Count} rooms with monster points");
    }
}
