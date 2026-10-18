using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class SkillMessagesTests
{
    [Fact]
    public void Bind_skill_is_slot_then_big_endian_skill_id()
    {
        // Live 2026-10-18: Fireball (0x0103) put on the right mouse button.
        byte[] m = Convert.FromHexString("004E020103");
        var split = InstanceProtocol.SplitClientMessages(m, out var partial, out var unknown);
        Assert.Single(split);
        Assert.Empty(partial);
        Assert.Empty(unknown);
        Assert.Equal(((byte)2, (ushort)0x0103), InstanceProtocol.ReadBindSkill(split[0].Bytes));
    }

    [Fact]
    public void Skill_bar_is_13_little_endian_ids_then_two_u16()
    {
        // Live 2026-10-18: the default attack on the left mouse button, Fireball on the right one came back.
        var ids = new ushort[13];
        ids[0] = 0x0102;
        ids[2] = 0x0103;
        string hex = Convert.ToHexString(InstanceProtocol.SkillBar(ids));
        Assert.Equal("0053" + "0201" + "0000" + "0301" + string.Concat(Enumerable.Repeat("0000", 10)) + "0000" + "0000", hex);
        Assert.Equal(2 + 26 + 4, hex.Length / 2);
    }

    [Fact]
    public void Projectile_blob_is_positioned_animated_and_tasks()
    {
        byte[] b = new ProjectileObject("Metadata/Projectiles/Fireball", 0x51e, 0x44d, Rotation: 0).Blob();
        Assert.Equal("0000" + "1E050000" + "4D040000" + "00000000" + "0000" + "00" + "02" + "00" + "01" + "00" + "00",
            Convert.ToHexString(b));
    }

    [Fact]
    public void Projectile_direction_block_sets_flag_0x04_before_visibility()
    {
        byte[] b = new ProjectileObject("Metadata/Projectiles/Fireball", 1, 2, Direction: (1f, 0f, 0f, 0f)).Blob();
        Assert.Equal("0000" + "01000000" + "02000000" + "00000000" + "0000" + "00" + "06" +
            "0000803F" + "00000000" + "00000000" + "00000000" + "00" + "01" + "00" + "00", Convert.ToHexString(b));
    }

    [Fact]
    public void Projectile_fixed_height_is_negative_z()
    {
        // Height 8 (up) is z = −8 × 7.8125 = −62.5: i32 −8, f32 −62.5.
        byte[] b = new ProjectileObject("Metadata/Projectiles/Fireball", 1, 2, Height: 8).Blob();
        Assert.Equal("0000" + "01000000" + "02000000" + "00000000" + "0000" + "04" + "02" +
            "F8FFFFFF" + "00007AC2" + "00" + "01" + "00" + "00", Convert.ToHexString(b));
    }

    [Fact]
    public void Animated_model_update_targets_the_component_with_a_path()
    {
        string hex = Convert.ToHexString(InstanceEntities.AnimatedModel(0x3001, ProjectileObject.AnimatedComponentIndex, "ab"));
        Assert.StartsWith("02BA" + "00003001", hex);
        Assert.EndsWith("02" + "01" + "02000000" + "61006200", hex);
    }
}
