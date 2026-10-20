using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// Item types: BaseItemTypes rows from the extracted tables (Id, Name, HASH32, size, visual identity → ItemVisualIdentity
/// HASH16), each type's components from data/item-types.json (`python -I scripts/re/ot_components.py --items --json
/// data/item-types.json`), flask charges from ComponentCharges and stack sizes from CurrencyItems. All game data: it stays in data/ and loads on first use.
/// </summary>
sealed class ItemCatalog
{
    public ItemCatalog(string folder)
    {
        Mods = new ModCatalog(folder);
        _rows = new(() => Load(folder, Mods));
        _byHash = new(() => _rows.Value.Rows.GroupBy(t => t.Hash).ToDictionary(g => g.Key, g => g.First()));
        _gemEffects = new(() => LoadGemEffects(folder));
        _requirements = new(() => LoadRequirements(folder));
        _shieldBlocks = new(() => LoadShieldBlocks(folder));
        _weaponCrits = new(() => LoadWeaponCrits(folder));
    }

    public sealed record BaseType(int Row, string Id, string Name, uint Hash, byte Width, byte Height, ushort VisualIdentity,
        IReadOnlyList<string> Components, int MaxCharges, string Class = "", byte Stance = 0xff, int DropLevel = 0, ushort MaxStack = 1,
        int FullStackRow = -1, IReadOnlyList<ModCatalog.Mod>? Implicits = null)
    {
        /// <summary>Tags.dat Ids for mod spawn weights: the .it chain's Base tags (item-types.json) and the row's TagsKeys.</summary>
        public IReadOnlySet<string> Tags { get; init; } = new HashSet<string>();

        /// <summary>BaseItemTypes ModDomain: the Mods.dat Domain its affixes come from (1 items, 2 flasks, …).</summary>
        public int ModDomain { get; init; } = 1;

        public bool Supported => Components.Count > 0 && Item.CanSerialize(Components);
        /// <summary>Several of the type share one cell (a Stack component and a CurrencyItems StackSize above 1).</summary>
        public bool Stackable => MaxStack > 1;
        /// <summary>A full stack becomes one of this BaseItemTypes row (CurrencyItems FullStack_BaseItemTypesKey: 5 Scroll
        /// Fragments → a Scroll of Wisdom, 20 shards → their orb), or -1.</summary>
        public bool BecomesWhenFull => FullStackRow >= 0;
        public override string ToString() => $"{Name} ({Id}, row {Row})";
    }

    private readonly Lazy<(List<BaseType> Rows, string? Error)> _rows;

    /// <summary>Mods.dat, for the implicits of new items and the mods of saved ones.</summary>
    public ModCatalog Mods { get; }

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

    private Lazy<Dictionary<int, int>> _shieldBlocks = null!;

    /// <summary>The shield's base block percentage from ShieldTypes.dat (0 for non-shields).</summary>
    public int ShieldBlock(BaseType t) => _shieldBlocks.Value.GetValueOrDefault(t.Row);

