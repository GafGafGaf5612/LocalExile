using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Login;
using GameServer.Storage;

namespace GameServer.Core.Tests;

public class LoginServerSessionTests
{
    private static AccountLogin Request(string name, string password) =>
        new(2, name, new byte[32], AccountService.ClientPasswordDigest(password), new byte[32], false, false, 0x40, 0);

    // Through the real codec both ways, as GameServer.Auth does it (minus encryption).
    private static LoginResult RoundTrip(LoginServerSession session, AccountLogin request)
    {
        var up = new LoginMessageStream(LoginDirection.ClientToServer).Push(LoginMessage.Encode(request));
        var replies = session.Handle(Assert.Single(up));
        var down = new LoginMessageStream(LoginDirection.ServerToClient).Push([.. replies.SelectMany(LoginMessage.Encode)]);
        Assert.Equal(replies.Count, down.Count);
        if (down.Count > 1) Assert.IsType<CharacterList>(down[1]);
        return Assert.IsType<LoginResult>(down[0]);
    }

    [Fact]
    public void Client_digest_matches_sha256_of_utf16le()
    {
        byte[] utf16le = [(byte)'p', 0, (byte)'w', 0, 0xe9, 0];
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(utf16le), AccountService.ClientPasswordDigest("pwé"));
    }

    [Fact]
    public void Correct_password_logs_in_and_wrong_one_is_rejected()
    {
        var accounts = new AccountService(new JsonAccountStore());
        Assert.True(accounts.Register("alice", "pw1234").Ok);

        var good = new LoginServerSession(accounts);
        var ok = RoundTrip(good, Request("ALICE", "pw1234"));
        Assert.Equal(LoginServerSession.ResultOk, ok.Result);
        Assert.Equal("", ok.AccountName); // shown to the player as a message box
        Assert.Equal("alice", good.Account!.Name);
        Assert.NotNull(good.Session);

        var bad = new LoginServerSession(accounts);
        Assert.Equal(LoginServerSession.ResultInvalidPassword, RoundTrip(bad, Request("alice", "nope")).Result);
        Assert.True(bad.Failed);
        Assert.Null(bad.Session);
        Assert.Equal(LoginServerSession.ResultAccountDoesNotExist, RoundTrip(new LoginServerSession(accounts), Request("nobody", "pw1234")).Result);
    }

    [Fact]
    public void Account_without_digest_verifier_gets_one_on_next_dev_login()
    {
        var store = new JsonAccountStore();
        var accounts = new AccountService(store);
        var acc = accounts.Register("bob", "secret").Value!;
        store.UpdateAccount(acc with { ClientDigestHash = null }); // as stored before the verifier existed

        Assert.False(accounts.LoginWithClientDigest("bob", AccountService.ClientPasswordDigest("secret")).Ok);
        Assert.True(accounts.Login("bob", "secret").Ok);
        Assert.True(accounts.LoginWithClientDigest("bob", AccountService.ClientPasswordDigest("secret")).Ok);
    }

    // The flow a real 3.29.3b client went through on 2026-10-16: 0x03 → (0x04, 0x1b), 0x18 → 0x15, 0x0e → 0x14.
    [Fact]
    public void Live_verified_flow_lists_characters_and_assigns_an_instance()
    {
        var accounts = new AccountService(new JsonAccountStore());
        var acc = accounts.Register("carol", "pw1234").Value!;
        accounts.CreateCharacter(acc.Id, "CarolWitch", CharacterClass.Witch);
        var session = new LoginServerSession(accounts);

        var login = session.Handle(Request("carol", "pw1234"));
        var result = Assert.IsType<LoginResult>(login[0]);
        Assert.Equal(LoginServerSession.FlagVerified, result.Flags0);
        Assert.IsType<CharacterList>(login[1]);

        var list = Assert.IsType<List15>(Assert.Single(session.Handle(new EmptyMessage(0x18))));
        var e = Assert.Single(list.Entries);
        Assert.Equal(("CarolWitch", "Standard", (byte)1, (byte)CharacterClass.Witch), (e.Name, e.Text, e.B48, e.B4a));

        var info = Assert.IsType<InstanceInfo>(Assert.Single(session.Handle(new U8U32(0, 0))));
        Assert.Equal(("127.0.0.1", (ushort)6112, (ushort)0x235c), (info.Host, info.U16_30, info.AreaHash));

        // Every reply survives the S→C codec.
        var all = login.Concat([list, info]).ToList();
        var decoded = new LoginMessageStream(LoginDirection.ServerToClient).Push([.. all.SelectMany(LoginMessage.Encode)]);
        Assert.Equal(all.Count, decoded.Count);
    }

    [Fact]
    public void Nothing_but_login_is_answered_before_logging_in()
    {
        var session = new LoginServerSession(new AccountService(new JsonAccountStore()));
        Assert.Empty(session.Handle(new EmptyMessage(0x18)).OfType<List15>().SelectMany(l => l.Entries));
        Assert.Empty(session.Handle(new U8U32(0, 0)));
    }
}
