using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    // Mageblood (UniqueCatalog): the belt's unique word and the mod that says how many magic utility flasks always apply.
    // The client shows the item and enforces "cannot be used" itself (the mod); the server is the backstop here.
    // Not modelled yet: the flask buff icons/durations (the stats are applied continuously), the "effects cannot be
    // removed" backstop and the Foulborn rightmost variant.
    const uint MagebloodWord = 3562871479;
    const string MagebloodFlasksMod = "MagicUtilityFlasksAlwaysApplyUnique__1";

    /// <summary>The Mageblood the character wears (belt row 10), or null when it is not worn or its requirements are unmet.</summary>
    static Item? MagebloodBelt(ClientState conn)
    {
        var belt = ItemsOf(conn).Items(PlayerInventories.Belt).FirstOrDefault();
        return belt is { } worn && !IsInactive(conn, worn.Id) && IsMageblood(worn.Item) ? worn.Item : null;
    }

    internal static bool IsMageblood(Item item) =>
        item.Rarity == ItemRarity.Unique && item.NameWords.Contains(MagebloodWord);

    /// <summary>Whether the item is a magic utility flask: the flasks Mageblood's mod applies to (not normal or unique
    /// flasks, and not life/mana flasks).</summary>
    internal static bool IsMagicUtilityFlask(Item item) =>
        item.Rarity == ItemRarity.Magic && Items.ByHash(item.TypeHash) is { } type && Items.UtilityFlaskOf(type) is not null;

    /// <summary>How many magic utility flasks Mageblood applies: its mod's value (2-4 in 3.29).</summary>
    internal static int MagebloodFlaskCount(Item belt)
    {
        foreach (var mod in belt.Mods)
            if (Items.Mods.ByHash(mod.Hash) is { Id: MagebloodFlasksMod } && mod.Values.Count > 0)
                return Math.Clamp(mod.Values[0], 0, 5);
        return 0;
    }

    /// <summary>The flasks Mageblood keeps applied: the leftmost <paramref name="count"/> magic utility flasks of the
    /// flask bar, in bar order.</summary>
    internal static IReadOnlyList<InventoryItem> MagebloodFlasks(IEnumerable<InventoryItem> bar, int count) =>
        [.. bar.Where(i => IsMagicUtilityFlask(i.Item)).OrderBy(i => i.X).Take(Math.Max(0, count))];

    /// <summary>The stats of the magic utility flasks Mageblood keeps applied, or none when it is not worn. The flasks'
    /// own effect mods count; <paramref name="character"/> is the character's stats for increased flask effect/duration.</summary>
    internal static IReadOnlyList<(string Stat, int Value)> MagebloodStats(ClientState conn, Func<string, int> character)
    {
        if (MagebloodBelt(conn) is not { } belt) return [];
        int count = MagebloodFlaskCount(belt);
        if (count == 0) return [];
        List<UtilityFlasks.Source> sources = [];
        foreach (var flask in MagebloodFlasks(ItemsOf(conn).Items(PlayerInventories.Flask), count))
            if (Items.ByHash(flask.Item.TypeHash) is { } type && Items.UtilityFlaskOf(type) is { } utility)
                sources.AddRange(UtilityFlasks.For(utility, flask.Item, ItemModStats.Local(flask.Item, Items.Mods), character).Stats);
        return UtilityFlasks.Strongest(sources);
    }

    /// <summary>Whether Mageblood forbids using this flask: magic utility flasks cannot be used while it is worn (normal and
    /// unique ones still can).</summary>
    internal static bool MagebloodBlocksFlaskUse(ClientState conn, Item flask) =>
        MagebloodBelt(conn) is not null && IsMagicUtilityFlask(flask);

    /// <summary>After the flask bar changed while Mageblood is worn: apply the new permanent flask stats now (the pools, the
    /// walk speed and the client's stats), instead of waiting for an equipment change or a relogin.</summary>
    static async Task RefreshMagebloodFlasks(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (MagebloodBelt(conn) is null) return;
        lock (conn.Monsters) RecomputePoolsAndSpeed(conn);
        await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, PlayerStatsWithCleared(conn)),
            "0x01b3 player stats (Mageblood flasks)");
    }
}