    private static Dictionary<int, int> LoadShieldBlocks(string folder)
    {
        string path = Path.Combine(folder, "tables", "ShieldTypes.json");
        var result = new Dictionary<int, int>();
        if (!File.Exists(path)) return result;
        using var d = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var r in d.RootElement.EnumerateArray())
            if (r.GetProperty("BaseItemTypesKey") is { ValueKind: JsonValueKind.Number } b &&
                r.GetProperty("Block") is { ValueKind: JsonValueKind.Number } block)
                result.TryAdd(b.GetInt32(), block.GetInt32());
        return result;
    }

    private Lazy<Dictionary<int, int>> _weaponCrits = null!;

    /// <summary>The weapon's base critical strike chance (permyriad: 500 = 5%) from WeaponTypes.dat.</summary>
    public int WeaponCritical(BaseType t) => _weaponCrits.Value.GetValueOrDefault(t.Row);

    private static Dictionary<int, int> LoadWeaponCrits(string folder)
    {
        string path = Path.Combine(folder, "tables", "WeaponTypes.json");
        var result = new Dictionary<int, int>();
        if (!File.Exists(path)) return result;
        using var d = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var r in d.RootElement.EnumerateArray())
            if (r.GetProperty("BaseItemTypesKey") is { ValueKind: JsonValueKind.Number } b &&
                r.GetProperty("Critical") is { ValueKind: JsonValueKind.Number } crit)
                result.TryAdd(b.GetInt32(), crit.GetInt32());
        return result;
    }

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

    /// <summary>The base's attribute requirements (ComponentAttributeRequirements), zero when it has none.</summary>
    public (int Str, int Dex, int Int) Requirements(BaseType t) => _requirements.Value.GetValueOrDefault(t.Id);

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

    /// <summary>A new item of this type: item level 1, normal, flasks full, its BaseItemTypes implicits rolled.</summary>
    public static Item Create(BaseType t, IReadOnlyList<ItemSocket>? sockets = null, IReadOnlyList<byte>? links = null) =>
        new(t.Hash, t.Components, t.VisualIdentity)
        {
            Charges = t.MaxCharges, Sockets = sockets ?? [], Links = links,
            Mods = t.Components.Contains("Mods") ? [.. (t.Implicits ?? []).Select(m => m.Roll(ModSection.Implicit, Random.Shared))] : [],
        };

    private static (List<BaseType>, string?) Load(string folder, ModCatalog mods)
    {
        string bases = Path.Combine(folder, "tables", "BaseItemTypes.json"), visuals = Path.Combine(folder, "tables", "ItemVisualIdentity.json");
        if (!File.Exists(bases) || !File.Exists(visuals)) return ([], $"{bases} is missing: extract the game tables (Tools/PoEDataExtractor)");
        var components = new Dictionary<string, IReadOnlyList<string>>();
        var itemTags = new Dictionary<string, IReadOnlyList<string>>();
        string types = Path.Combine(folder, "item-types.json");
        if (!File.Exists(types)) return ([], $"{types} is missing: python -I scripts/re/ot_components.py --items --json {types}");
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(types)))
            foreach (var t in doc.RootElement.GetProperty("types").EnumerateArray())
            {
                components[t.GetProperty("type").GetString()!] =
                    [.. t.GetProperty("components").EnumerateArray().Take(t.GetProperty("read").GetInt32()).Select(c => c.GetString()!)];
                // "tags" since 2026-10-20; older files have none (regenerate with ot_components.py --items).
                if (t.TryGetProperty("tags", out var tg)) itemTags[t.GetProperty("type").GetString()!] = [.. tg.EnumerateArray().Select(x => x.GetString()!)];
            }
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
        // CurrencyItems StackSize by BaseItemTypes row (Orb of Transmutation 40, Scroll of Wisdom 40, Portal Scroll 40 …);
        // only types with a Stack component stack.
        var stackSizes = new Dictionary<int, ushort>();
        var fullStacks = new Dictionary<int, int>();
        string currencyPath = Path.Combine(folder, "tables", "CurrencyItems.json");
        if (File.Exists(currencyPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(currencyPath));
            foreach (var r in doc.RootElement.EnumerateArray())
                if (r.GetProperty("BaseItemTypesKey") is { ValueKind: JsonValueKind.Number } b && r.GetProperty("StackSize").GetInt32() is > 1 and var size)
                {
                    stackSizes.TryAdd(b.GetInt32(), (ushort)Math.Min(size, ushort.MaxValue));
                    if (r.GetProperty("FullStack_BaseItemTypesKey") is { ValueKind: JsonValueKind.Number } full) fullStacks.TryAdd(b.GetInt32(), full.GetInt32());
                }
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
        List<string> tagIds = [];
        string tagsPath = Path.Combine(folder, "tables", "Tags.json");
        if (File.Exists(tagsPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(tagsPath));
            tagIds = [.. doc.RootElement.EnumerateArray().Select(r => r.GetProperty("Id").GetString() ?? "")];
        }
        var rows = new List<BaseType>();
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(bases)))
        {
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                string id = r.GetProperty("Id").GetString() ?? "";
                var itemComponents = components.GetValueOrDefault(id) ?? [];
                int visual = r.GetProperty("ItemVisualIdentity").GetInt32();
                rows.Add(new BaseType(rows.Count, id, r.GetProperty("Name").GetString() ?? "", r.GetProperty("HASH32").GetUInt32(),
                    (byte)r.GetProperty("Width").GetInt32(), (byte)r.GetProperty("Height").GetInt32(),
                    visual >= 0 && visual < visualHashes.Count ? visualHashes[visual] : (ushort)0,
                    itemComponents, charges.GetValueOrDefault(id),
                    r.GetProperty("ItemClassesKey") is { ValueKind: JsonValueKind.Number } c && c.GetInt32() < classes.Count ? classes[c.GetInt32()] : "",
                    // the row's own ItemStance, else its class's (ItemStances: Wand = 2)
                    r.GetProperty("ItemStance") is { ValueKind: JsonValueKind.Number } st ? (byte)st.GetInt32()
                        : r.GetProperty("ItemClassesKey") is { ValueKind: JsonValueKind.Number } c2 && c2.GetInt32() < stances.Count ? stances[c2.GetInt32()] : (byte)0xff,
                    r.GetProperty("DropLevel").GetInt32(),
                    itemComponents.Contains("Stack") ? stackSizes.GetValueOrDefault(rows.Count, (ushort)1) : (ushort)1,
                    fullStacks.GetValueOrDefault(rows.Count, -1),
                    // Implicit_ModsKeys: Mods.dat rows every item of the type has (Driftwood Wand: 8-12% increased Spell Damage)
                    [.. r.GetProperty("Implicit_ModsKeys").EnumerateArray().Select(k => mods.Row(k.GetInt32())).OfType<ModCatalog.Mod>()])
                {
                    Tags = new HashSet<string>([.. itemTags.GetValueOrDefault(id) ?? [],
                        .. r.GetProperty("TagsKeys").EnumerateArray().Select(k => k.GetInt32()).Where(k => k < tagIds.Count).Select(k => tagIds[k])]),
                    ModDomain = r.GetProperty("ModDomain").GetInt32(),
                });
            }
        }
        return (rows, null);
    }
}
