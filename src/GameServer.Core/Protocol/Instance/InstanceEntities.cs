using System.Buffers.Binary;
using System.Text;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// Entity messages on the instance connection (Agents/docs/re/010, "Entering the area"). Message fields are big-endian like
/// every other message. The component blob inside 0x2b9 is read with plain memcpy (0x140e28cf0 and friends), so its
/// multi-byte fields are <b>little-endian</b>.
/// </summary>
public static class InstanceEntities
{
    public const ushort LocalPlayerId = 0x22, CreateObjectId = 0x2b9, AddInventoryId = 0xc0, PassiveSlotsId = 0x3d;

    /// <summary>
    /// The u32 an object type is known by: MurmurHash2 (seed 0) over the metadata path with each UTF-16 unit narrowed to
    /// a byte (0x1424748a0 → 0x140df8570). The client's Game::GameObjectTypeRegister is built from its own dat tables
    /// (0x141ec18c0; Characters.dat rows among them), so nothing has to be registered over the network.
    /// </summary>
    public static uint ObjectTypeHash(string path)
    {
        Span<byte> b = path.Length <= 256 ? stackalloc byte[path.Length] : new byte[path.Length];
        for (int i = 0; i < path.Length; i++) b[i] = (byte)path[i];
        const uint m = 0x5bd1e995;
        uint h = (uint)b.Length;
        int p = 0;
        for (; b.Length - p >= 4; p += 4)
        {
            uint k = BinaryPrimitives.ReadUInt32LittleEndian(b[p..]) * m;
            k ^= k >> 24;
            h = h * m ^ k * m;
        }
        switch (b.Length - p)
        {
            case 3: h ^= (uint)b[p + 2] << 16; goto case 2;
            case 2: h ^= (uint)b[p + 1] << 8; goto case 1;
            case 1: h = (h ^ b[p]) * m; break;
        }
        h = (h ^ h >> 13) * m;
        return h ^ h >> 15;
    }

    /// <summary>
    /// S→C 0x3d (dispatcher case 0x11, class 0x1435dcd00, write 0x14219eb40): u16 +0x28, u16 +0x2a, flags (bit0 +0x2c,
    /// bit1 +0x2d = no list, bits 2/3/4 → create slot 0/1/2 via 0x142155bf0), then unless bit1 a vec16 of raw u16
    /// (likely allocated passive nodes). The three slots at instance+0xc2f8 (active index +0xc2f0) are read when the
    /// area UI is built (0x140785160), so all three are created, with an empty list.
    /// </summary>
    public static byte[] PassiveSlots(ushort u28 = 0, ushort u2a = 0) =>
        new LoginWriter().U16(PassiveSlotsId).U16(u28).U16(u2a).U8(0x1c).U16(0).ToArray();

    /// <summary>S→C 0x22 (u32, dispatcher case 0xb): the local player's entity id (world+0x980, setter 0x142186fa0).</summary>
    public static byte[] SetLocalPlayer(uint entityId) => new LoginWriter().U16(LocalPlayerId).U32(entityId).ToArray();

    /// <summary>
    /// S→C 0x2b9 (world group, class 0x1435dd8f8, write 0x141fe04d0): entity header (u32 id, u32, u16; 0x141f91f60),
    /// u32 object type hash, blob16 component data. An object whose id equals the local player id becomes the player.
    /// </summary>
    public static byte[] CreateObject(uint entityId, uint objectType, byte[] blob, uint headerU32 = 0, ushort headerU16 = 0) =>
        new LoginWriter().U16(CreateObjectId).U32(entityId).U32(headerU32).U16(headerU16).U32(objectType).Blob(blob).ToArray();

    public const ushort MonsterTypesId = 0x20;

    /// <summary>
    /// S→C 0x20 (instance group, dispatcher case 9, class 0x1435dc320, read 0x1421a4f30): monster types at a level.
    /// u16 count, u16 preload (+0x10, the first n new types are loaded at once), then count × raw u16 (little-endian)
    /// MonsterVarieties row and count × u8 level. Per pair the client registers "&lt;variety Id&gt;@&lt;level&gt;"
    /// (0x141ec2540; level 0 registers the plain Id, 0x141ec28e0). The Monster component takes its level from that
    /// suffix (1..100) and throws "Creating monster with no level" without one, so a monster's 0x2b9 type is
    /// <see cref="MonsterObject.TypePath"/>.
    /// </summary>
    public static byte[] MonsterTypes(IReadOnlyList<(ushort Row, byte Level)> types, ushort preload = ushort.MaxValue)
    {
        var w = new LoginWriter().U16(MonsterTypesId).U16((ushort)types.Count).U16(Math.Min(preload, (ushort)types.Count));
        foreach (var (row, _) in types) w.U8((byte)row).U8((byte)(row >> 8));
        foreach (var (_, level) in types) w.U8(level);
        return w.ToArray();
    }

