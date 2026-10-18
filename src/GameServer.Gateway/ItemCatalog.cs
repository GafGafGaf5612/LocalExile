using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// Item types: BaseItemTypes rows from the extracted tables (Id, Name, HASH32, size, visual identity → ItemVisualIdentity
/// HASH16), each type's components from data/item-types.json (`python -I scripts/re/ot_components.py --items --json
/// data/item-types.json`), and flask charges from ComponentCharges. All game data: it stays in data/ and loads on first use.
/// </summary>
sealed class ItemCatalog
{
    public ItemCatalog(string folder)
    {
        _rows = new(() => Load(folder));
        _byHash = new(() => _rows.Value.Rows.GroupBy(t => t.Hash).ToDictionary(g => g.Key, g => g.First()));
        _gemEffects = new(() => LoadGemEffects(folder));
        _requirements = new(() => LoadRequirements(folder));
    }

    public sealed record BaseType(int Row, string Id, string Name, uint Hash, byte Width, byte Height, ushort VisualIdentity,
        IReadOnlyList<string> Components, int MaxCharges, string Class = "", byte Stance = 0xff)
    {
        public bool Supported => Components.Count > 0 && Item.CanSerialize(Components);
        public override string ToString() => $"{Name} ({Id}, row {Row})";
    }

    private readonly Lazy<(List<BaseType> Rows, string? Error)> _rows;

    public string? Error => _rows.Value.Error;

    /// <summary>The type of an item (its BaseItemTypes HASH32).</summary>
    public BaseType? ByHash(uint hash) => _byHash.Value.GetValueOrDefault(hash);

    private readonly Lazy<Dictionary<uint, BaseType>> _byHash;

    public ItemCatalog() : this("data") { }

    public BaseType? Row(int row) => row >= 0 && row < _rows.Value.Rows.Count ? _rows.Value.Rows[row] : null;

    /// <summary>A BaseItemTypes row by number, or by Id: exact, then the last path segment, then the display name, then
    /// any part of the Id. Several matches: the first one the serializer supports.</summary>
    public BaseType? Find(string query)
    {
        var rows = _rows.Value.Rows;
        if (int.TryParse(query, out int row)) return Row(row);
        foreach (Func<BaseType, bool> match in new Func<BaseType, bool>[]
        {
            t => t.Id.Equals(query, StringComparison.OrdinalIgnoreCase),
            t => t.Id.EndsWith("/" + query, StringComparison.OrdinalIgnoreCase),
            t => t.Name.Equals(query, StringComparison.OrdinalIgnoreCase),
            t => t.Id.Contains(query, StringComparison.OrdinalIgnoreCase),
        })
        {
            var hits = rows.Where(match).ToList();
            if (hits.Count > 0) return hits.FirstOrDefault(t => t.Supported) ?? hits[0];
        }
        return null;
    }

    /// <summary>The GrantedEffects row an active skill gem type grants (SkillGems → its first GemEffects variant), or -1.
    /// Support gems grant none: they modify the linked active skills (granting one as a skill crashed the client in the
    /// Actor skill reader, 0x141ee795a).</summary>
    public int GemEffect(BaseType t) => _gemEffects.Value.GetValueOrDefault(t.Row, -1);

    private Lazy<Dictionary<int, int>> _gemEffects = null!;

