using System.Collections.Concurrent;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// An account's stash (Agents/docs/protocol/18-stash.md): its tabs and their items, shared by every character of the
/// account, the tab the stash was last on, and the account's gold (S→C 0x2a9, also shared by every character). One JSON file per account in data/stashes/ (git-ignored), written after
/// every change. A new account gets <see cref="StarterTabs"/>.
/// <para>Item ids: characters number their items from 1, each on its own, so an item put into the stash gets a stash id
/// (from <see cref="FirstItemId"/>, never reused: the next id is saved) and keeps it when a character takes it out.
/// PlayerInventories does not count stash ids when it picks its next id.</para>
/// </summary>
sealed class AccountStash
{
    /// <summary>Stash item ids start here (PlayerInventories counts from 1, ground items from 0x100000).</summary>
    public const uint FirstItemId = 0x40000000;

    /// <summary>Tab i's inventory id in the player's inventory manager (the player's own are 1..117, shops 0x400…).</summary>
    public const uint FirstInventoryId = 0x800;

    public static uint InventoryId(int tab) => FirstInventoryId + (uint)tab;

    public sealed record SavedTab(string Name, byte Type, byte R, byte G, byte B, List<CharacterSaves.SavedPlacement> Items);

    public sealed record Save(List<SavedTab> Tabs, int LastTab = 0, uint NextItemId = FirstItemId, uint Gold = 0);

    /// <summary>A StashType row's grid: StorageSlots × column 4 (NormalStash 12 × 12, QuadStash 24 × 24; the special tabs'
    /// slots are cells x = slot index of a StorageSlots-wide grid, as their layouts' FirstSlotIndex).</summary>
    public static readonly Dictionary<byte, (string Name, byte Width, byte Height)> Types = new()
    {
        [0] = ("NormalStash", 12, 12), [1] = ("PremiumStash", 12, 12), [3] = ("CurrencyStash", 142, 4), [4] = ("UniqueStash", 254, 4),
        [5] = ("MapStash", 12, 6), [7] = ("QuadStash", 24, 24), [8] = ("EssenceStash", 110, 4), [9] = ("FragmentStash", 255, 9),
        [17] = ("FlaskStash", 250, 4), [18] = ("GemStash", 250, 2),
    };

    /// <summary>The tabs every account starts with: two normal tabs, two premium tabs, a quad tab and the special tabs
    /// whose StashType the client supports (StashAvailabilities lists Divination, Delve, Blight and Ultimatum as not).</summary>
    static readonly SavedTab[] StarterTabs =
    [
        new("1", 0, 0x7c, 0x54, 0x36, []), new("2", 0, 0x7c, 0x54, 0x36, []),
        new("Premium", 1, 0x2c, 0x6e, 0xc8, []), new("Loot", 1, 0xc8, 0x8c, 0x1e, []),
        new("Quad", 7, 0x8a, 0x2b, 0xe2, []),
        new("Currency", 3, 0xd2, 0xb4, 0x28, []), new("Essence", 8, 0x3c, 0xb4, 0x50, []),
        new("Fragment", 9, 0xa0, 0x3c, 0x3c, []), new("Map", 5, 0x50, 0x82, 0xb4, []),
        new("Flask", 17, 0xb4, 0x28, 0x28, []), new("Gem", 18, 0x28, 0xa0, 0x78, []),
    ];

    sealed class TabState(SavedTab saved)
    {
        public SavedTab Saved = saved;
        public readonly List<InventoryItem> Items = [];
        public uint Revision;
        public (byte W, byte H) Grid => Types.TryGetValue(Saved.Type, out var t) ? (t.Width, t.Height) : ((byte)12, (byte)12);
    }

    static readonly ConcurrentDictionary<string, AccountStash> ByAccount = new(StringComparer.OrdinalIgnoreCase);
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static string Folder { get; set; } = Path.Combine("data", "stashes");

