using GameServer.Core.Accounts;
using GameServer.Storage;

namespace GameServer.Core.Tests;

public class LastTownTests
{
    static string TempPath() => Path.Combine(Path.GetTempPath(), $"Game-{Guid.NewGuid():N}.json");

    [Fact]
    public void Last_town_is_stored_by_character_name_and_survives_a_reload()
    {
        string path = TempPath();
        try
        {
            var service = new AccountService(new JsonAccountStore(path));
            var account = service.Register("gwen", "pw1234").Value!;
            service.CreateCharacter(account.Id, "GwenWitch", CharacterClass.Witch);
            Assert.Null(service.Characters(account.Id)[0].LastTown);   // never entered a town yet

            Assert.True(service.SetLastTown("gwenwitch", "1_2_town").Ok);

            var reloaded = new AccountService(new JsonAccountStore(path));
            Assert.Equal("1_2_town", reloaded.Characters(account.Id)[0].LastTown);
            Assert.Equal(AuthError.NotFound, reloaded.SetLastTown("Nobody", "1_2_town").Error);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Play_carries_the_chosen_characters_last_town()
    {
        var accounts = new AccountService(new JsonAccountStore());
        var account = accounts.Register("hana", "pw1234").Value!;
        accounts.CreateCharacter(account.Id, "HanaWitch", CharacterClass.Witch);
        accounts.SetLastTown("HanaWitch", "1_3_town");
        var session = new GameServer.Core.Protocol.Login.LoginServerSession(accounts);
        session.Handle(new GameServer.Core.Protocol.Login.AccountLogin(2, "hana", new byte[32],
            AccountService.ClientPasswordDigest("pw1234"), new byte[32], false, false, 0x40, 0));

        session.Handle(new GameServer.Core.Protocol.Login.U8U32(0, 0));   // Play

        Assert.Equal("1_3_town", session.CharacterLastTown);
    }

    [Fact]
    public void An_accounts_file_from_before_last_town_still_loads()
    {
        // The shape data/accounts.json had before Character.LastTown existed.
        string path = TempPath();
        File.WriteAllText(path, """
            {"Accounts":[],"Characters":[{"Id":"6f7cbb4e-6b49-4c5b-9f55-1d3a3b6c0a01","AccountId":"0b1f8d7e-2c3a-4e5f-8a9b-0c1d2e3f4a5b",
             "Name":"OldWitch","Class":3,"Level":12,"League":"Standard","AreaId":"1_1_town",
             "CreatedUtc":"2026-10-16T00:00:00+00:00","LastPlayedUtc":"2026-10-16T00:00:00+00:00"}]}
            """);
        try
        {
            var c = new JsonAccountStore(path).FindCharacter("OldWitch")!;
            Assert.Equal(12, c.Level);
            Assert.Null(c.LastTown);
        }
        finally { File.Delete(path); }
    }
}