    private static Dictionary<int, int> LoadGemEffects(string folder)
    {
        string gems = Path.Combine(folder, "tables", "SkillGems.json"), effects = Path.Combine(folder, "tables", "GemEffects.json");
        var result = new Dictionary<int, int>();
        if (!File.Exists(gems) || !File.Exists(effects)) return result;
        using var g = JsonDocument.Parse(File.ReadAllBytes(gems));
        using var e = JsonDocument.Parse(File.ReadAllBytes(effects));
        foreach (var r in g.RootElement.EnumerateArray())
            if (!r.GetProperty("IsSupport").GetBoolean() && r.GetProperty("BaseItemTypesKey") is { ValueKind: JsonValueKind.Number } b &&
                r.GetProperty("GemVariants").EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Number } v &&
                e.RootElement[v.GetInt32()].GetProperty("GrantedEffect") is { ValueKind: JsonValueKind.Number } ge)
                result.TryAdd(b.GetInt32(), ge.GetInt32());
        return result;
    }

    /// <summary>Two linked sockets for a type with a Sockets component, in the colour of its highest attribute
    /// requirement (ComponentAttributeRequirements: strength red, dexterity green, intelligence blue; none: white); no
    /// sockets for other types.</summary>
    public (IReadOnlyList<ItemSocket> Sockets, IReadOnlyList<byte>? Links) DefaultSockets(BaseType t)
    {
        if (!t.Components.Contains("Sockets")) return ([], null);
        var colour = _requirements.Value.TryGetValue(t.Id, out var r)
            ? r.Str >= r.Dex && r.Str >= r.Int && r.Str > 0 ? SocketColour.Red
              : r.Dex >= r.Int && r.Dex > 0 ? SocketColour.Green : r.Int > 0 ? SocketColour.Blue : SocketColour.White
            : SocketColour.White;
        return ([new(colour), new(colour)], [2]);
    }

    private Lazy<Dictionary<string, (int Str, int Dex, int Int)>> _requirements = null!;

    private static Dictionary<string, (int, int, int)> LoadRequirements(string folder)
    {
        string path = Path.Combine(folder, "tables", "ComponentAttributeRequirements.json");
        var result = new Dictionary<string, (int, int, int)>();
        if (!File.Exists(path)) return result;
        using var d = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var r in d.RootElement.EnumerateArray())
            result[r.GetProperty("BaseItemTypesKey").GetString() ?? ""] =
                (r.GetProperty("ReqStr").GetInt32(), r.GetProperty("ReqDex").GetInt32(), r.GetProperty("ReqInt").GetInt32());
        return result;
    }

    /// <summary>A new item of this type: item level 1, normal, flasks full.</summary>
    public static Item Create(BaseType t, IReadOnlyList<ItemSocket>? sockets = null, IReadOnlyList<byte>? links = null) =>
        new(t.Hash, t.Components, t.VisualIdentity) { Charges = t.MaxCharges, Sockets = sockets ?? [], Links = links };

    private static (List<BaseType>, string?) Load(string folder)
    {
        string bases = Path.Combine(folder, "tables", "BaseItemTypes.json"), visuals = Path.Combine(folder, "tables", "ItemVisualIdentity.json");
        if (!File.Exists(bases) || !File.Exists(visuals)) return ([], $"{bases} is missing: extract the game tables (Tools/PoEDataExtractor)");
        var components = new Dictionary<string, IReadOnlyList<string>>();
        string types = Path.Combine(folder, "item-types.json");
        if (!File.Exists(types)) return ([], $"{types} is missing: python -I scripts/re/ot_components.py --items --json {types}");
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(types)))
            foreach (var t in doc.RootElement.GetProperty("types").EnumerateArray())
                components[t.GetProperty("type").GetString()!] =
                    [.. t.GetProperty("components").EnumerateArray().Take(t.GetProperty("read").GetInt32()).Select(c => c.GetString()!)];
        List<ushort> visualHashes;
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(visuals)))
            visualHashes = [.. doc.RootElement.EnumerateArray().Select(r => (ushort)r.GetProperty("HASH16").GetInt32())];
        var charges = new Dictionary<string, int>();
        string chargesPath = Path.Combine(folder, "tables", "ComponentCharges.json");
        if (File.Exists(chargesPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(chargesPath));
            foreach (var r in doc.RootElement.EnumerateArray())
                charges[r.GetProperty("BaseItemTypesKey").GetString() ?? ""] = r.GetProperty("MaxCharges").GetInt32();
        }
        var classes = new List<string>();
        var stances = new List<byte>();
        string classesPath = Path.Combine(folder, "tables", "ItemClasses.json");
        if (File.Exists(classesPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(classesPath));
            classes = [.. doc.RootElement.EnumerateArray().Select(r => r.GetProperty("Id").GetString() ?? "")];
            stances = [.. doc.RootElement.EnumerateArray().Select(r => r.GetProperty("ItemStance") is { ValueKind: JsonValueKind.Number } s ? (byte)s.GetInt32() : (byte)0xff)];
        }
        var rows = new List<BaseType>();
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(bases)))
        {
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                string id = r.GetProperty("Id").GetString() ?? "";
                int visual = r.GetProperty("ItemVisualIdentity").GetInt32();
                rows.Add(new BaseType(rows.Count, id, r.GetProperty("Name").GetString() ?? "", r.GetProperty("HASH32").GetUInt32(),
                    (byte)r.GetProperty("Width").GetInt32(), (byte)r.GetProperty("Height").GetInt32(),
                    visual >= 0 && visual < visualHashes.Count ? visualHashes[visual] : (ushort)0,
                    components.GetValueOrDefault(id) ?? [], charges.GetValueOrDefault(id),
                    r.GetProperty("ItemClassesKey") is { ValueKind: JsonValueKind.Number } c && c.GetInt32() < classes.Count ? classes[c.GetInt32()] : "",
                    // the row's own ItemStance, else its class's (ItemStances: Wand = 2)
                    r.GetProperty("ItemStance") is { ValueKind: JsonValueKind.Number } st ? (byte)st.GetInt32()
                        : r.GetProperty("ItemClassesKey") is { ValueKind: JsonValueKind.Number } c2 && c2.GetInt32() < stances.Count ? stances[c2.GetInt32()] : (byte)0xff));
            }
        }
        return (rows, null);
    }
}
