using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>NPC messages (Agents/docs/protocol/17-npcs.md).</summary>
public static class InstanceNpcs
{
    public const ushort TalkId = 0x84;

    /// <summary>C→S 0x85 (id only): the dialogue window was closed (Goodbye, Esc or walking away). Live 2026-10-19: sent
    /// after every 0x84, alone in its chunk.</summary>
    public const ushort DialogueClosedId = 0x85;

    /// <summary>C→S 0x82 (3 bytes, class 0x14345ef10 "u8 message", sent by NPCDialogueWindow 0x140722960): u8 = the chosen
    /// NPCTalk row's index among the NPC's rows (column 1). The client handles NPCTalkCategory 5 (Topic), 0x19 and 0x26
    /// itself and sends 0x82 for the others.</summary>
    public const ushort OptionId = 0x82;
    public const int OptionLength = 3;

    public static byte ReadOption(ReadOnlySpan<byte> m) => m[2];

    public const ushort TalkTextId = 0x87;

    /// <summary>
    /// S→C 0x87 (instance group, class 0x1435dc220, read 0x1421a1c00, dispatcher case 0x1e): u32 BE (+0x10), u16 BE whose low
    /// 15 bits are a value (+0x14) and bit 15 a flag (+0x16). The dispatcher sets the dialogue state (+0xd7b8 = 2) and calls
    /// the listeners at instance +0x14f0; the in-game UI's (0x140dccbf0) closes the NPC dialogue windows, opens the inventory
    /// (UI +0x628) and the quest reward window (UI +0x7a8, 0x140c2b690), which reads the value as a QuestRewardOffers row.
    /// Live 2026-10-19 the value 9 (an NPCTextAudio row) crashed it (0x140c2bc20). Not used yet.
    /// </summary>
    public static byte[] TalkText(uint npcObjectId, ushort value, bool flag = false) =>
        new LoginWriter().U16(TalkTextId).U32(npcObjectId).U16((ushort)((value & 0x7fff) | (flag ? 0x8000 : 0))).ToArray();

    public const ushort DialogueId = 0x8c;

    /// <summary>A choice in an S→C 0x8c dialogue: compact int id (+0x24), wstr text, u8 enabled (+0x20).</summary>
    public sealed record Choice(int Id, string Text, bool Enabled = true);

    /// <summary>
    /// S→C 0x8c (instance group, class 0x1435dc720, read 0x1421a1f60, dispatcher case 0x1f): u32 BE NPC object id (+0x28),
    /// u16 BE (+0x2c, passed to the listeners at instance +0x1570), u8 flag (+0x2e), u16 BE n + n × u32 BE (+0x10, being tested
    /// as NPCTextAudio rows), u16 BE m + m × (compact int, wstr, u8) choices (+0x30). The dispatcher sets the dialogue state
    /// (+0xd7b8 = 2) and calls the listeners at +0x15f0 with both lists and the NPC (0x142171be0); the in-game UI's
    /// (0x140dcccf0) closes the dialogue windows, opens the inventory and a dialogue panel (UI +0x7b0, or +0x7b8 for the new
    /// style) with them. Live 2026-10-19 (lines [9], no choices) the client stopped. Not used.
    /// </summary>
    public static byte[] Dialogue(uint npcObjectId, ushort value, bool flag, IReadOnlyList<uint> lines, IReadOnlyList<Choice> choices)
    {
        var w = new LoginWriter().U16(DialogueId).U32(npcObjectId).U16(value).U8((byte)(flag ? 1 : 0));
        w.U16((ushort)lines.Count);
        foreach (uint l in lines) w.U32(l);
        w.U16((ushort)choices.Count);
        foreach (var c in choices)
        {
            InstanceProtocol.CompactUInt(w, (uint)c.Id);
            w.WStr(c.Text).U8((byte)(c.Enabled ? 1 : 0));
        }
        return w.ToArray();
    }

    /// <summary>S→C 0x84 "NPC dialogue" byte that opens the NPC's dialogue window (0x142149db0: the +0x13f8 listeners →
    /// 0x140dcc720 → NPCDialogueWindow).</summary>
    public const byte OpenDialogue = 0xff;

    /// <summary>
    /// S→C 0x84 (world group, class 0x1435dd7d8, read 0x141fddcb0; world handler 0x142187480 → 0x142149db0): object header
    /// (u16 id, u32 BE object id → +0x10, u32, u16), u8 → +0x20. Object id 0 closes the dialogue. With 0xff the client opens
    /// the NPC's dialogue window on its option list; with an NPCTalk index (column 1) it looks the row up for the NPC
    /// (0x14225e4b0) and opens the window on that row's talk (dialogue state 2); an unknown row makes it answer 0x85.
    /// </summary>
    public static byte[] Talk(uint npcObjectId, byte mode = OpenDialogue) =>
        new LoginWriter().U16(TalkId).U32(npcObjectId).U32(0).U16(0).U8(mode).ToArray();
}

/// <summary>
/// An NPC (Agents/docs/protocol/17-npcs.md): a 0x2b9 object of an NPCs.dat type such as <c>Metadata/NPC/Act1/Tarkleigh</c>.
/// <c>Metadata/NPC/NPC</c> deserializes Positioned, Animated, BaseEvents, InteractionAction, NPC and MinimapIcon (client
/// Render + Targetable); 190 NPC types have exactly that list, others add Transitionable and StateMachine.
/// <para>NPC (slot 17 0x141e75050) reads two u8 (→ +0x20 has dialogue, +0x21 a component index or 0xff). BaseEvents reads nothing.</para>
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
                // +0x20: has dialogue (0x141e76d40 offers NPCTalk rows only when set); +0x21: index of a component that
                // must be alive (its +0x1a8 > 0) for the NPC to talk, 0xff = none.
                case "NPC": w.U8(1).U8(0xff); break;
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
