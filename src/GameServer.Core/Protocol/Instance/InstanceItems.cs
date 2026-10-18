using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>Socket colours as the Sockets component stores them (u8 per socket, bits 1..7).</summary>
public enum SocketColour : byte { Red = 1, Green = 2, Blue = 3, White = 4 }

/// <summary>A socket of an item, with the gem in it (if any).</summary>
public sealed record ItemSocket(SocketColour Colour, Item? Gem = null);

/// <summary>
/// An item as the client deserializes it (Agents/docs/protocol/05-inventories-and-ui.md §6). The type is a BaseItemTypes
/// row: <see cref="TypeHash"/> is its HASH32 (MurmurHash2 of the Id, <see cref="InstanceEntities.ObjectTypeHash"/>),
/// which the client looks up in Metadata/ItemTypeRegister; <see cref="Components"/> are the type's components from its
/// .it chain (`scripts/re/ot_components.py --items`), in order. Each one reads its own data (vtable slot 5, +0x28).
/// </summary>
public sealed record Item(uint TypeHash, IReadOnlyList<string> Components, ushort VisualIdentity)
{
    public byte ItemLevel { get; init; } = 1;
    public byte Quality { get; init; }
    public int Charges { get; init; }
    public ushort StackSize { get; init; } = 1;
    public IReadOnlyList<ItemSocket> Sockets { get; init; } = [];

    /// <summary>Link groups: the number of sockets in each, in socket order (null: every socket alone).</summary>
    public IReadOnlyList<byte>? Links { get; init; }
    public int GemExperience { get; init; }

    /// <summary>The four item types whose data ends with an extra u16 (0x1421ebe90; the writer sends 1).</summary>
    static readonly uint[] TrailingU16 = [0x192c7aab, 0x365f206a, 0x503da850, 0x9aff2df6];

    /// <summary>Components whose slot 5 reads nothing (their vtables' slot 5 is the bare `ret` 0x1400f0f00).</summary>
    public static readonly HashSet<string> NoData = ["LocalStats", "AttributeRequirements", "Weapon", "Usable", "Flask", "Quest", "Shield"];

    /// <summary>Components <see cref="Payload"/> can write.</summary>
    public static readonly HashSet<string> Supported = [.. NoData, "Base", "Mods", "Quality", "Sockets", "Charges", "Stack", "SkillGem"];

    public static bool CanSerialize(IEnumerable<string> components) => components.All(Supported.Contains);

    /// <summary>
    /// The item data the reader 0x1421ec390 takes (all little-endian: it reads with memcpy): u32 type hash, u16 flags
    /// (bits 0-8 → item +0x28..+0x2f, bit 4 also passed to the item ctor; bit 9 → a u64 follows), then each component's
    /// data in the type's order, then a u16 for four types.
    /// </summary>
    public byte[] Payload()
    {
        if (!CanSerialize(Components)) throw new ArgumentException($"unsupported item components: {string.Join(", ", Components.Where(c => !Supported.Contains(c)))}");
        var w = new BlobWriter().U32(TypeHash).U16(0);
        foreach (string c in Components)
        {
            switch (c)
            {
                // 0x1421e69c0: u16 ItemVisualIdentity HASH16 (null → "Attempt to set visual identity to NULL"), u8 +0xc0,
                // u8 +0xc7 flags, u16 flags (low: bit0 → u8 +0xc5, bit1 flavour text u16, bit2 wide string, bit3 u32,
                // bit4 wide string, bit5 u8 n × (u32, i32), bit6 nested item, bit7 u8 n × (u32 item type, compact);
                // high: bits 0-2 → +0xc4, bit4 → 2 × u32), u8 +0xc6.
                case "Base": w.U16(VisualIdentity).U8(0).U8(0).U16(0).U8(0); break;
                // 0x1421df6e0: u8 +0x2b8 (item level), u8 +0x2bc, u16 flags (bit 6 incubator, bit 4 → u8, bit 15 a 0x1c8
                // record), the base magic properties 0x142201d70 (u16 flags: bits 0-2 rarity, bit 3 identified; u8), u8, u8.
                case "Mods": w.U8(ItemLevel).U8(0).U16(0).U16(0x0008).U8(0).U8(0).U8(0); break;
                case "Quality": w.U8(Quality); break;                 // 0x1424aec50
                case "Charges": w.CompactI(Charges); break;           // 0x14249d4b0: signed compact (0x141e490e0)
                case "Stack": w.U16(StackSize); break;                // 0x1423f4b30
                // 0x14246eb00: i32 experience (-1: none), u32 +0x3c, u8 +0x10, u16 +0x40.
                case "SkillGem": w.I32(GemExperience).U32(0).U8(0).U16(0); break;
                // 0x1421e2e20: u8 = bit0/bit1 (special, nothing more) | socket count << 2 | link group count << 5; per socket
                // u8 colour << 1 | has gem, then the gem (item data without the length prefix); then a u8 per link group
                // (assumed: the group's socket count).
                case "Sockets":
                    int n = Math.Min(Sockets.Count, 6);
                    var links = Links ?? [.. Enumerable.Repeat((byte)1, n)];
                    w.U8((byte)(n << 2 | Math.Min(links.Count, 7) << 5));
                    foreach (var s in Sockets.Take(n))
                    {
                        w.U8((byte)((byte)s.Colour << 1 | (s.Gem is null ? 0 : 1)));
                        if (s.Gem is not null) w.Bytes(s.Gem.Payload());
                    }
                    foreach (byte g in links.Take(7)) w.U8(g);
                    break;
            }
        }
        if (Array.IndexOf(TrailingU16, TypeHash) >= 0) w.U16(1);
        return w.ToArray();
    }

