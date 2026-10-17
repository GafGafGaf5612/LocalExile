using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class InstanceEntitiesTests
{
    [Theory]
    // Reference values from an independent MurmurHash2 implementation (seed 0).
    [InlineData("Metadata/Characters/Int/Int", 0x9c41993eu)]
    [InlineData("Metadata/Characters/Str/Str", 0xd7aa9065u)]
    [InlineData("", 0u)]
    public void Object_type_hash_is_murmur2_of_the_narrowed_path(string path, uint expected)
    {
        Assert.Equal(expected, InstanceEntities.ObjectTypeHash(path));
    }

    [Fact]
    public void Local_player_id_is_a_u32_message()
    {
        Assert.Equal("002200001000", Convert.ToHexString(InstanceEntities.SetLocalPlayer(0x1000)));
    }

    [Fact]
    public void Create_object_has_entity_header_type_and_blob16()
    {
        byte[] m = InstanceEntities.CreateObject(0x1000, 0x9c41993e, [0xaa, 0xbb]);
        Assert.Equal("02B9" + "00001000" + "00000000" + "0000" + "9C41993E" + "0002" + "AABB", Convert.ToHexString(m));
    }

    [Fact]
    public void Minimal_player_blob_layout()
    {
        byte[] b = new PlayerObject("Ab", ClassIndex: 1, GridX: 0x73, GridY: 0x45, Life: 50, Mana: 40).Blob();
        string hex = Convert.ToHexString(b);
        string pool(uint v) => Convert.ToHexString(BitConverter.GetBytes(v)) + "00000000" + "0000" + "00000000" + "00000000"; // current, reserved 0
        string expected =
            "00" + "00" +                                                     // flags, base data
            "73000000" + "45000000" + "00000000" + "0000" + "0002" + "0001" + // Positioned, flags1 0x02: not hidden, shown
            "00" + "00000000" + "00000000" + "00" +                           // comp1
            "0000" +                                                          // comp3
            pool(50) + pool(40) + pool(0) + "00" +                            // Life
            "00" +                                                            // Animated
            "02000000" + "41006200" + "01" + "00000000" + "00000000" + "0000" + "0000" + "0000" + "0000" +
            "000000000000000000" + "0000" + "0000" + "00" + "0000000000" + "00000000" + "00" + // Player
            "0000" +                                                          // Inventories
            "00000000" + "0000" + "00" + "00000000" + "00000000" + "0000" +    // comp9
            "00";                                                             // tasks
        Assert.Equal(expected, hex);
    }

    [Fact]
    public void Minimal_monster_blob_layout()
    {
        byte[] b = new MonsterObject(0x73, 0x45, Life: 100, MonsterObject.BaseComponents).Blob();
        string pool(uint v) => Convert.ToHexString(BitConverter.GetBytes(v)) + "00000000" + "0000" + "00000000" + "00000000";
        string expected =
            "00" + "00" +                                                     // flags, base data
            "73000000" + "45000000" + "00000000" + "0000" + "0002" + "0001" + // Positioned (no Sector prefix), shown
            "00" + "00000000" + "00000000" + "00" +                           // Stats
            "0000" + "00" + "00" +                                            // ObjectMagicProperties: base part, flags
            "0000" +                                                          // Buffs (BaseEvents, Pathfinding: none)
            pool(100) + pool(0) + pool(0) + "00" +                            // Life
            "00" +                                                            // Animated
            "0000" +                                                          // Inventories
            "00000000" + "0000" + "00" + "00000000" + "00000000" + "0000" +    // Actor (life >= 1: no conditional u8s)
            "00";                                                             // Monster reads nothing; tasks
        Assert.Equal(expected, Convert.ToHexString(b));
        Assert.Equal(112, b.Length);
    }

    [Fact]
    public void Monster_types_message_and_type_path()
    {
        // u16 count, u16 preload (big-endian), rows as raw little-endian u16, then one level byte each.
        Assert.Equal("0020" + "0002" + "0002" + "8704" + "0100" + "0D" + "01",
            Convert.ToHexString(InstanceEntities.MonsterTypes([(1159, 13), (1, 1)])));
        Assert.Equal("0020" + "0001" + "0000" + "8704" + "0D",
            Convert.ToHexString(InstanceEntities.MonsterTypes([(1159, 13)], preload: 0)));
        Assert.Equal("Metadata/Monsters/Zombies/ZombieBite@13", MonsterObject.TypePath("Metadata/Monsters/Zombies/ZombieBite", 13));
    }

    [Fact]
    public void Monster_blob_writes_known_extras_and_the_animated_object()
    {
        string[] withExtras = [.. MonsterObject.BaseComponents, "Transitionable", "Preload"];
        byte[] plain = new MonsterObject(1, 2, 100, MonsterObject.BaseComponents).Blob();
        Assert.Equal(plain.Length + 1, new MonsterObject(1, 2, 100, withExtras).Blob().Length);
        byte[] withAo = new MonsterObject(1, 2, 100, MonsterObject.BaseComponents, "A.ao").Blob();
        Assert.Equal(plain.Length + 4 + 2 * 4, withAo.Length);
    }

    [Fact]
    public void Monster_blob_refuses_components_it_cannot_write()
    {
        Assert.False(MonsterObject.CanSerialize([.. MonsterObject.BaseComponents, "StateMachine"]));
        Assert.False(MonsterObject.CanSerialize(["Positioned", "Animated"]));
        Assert.False(MonsterObject.CanSerialize([]));
        Assert.Throws<ArgumentException>(() => new MonsterObject(0, 0, 1, [.. MonsterObject.BaseComponents, "DiesAfterTime"]).Blob());
    }

    [Fact]
    public void Add_inventory_layout()
    {
        var flask = InstanceInventories.PlayerDefaults[11];
        Assert.Equal((11, 0x0a, 5, 2), (flask.Row, flask.Kind, flask.Width, flask.Height));
        Assert.Equal("00C0" + "00" + "0000000C" + "0A" + "0B" + "05" + "02" + "00" + "00" + "00" + "00",
            Convert.ToHexString(InstanceInventories.AddInventory(12, flask)));
    }

    [Fact]
    public void Passive_slots_create_all_three()
    {
        Assert.Equal("003D" + "0000" + "0000" + "1C" + "0000", Convert.ToHexString(InstanceEntities.PassiveSlots()));
    }

    [Fact]
    public void Area_transition_blob_layout_and_type()
    {
        // Traced live 2026-10-16: the client consumed all 41 bytes (Positioned 16, Animated 1, AreaTransition 5, Stats 10,
        // ObjectMagicProperties 4, MinimapIcon 1, InteractionAction 1, + flags, base data, tasks).
        byte[] b = new AreaTransitionObject(GridX: 384, GridY: 217, DestinationHash16: 0x7ad2).Blob();
        Assert.Equal(41, b.Length);
        Assert.Equal(
            "00" + "00" +                                                    // flags, base data
            "80010000" + "D9000000" + "00000000" + "0000" + "0000" +         // Positioned (384, 217)
            "00" +                                                           // Animated
            "00" + "02" + "D27A" + "00" +                                    // AreaTransition: state 0, variant 2 = HASH16 0x7ad2, compact int
            "00" + "00000000" + "00000000" + "00" +                          // Stats
            "0000" + "00" + "00" +                                           // ObjectMagicProperties
            "00" + "00" +                                                    // MinimapIcon, InteractionAction
            "00",                                                            // tasks
            Convert.ToHexString(b));
        Assert.Equal(InstanceEntities.ObjectTypeHash("Metadata/MiscellaneousObjects/AreaTransition"),
            InstanceEntities.ObjectTypeHash(AreaTransitionObject.MetadataPath));
    }
}
