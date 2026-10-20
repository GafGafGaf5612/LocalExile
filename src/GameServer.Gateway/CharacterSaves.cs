using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// A character's saved state: its items per inventory (Inventories.dat row) and its skill bar, one JSON file per character
/// in data/saves/ (git-ignored, like the account store). Items are stored by BaseItemTypes HASH32 and rebuilt through the
/// <see cref="ItemCatalog"/>, so the file holds no client-side layout. Writes replace the file atomically.
/// </summary>
static class CharacterSaves
{
    public sealed record SavedSocket(SocketColour Colour, SavedItem? Gem = null);

    /// <summary>A mod by its Mods.dat Id (hashes are the client's lookup key, Ids are what people read), the list it is
    /// in and its stat values.</summary>
    public sealed record SavedMod(string Id, ModSection Section, List<int> Values);

    /// <summary>Mods: null in saves from before mods (the item gets its type's implicits again), else every mod.</summary>
    public sealed record SavedItem(uint Type, byte ItemLevel = 1, byte Quality = 0, int Charges = 0, ushort StackSize = 1,
        List<SavedSocket>? Sockets = null, List<byte>? Links = null, int GemExperience = 0,
        ItemRarity Rarity = ItemRarity.Normal, bool Identified = true, List<SavedMod>? Mods = null, List<uint>? Name = null);

    public sealed record SavedPlacement(uint Id, byte X, byte Y, SavedItem Item);

    /// <summary>Skill bar: slot index → a GrantedEffects Id (skill ids change with the socketed gems, effects do not),
    /// "Action/&lt;ActionTypes Id&gt;" for a basic action ("Action/Move") or "" for a cleared slot. Experience: the
    /// character's total (its level is the account's; a save from another level is ignored). Passives: the allocated
    /// nodes' PassiveSkillGraphId. Waypoints: the WorldAreas Ids of the activated waypoints; Visited: of the areas
    /// entered (their visit quest flags reveal the World panel's pins). QuestFlags: the quest flags quests set
    /// (QuestFlags.dat Ids, 12-quests.md §5). Gold: only in older saves, see <see cref="TakeGold"/>.</summary>
    public sealed record Save(Dictionary<byte, List<SavedPlacement>> Inventories, Dictionary<int, string>? SkillBar = null,
        uint? Experience = null, List<ushort>? Passives = null, List<string>? Waypoints = null, List<string>? Visited = null,
        List<string>? QuestFlags = null, ushort? ExtraPassivePoints = null,
        Dictionary<ushort, ushort>? MasteryEffects = null, uint? Gold = null, ushort? RefundPoints = null);

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    static readonly Lock Gate = new();

    public static string Folder { get; set; } = Path.Combine("data", "saves");

    static string PathOf(string character)
    {
        string safe = string.Concat(character.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
        return Path.Combine(Folder, safe + ".json");
    }

    public static Save? Load(string character)
    {
        string path = PathOf(character);
        lock (Gate)
        {
            if (!File.Exists(path)) return null;
            try { return JsonSerializer.Deserialize<Save>(File.ReadAllText(path), Opts); }
            catch (Exception e) { Console.WriteLine($"[gateway] save {path} unreadable: {e.Message}"); return null; }
        }
    }

    public static void Write(string character, Save save)
    {
        string path = PathOf(character);
        lock (Gate)
        {
            Directory.CreateDirectory(Folder);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(save, Opts));
            File.Move(tmp, path, overwrite: true);
        }
    }

    /// <summary>Records what a skill bar slot holds (see <see cref="Save.SkillBar"/>), keeping the saved items.</summary>
    public static void SetSkillBar(string character, byte slot, string entry)
    {
        lock (Gate)
        {
            var save = Load(character) ?? new Save([]);
            var bar = save.SkillBar is null ? new Dictionary<int, string>() : new Dictionary<int, string>(save.SkillBar);
            bar[slot] = entry;
            Write(character, save with { SkillBar = bar });
        }
    }

    /// <summary>Records the character's inventories, keeping the rest of the save.</summary>
    public static void SetInventories(string character, Dictionary<byte, List<SavedPlacement>> inventories) =>
        Update(character, s => s with { Inventories = inventories });

    /// <summary>Records the character's experience, keeping the rest of the save.</summary>
    public static void SetExperience(string character, uint experience) => Update(character, s => s with { Experience = experience });