    readonly string _account;
    readonly List<TabState> _tabs = [];
    readonly Lock _lock = new();
    int _lastTab;
    uint _nextItemId = FirstItemId;
    uint _gold;
    ModCatalog? _mods;  // to save mods by their Mods.dat Id

    AccountStash(string account) => _account = account;

    /// <summary>The account's stash, loaded from its file or made with the starter tabs.</summary>
    public static AccountStash For(string account, ItemCatalog catalog) =>
        ByAccount.GetOrAdd(account, name =>
        {
            var stash = new AccountStash(name) { _mods = catalog.Mods };
            var save = Load(name);
            foreach (var t in save?.Tabs ?? [.. StarterTabs])
            {
                var tab = new TabState(t with { Items = [] });
                foreach (var p in t.Items)
                    if (CharacterSaves.FromSaved(p.Item, catalog) is { } item) tab.Items.Add(new InventoryItem(p.Id, p.X, p.Y, item));
                    else Console.WriteLine($"[gateway] stash of {name}: saved item type 0x{p.Item.Type:x8} is unknown, dropped");
                stash._tabs.Add(tab);
            }
            stash._lastTab = Math.Clamp(save?.LastTab ?? 0, 0, Math.Max(0, stash._tabs.Count - 1));
            // Never below an id handed out before (an item that left for a character keeps its stash id).
            uint used = stash._tabs.SelectMany(t => t.Items).Select(i => i.Id).Where(i => i >= FirstItemId).DefaultIfEmpty(FirstItemId - 1).Max();
            stash._nextItemId = Math.Max(save?.NextItemId ?? FirstItemId, used + 1);
            stash._gold = save?.Gold ?? 0;
            if (save is null) stash.Persist();
            return stash;
        });

    static string PathOf(string account) =>
        Path.Combine(Folder, string.Concat(account.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_')) + ".json");

    static Save? Load(string account)
    {
        string path = PathOf(account);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<Save>(File.ReadAllText(path), Opts); }
        catch (Exception e) when (e is IOException or JsonException)
        {
            Console.WriteLine($"[gateway] stash {path} unreadable: {e.Message}");
            return null;
        }
    }

    void Persist()
    {
        Save save;
        lock (_lock)
            save = new([.. _tabs.Select(t => t.Saved with
            {
                Items = [.. t.Items.Select(i => new CharacterSaves.SavedPlacement(i.Id, i.X, i.Y, CharacterSaves.ToSaved(i.Item, _mods)))],
            })], _lastTab, _nextItemId, _gold);
        string path = PathOf(_account);
        Directory.CreateDirectory(Folder);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(save, Opts));
        File.Move(tmp, path, overwrite: true);
    }

    public int Count { get { lock (_lock) return _tabs.Count; } }

    /// <summary>The account's gold (S→C 0x2a9).</summary>
    public uint Gold { get { lock (_lock) return _gold; } }

    /// <summary>Adds gold (or takes it, with a negative amount, down to 0); the account's gold after.</summary>
    public uint AddGold(long delta)
    {
        uint gold;
        lock (_lock) gold = _gold = (uint)Math.Clamp(_gold + delta, 0, uint.MaxValue);
        Persist();
        return gold;
    }

    /// <summary>Takes <paramref name="cost"/> gold if the account has that much; the account's gold after, or null when
    /// it has less (nothing is taken).</summary>
    public uint? TrySpendGold(long cost)
    {
        uint gold;
        lock (_lock)
        {
            if (cost < 0 || cost > _gold) return null;
            gold = _gold -= (uint)cost;
        }
        if (cost > 0) Persist();
        return gold;
    }

    /// <summary>The tab the stash opens on: the last one the client showed (C→S 0xc7).</summary>
    public int LastTab { get { lock (_lock) return _lastTab; } }

    public void SetLastTab(int tab)
    {
        lock (_lock)
        {
            if (tab < 0 || tab >= _tabs.Count || tab == _lastTab) return;
            _lastTab = tab;
        }
        Persist();
    }

