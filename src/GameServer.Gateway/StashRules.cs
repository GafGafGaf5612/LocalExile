using System.Text.Json;

/// <summary>
/// What each stash tab type holds (Agents/docs/protocol/18-stash.md §7). An item's affinities (StashTabAffinityId rows)
/// come from StashTabAffinityByBaseItemType, StashtabAffinityByItemClassCategory and the special tabs' layouts; each
/// affinity lists the StashType rows that may hold it (StashTabAffinities). Normal, premium and quad tabs hold anything.
/// The currency, essence and fragment tabs give each item type one slot (their *StashTabLayout FirstSlotIndex): the client
/// shows a slot as cell (slot, 0), and an item dropped anywhere on the tab goes into its own slot. A currency tab's slot
/// holds CurrencyItems CurrencyTab_StackSize (10 000 for most currency) instead of the item's own stack size.
/// </summary>
sealed class StashRules
{
    public const byte Currency = 3, Map = 5, Essence = 8, Fragment = 9;

    // Affinity rows (StashTabAffinityId).
    const int CurrencyItems = 3, Maps = 5, Essences = 8, Fragments = 9;

    readonly Dictionary<uint, HashSet<int>> _affinities = [];        // item type HASH32 → affinities
    readonly Dictionary<int, HashSet<byte>> _heldBy = [];             // affinity → StashType rows
    readonly Dictionary<byte, Dictionary<uint, int>> _slots = [];     // StashType row → item type → slot
    readonly Dictionary<uint, ushort> _currencyTabStack = [];

    /// <summary>False without the tables: then every tab holds anything (there is nothing to check against).</summary>
    public bool Loaded { get; private set; }

    /// <summary>Normal, premium, quad (and PC-bang premium) tabs: a grid that holds any item.</summary>
    public static bool IsGrid(byte tabType) => tabType is 0 or 1 or 7 or 10;

    /// <summary>
    /// Tab types whose page the client lays out its own way, which the Gateway doesn't know yet: items saved there are
    /// not shown, so these tabs take nothing. The map tab: the 3.29 client lists maps by tier, not at grid cells (two Beach
    /// Maps at (0, 0) and (0, 1) were saved but not shown, live 2026-10-20).
    /// </summary>
    public static bool NotShown(byte tabType) => tabType == Map;

    public bool Holds(byte tabType, uint itemType) =>
        !NotShown(tabType) && (IsGrid(tabType) || !Loaded
            || (_affinities.TryGetValue(itemType, out var a) && a.Any(x => _heldBy.TryGetValue(x, out var t) && t.Contains(tabType))));

    /// <summary>Whether the tab type gives each item type its own slot.</summary>
    public bool Slotted(byte tabType) => _slots.ContainsKey(tabType);

    /// <summary>The item type's slot in a slotted tab type, or null.</summary>
    public int? SlotOf(byte tabType, uint itemType) =>
        _slots.TryGetValue(tabType, out var s) && s.TryGetValue(itemType, out int slot) ? slot : null;

    /// <summary>How many of the item type one cell of the tab holds, or null for the item's own stack size.</summary>
    public ushort? MaxStack(byte tabType, uint itemType) =>
        tabType == Currency && _currencyTabStack.TryGetValue(itemType, out var n) ? n : null;

    public static StashRules Load(string folder, IReadOnlyList<ItemCatalog.BaseType> rows)
    {
        var rules = new StashRules();
        string Table(string name) => Path.Combine(folder, "tables", name + ".json");
        string[] needed = ["StashTabAffinities", "StashTabAffinityByBaseItemType", "StashtabAffinityByItemClassCategory", "ItemClasses",
            "CurrencyStashTabLayout", "EssenceStashTabLayout", "FragmentStashTabLayout", "CurrencyItems"];
        if (rows.Count == 0 || needed.Any(n => !File.Exists(Table(n)))) return rules;
        var byId = rows.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First().Hash);
        uint? HashOfRow(int row) => row >= 0 && row < rows.Count ? rows[row].Hash : null;
        void Give(uint hash, int affinity) => (rules._affinities.TryGetValue(hash, out var s) ? s : rules._affinities[hash] = []).Add(affinity);

