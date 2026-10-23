using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class SkillRecordTests
{
    [Fact]
    public void An_additional_stat_set_adds_its_reference_and_its_stat_list_to_the_record()
    {
        var plain = new GrantedSkill(0x0107, 10, 20, [(5u, 1)]);
        var withExtra = plain with { ExtraStatSets = [(30u, [(6u, 2)])] };
        byte[] a = InstanceEntities.PlayerSkillsChange(0x1000, [plain], []);
        byte[] b = InstanceEntities.PlayerSkillsChange(0x1000, [withExtra], []);
        // one more stat-set reference (u8 0, u32 row) and one more stat list (compact count 1, key 6, value 2)
        Assert.Equal(a.Length + 5 + 3, b.Length);
    }
}