    public const ushort StatsUpdateId = 0x1b3;

    /// <summary>
    /// S→C 0x1b3 (world group, class 0x1435dda98, read 0x141fe1350): entity header, unsigned compact count, then per stat
    /// an unsigned compact key (Stats.dat row) and a signed compact value. The world handler (0x142187480) sets each on the
    /// object's Stats component (0x1424cfaf0).
    /// </summary>
    public const ushort ComponentUpdateId = 0x2ba;

    /// <summary>
    /// S→C 0x2ba (world group, read 0x141fe05b0): world header, then blob16 = u8 component index (in the object's
    /// component order, 04 §3) + that component's vtable+0xa0 reader data (world handler 0x142187480).
    /// </summary>
    public static byte[] ComponentUpdate(uint entityId, byte componentIndex, byte[] data) =>
        new LoginWriter().U16(ComponentUpdateId).U32(entityId).U32(0).U16(0).Blob([componentIndex, .. data]).ToArray();

    /// <summary>The player's Actor (component 9) skill list again (slot 20 f0 0x40, 11-skills.md §3): replaces skills of the
    /// same id, e.g. with new stats.</summary>
    public static byte[] PlayerSkillsUpdate(uint playerId, IReadOnlyList<GrantedSkill> skills)
    {
        var w = new BlobWriter().U8(0x40).U8(0);
        PlayerObject.WriteSkillList(w, skills);
        return ComponentUpdate(playerId, PlayerObject.ActorComponentIndex, w.ToArray());
    }

    public static byte[] StatsUpdate(uint entityId, IReadOnlyList<(uint Stat, int Value)> stats)
    {
        var w = new LoginWriter().U16(StatsUpdateId).U32(entityId).U32(0).U16(0);
        InstanceProtocol.CompactUInt(w, (uint)stats.Count);
        foreach (var (stat, value) in stats)
        {
            InstanceProtocol.CompactUInt(w, stat);
            InstanceProtocol.CompactInt(w, value);
        }
        return w.ToArray();
    }
}

public static partial class InstanceInventories
{
    /// <summary>One Inventories.dat row the player starts with: row index (the manager's type slot), the row's InventoryType
    /// enum (first u32) and its grid size.</summary>
    public readonly record struct Slot(byte Row, byte Kind, byte Width, byte Height);

