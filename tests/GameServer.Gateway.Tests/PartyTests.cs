using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// Party invite, accept and leave between two clients in one town (live 2026-10-29).
public class PartyTests
{
    [Fact]
    public void Accept_and_leave_are_framed_and_read()
    {
        // Live: Accept on the invite notice, then "Leave Party", both for party 0x101.
        byte[] accept = Convert.FromHexString("009500000101"), leave = Convert.FromHexString("009600000101");
        var both = InstanceProtocol.SplitClientMessages([.. accept, .. leave], out var partial, out var unknown);
        Assert.Empty(unknown);
        Assert.Empty(partial);
        Assert.Equal([InstanceSocial.PartyAcceptId, InstanceSocial.PartyLeaveId], both.Select(m => m.Id));
        Assert.Equal(0x101u, InstanceSocial.ReadPartyAccept(both[0].Bytes));
        Assert.Equal(0x101u, InstanceSocial.ReadPartyAccept(both[1].Bytes));
    }

    [Fact]
    public void The_invite_that_showed_the_notice_has_these_bytes()
    {
        // Live 2026-10-29: the invite DedOK (account test1) sent Admin, which showed "TEST1 invites you to a party".
        // u32 party, wstr leader character, wstr leader account, u32 party, +0x6e 0, +0x6c 0, +0x6d 1, +0x6f 0, two members.
        Assert.Equal(Convert.FromHexString("00A9" + "00000102" + "00054465644F4B" + "00057465737431" + "00000102" + "00000100" +
                "02" + "00054465644F4B00" + "000541646D696E00"),
            InstanceSocial.PartyInfo(0x102, "DedOK", "test1", 0, 0, 1, 0, [("DedOK", 0), ("Admin", 0)]));
    }

    [Fact]
    public void Removing_a_party_is_its_id_and_a_reason()
    {
        Assert.Equal(Convert.FromHexString("00AA0000010100"), InstanceSocial.PartyRemove(0x101));
    }
}
