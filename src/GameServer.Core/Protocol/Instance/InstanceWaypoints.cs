using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// Waypoints (Agents/docs/protocol/16-waypoints.md): the character's unlocked waypoints (S→C 0x4c), the World panel opened
/// for travel (S→C 0x7b kind 0) and the travel request from it (C→S 0x4d).
/// </summary>
public static class InstanceWaypoints
{
    public const ushort WaypointsId = 0x4c, OpenUiId = 0x7b, TravelId = 0x4d;

    /// <summary>Bytes of the unlocked-waypoint bitset (152 bits; the client reads at most 150 waypoint areas).</summary>
    public const int BitsetLength = 19;

    /// <summary>S→C 0x7b kind that opens the World panel in waypoint mode (0x142163420 case 0: listeners at instance
    /// +0xff8 → 0x140dcd570 → 0x140ab1440 → each panel's 0x140a97330).</summary>
    public const byte WorldPanelUi = 0;

    /// <summary>
    /// S→C 0x4c (class 0x1435dc7e0, read 0x14219f460; dispatcher case 0x12): u32 BE (+0x10, passed with the bitset to the
    /// listeners at instance +0x73f0), then 19 raw bytes (+0x14) copied to instance +0xcf31: bit i = the i-th WorldAreas
    /// row with HasWaypoint whose column +0x1e4 is 1, 2, 5 or 11 (0x141fdcf90; <see cref="World.WorldAreaWaypoints"/>).
    /// The World panel lights the pins of these areas and lets the player travel to them. When the current area's bit
    /// turns on in a later 0x4c of the same connection, the client plays the waypoint's activation (0x1421449f0 state 2).
    /// </summary>
    public static byte[] Waypoints(IEnumerable<int> unlockedBits, uint u10 = 0)
    {
        var bits = new byte[BitsetLength];
        foreach (int i in unlockedBits)
            if (i >= 0 && i < BitsetLength * 8) bits[i >> 3] |= (byte)(1 << (i & 7));
        return new LoginWriter().U16(WaypointsId).U32(u10).Fixed(bits, BitsetLength).ToArray();
    }

    /// <summary>
    /// S→C 0x7b (class 0x1435dc2a0, read 0x1421a0450; dispatcher case 0x1c → 0x142163420): open a UI. u8 kind (0x00..0x72),
    /// then four u32 BE (+0x14, +0x18, +0x1c, +0x20) whose use depends on the kind. Kind 0 opens the World panel for
    /// waypoint travel; its listener ignores the u32s, which the Gateway fills with the waypoint object id.
    /// </summary>
    public static byte[] OpenUi(byte kind, uint a = 0, uint b = 0, uint c = 0, uint d = 0) =>
        new LoginWriter().U16(OpenUiId).U8(kind).U32(a).U32(b).U32(c).U32(d).ToArray();

    /// <summary>C→S 0x4d: a waypoint picked on the World panel. Ctrl asks for the instance chooser.</summary>
    public sealed record Travel(ushort AreaHash16, uint U32, bool Ctrl);

    /// <summary>
    /// C→S 0x4d (9 bytes; class 0x1435c08b8, write 0x141f92e60; sent by the panel's pin click 0x140a9b580): u16 BE
    /// destination WorldAreas.HASH16 (the pin's row +0x2a), u32 BE (the panel's +0x524), u8 Ctrl held.
    /// </summary>
    public static Travel ReadTravel(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        return new(r.U16(), r.U32(), r.U8() != 0);
    }

    public const int TravelLength = 9;
}

/// <summary>
/// A waypoint, <c>Metadata/MiscellaneousObjects/Waypoint</c>: Positioned (object_size 6, not blocking), BaseEvents (server
/// only), Animated, HideoutDoodad, MinimapIcon (icon 9), InteractionAction (action "waypoint"), StateMachine (shared state
/// "active"), client Render + Targetable. Like an area transition it is a tile object: the area's .tmd record gives its
/// place, rotation, height and virtual .ao (08-area-transitions.md §5, §7).
/// <para>HideoutDoodad (slot 22 0x141e1c550, reached through slot 17 0x1415d9920) reads u32 + u8 + u8 only when the
/// type's HideoutDoodad data sets +0x14e; Waypoint.ot's section is empty, so it reads nothing. StateMachine as for
/// monsters: u8 1 shared state, u8 0 timers, compact int "active" + u8 flags. The client plays the waypoint's look
/// from S→C 0x4c, not from "active" (its client handler plays the "inert" animation).</para>
/// </summary>
public sealed record WaypointObject(int GridX, int GridY, string? Model = null, float Rotation = 0, float? Height = null, int Active = 0)
{
    public const string MetadataPath = "Metadata/MiscellaneousObjects/Waypoint";

    public byte[] Blob()
    {
        var w = new BlobWriter();
        w.U8(0).U8(0);                                                              // object flags, base data entry count
        // Positioned as an area transition's: x, y, rotation, u16, flags0 (0x04: the record's fixed height), flags1.
        w.I32(GridX).I32(GridY).F32(Rotation).U16(0).U8((byte)(Height is null ? 0 : 0x04)).U8(0);
        if (Height is float z) w.I32((int)MathF.Round(z / 7.8125f)).F32(z);
        if (Model is null) w.U8(0); else w.U8(1).WStr32(Model);                    // Animated
        w.U8(0);                                                                    // MinimapIcon flags
        w.U8(0);                                                                    // InteractionAction flags
        w.U8(1).U8(0);                                                              // StateMachine: 1 shared state, 0 timers
        w.CompactI(Active).U8(0);                                                   // "active", flags
        w.U8(0);                                                                    // tasks
        return w.ToArray();
    }
}
