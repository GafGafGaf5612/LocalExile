using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public partial class OwnAuraGatewayTests
{

    [Fact]
    public async Task Remembered_duplicate_aura_skips_only_the_gem_with_unmet_instance_requirements()
    {
        using var f = new Fixture("Clarity", "Clarity");
        var gemType = Program.Items.Gem(Program.Items.Find("Metadata/Items/Gems/SkillGemClarity")!)!;
        f.Items.Change(PlayerInventories.Weapon1, f.Host.Id, item => item with
        {
            Sockets = [item.Sockets[0] with
                { Gem = item.Sockets[0].Gem! with { GemExperience = gemType.ExperienceFor(20) } }, item.Sockets[1]]
        });
        f.Conn.PlayerStats[Program.Stats.Key("virtual_intelligence_for_requirements")!.Value] = 100;
        int effect = f.Effect("Clarity");
        Assert.NotNull(Call("GemSkillInstanceDisabled", f.Conn, effect, f.Skill()));
        Assert.Null(Call("GemSkillInstanceDisabled", f.Conn, effect, f.Skill(1)));
        CharacterSaves.SetAuraActive(f.Conn.Character!, "Clarity", true);
        await f.Set(21148, 1);
        Assert.Empty(f.Conn.Auras);
        await f.Set(21148, 0);
        var restored = Assert.Single(f.Conn.Auras).Value;
        Assert.Equal(f.Skill(1), restored.SkillId);
        Assert.Equal(1, restored.Level);
    }

    static IEnumerable<(uint Current, uint Reserved)> ManaPackets(byte[] bytes)
    {
        byte[] header = InstanceProtocol.LifeUpdate(0x1000, 0, 0, pool: Program.ManaPool)[..12];
        for (int i = 0; i <= bytes.Length - 24; i++)
            if (bytes.AsSpan(i).StartsWith(header) && bytes[i + 20] == Program.ManaPool)
            {
                yield return (BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i + 12)),
                    BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i + 16)));
                i += 23;
            }
    }
}