    /// <summary>Every Inventories.dat row of 3.29.3b (117, dumped from Data/Inventories.datc64): kind = the row's first
    /// u32, width = second u32, height = byte 10 after the Id. The client looks inventories up by row from many UI places
    /// (409 call sites of 0x141d621a0 across ~50 rows), so an empty inventory is registered for every row.</summary>
    public static readonly Slot[] PlayerDefaults =
    [
        new(0, 0x00, 12, 5), new(1, 0x01, 2, 3), new(2, 0x02, 2, 4), new(3, 0x03, 2, 4), new(4, 0x04, 2, 2), new(5, 0x05, 1, 1),
        new(6, 0x06, 1, 1), new(7, 0x06, 1, 1), new(8, 0x07, 2, 2), new(9, 0x08, 2, 2), new(10, 0x09, 2, 1), new(11, 0x0a, 5, 2),
        new(12, 0x0b, 4, 4), new(13, 0x0c, 3, 2), new(14, 0x02, 2, 4), new(15, 0x03, 2, 4), new(16, 0x0f, 2, 4), new(17, 0x0f, 2, 4),
        new(18, 0x0f, 2, 4), new(19, 0x0f, 2, 4), new(20, 0x0f, 2, 4), new(21, 0x0f, 2, 4), new(22, 0x0f, 2, 4), new(23, 0x0d, 60, 1),
        new(24, 0x0e, 10, 4), new(25, 0x0c, 6, 1), new(26, 0x11, 1, 1), new(27, 0x0f, 4, 8), new(28, 0x0f, 2, 4), new(29, 0x0f, 5, 1),
        new(30, 0x10, 3, 1), new(31, 0x0f, 2, 4), new(32, 0x0f, 2, 5), new(33, 0x0f, 2, 4), new(34, 0x0f, 6, 4), new(35, 0x0f, 2, 4),
        new(36, 0x0f, 4, 1), new(37, 0x0f, 2, 4), new(38, 0x0f, 5, 1), new(39, 0x1c, 4, 1), new(40, 0x1c, 4, 1), new(41, 0x0f, 1, 1),
        new(42, 0x0f, 2, 4), new(43, 0x0f, 2, 4), new(44, 0x0f, 1, 1), new(45, 0x22, 7, 2), new(46, 0x22, 7, 2), new(47, 0x22, 7, 2),
        new(48, 0x22, 7, 2), new(49, 0x22, 7, 2), new(50, 0x22, 7, 2), new(51, 0x22, 7, 2), new(52, 0x22, 7, 2), new(53, 0x22, 7, 2),
        new(54, 0x23, 1, 1), new(55, 0x0f, 1, 1), new(56, 0x24, 107, 12), new(57, 0x25, 100, 4), new(58, 0x0c, 3, 2), new(59, 0x0c, 3, 2),
        new(60, 0x0c, 3, 2), new(61, 0x0c, 3, 2), new(62, 0x3d, 4, 5), new(63, 0x2a, 5, 5), new(64, 0x2b, 3, 1), new(65, 0x0c, 64, 5),
        new(66, 0x2c, 2, 4), new(67, 0x29, 1, 1), new(68, 0x29, 5, 5), new(69, 0x1d, 14, 9), new(70, 0x2d, 6, 5), new(71, 0x0f, 1, 1),
        new(72, 0x30, 6, 2), new(73, 0x31, 37, 10), new(74, 0x32, 1, 1), new(75, 0x0c, 5, 1), new(76, 0x34, 5, 4), new(77, 0x35, 12, 12),
        new(78, 0x36, 1, 2), new(79, 0x29, 2, 4), new(80, 0x29, 2, 4), new(81, 0x29, 1, 1), new(82, 0x37, 1, 1), new(83, 0x0f, 6, 4),
        new(84, 0x00, 4, 5), new(85, 0x38, 6, 5), new(86, 0x0c, 11, 1), new(87, 0x39, 12, 10), new(88, 0x3a, 12, 3), new(89, 0x3b, 6, 7),
        new(90, 0x3b, 6, 7), new(91, 0x3b, 6, 7), new(92, 0x0f, 8, 4), new(93, 0x0f, 4, 4), new(94, 0x0c, 3, 2), new(95, 0x3c, 17, 4),
        new(96, 0x3c, 17, 4), new(97, 0x3c, 17, 4), new(98, 0x11, 1, 2), new(99, 0x11, 1, 1), new(100, 0x11, 1, 2), new(101, 0x3f, 24, 12),
        new(102, 0x06, 1, 1), new(103, 0x0f, 1, 1), new(104, 0x0f, 120, 1), new(105, 0x0f, 2, 4), new(106, 0x0f, 8, 4), new(107, 0x3c, 17, 4),
        new(108, 0x0f, 10, 4), new(109, 0x0f, 1, 1), new(110, 0x0f, 4, 1), new(111, 0x0f, 1, 1), new(112, 0x44, 12, 79), new(113, 0x41, 12, 5),
        new(114, 0x45, 12, 40), new(115, 0x46, 3, 1), new(116, 0x46, 6, 1),
    ];

    /// <summary>
    /// S→C 0xc0 (dispatcher case 0x35, class 0x1431ab2f0, write 0x140b182a0): add an empty inventory. u8 manager
    /// (0x14216aa30: 0 = the player's, at instance+0xc890), u32 inventory id, inventory object (0x1421f4a40 / reader
    /// 0x1421f4cc0: u8 kind +0x140, u8 Inventories.dat row +0x144, u8 width, u8 height, u8 flags, compact-int item count),
    /// u8 +0x28, u8 +0x29. 0x141d625f0 files the id under its row; UI such as the flask bar looks it up by row (row 11).
    /// </summary>
    public static byte[] AddInventory(uint inventoryId, Slot slot, byte manager = 0) =>
        new LoginWriter().U16(InstanceEntities.AddInventoryId).U8(manager).U32(inventoryId)
            .U8(slot.Kind).U8(slot.Row).U8(slot.Width).U8(slot.Height).U8(0).U8(0)
            .U8(0).U8(0).ToArray();
}

