namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// A hideout doodad the server places from the hideout's decoration (Agents/docs/protocol/21-hideouts.md §2), e.g. the
/// map device (<c>Metadata/Terrain/Missions/Hideouts/Objects/StrDexIntMappingDevice</c>: Positioned, BaseEvents,
/// Animated, InteractionAction, HideoutDoodad, StateMachine, MinimapIcon). Components are written in the type's order
/// (<paramref name="Components"/>: the deserialized ones, data/object-types.json); a type with any other component is
/// refused (<see cref="CanSerialize"/>). StateMachine: one value per shared state (<paramref name="States"/>, in the
/// type's order), no timers.
/// </summary>
public sealed record HideoutObject(int GridX, int GridY, float Rotation, IReadOnlyList<string> Components, HideoutDoodad Doodad,
    IReadOnlyList<int>? States = null, string? Model = null)
{
    static readonly HashSet<string> Supported = ["Positioned", "BaseEvents", "Animated", "InteractionAction", "HideoutDoodad", "StateMachine", "MinimapIcon"];

    public static bool CanSerialize(IReadOnlyList<string> components) => components.All(Supported.Contains);

    public byte[] Blob()
    {
        if (!CanSerialize(Components)) throw new ArgumentException($"unsupported components: {string.Join(", ", Components)}");
        var w = new BlobWriter();
        w.U8(0).U8(0);                                                              // object flags, base data entry count
        foreach (string c in Components)
            switch (c)
            {
                // As the stash's: x, y, rotation, u16 team 0, flags0, flags1 0x02 + two u8 (shown).
                case "Positioned": w.I32(GridX).I32(GridY).F32(Rotation).U16(0).U8(0).U8(0x02).U8(0).U8(1); break;
                case "Animated": if (Model is null) w.U8(0); else w.U8(1).WStr32(Model); break;
                case "InteractionAction": w.U8(0); break;
                case "HideoutDoodad": Doodad.Write(w); break;
                case "StateMachine":
                    w.U8((byte)(States?.Count ?? 0)).U8(0);
                    foreach (int v in States ?? []) w.CompactI(v).U8(0);   // value, flags
                    break;
                case "MinimapIcon": w.U8(0); break;
                // BaseEvents is server-only: nothing on the wire.
            }
        w.U8(0);                                                                    // tasks
        return w.ToArray();
    }
}