        using (var d = JsonDocument.Parse(File.ReadAllBytes(Table("StashTabAffinities"))))
            foreach (var r in d.RootElement.EnumerateArray())
                rules._heldBy[r.GetProperty("Id").GetInt32()] = [.. r.GetProperty("__col2").EnumerateArray().Select(t => (byte)t.GetInt32())];
        using (var d = JsonDocument.Parse(File.ReadAllBytes(Table("StashTabAffinityByBaseItemType"))))
            foreach (var r in d.RootElement.EnumerateArray())
                if (HashOfRow(r.GetProperty("BaseItemType").GetInt32()) is uint hash)
                    foreach (var a in r.GetProperty("StashTabAffinityId").EnumerateArray()) Give(hash, a.GetInt32());

        // By the item's class category. Maps (category Map) are not in the table, only map keys: the map tab holds both.
        var byCategory = new Dictionary<int, int> { [25] = Maps };
        using (var d = JsonDocument.Parse(File.ReadAllBytes(Table("StashtabAffinityByItemClassCategory"))))
            foreach (var r in d.RootElement.EnumerateArray())
                byCategory[r.GetProperty("ItemClassCategory").GetInt32()] = r.GetProperty("StashTabAffinityId").GetInt32();
        var categoryOfClass = new Dictionary<string, int>();
        using (var d = JsonDocument.Parse(File.ReadAllBytes(Table("ItemClasses"))))
            foreach (var r in d.RootElement.EnumerateArray())
                if (r.GetProperty("ItemClassCategory") is { ValueKind: JsonValueKind.Number } c) categoryOfClass[r.GetProperty("Id").GetString() ?? ""] = c.GetInt32();
        foreach (var t in rows)
            if (categoryOfClass.TryGetValue(t.Class, out int category) && byCategory.TryGetValue(category, out int affinity)) Give(t.Hash, affinity);

        // The slotted tabs: each layout row's item type (its Id, and a fragment slot's StoredItems rows) has that slot.
        void Layout(string table, byte tabType, int affinity)
        {
            var slots = rules._slots[tabType] = [];
            using var d = JsonDocument.Parse(File.ReadAllBytes(Table(table)));
            foreach (var r in d.RootElement.EnumerateArray())
            {
                int slot = r.GetProperty("FirstSlotIndex").GetInt32();
                var types = new List<uint>();
                if (byId.TryGetValue(r.GetProperty("Id").GetString() ?? "", out uint own)) types.Add(own);
                if (r.TryGetProperty("StoredItems", out var stored))
                    foreach (var s in stored.EnumerateArray()) if (HashOfRow(s.GetInt32()) is uint h) types.Add(h);
                foreach (uint hash in types)
                {
                    slots.TryAdd(hash, slot);
                    Give(hash, affinity);
                }
            }
        }
        Layout("CurrencyStashTabLayout", Currency, CurrencyItems);
        Layout("EssenceStashTabLayout", Essence, Essences);
        Layout("FragmentStashTabLayout", Fragment, Fragments);

        using (var d = JsonDocument.Parse(File.ReadAllBytes(Table("CurrencyItems"))))
            foreach (var r in d.RootElement.EnumerateArray())
                if (r.GetProperty("BaseItemTypesKey") is { ValueKind: JsonValueKind.Number } b && HashOfRow(b.GetInt32()) is uint hash
                    && r.GetProperty("CurrencyTab_StackSize").GetInt32() is > 1 and var n)
                    rules._currencyTabStack[hash] = (ushort)Math.Min(n, ushort.MaxValue);
        rules.Loaded = true;
        return rules;
    }

    /// <summary>Rules from given data (tests).</summary>
    internal static StashRules For(Dictionary<uint, HashSet<int>> affinities, Dictionary<int, HashSet<byte>> heldBy,
        Dictionary<byte, Dictionary<uint, int>> slots, Dictionary<uint, ushort>? currencyTabStack = null)
    {
        var rules = new StashRules { Loaded = true };
        foreach (var (k, v) in affinities) rules._affinities[k] = v;
        foreach (var (k, v) in heldBy) rules._heldBy[k] = v;
        foreach (var (k, v) in slots) rules._slots[k] = v;
        foreach (var (k, v) in currencyTabStack ?? []) rules._currencyTabStack[k] = v;
        return rules;
    }
}