    /// <summary>The tab of a stash inventory id, or null.</summary>
    public int? TabOf(uint inventoryId)
    {
        lock (_lock)
            return inventoryId >= FirstInventoryId && inventoryId < FirstInventoryId + _tabs.Count ? (int)(inventoryId - FirstInventoryId) : null;
    }

    /// <summary>The tab list for S→C 0x21 / 0x136. The last tab carries affinity bit <paramref name="selectBit"/> (if
    /// any): S→C 0x7b kind 1 with that affinity makes the panel select it. Loaded tabs are the ones this connection
    /// already has the inventory of.</summary>
    public List<InstanceStash.Tab> Records(int? selectBit, ISet<int>? loaded = null)
    {
        lock (_lock)
            return [.. _tabs.Select((t, i) => new InstanceStash.Tab((ushort)i, t.Saved.Name, InventoryId(i), t.Saved.Type,
                t.Saved.R, t.Saved.G, t.Saved.B,
                Flags: t.Saved.Type is 1 or 7 ? InstanceStash.TabFlags.Premium : InstanceStash.TabFlags.None,
                Position: (ushort)i, Affinities: selectBit is int b && i == _lastTab ? 1u << b : 0,
                Loaded: loaded?.Contains(i) ?? false, Id: $"{i + 1:x8}"))];
    }

    public string NameOf(int tab) { lock (_lock) return tab >= 0 && tab < _tabs.Count ? _tabs[tab].Saved.Name : "?"; }

    public string TypeOf(int tab) { lock (_lock) return tab >= 0 && tab < _tabs.Count && Types.TryGetValue(_tabs[tab].Saved.Type, out var t) ? t.Name : "?"; }

    /// <summary>The tab's inventory (S→C 0xc0, manager 0) and items.</summary>
    public (InstanceInventories.Slot Slot, IReadOnlyList<InventoryItem> Items) Inventory(int tab)
    {
        lock (_lock)
        {
            var t = _tabs[tab];
            return (InstanceStash.TabSlot(t.Grid.W, t.Grid.H), [.. t.Items]);
        }
    }

    public uint NextRevision(int tab) { lock (_lock) return ++_tabs[tab].Revision; }

    /// <summary>Takes an item out of a tab (onto the cursor); null when it is not there.</summary>
    public InventoryItem? Take(int tab, uint itemId)
    {
        InventoryItem it;
        lock (_lock)
        {
            if (tab < 0 || tab >= _tabs.Count) return null;
            var items = _tabs[tab].Items;
            int at = items.FindIndex(i => i.Id == itemId);
            if (at < 0) return null;
            it = items[at];
            items.RemoveAt(at);
        }
        Persist();
        return it;
    }

    /// <summary>The item put into a tab at (x, y), and the one item it was dropped on (taken out, for the cursor), or null
    /// with the reason: outside the grid or more than one item in the way.</summary>
    public (InventoryItem Placed, InventoryItem? Displaced)? Place(int tab, int x, int y, InventoryItem held,
        Func<Item, PlayerInventories.Shape?> shapeOf, out string why)
    {
        (InventoryItem, InventoryItem?) result;
        lock (_lock)
        {
            why = "";
            if (tab < 0 || tab >= _tabs.Count) { why = "no such tab"; return null; }
            var t = _tabs[tab];
            if (shapeOf(held.Item) is not { } shape) { why = "unknown item type"; return null; }
            if (x < 0 || y < 0 || x + shape.Width > t.Grid.W || y + shape.Height > t.Grid.H) { why = "outside the grid"; return null; }
            var inTheWay = t.Items.Where(i => shapeOf(i.Item) is { } s && i.X < x + shape.Width && x < i.X + s.Width && i.Y < y + shape.Height && y < i.Y + s.Height).ToList();
            if (inTheWay.Count > 1) { why = $"{inTheWay.Count} items in the way"; return null; }
            InventoryItem? displaced = null;
            if (inTheWay.Count == 1)
            {
                t.Items.Remove(inTheWay[0]);
                displaced = inTheWay[0] with { X = 0, Y = 0 };
            }
            // A character's id (counted from 1 per character) becomes a stash id: two characters' items never share one.
            var placed = held with { Id = held.Id >= FirstItemId ? held.Id : _nextItemId++, X = (byte)x, Y = (byte)y };
            t.Items.Add(placed);
            result = (placed, displaced);
        }
        Persist();
        return result;
    }