    /// <summary>Records the character's quest flags (QuestFlags.dat Ids), keeping the rest of the save.</summary>
    public static void SetQuestFlags(string character, IEnumerable<string> flags) => Update(character, s => s with { QuestFlags = [.. flags.Order()] });

    /// <summary>Records the character's allocated passives, keeping the rest of the save.</summary>
    public static void SetPassives(string character, IEnumerable<ushort> nodes) => Update(character, s => s with { Passives = [.. nodes] });

    /// <summary>Records the selected mastery effects (node -> effect HASH16), keeping the rest of the save.</summary>
    public static void SetMasteryEffects(string character, IReadOnlyDictionary<ushort, ushort> effects) =>
        Update(character, s => s with { MasteryEffects = new(effects) });

    /// <summary>Records the extra passive skill points from /add_passive_points.</summary>
    public static void SetExtraPassivePoints(string character, ushort points) =>
        Update(character, s => s with { ExtraPassivePoints = points });

    /// <summary>Records the refund points (0x33's first u16; /add_refund_points grants them for now).</summary>
    public static void SetRefundPoints(string character, ushort points) =>
        Update(character, s => s with { RefundPoints = points });


    /// <summary>Takes the gold an older save kept with the character (gold is the account's now, AccountStash.Gold): the
    /// amount, 0 when there is none; the save no longer holds it.</summary>
    public static uint TakeGold(string character)
    {
        lock (Gate)
        {
            if (Load(character) is not { Gold: uint gold } save) return 0;
            Write(character, save with { Gold = null });
            return gold;
        }
    }

    /// <summary>Records the character's activated waypoints, keeping the rest of the save.</summary>
    public static void SetWaypoints(string character, IEnumerable<string> areaIds) => Update(character, s => s with { Waypoints = [.. areaIds] });

    /// <summary>Records the areas the character has entered, keeping the rest of the save.</summary>
    public static void SetVisited(string character, IEnumerable<string> areaIds) => Update(character, s => s with { Visited = [.. areaIds] });

    static void Update(string character, Func<Save, Save> change)
    {
        lock (Gate) Write(character, change(Load(character) ?? new Save([])));
    }

    public static SavedItem ToSaved(Item i, ModCatalog? mods = null) => new(i.TypeHash, i.ItemLevel, i.Quality, i.Charges, i.StackSize,
        i.Sockets.Count == 0 ? null : [.. i.Sockets.Select(s => new SavedSocket(s.Colour, s.Gem is null ? null : ToSaved(s.Gem, mods)))],
        i.Links is null ? null : [.. i.Links], i.GemExperience, i.Rarity, i.Identified,
        [.. i.Mods.Select(m => new SavedMod(mods?.ByHash(m.Hash)?.Id ?? $"0x{m.Hash:x4}", m.Section, [.. m.Values]))],
        i.NameWords.Count == 0 ? null : [.. i.NameWords]);

    /// <summary>The item again, or null when its type is no longer known.</summary>
    public static Item? FromSaved(SavedItem s, ItemCatalog catalog)
    {
        if (catalog.ByHash(s.Type) is not { } t) return null;
        return ItemCatalog.Create(t) with
        {
            ItemLevel = s.ItemLevel, Quality = s.Quality, Charges = s.Charges, StackSize = s.StackSize, GemExperience = s.GemExperience,
            Sockets = [.. (s.Sockets ?? []).Select(k => new ItemSocket(k.Colour, k.Gem is null ? null : FromSaved(k.Gem, catalog)))],
            Links = s.Links, Rarity = s.Rarity, Identified = s.Identified, NameWords = s.Name ?? [],
        } is var item && s.Mods is null ? item : item with { Mods = [.. s.Mods.Select(m => FromSaved(m, catalog.Mods)).OfType<ItemMod>()] };
    }

    /// <summary>The mod again (by Id, or by an "0x…" hash when Mods.dat was not loaded at save time), or null when
    /// Mods.dat no longer has it: an unknown hash would stop the client.</summary>
    static ItemMod? FromSaved(SavedMod m, ModCatalog mods)
    {
        var mod = m.Id.StartsWith("0x") && ushort.TryParse(m.Id[2..], System.Globalization.NumberStyles.HexNumber, null, out ushort h)
            ? mods.ByHash(h) : mods.ById(m.Id);
        return mod?.With(m.Section, m.Values, Random.Shared);
    }
}
