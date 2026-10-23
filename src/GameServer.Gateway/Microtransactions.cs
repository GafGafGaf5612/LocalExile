using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// Every microtransaction in the game, owned by every account: one 0x1f4 record per <c>MtxTypes</c> row the client can
/// resolve, which the client's Microtransactions panel (N) and the store's ALREADY OWNED marks read
/// (23-microtransactions.md). A record's <c>MtxTypeHash</c> is the row's HASH32 and its <c>Id</c> the row index + 1,
/// unique per record as the client's model needs. The panel looks each hash up in <c>MTXTypeGameSpecific.dat</c>
/// (0x1408e4fb0 → 0x142222170), and a hash that is not there leaves a null the row build faults on (live 2026-10-23:
/// all 8411 MtxTypes rows crashed the client; 690 of them have no MtxTypeGameSpecific row), so only rows with one are
/// sent. Names stay empty: the client names a record from its catalogue.
/// Game data, loaded on first use; without <c>data/tables</c> the list is empty.
/// </summary>
sealed class MtxCatalog(string folder = "data")
{
    private readonly Lazy<List<InstanceMicrotransactions.Record>> _owned = new(() => Load(folder));
    private Dictionary<uint, InstanceMicrotransactions.Record>? _byId;

    public IReadOnlyList<InstanceMicrotransactions.Record> Owned => _owned.Value;

    /// <summary>The owned record with this id (what 0x1f9 picks), or null.</summary>
    public InstanceMicrotransactions.Record? Find(uint id) =>
        (_byId ??= Owned.ToDictionary(r => r.Id)).GetValueOrDefault(id);

    /// <summary>The owned record of a MtxTypes HASH32, or null.</summary>
    public InstanceMicrotransactions.Record? ByHash(uint hash) => Owned.FirstOrDefault(r => r.MtxTypeHash == hash);

    /// <summary>
    /// The worn table 0x1f3 carries for <paramref name="worn"/> (MicrotransactionSlot Id → saved pick): a pick whose
    /// record no longer exists (another game version's data) is left out, so the client never gets a hash it cannot
    /// resolve.
    /// </summary>
    public List<InstanceMicrotransactions.Equipped> Equipped(IReadOnlyDictionary<byte, CharacterSaves.SavedCosmetic> worn) =>
        [.. worn.Where(w => w.Key < InstanceMicrotransactions.SlotCount)
            .Select(w => (w.Key, w.Value, Record: Find(w.Value.Record)))
            .Where(w => w.Record is not null)
            .OrderBy(w => w.Key)
            .Select(w => new InstanceMicrotransactions.Equipped(w.Key, w.Record!.Id, w.Record.MtxTypeHash, w.Record.Qword, w.Value.Option))];

    static List<InstanceMicrotransactions.Record> Load(string folder)
    {
        string dir = Path.Combine(folder, "tables");
        var resolvable = Hashes(Path.Combine(dir, "MtxTypeGameSpecific.json"));
        var types = Hashes(Path.Combine(dir, "MtxTypes.json"));
        if (resolvable is null || types is null) return [];
        var list = new List<InstanceMicrotransactions.Record>();
        for (int row = 0; row < types.Count; row++)
            if (types[row] is uint h && resolvable.Contains(h))
                list.Add(new InstanceMicrotransactions.Record(h, (uint)row + 1));
        return list;
    }

    /// <summary>Each row's HASH32 in table order (null for a row without one), or null without the file.</summary>
    internal static List<uint?>? Hashes(string path)
    {
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return [.. doc.RootElement.EnumerateArray().Select(r =>
            r.TryGetProperty("HASH32", out var hash) && hash.TryGetUInt32(out uint h) ? h : (uint?)null)];
    }
}

/// <summary>
/// What a worn cosmetic does in the world, beyond the client's own worn table (which already draws cursors, portals
/// and flask skins). <c>ItemisedVisualEffect</c> has one row per cosmetic (<c>Mtx</c>: its MtxTypeGameSpecific row):
/// <c>VisualIdentity</c> is a skin, attachment or character effect the model draws, <c>VisualEffect</c> a weapon
/// effect, and <c>Stats</c> with the values in <c>__col7</c> the stats it gives the character (footprints:
/// footstep_effect_variation, apparitions: imp_variation, …). <c>MicrotransactionSlot.__col7</c> is the model slot a
/// Cosmetics tab slot dresses: the Inventories component's slot numbers (Weapon1 0 … Amulet1 11) and the slots only
/// cosmetics use (armour attachment 3, character effect 12, footprints 13, helmet attachments 14 and 15, hip 16);
/// 17 is none.
/// Game data, loaded on first use; without <c>data/tables</c> nothing applies.
/// </summary>
sealed class CosmeticEffects(string folder = "data")
{
    /// <summary>One cosmetic's effect: ItemVisualIdentity / ItemVisualEffect HASH16s (0 for none), its stats (Stats
    /// row + 1, value) and, for a pet, its <c>Pet.dat</c> row + 1 (0 otherwise).</summary>
    public sealed record Effect(ushort Visual, ushort WeaponEffect, IReadOnlyList<(uint Stat, int Value)> Stats, int Pet = 0);