/// <summary>
/// An area transition (exit) object, `Metadata/MiscellaneousObjects/AreaTransition`: the clickable exit to another area,
/// with its minimap icon. AreaTransition.ot components: Positioned, BaseEvents (server only), Animated, AreaTransition,
/// Stats, ObjectMagicProperties, MinimapIcon, InteractionAction (+ client Render, Targetable).
/// <para>Component deserializers (vtable +0x88, found from the components' static initializers → pool → factory):
/// AreaTransition `0x141e5b870`: u8 flags (low nibble +0xb2 state, bit4 +0xb5, bit5 +0xb6, bit6 +0xb7, bit7 → extra record
/// `0x141e62df0`), then the destination `0x141e62490`, a std::variant read as u8 index + value (0 → u16, 1 → u32,
/// 2 → u16). Index 2 is a WorldAreas.HASH16 (`0x141e5be60` resolves it with `0x1422d9920`, the 0x14/0x1d area lookup).
/// MinimapIcon slot 20 `0x141e6f5d0`: u8 flags (bit0 → u16 icon override, bit3 → variant). ObjectMagicProperties slot 20
/// `0x141e77990`: u8 flags (bit1 → u16 key, bit2 → string …). InteractionAction `0x141e1e240`: u8 flags (bit2 → u8 [+ u16]).
/// Stats as the player's comp1 (`0x1421c2f40`).</para>
/// </summary>
public sealed record AreaTransitionObject(int GridX, int GridY, ushort DestinationHash16)
{
    public const string MetadataPath = "Metadata/MiscellaneousObjects/AreaTransition";

    /// <summary>Components that read data, in the order the client deserializes them for this type (traced live
    /// 2026-10-16: Positioned, BaseEvents (no data), Animated, AreaTransition (live vtable 0x14346de00), Stats,
    /// ObjectMagicProperties, MinimapIcon, InteractionAction, then two components the loop does not reach).</summary>
    public static readonly string[] ComponentOrder =
        ["Positioned", "Animated", "AreaTransition", "Stats", "ObjectMagicProperties", "MinimapIcon", "InteractionAction"];

    public byte[] Blob()
    {
        var w = new BlobWriter();
        w.U8(0);            // object flags
        w.U8(0);            // base data entry count
        foreach (string c in ComponentOrder)
        {
            switch (c)
            {
                case "Positioned": w.I32(GridX).I32(GridY).U32(0).U16(0).U8(0).U8(0); break;
                case "Animated": w.U8(0); break;
                // state 0, variant 2 = HASH16, then a compact int (+0xa4) that is read when the type's flag
                // (type+0x78 → +0xb0 → +0x14) is set and the state is 0: true for AreaTransition (traced live).
                case "AreaTransition": w.U8(0).U8(2).U16(DestinationHash16).U8(0); break;
                case "Stats": w.U8(0).U32(0).U32(0).U8(0); break;
                // base magic properties first (0x142201d70: u16 flags, rarity = low 3 bits; u8 +0x7a; sections behind
                // flag bits 0x10/0x40/0x200/0x400/0x800 and, with bit3, 0x80/0x100/0x20), then the component's u8 flags.
                case "ObjectMagicProperties": w.U16(0).U8(0).U8(0); break;
                case "MinimapIcon": w.U8(0); break;
                case "InteractionAction": w.U8(0); break;
            }
        }
        w.U8(0);            // tasks
        return w.ToArray();
    }
}