    /// <summary>The item as inventories and other messages carry it (0x1421ec1d0 / 0x1421ec2c0): u16 big-endian length,
    /// then <see cref="Payload"/>.</summary>
    public byte[] Wire() => new LoginWriter().Blob(Payload()).ToArray();
}

/// <summary>
/// An equipped item as the player's model shows it (Inventories component entry, 0x141e1ee30 → 0x1424f44e0). u8 slot
/// (Inventories.dat column 7: Weapon1 0, Offhand1 1, BodyArmour1 2, Helm1 4, Gloves1 5, Boots1 6, Ring1 8, Ring2 9,
/// Belt1 10, Amulet1 11), then the record: u8 tag (0 = empty, nothing else), two (u16 ItemVisualIdentity HASH16, u32
/// TieredMicrotransactions key, u8) references (0x1424f3fc0, 0x1424f4250), u8 ItemStances row (0x1424f4b10; Wand = 2),
/// u16 ItemVisualIdentity HASH16 (0x1424f4730), u8 (+0x1e).
/// </summary>
public sealed record VisibleItem(byte Slot, ushort VisualIdentity, byte Stance = 0xff)
{
    /// <summary>An empty slot (tag 0): what clears an unequipped item from the model. A slot left out of the list keeps
    /// whatever the model showed before.</summary>
    public static VisibleItem Empty(byte slot) => new(slot, 0);

    public void Write(BlobWriter w)
    {
        if (VisualIdentity == 0) { w.U8(Slot).U8(0); return; }
        w.U8(Slot).U8(1).U16(VisualIdentity).U32(0).U8(0).U16(0).U32(0).U8(0).U8(Stance).U16(VisualIdentity).U8(0);
    }
}

/// <summary>
/// An item lying on the ground, `Metadata/MiscellaneousObjects/WorldItem`: Positioned, BaseEvents (no data), Animated,
/// WorldItem, InteractionAction (+ client Render, Targetable). WorldItem (0x141ea10a0): u32 a (non-zero → one more u32),
/// u8 (+0x50), u32 ItemVisualIdentity HASH16 override (0 = the item's own), then the item data without its length
/// prefix (0x1421ec390). The component then calls its vtable +0xb0 with the item, the u8 and both u32.
/// </summary>
public sealed record WorldItemObject(int GridX, int GridY, Item Item)
{
    public const string MetadataPath = "Metadata/MiscellaneousObjects/WorldItem";

    public byte[] Blob()
    {
        var w = new BlobWriter();
        w.U8(0).U8(0);                                   // object flags, base data entry count
        w.I32(GridX).I32(GridY).F32(0).U16(0).U8(0).U8(0); // Positioned
        w.U8(0);                                         // Animated: none
        w.U32(0).U8(0).U32(0).Bytes(Item.Payload());     // WorldItem
        w.U8(0);                                         // InteractionAction
        w.U8(0);                                         // tasks
        return w.ToArray();
    }
}

/// <summary>An item at a grid cell of an inventory. <see cref="Id"/> identifies it within the inventory (the client keeps
/// a map by id and drops duplicates).</summary>
public sealed record InventoryItem(uint Id, byte X, byte Y, Item Item);

public static class InstanceItems
{
    public const ushort InventoryChangeId = 0xbf, PickUpItemId = 0x28, DropItemId = 0x29, PlaceItemId = 0x2a, InventoryRequestId = 0x2d,
        ItemActionId = 0x4a, CommandId = 0x16, SocketGemId = 0x30, UnsocketGemId = 0x2f;

    /// <summary>
    /// C→S 0x2f (17 bytes): take the gem out of a socket onto the cursor (a click on a socketed gem with nothing held).
    /// u32 inventory id, u32 item id, u32 socket index, then three bytes (u8 0 in every capture, two that vary).
    /// </summary>
    public static SocketGem ReadUnsocketGem(ReadOnlySpan<byte> m) => ReadSocketGem(m);

    /// <summary>
    /// C→S 0x30 (class 0x1435c0838, write 0x141f90650; 16 bytes): put the held gem into a socket. u32 inventory id, u32
    /// item id, u32 socket index, u8 +0x1c, u8 +0x1d (both vary between clicks; unused here).
    /// </summary>
    public sealed record SocketGem(uint InventoryId, uint ItemId, uint Socket);

