using System.Runtime.CompilerServices;
using System.Text.Json;

/// <summary>
/// Monsters that rise from the ground (Gravel Eaters, Dripping Dead, the Twilight Strand's Drowned …;
/// Agents/docs/protocol/10-chat-and-commands.md §5 "Emerge monsters"). Their type has a Transitionable without
/// <c>num_states</c> (2 states: 1 untransitioned, 2 transitioned) whose client script pauses the emerge animation at
/// creation and makes them untargetable, and the basic action <c>Emerge</c> (ActionTypes 16641 = 0x4101, skill id 0x0101).
/// They are created in state 1 (buried), kept out of the connection's monsters (so no attack, skill, projectile or aura
/// reaches them and the AI leaves them alone), and when the player comes within <see cref="Range"/> cells (or their
/// aggro range, if shorter) they rise: the Emerge action (S→C 0x1ab, which plays the .act's Emerge clip), then
/// Transitionable → 2 (0x2ba, whose <c>on_transition</c> runs EnableTargetable), and they join the monsters, busy until
/// the clip has played. Data: object-types.json (<c>basic_actions</c>, scripts/re/ot_components.py),
/// monster-takehit.json (<c>emerge</c>: the clip's length, scripts/re/monster_takehit.py), ActionTypes.json.
/// </summary>
sealed class MonsterEmerge(string folder = "data")
{
    /// <summary>How near (grid cells) the player comes before a buried monster rises; its aggro range when that is
    /// shorter. The official server decides it in the monster's .ais file, which the client does not have: 24 cells
    /// keeps the emerge on screen.</summary>
    public const int Range = 24;

    /// <summary>How long a monster without a known Emerge clip takes to rise.</summary>
    public const double DefaultSeconds = 1.5;

    /// <summary>The Transitionable state a buried monster is created in (untransitioned).</summary>
    public const byte BuriedState = 1;

    private readonly Lazy<HashSet<string>> _types = new(() => LoadTypes(folder));
    private readonly Lazy<Dictionary<string, double>> _clips = new(() => LoadClips(folder));
    private readonly Lazy<ushort?> _action = new(() => LoadAction(folder));

    /// <summary>The Emerge basic action's skill id (ActionTypes HASH16 &amp; 0x3fff: 0x0101), null without the table.</summary>
    public ushort? SkillId => _action.Value is ushort hash ? (ushort)(hash & 0x3fff) : null;

    /// <summary>Whether this variety is created buried and rises when the player comes near: its type reads a
    /// Transitionable without num_states and has the Emerge basic action.</summary>
    public bool Burrows(MonsterCatalog.Variety v) =>
        v.TransitionStates <= 1 && v.ComponentsOrBase.Contains("Transitionable") && SkillId is not null && _types.Value.Contains(v.BaseType);

    /// <summary>Whether area packs may place this variety: not an "Emerge" variety that would stay buried for good (a
    /// Transitionable but no Emerge action the server can start).</summary>
    public bool CanPlace(MonsterCatalog.Variety v) =>
        Burrows(v) || !(v.Id.Contains("Emerge", StringComparison.OrdinalIgnoreCase) && v.ComponentsOrBase.Contains("Transitionable"));

    /// <summary>The Transitionable state of a new monster: buried (1), risen (2) for an emerge monster that is not
    /// buried, else the type's own (MonsterCatalog: num_states, 1 when the .ot gives none).</summary>
    public byte CreateState(MonsterCatalog.Variety v, bool buried) =>
        buried ? BuriedState : Burrows(v) ? RisenState(v) : v.TransitionStates;

    /// <summary>The fully transitioned state: num_states, 2 when the .ot gives none.</summary>
    public static byte RisenState(MonsterCatalog.Variety v) => (byte)Math.Max(2, (int)v.TransitionStates);

    /// <summary>How long the variety's Emerge clip plays, in seconds (<see cref="DefaultSeconds"/> when unknown).</summary>
    public double Seconds(MonsterCatalog.Variety v) =>
        _clips.Value.TryGetValue(v.Id, out double s) && s > 0 ? s : DefaultSeconds;