    /// <summary>A Cosmetics tab slot: its name and the model slot it dresses (null: none).</summary>
    public sealed record Slot(string Name, byte? ModelSlot)
    {
        /// <summary>Weapon effects go in the second reference of the weapon's entry, everything else in the first.</summary>
        public bool IsWeaponEffect => Name is "Weapon Effect" or "Weapon Added Effect";
    }

    /// <summary>Model slots no item fills: they only ever show a cosmetic, and are sent empty when none is worn.</summary>
    public static readonly byte[] CosmeticOnlySlots = [3, 12, 13, 14, 15, 16];

    readonly Lazy<(Dictionary<uint, Effect> Effects, Dictionary<byte, Slot> Slots)> _data = new(() => Load(folder));

    /// <summary>The effect of the cosmetic with this MtxTypeGameSpecific HASH32, or null.</summary>
    public Effect? Of(uint hash) => _data.Value.Effects.GetValueOrDefault(hash);

    /// <summary>The Cosmetics tab slot with this MicrotransactionSlot Id, or null.</summary>
    public Slot? SlotOf(byte id) => _data.Value.Slots.GetValueOrDefault(id);

    static (Dictionary<uint, Effect>, Dictionary<byte, Slot>) Load(string folder)
    {
        string dir = Path.Combine(folder, "tables");
        var effects = new Dictionary<uint, Effect>();
        var slots = new Dictionary<byte, Slot>();
        var gameSpecific = MtxCatalog.Hashes(Path.Combine(dir, "MtxTypeGameSpecific.json"));
        var identities = Hash16s(Path.Combine(dir, "ItemVisualIdentity.json"));
        var weaponEffects = Hash16s(Path.Combine(dir, "ItemVisualEffect.json"));
        if (gameSpecific is null || identities is null || weaponEffects is null) return (effects, slots);

        foreach (var row in Rows(Path.Combine(dir, "ItemisedVisualEffect.json")))
        {
            if (Int(row, "Mtx") is not int mtx || mtx < 0 || mtx >= gameSpecific.Count || gameSpecific[mtx] is not uint hash) continue;
            ushort visual = Int(row, "VisualIdentity") is int vi && vi >= 0 && vi < identities.Count ? identities[vi] : (ushort)0;
            ushort effect = Int(row, "VisualEffect") is int ve && ve >= 0 && ve < weaponEffects.Count ? weaponEffects[ve] : (ushort)0;
            var stats = new List<(uint, int)>();
            if (row.TryGetProperty("Stats", out var keys) && keys.ValueKind == JsonValueKind.Array
                && row.TryGetProperty("__col7", out var values) && values.ValueKind == JsonValueKind.Array)
                foreach (var (key, value) in keys.EnumerateArray().Zip(values.EnumerateArray()))
                    if (key.TryGetInt32(out int stat) && value.TryGetInt32(out int v)) stats.Add(((uint)stat + 1, v));
            effects.TryAdd(hash, new Effect(visual, effect, stats));
        }
        // Pets have no ItemisedVisualEffect row: the client spawns the pet itself from the player's pet_slot1 / pet_slot2
        // stat, a Pet.dat row + 1 (0x1401093a0 → 0x14010f530), and each Pet row names its MtxTypeGameSpecific row.
        int petRow = 0;
        foreach (var row in Rows(Path.Combine(dir, "Pet.json")))
        {
            petRow++;
            if (Int(row, "MtxTypeGameSpecificKey") is int mtx && mtx >= 0 && mtx < gameSpecific.Count && gameSpecific[mtx] is uint hash)
                effects[hash] = (effects.GetValueOrDefault(hash) ?? new Effect(0, 0, [])) with { Pet = petRow };
        }
        foreach (var row in Rows(Path.Combine(dir, "MicrotransactionSlot.json")))
            if (Int(row, "Id") is int id and > 0 and < InstanceMicrotransactions.SlotCount)
                slots[(byte)id] = new Slot(row.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "",
                    Int(row, "__col7") is int model and >= 0 and < 17 ? (byte)model : null);
        return (effects, slots);
    }

    static int? Int(JsonElement row, string name) =>
        row.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int v) ? v : null;

    static List<ushort>? Hash16s(string path) => File.Exists(path)
        ? [.. Rows(path).Select(r => Int(r, "HASH16") is int h ? (ushort)h : (ushort)0)]
        : null;

    static List<JsonElement> Rows(string path)
    {
        if (!File.Exists(path)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return [.. doc.RootElement.EnumerateArray().Select(e => e.Clone())];
    }
}