    public static SocketGem ReadSocketGem(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        return new(r.U32(), r.U32(), r.U32());
    }

    /// <summary>Commands.dat row 8 (DestroyCursorItem, "/destroy") + 1: what the Keep / Destroy popup's Destroy sends.</summary>
    public const byte DestroyCursorItemCommand = 9;

    /// <summary>
    /// C→S 0x16 (class 0x1435bff00, write 0x141f91480, sender 0x14215c3b0): a chat command from Commands.dat with
    /// SendsGenericCommandPacket. u8 command (row + 1), then the argument: u16 byte count + UTF-8 (0x140e0c9b0; at most
    /// 300 characters, 1000 for command 0x39). The Keep / Destroy popup for an item dropped in town sends
    /// <see cref="DestroyCursorItemCommand"/> with no argument.
    /// </summary>
    public sealed record Command(byte Id, string Argument);

    public static Command ReadCommand(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        byte id = r.U8();
        return new(id, System.Text.Encoding.UTF8.GetString(r.Blob()));
    }

    /// <summary>The length of a 0x16 at the start of <paramref name="data"/>, or null until its header has arrived.</summary>
    public static int? CommandLength(ReadOnlySpan<byte> data) =>
        data.Length < 5 ? null : 5 + System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data[3..]);

    // C→S 0x29 (class 0x14316c930, id only, sender 0x140117b80): drop the held item into the world. Outside town the
    // client sends it at once; in town (and for quest items) it shows the Keep / Destroy popup instead and sends nothing
    // unless Destroy is chosen (0x16, DestroyCursorItemCommand).

    /// <summary>
    /// S→C 0xbf (dispatcher case 0x34, class 0x1435dbd28, write 0x1421a37f0 → 0x1425079a0; handler 0x141d62550 →
    /// 0x1421f5160): change one inventory. u8 manager, u32 inventory id, u32 revision (→ inventory +0x1e8), u32 removed
    /// count (0xffffffff = remove every item) + that many u32 item ids, u32 added count + (u32 id, u8 x, u8 y, item), u8
    /// flag (1: 4 × u32 follow, the 0xc0 flag-bit-4 block). An unknown inventory id is fatal ("Got an inventory set change
    /// for an inventory that isn't in the set").
    /// </summary>
    public static byte[] InventoryChange(uint inventoryId, uint revision, IReadOnlyList<uint> removed, IReadOnlyList<InventoryItem> added, byte manager = 0)
    {
        var w = new LoginWriter().U16(InventoryChangeId).U8(manager).U32(inventoryId).U32(revision).U32((uint)removed.Count);
        foreach (uint id in removed) w.U32(id);
        w.U32((uint)added.Count);
        foreach (var it in added) w.U32(it.Id).U8(it.X).U8(it.Y).Bytes(it.Item.Wire());
        return w.U8(0).ToArray();
    }

    /// <summary>
    /// C→S 0x28 (class 0x1435bf5f0, write 0x141fa0d00; 13 bytes): a click on an item in an inventory, which picks it up to
    /// the cursor. u32 inventory id with HNC side-channel bits in bits 21-29 (the reader 0x141fa0ea0 masks with
    /// 0xc01fffff), u32 item id, u8, u8, u8 (+0x18..+0x1a; the first was 3 in every capture).
    /// </summary>
    public sealed record PickUpItem(uint InventoryId, uint ItemId, byte A, byte B, byte C);

    public static PickUpItem ReadPickUpItem(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        return new(r.U32() & 0xc01fffff, r.U32(), r.U8(), r.U8(), r.U8());
    }

    /// <summary>
    /// C→S 0x2a (class 0x1435bf710, write 0x141f8feb0, sender 0x14215f400; 18 bytes): put the item held on the cursor
    /// into an inventory. u32 inventory id, u32 x, u32 y (the target cell; 0, 0 for equipment), u8 +0x1d, u8 +0x1c (3 in
    /// every capture), u8 +0x1e, u8 +0x1f. The client sends it for any click while it holds an item, including on an
    /// occupied cell (a swap).
    /// </summary>
    public sealed record PlaceItem(uint InventoryId, uint X, uint Y, byte A, byte B, byte C, byte D);

    public static PlaceItem ReadPlaceItem(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        return new(r.U32(), r.U32(), r.U32(), r.U8(), r.U8(), r.U8(), r.U8());
    }

    // Framed so they do not stop the parser, not handled yet:
    // C→S 0x2d (class 0x1435c02b8, senders 0x14215f520 / 0x14215f570; 8 bytes): u8, u32, u8 flag (0 / 1 by sender).
    // C→S 0x4a (class 0x1435bf6d0, sender 0x14215f5c0; 12 bytes): u32, u32, u8 flags, u8 (default 3), u16.
}