/// <summary>
/// A monster: a 0x2b9 object whose type is a MonsterVarieties row. The client's object-type register files every
/// MonsterVarieties row (0x31a bytes) under its Id with the row's BaseMonsterTypeIndex (+0x48) as the .ot, but a monster
/// also needs a level: the server registers "&lt;Id&gt;@&lt;level&gt;" with S→C 0x20 (<see cref="InstanceEntities.MonsterTypes"/>)
/// and the 0x2b9 type is <c>ObjectTypeHash(TypePath(Id, level))</c>. The components come from the .ot chain
/// (`Metadata/Monsters/Monster`: Positioned, Stats, ObjectMagicProperties, BaseEvents, Pathfinding, Buffs, Life,
/// Animated, Inventories, Actor, Monster, then whatever the type adds).
/// <para>Every component that reads is one the player or the exit already uses; Monster itself (vtable 0x1435a6778),
/// BaseEvents and Pathfinding read nothing. Actor reads its two conditional bytes only when the current life (Life pool
/// +0x30, read just before) is below 1, so a living monster has the player's 17 bytes. Positioned's 8-byte prefix belongs
/// to types with a Sector component, which monsters lack.</para>
/// </summary>
public sealed record MonsterObject(int GridX, int GridY, uint Life, IReadOnlyList<string> Components, string? AnimatedObject = null,
    MonsterRarity Rarity = MonsterRarity.Normal, string? Name = null, StateMachineData? StateMachine = null, byte TransitionState = 1,
    ushort Team = 1, IReadOnlyList<GrantedSkill>? Skills = null, IReadOnlyList<(uint Key, int Value)>? Stats = null)
{
    /// <summary>Deserialized components of `Metadata/Monsters/Monster` in read order (scripts/re/ot_components.py).</summary>
    public static readonly string[] BaseComponents =
        ["Positioned", "Stats", "ObjectMagicProperties", "BaseEvents", "Pathfinding", "Buffs", "Life", "Animated",
         "Inventories", "Actor", "Monster"];

    /// <summary>Components a type may add after the base list that this blob can write. StateMachine (per-type state
    /// lists from the .ot) needs <see cref="StateMachineData"/>; DiesAfterTime's primary vtable 0x14359f478 has a
    /// bare `ret` slot 17, so it reads nothing.</summary>
    static readonly HashSet<string> Extras = ["Transitionable", "MinimapIcon", "Preload", "Functions", "PlayerClass", "StateMachine", "DiesAfterTime"];

    /// <summary>The registered type path of a variety at a level (1..100), after S→C 0x20 has registered it:
    /// "&lt;variety Id&gt;@&lt;level&gt;" (0x141ec2540; the Monster type parses the number after the last '@').</summary>
    public static string TypePath(string varietyId, byte level) => $"{varietyId}@{level}";

    /// <summary>Whether <see cref="Blob"/> can serialize a type with these deserialized components.</summary>
    /// StateMachine needs the type's shared states and timers (<see cref="StateMachineData"/>).
    public static bool CanSerialize(IReadOnlyList<string> components, bool haveStateMachine = false) =>
        components.Count >= BaseComponents.Length && components.Take(BaseComponents.Length).SequenceEqual(BaseComponents) &&
        components.Skip(BaseComponents.Length).All(c => Extras.Contains(c) && (c != "StateMachine" || haveStateMachine));

    public byte[] Blob()
    {
        if (!CanSerialize(Components, StateMachine is not null)) throw new ArgumentException($"unsupported components: {string.Join(", ", Components)}");
        var w = new BlobWriter();
        w.U8(0);            // object flags
        w.U8(0);            // base data entry count
        foreach (string c in Components)
        {
            switch (c)
            {
                // x, y, f32 +0x29c, u16 team (+0x1e0; the player is team 0: 0 = friendly, green outline; 1 = hostile, red),
                // flags; flags1 0x02 + (hidden 0, shown 1) as for the player.
                case "Positioned": w.I32(GridX).I32(GridY).U32(0).U16(Team).U8(0).U8(0x02).U8(0).U8(1); break;
                case "Stats": w.Stats(Stats).U32(0).U32(0).U8(0); break;
                // base magic properties (0x142201d70): u16 flags (bits 0-2 rarity → +0x7c, 0 = normal; bit 3 identified
                // → +0x78, which gates the bit 7 / bit 8 / bit 5 sections, all off here), u8 +0x7a; then the own flags.
                // Without bit 3 the client tags the monster "Unidentified". Identified, bit 7 (0x80) adds a name: u32
                // UTF-16 unit count + units (→ +0x18), what rare and unique monsters show.
                case "ObjectMagicProperties":
                    w.U16((ushort)((ushort)Rarity | Identified | (Name is null ? 0 : CustomName))).U8(0);
                    if (Name is not null) w.WStr32(Name);
                    w.U8(0);
                    break;
                case "Buffs": w.U16(0); break;
                // life, mana, energy shield pools (current, reserved, i16, u32, u32), then the flags byte.
                case "Life": Pool(w, Life); Pool(w, 0); Pool(w, 0); w.U8(0); break;
                case "Animated":
                    if (AnimatedObject is null) w.U8(0);
                    else w.U8(1).WStr32(AnimatedObject);
                    break;
                case "Inventories": w.U8(0).U8(0); break;
                // path, u16, u8, (life >= 1: no conditional bytes), u32, u32, then slot 20's two flag bytes.
                case "Actor":
                    w.WStr32("").U16(0).U8(0).U32(0).U32(0);
                    if (Skills is { Count: > 0 }) { w.U8(0x40).U8(0); PlayerObject.WriteSkillList(w, Skills); }   // its skills (11-skills.md §3)
                    else w.U8(0).U8(0);
                    break;
                // 0x1421c24b0: u8 = state (low 7 bits, → +0x120) | bit 7 (+0x122, "transitioning"). States run 1..num_states
                // (.ot Transitionable num_states, default 1); num_states = fully transitioned (create_transitioned).
                // State 0 is invalid: the post-create slot 0x1421c26f0 runs event [(state − 1) × 0x18] and crashed in the
                // event interpreter (0x141ded7b9).
                case "Transitionable": w.U8((byte)(TransitionState & 0x7f)); break;
                case "MinimapIcon": w.U8(0); break;      // slot 20 0x141e6f5d0: u8 flags
                // 0x141e3ee40: u8 shared-state count, u8 shared-timer count (the client throws unless they match the
                // type); per define_shared_state a compact int value + u8 flags (0x02 keeps the sent value), per
                // define_shared_timer a u32 + u8. The .ot defaults are sent.
                case "StateMachine":
                    var sm = StateMachine!;
                    w.U8((byte)sm.SharedStates.Count).U8((byte)sm.SharedTimers);
                    foreach (int v in sm.SharedStates) { CompactInt(w, v); w.U8(0); }
                    for (int i = 0; i < sm.SharedTimers; i++) w.U32(0).U8(0);
                    break;
                // BaseEvents, Pathfinding, Monster, Preload, Functions, PlayerClass: no data.
            }
        }
        w.U8(0);            // tasks
        return w.ToArray();
    }

    private static void Pool(BlobWriter w, uint current) => w.U32(current).U32(0).U16(0).U32(0).U32(0);

    /// <summary>Signed compact int as the blob reader 0x141e490e0 reads it (big-endian groups: 1 byte for -0x40..0x3f,
    /// 0x80 + 14 bits, 0xc0 + 21 bits, 0xe0 + 28 bits), the same encoding as InstanceProtocol.CompactInt.</summary>
    static void CompactInt(BlobWriter w, int v)
    {
        var lw = new LoginWriter();
        InstanceProtocol.CompactInt(lw, v);
        foreach (byte b in lw.ToArray()) w.U8(b);
    }

    /// <summary>ObjectMagicProperties flags bit 3: identified (+0x78); bit 7: a custom name follows.</summary>
    const ushort Identified = 0x08, CustomName = 0x80;
}

