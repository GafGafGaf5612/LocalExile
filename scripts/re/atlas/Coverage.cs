namespace Atlas;

sealed record CoverageOut(int Areas, int AreasWithTopology, Dictionary<string, int> Topologies, int AreasWithConnections,
    int Connections, Dictionary<string, int> ConnectionStatus, Dictionary<string, int> ConnectionReasons,
    int TransitionObjects, int Resolved, int PointOnly, int Internal, int Unused, int Unresolved, Dictionary<string, int> UnresolvedReasons, int FixedWithGrid,
    List<string> UnresolvedFixedConnections);

/// <summary>Counts for the coverage report (end of the run, the JSON's "coverage", and 08-area-transitions.md §6).</summary>
sealed class Coverage
{
    int _internal, _unused, _pointOnly, _areas, _withTopology, _withConnections, _connections, _objects, _resolved, _unresolved, _fixedGrid;
    readonly Dictionary<string, int> _topologies = new(), _connStatus = new(), _connReasons = new(), _reasons = new();
    readonly List<string> _fixedMisses = [];

    static void Inc(Dictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;

    /// <summary>A reason with the variable parts (names, numbers) taken out, so equal causes count together.</summary>
    static string Generalize(string reason) =>
        System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(reason, "\"[^\"]*\"", "\"…\""), @"\([^)]*\)|\d+", "…");

    public void Add(AreaOut a)
    {
        _areas++;
        if (a.Topologies.Count > 0) _withTopology++;
        if (a.Connections.Count > 0) _withConnections++;
        foreach (var t in a.Topologies)
        {
            Inc(_topologies, t.Kind);
            foreach (var x in t.Transitions)
            {
                _objects++;
                if (x.Status == "resolved") _resolved++;
                else if (x.Status == "internal") _internal++;
                else if (x.Status == "unused") _unused++;
                else if (x.Status == "point-only") _pointOnly++;
                else { _unresolved++; Inc(_reasons, Generalize(x.Reason ?? "?")); }
                if (x.Grid is not null) _fixedGrid++;
            }
        }
        bool isFixed = a.Topologies.Count > 0 && a.Topologies.All(t => t.Kind == "fixed");
        foreach (var c in a.ConnectionStatus)
        {
            _connections++;
            Inc(_connStatus, c.Status);
            foreach (var r in c.Reasons ?? []) Inc(_connReasons, r);
            if (isFixed && c.Status is "unresolved" or "partial" or "point-only") _fixedMisses.Add($"{a.Id} → {c.Id} ({c.Status}: {string.Join("; ", c.Reasons ?? [])})");
        }
    }

    public CoverageOut Summary() => new(_areas, _withTopology, _topologies, _withConnections, _connections, _connStatus, _connReasons,
        _objects, _resolved, _pointOnly, _internal, _unused, _unresolved, _reasons, _fixedGrid, _fixedMisses);

    public void Print()
    {
        var s = Summary();
        Console.WriteLine($"areas {s.Areas}, with a topology {s.AreasWithTopology}, with connections {s.AreasWithConnections}");
        Console.WriteLine("topologies (area × topology): " + string.Join(", ", s.Topologies.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value}")));
        Console.WriteLine($"connections (area → destination) {s.Connections}: " + string.Join(", ", s.ConnectionStatus.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value}")));
        foreach (var (k, v) in s.ConnectionReasons.OrderByDescending(x => x.Value)) Console.WriteLine($"  {v,6}  topology-level reason: {k}");
        Console.WriteLine($"transition objects (area × topology × room) {s.TransitionObjects}: resolved {s.Resolved}, entrance points without an object {s.PointOnly}, in-area teleports {s.Internal}, unused by the graph node {s.Unused}, unresolved {s.Unresolved}, with grid placement {s.FixedWithGrid}");
        foreach (var (k, v) in s.UnresolvedReasons.OrderByDescending(x => x.Value)) Console.WriteLine($"  {v,6}  {k}");
        Console.WriteLine($"fixed-layout connections not resolved: {s.UnresolvedFixedConnections.Count}");
        foreach (var m in s.UnresolvedFixedConnections.Take(40)) Console.WriteLine("  " + m);
    }
}
