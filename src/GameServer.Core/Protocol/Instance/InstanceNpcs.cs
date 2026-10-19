using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>NPC messages (Agents/docs/protocol/17-npcs.md).</summary>
public static class InstanceNpcs
{
    public const ushort TalkId = 0x84;

    /// <summary>C→S 0x85 (id only): the dialogue window was closed (Goodbye, Esc or walking away). Live 2026-10-19: sent
    /// after every 0x84, alone in its chunk.</summary>
    public const ushort DialogueClosedId = 0x85;

    /// <summary>S→C 0x84 "NPC dialogue" byte that opens the NPC's dialogue window (0x142149db0: the +0x13f8 listeners →
    /// 0x140dcc720 → NPCDialogueWindow).</summary>
    public const byte OpenDialogue = 0xff;

    /// <summary>
    /// S→C 0x84 (world group, class 0x1435dd7d8, read 0x141fddcb0; world handler 0x142187480 → 0x142149db0): object header
    /// (u16 id, u32 BE object id → +0x10, u32, u16), u8 → +0x20. Object id 0 closes the dialogue. With 0xff the client opens
    /// the NPC's dialogue window; other values take another path (not mapped).
    /// </summary>
    public static byte[] Talk(uint npcObjectId, byte mode = OpenDialogue) =>
        new LoginWriter().U16(TalkId).U32(npcObjectId).U32(0).U16(0).U8(mode).ToArray();
}

/// <summary>
/// An NPC (Agents/docs/protocol/17-npcs.md): a 0x2b9 object of an NPCs.dat type such as <c>Metadata/NPC/Act1/Tarkleigh</c>.
/// <c>Metadata/NPC/NPC</c> deserializes Positioned, Animated, BaseEvents, InteractionAction, NPC and MinimapIcon (client
/// Render + Targetable); 190 NPC types have exactly that list, others add Transitionable and StateMachine.
/// <para>NPC (slot 17 0x141e75050) reads two u8 (→ +0x20, +0x21). BaseEvents reads nothing.</para>
/// </summary>
public sealed record NpcObject(int GridX, int GridY, IReadOnlyList<string> Components, float Rotation = 0,
    StateMachineData? StateMachine = null, byte TransitionState = 1)
{
    /// <summary>Deserialized components of <c>Metadata/NPC/NPC</c> in read order (scripts/re/ot_components.py).</summary>
    public static readonly string[] BaseComponents = ["Positioned", "Animated", "BaseEvents", "InteractionAction", "NPC", "MinimapIcon"];

    static readonly HashSet<string> Supported = [.. BaseComponents, "Transitionable", "StateMachine"];

    /// <summary>Whether <see cref="Blob"/> can serialize a type with these deserialized components.</summary>
    public static bool CanSerialize(IReadOnlyList<string> components, bool haveStateMachine = false) =>
        components.Contains("NPC") && components.All(c => Supported.Contains(c) && (c != "StateMachine" || haveStateMachine));

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
                // As a monster's: x, y, rotation (+0x29c), u16 team (0: friendly), flags0, flags1 0x02 + shown.
                case "Positioned": w.I32(GridX).I32(GridY).F32(Rotation).U16(0).U8(0).U8(0x02).U8(0).U8(1); break;
                case "Animated": w.U8(0); break;              // the type supplies the model
                case "InteractionAction": w.U8(0); break;
                case "NPC": w.U8(0).U8(0); break;             // +0x20, +0x21
                case "MinimapIcon": w.U8(0); break;
                case "Transitionable": w.U8((byte)(TransitionState & 0x7f)); break;
                case "StateMachine":
                    var sm = StateMachine!;
                    w.U8((byte)sm.SharedStates.Count).U8((byte)sm.SharedTimers);
                    foreach (int v in sm.SharedStates)
                    {
                        var lw = new LoginWriter();
                        InstanceProtocol.CompactInt(lw, v);
                        foreach (byte b in lw.ToArray()) w.U8(b);
                        w.U8(0);
                    }
                    for (int i = 0; i < sm.SharedTimers; i++) w.U32(0).U8(0);
                    break;
                // BaseEvents: no data.
            }
        }
        w.U8(0);            // tasks
        return w.ToArray();
    }
}