    /// <summary>
    /// The held stack put on a stack of its type with room at (x, y) in a tab: that stack grows by what fits (up to the
    /// type's max; at most <paramref name="most"/>) and is returned with how many moved; null (nothing changes) when there
    /// is no such stack. A full stack becomes its full-stack item (<see cref="PlayerInventories.Completed"/>).
    /// </summary>
    public (InventoryItem Stack, int Moved)? Merge(int tab, int x, int y, InventoryItem held, Func<Item, PlayerInventories.Shape?> shapeOf,
        int most = int.MaxValue)
    {
        (InventoryItem, int) result;
        lock (_lock)
        {
            if (tab < 0 || tab >= _tabs.Count || shapeOf(held.Item) is not { MaxStack: > 1 } shape) return null;
            var items = _tabs[tab].Items;
            var inTheWay = items.Where(i => shapeOf(i.Item) is { } s && i.X < x + shape.Width && x < i.X + s.Width && i.Y < y + shape.Height && y < i.Y + s.Height).ToList();
            if (inTheWay is not [var under] || under.Item.TypeHash != held.Item.TypeHash || under.Item.StackSize >= shape.MaxStack) return null;
            int moved = Math.Min(Math.Min(held.Item.StackSize, most), shape.MaxStack - under.Item.StackSize);
            var grown = PlayerInventories.Completed(under with { Item = under.Item with { StackSize = (ushort)(under.Item.StackSize + moved) } }, shape);
            items[items.IndexOf(under)] = grown;
            result = (grown, moved);
        }
        Persist();
        return result;
    }

    /// <summary>Takes <paramref name="amount"/> off a stack in a tab (a split: less than the whole stack). Returns the
    /// stack as it is now, or null (nothing changes).</summary>
    public InventoryItem? Shrink(int tab, uint itemId, int amount)
    {
        InventoryItem result;
        lock (_lock)
        {
            if (tab < 0 || tab >= _tabs.Count) return null;
            var items = _tabs[tab].Items;
            int at = items.FindIndex(i => i.Id == itemId);
            if (at < 0 || amount < 1 || amount >= items[at].Item.StackSize) return null;
            result = items[at] = items[at] with { Item = items[at].Item with { StackSize = (ushort)(items[at].Item.StackSize - amount) } };
        }
        Persist();
        return result;
    }

    /// <summary>The first free cell of a tab for an item of this size (column by column), or null.</summary>
    public (byte X, byte Y)? FreeCell(int tab, byte width, byte height, Func<Item, PlayerInventories.Shape?> shapeOf)
    {
        lock (_lock)
        {
            var t = _tabs[tab];
            for (int x = 0; x + width <= t.Grid.W; x++)
                for (int y = 0; y + height <= t.Grid.H; y++)
                    if (!t.Items.Any(i => shapeOf(i.Item) is { } s && i.X < x + width && x < i.X + s.Width && i.Y < y + height && y < i.Y + s.Height))
                        return ((byte)x, (byte)y);
            return null;
        }
    }

    /// <summary>A fresh stash item id.</summary>
    public uint NewItemId() { lock (_lock) return _nextItemId++; }

    /// <summary>Forgets the loaded stashes (tests).</summary>
    internal static void Reset() => ByAccount.Clear();
}