/// <summary>A type's networked StateMachine data: the define_shared_state defaults in order and the number of
/// define_shared_timer entries (scripts/re/ot_components.py "shared_states" / "shared_timers").</summary>
public sealed record StateMachineData(IReadOnlyList<int> SharedStates, int SharedTimers);

/// <summary>A skill the server grants an Actor (ActiveSkillManager, Actor +0x598): Id is the instance's u16 (+0x40),
/// which the client uses in its skill messages; the action id (+0x42) comes from the data (GEPL row → GrantedEffect →
/// ActiveSkill → ActionTypes HASH16). Rows are GrantedEffectsPerLevel and GrantedEffectStatSetsPerLevel indices.</summary>
public sealed record GrantedSkill(ushort Id, uint EffectsPerLevelRow, uint StatSetsPerLevelRow,
    IReadOnlyList<(uint Key, int Value)>? Stats = null);

/// <summary>ObjectMagicProperties rarity (flags bits 0-2 → +0x7c).</summary>
public enum MonsterRarity : byte { Normal = 0, Magic = 1, Rare = 2, Unique = 3 }

/// <summary>Little-endian writer for 0x2b9 component blobs.</summary>
public sealed class BlobWriter
{
    private readonly List<byte> _b = [];
    public byte[] ToArray() => [.. _b];
    public BlobWriter U8(byte v) { _b.Add(v); return this; }
    public BlobWriter U16(ushort v) { _b.Add((byte)v); _b.Add((byte)(v >> 8)); return this; }
    public BlobWriter U32(uint v) { U16((ushort)v); return U16((ushort)(v >> 16)); }
    public BlobWriter I32(int v) => U32((uint)v);
    public BlobWriter Zeros(int n) { for (int i = 0; i < n; i++) _b.Add(0); return this; }
    /// <summary>u32 UTF-16 unit count, then the units (0x140e28d60).</summary>
    public BlobWriter WStr32(string s) { U32((uint)s.Length); foreach (byte x in Encoding.Unicode.GetBytes(s)) _b.Add(x); return this; }
    /// <summary>Unsigned compact int (stat keys and counts, 0x141e61010), InstanceProtocol.CompactUInt.</summary>
    public BlobWriter CompactU(uint v) { var lw = new LoginWriter(); InstanceProtocol.CompactUInt(lw, v); _b.AddRange(lw.ToArray()); return this; }
    /// <summary>Signed compact int (stat values, 0x141e490e0), InstanceProtocol.CompactInt.</summary>
    public BlobWriter CompactI(int v) { var lw = new LoginWriter(); InstanceProtocol.CompactInt(lw, v); _b.AddRange(lw.ToArray()); return this; }
    /// <summary>Compact count, then (compact key, compact value) pairs: a stats list (0x1424cfaf0 per pair).</summary>
    public BlobWriter Stats(IReadOnlyList<(uint Key, int Value)>? stats)
    {
        CompactU((uint)(stats?.Count ?? 0));
        foreach (var (k, v) in stats ?? []) CompactU(k).CompactI(v);
        return this;
    }
}