    /// <summary>The buried monsters of a connection by object id: not in <see cref="ClientState.Monsters"/> until they
    /// rise. Guarded by the Monsters lock.</summary>
    public static SortedDictionary<uint, SpawnedMonster> Buried(ClientState conn) => Grounds.GetValue(conn, _ => new()).Monsters;

    /// <summary>Marks the connection's watch as running; false when it already was. Under the Monsters lock.</summary>
    public static bool StartWatch(ClientState conn)
    {
        var g = Grounds.GetValue(conn, _ => new());
        if (g.Watching) return false;
        return g.Watching = true;
    }

    /// <summary>The buried monsters within reach of the player rise: each leaves <see cref="Buried"/> for the
    /// connection's monsters, aggroed and busy for its clip. Returns them with where they stand; the caller sends the
    /// action and the transition. When none is left buried the watch ends (<paramref name="watching"/> false). Under the
    /// Monsters lock.</summary>
    public List<(uint Id, SpawnedMonster Monster, (int X, int Y) At, double Seconds)> RiseLocked(ClientState conn,
        (int X, int Y) player, double now, out bool watching)
    {
        var g = Grounds.GetValue(conn, _ => new());
        var risen = new List<(uint, SpawnedMonster, (int, int), double)>();
        foreach (var (id, m) in g.Monsters)
        {
            var at = m.Walk.Position(now);
            double d = Math.Sqrt((double)(at.X - player.X) * (at.X - player.X) + (double)(at.Y - player.Y) * (at.Y - player.Y));
            if (d > (m.AggroRange > 0 ? Math.Min(Range, m.AggroRange) : Range)) continue;
            double seconds = m.Variety is { } v ? Seconds(v) : DefaultSeconds;
            risen.Add((id, m, at, seconds));
        }
        foreach (var (id, m, _, seconds) in risen)
        {
            g.Monsters.Remove(id);
            m.Aggro = true;
            m.LastTarget = null;
            // A little past the clip, so the action's end (0x1ac) goes out before the AI's first order.
            m.BusyUntil = now + seconds + 0.3;
            conn.Monsters[id] = m;
        }
        watching = g.Watching = g.Monsters.Count > 0;
        return risen;
    }

    sealed class Ground
    {
        public readonly SortedDictionary<uint, SpawnedMonster> Monsters = [];
        public bool Watching;
    }

    static readonly ConditionalWeakTable<ClientState, Ground> Grounds = new();

    // object-types.json types that read a Transitionable and have the Emerge basic action.
    static HashSet<string> LoadTypes(string folder)
    {
        string path = Path.Combine(folder, "object-types.json");
        if (!File.Exists(path)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var types = new HashSet<string>();
        foreach (var t in doc.RootElement.GetProperty("types").EnumerateArray())
        {
            if (!t.TryGetProperty("basic_actions", out var actions) || actions.ValueKind != JsonValueKind.Array) continue;
            if (!actions.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.String && a.GetString() == "Emerge")) continue;
            int read = t.GetProperty("read").GetInt32();
            if (t.GetProperty("components").EnumerateArray().Take(read).Any(c => c.GetString() == "Transitionable"))
                types.Add(t.GetProperty("type").GetString()!);
        }
        return types;
    }

    // monster-takehit.json: each variety's "emerge" clip length (ms) → seconds.
    static Dictionary<string, double> LoadClips(string folder)
    {
        string path = Path.Combine(folder, "monster-takehit.json");
        if (!File.Exists(path)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var clips = new Dictionary<string, double>();
        foreach (var p in doc.RootElement.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("emerge", out var e) && e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("length", out var l) && l.ValueKind == JsonValueKind.Number)
                clips[p.Name] = l.GetDouble() / 1000;
        return clips;
    }

    // ActionTypes.json: the Emerge row's HASH16.
    static ushort? LoadAction(string folder)
    {
        string path = Path.Combine(folder, "tables", "ActionTypes.json");
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var r in doc.RootElement.EnumerateArray())
            if (r.TryGetProperty("Id", out var id) && id.GetString() == "Emerge" && r.TryGetProperty("HASH16", out var h) && h.ValueKind == JsonValueKind.Number)
                return (ushort)h.GetInt32();
        return null;
    }
}
