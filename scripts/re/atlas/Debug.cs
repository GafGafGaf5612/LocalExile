using GameServer.Data.Terrain;

namespace Atlas;

/// <summary>"debug &lt;area id&gt;": per graph node room, every tagged tile slot, the tile set's tiles with that tag, whether
/// they match (codes) and what their .tmd holds.</summary>
static class Debug
{
    public static void Run(GameFiles files, List<AreaRow> areas, List<TopologyRow> topologies, string areaId)
    {
        var area = areas.First(a => a.Id == areaId);
        foreach (var tid in area.Topologies.Take(2))
        {
            var topo = topologies.First(t => t.Id == tid);
            var graph = files.Graph(topo.Graph)!;
            var tsi = files.Tsi(graph.MasterFile)!;
            string rs = TerrainText.Resolve(graph.MasterFile, tsi["RoomSet"]), ts = TerrainText.Resolve(graph.MasterFile, tsi["TileSet"]);
            Console.WriteLine($"{tid} {topo.Graph} rooms {rs} tiles {ts}");
            var tdts = files.List(ts, ".tdt").Select(p => (p, d: files.Tdt(p))).Where(x => x.d is not null).ToList();
            foreach (var node in graph.Nodes.Where(n => n.Names.Count > 0))
                foreach (var arm in files.List(rs, ".arm").Where(p => files.Room(p)?.Tag.Equals(node.Tag, StringComparison.OrdinalIgnoreCase) == true).Distinct())
                {
                    var room = files.Room(arm)!;
                    Console.WriteLine($" node {node.Tag} [{string.Join(",", node.Names)}] room {arm} {room.Width}x{room.Height} problem={room.Problem} points={string.Join(",", room.Points.Select(p => $"{p.Name}({p.X},{p.Y})"))}");
                    foreach (var d in room.Doodads.Where(d => d.Type.Contains("Transition", StringComparison.OrdinalIgnoreCase) || d.Model.Contains("ransition")))
                        Console.WriteLine($"   doodad {d.Model} {d.Type} ({d.X},{d.Y})");
                    foreach (var s in room.Slots.Where(s => s.Key.Tag != 0))
                    {
                        string tag = room.Dict(s.Key.Tag);
                        Console.WriteLine($"   slot ({s.Column},{s.Row}) {s.Key.Width}x{s.Key.Height} tag {tag} E[{string.Join(",", s.Key.Edges.Select(room.Dict).Select(Path.GetFileName))}] G[{string.Join(",", s.Key.Grounds.Select(room.Dict).Select(Path.GetFileName))}] P[{string.Join(" ", s.Key.Points)}]");
                        foreach (var (p, d) in tdts.Where(x => x.d!.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)))
                        {
                            var objs = files.TileObjects(p, d!);
                            Console.WriteLine($"     {p} {d!.Width}x{d.Height} codes [{string.Join(",", KeyMatch.Codes(room, s.Key, d))}] E[{string.Join(",", d.Edges.Select(Path.GetFileName))}] G[{string.Join(",", d.Grounds.Select(Path.GetFileName))}] P[{string.Join(" ", d.Points)}] tgt {d.Tgt} tmd {(objs is null ? "none" : string.Join("; ", objs.Select(o => o.Type.Split('/').Last())))}");
                        }
                    }
                }
        }
    }
}
