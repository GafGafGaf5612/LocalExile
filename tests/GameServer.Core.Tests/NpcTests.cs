using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class NpcTests
{
    [Fact]
    public void Npc_blob_writes_the_base_npc_components()
    {
        // Positioned as a monster's (team 0), Animated 00, InteractionAction 00, NPC 2 × u8 (0x141e75050), MinimapIcon 00, tasks.
        byte[] blob = new NpcObject(236, 190, NpcObject.BaseComponents).Blob();
        Assert.Equal("0000" + "EC000000" + "BE000000" + "00000000" + "0000" + "00020001" + "00" + "00" + "0000" + "00" + "00",
            Convert.ToHexString(blob));
    }

    [Fact]
    public void Npc_blob_adds_transitionable_and_state_machine()
    {
        string[] components = [.. NpcObject.BaseComponents, "Transitionable", "StateMachine"];
        byte[] blob = new NpcObject(1, 2, components, StateMachine: new([1], 1), TransitionState: 3).Blob();
        Assert.EndsWith("00" + "03" + "0101" + "0100" + "0000000000" + "00", Convert.ToHexString(blob));
    }

    [Fact]
    public void Talk_is_the_world_header_and_a_mode_byte()
    {
        // S→C 0x84 (read 0x141fddcb0): u16 id, u32 BE object id, u32, u16, u8 (0xff opens the dialogue window).
        Assert.Equal("0084" + "00003001" + "00000000" + "0000" + "FF", Convert.ToHexString(InstanceNpcs.Talk(0x3001)));
    }

    [Fact]
    public void Npc_types_need_the_npc_component_and_known_ones_only()
    {
        Assert.True(NpcObject.CanSerialize(NpcObject.BaseComponents));
        Assert.False(NpcObject.CanSerialize([.. NpcObject.BaseComponents, "StateMachine"]));
        Assert.False(NpcObject.CanSerialize([.. NpcObject.BaseComponents, "ControlZone"]));
        Assert.False(NpcObject.CanSerialize(["Positioned", "Animated"]));
    }
}