/// <summary>
/// The smallest component blob for a player object (`Metadata/Characters/{class}`), every optional part off. Components
/// are serialized in the created object's component order, which was traced live with Frida (2026-10-16): 12 components,
/// of which these read data, in this order: Positioned, comp1 (vtable 0x1435dd4e8, 0x1421c2f40, stats-like), comp3
/// (0x14359f5f8, 0x141e114d0, u16-counted entries), Life, Animated, Player (0x143466450), Inventories, comp9 (0x14346e558,
/// 0x142192a80). Pathfinding and 0x1435a0078 read nothing. Comps 10/11 (0x143468920, 0x14346c238) are still unverified.
/// </summary>
public sealed record PlayerObject(string Name, byte ClassIndex, int GridX, int GridY,
    uint Life, uint Mana, uint EnergyShield = 0, uint Experience = 0, bool Shown = true, string? AnimatedObject = null,
    IReadOnlyList<GrantedSkill>? Skills = null, IReadOnlyList<(uint Key, int Value)>? Stats = null)
{
    public byte[] Blob()
    {
        var w = new BlobWriter();
        w.U8(0);            // object flags (0x141ebe190: bit0 → +0x8c|0x80, bit1 → +0x8d|2)
        w.U8(0);            // base data entry count (0x141dea920)
        // Positioned (0x141e2f0b0): x, y, u32 +0x29c, u16 +0x1e0, two flag bytes. The type's +0x21 prefix (Sector) is off.
        // flags1 0x02 adds two bytes: hidden (→ +0x1e7 bit0) and shown (→ +0x1e5 bit3); both changes call the object's
        // vtable +0xb0 (visibility). Without "shown" the player entity exists but its model is not drawn.
        w.I32(GridX).I32(GridY).U32(0).U16(0).U8(0).U8(Shown ? (byte)0x02 : (byte)0);
        if (Shown) w.U8(0).U8(1);
        // comp1 = Stats (0x1421c2f40): compact-int count of (compact-int key, value) pairs, then u32 +0x28, u32 +0x2c, u8
        // +0x30. Keys are the client's stat ids (Stats.dat row + 1).
        w.Stats(Stats).U32(0).U32(0).U8(0);
        // comp3 (0x141e114d0): u16 count of entries (u16, u8, 0x141e06bb0 record).
        w.U16(0);
        // Life (0x141e6c2f0): life, mana and energy shield pools (0x141e6be70), then a flags byte (0x141e6c270).
        Pool(w, Life); Pool(w, Mana); Pool(w, EnergyShield);
        w.U8(0);
        // Animated (0x141e56a80): flags. Bit0 + !bit1: u32 char count + UTF-16LE animated-object path; the client loads it
        // when it differs from the current one or none is set (+0x260 null), creating the 0x308-byte AO instance.
        if (AnimatedObject is null) w.U8(0);
        else w.U8(1).WStr32(AnimatedObject);
        // Player (0x141e7dc10).
        w.WStr32(Name)
         .U8(ClassIndex)     // Characters.dat row; bit5 and bits6-7 are the ascendancy (0x142156d80)
         .U32(Experience)    // 0x141e7f6b0 → +0x18c; the level is derived from it (+0x1ac)
         .U32(0).U16(0).U16(0)
         .U16(0).U16(0)      // two vec16<u8, 8 bytes> (0x141e83340)
         .Zeros(9)           // raw +0x1e8
         .U16(0).U16(0)      // Hideouts.dat keys
         .U8(0)              // +0x1b1 (non-zero → "WashedUp" state)
         .U8(0).U8(0).U8(0).U8(0).U8(0)
         .U32(0)             // u32 count + u32 list (vtable +0x158)
         .U8(0);             // microtransaction counters (0x141e83470)
        w.U8(0).U8(0);      // Inventories (0x141e1ee30): inventory count, second count
        // comp9 = Actor (0x142192a80): u32-counted UTF-16 string, u16 +0x21c, u8; two more u8 (+0x230, +0x234) only
        // when a runtime value is < 1 (false here: traced live, comp9 read 15 bytes); u32 +0x26c, u32 +0x274; then its
        // vtable+0xa0 reader 0x1421932b0: two flag bytes. f0 0x40 = the skill list (0x142197b10); the rest stay off.
        w.WStr32("").U16(0).U8(0).U32(0).U32(0);
        if (Skills is { Count: > 0 })
        {
            w.U8(0x40).U8(0);
            WriteSkillList(w, Skills);
        }
        else w.U8(0).U8(0);
        w.U8(0);            // tasks (0x141ee00f0)
        return w.ToArray();
    }

    /// <summary>Actor slot 20, f0 0x40 (0x142197b10): u8 n × skill (0x142196ce0), u8 m × (u32 row, u32, u32), u32 k ×
    /// (u16 skill id, u32 c × 4 u32). Each skill: u16 id (+0x40), the GrantedEffectsPerLevel reference (0x1424a0dc0:
    /// u8 → +0x10, u32 GEPL row, then a GrantedEffectStatSetsPerLevel reference 0x1424a0990: u8 a [u8 if a], u32 GESSPL
    /// row; then one more stat-set reference per entry of the effect's additional stat sets, none for these skills), u8
    /// flags (bit0 → u32 parent object id + u16), u8 support count × (GEPL reference, u32), u32, u32, u32, u8, then the
    /// skill's stats (compact count × compact key, compact value) and one such stat list per additional stat set.</summary>
    /// <summary>The Actor's index in the player object's component order (04 §3).</summary>
    public const byte ActorComponentIndex = 9;

    internal static void WriteSkillList(BlobWriter w, IReadOnlyList<GrantedSkill> skills)
    {
        w.U8((byte)skills.Count);
        foreach (var s in skills)
        {
            w.U16(s.Id)
             .U8(0).U32(s.EffectsPerLevelRow)      // GEPL reference
             .U8(0).U32(s.StatSetsPerLevelRow)     // its stat set
             .U8(0)                                // flags: no parent object
             .U8(0)                                // supports
             .U32(0).U32(0).U32(0).U8(0)
             .Stats(s.Stats);                      // the skill's own stats
        }
        w.U8(0).U32(0);
    }

    /// <summary>One Life pool (0x141e6be70): u32 +0x30 current, u32 +0x10 reserved (flat), i16 +0x14 (reserved percent?),
    /// u32 +0x28, u32 +0x40. The maximum is not in the blob: the client computes it from stats (Witch level 1: life 50,
    /// mana 40). Live 2026-10-16: sending 50 in both u32 showed "Life 50/50 Reserved 50" and "Mana 50/40 Reserved 40", i.e.
    /// +0x30 is current and +0x10 a reserve clamped to the maximum.</summary>
    private static void Pool(BlobWriter w, uint current) => w.U32(current).U32(0).U16(0).U32(0).U32(0);
}
