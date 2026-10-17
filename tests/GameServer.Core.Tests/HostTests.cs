using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using GameServer.Core.Accounts;
using GameServer.Core.World;
using GameServer.Host;
using GameServer.Storage;

namespace GameServer.Core.Tests;

public class HostTests
{
    private sealed class Conn : IDisposable
    {
        private readonly TcpClient _c;
        private readonly StreamReader _r;
        private readonly StreamWriter _w;
        public Conn(int port)
        {
            _c = new TcpClient("127.0.0.1", port);
            var s = _c.GetStream();
            _r = new StreamReader(s, Encoding.UTF8);
            _w = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        }
        public JsonObject Call(string json)
        {
            _w.WriteLine(json);
            return JsonNode.Parse(_r.ReadLine()!)!.AsObject();
        }
        public void Dispose() { _c.Dispose(); }
    }

    private static GameHost NewHost()
    {
        var h = new GameHost(new AccountService(new JsonAccountStore()), new InstanceManager(), IPAddress.Loopback, 0);
        h.Start();
        return h;
    }

    [Fact]
    public async Task FullFlow_Register_Login_Character_World_Instances()
    {
        await using var host = NewHost();
        using var a = new Conn(host.Port);

        Assert.True((bool)a.Call("""{"op":"register","name":"alice","password":"pw1234"}""")["ok"]!);
        Assert.False((bool)a.Call("""{"op":"login","name":"alice","password":"nope"}""")["ok"]!);
        Assert.Equal("not_logged_in", (string)a.Call("""{"op":"characters"}""")["error"]!);
        Assert.True((bool)a.Call("""{"op":"login","name":"alice","password":"pw1234"}""")["ok"]!);

        var created = a.Call("""{"op":"create_character","name":"Exile","class":"witch"}""");
        Assert.True((bool)created["ok"]!);
        Assert.Equal("1_1_town", (string)created["character"]!["area"]!);
        Assert.Single(a.Call("""{"op":"characters"}""")["characters"]!.AsArray());

        var w = a.Call("""{"op":"enter_world","name":"Exile"}""");
        Assert.Equal("1_1_town", (string)w["instance"]!["area"]!);

        var z1 = a.Call("""{"op":"enter_area","area":"1_1_2","monster_level":2}""");
        var id1 = (uint)z1["instance"]!["id"]!;
        Assert.Equal(2, (int)z1["instance"]!["monster_level"]!);
        var z2 = a.Call("""{"op":"enter_area","area":"1_1_2"}""");
        Assert.NotEqual(id1, (uint)z2["instance"]!["id"]!);
        Assert.Equal("1_1_2", (string)a.Call("""{"op":"who"}""")["instance"]!["area"]!);

        // area persisted: a second connection resumes into the last area
        var token = (string)a.Call("""{"op":"login","name":"alice","password":"pw1234"}""")["token"]!;
        using var b = new Conn(host.Port);
        Assert.True((bool)b.Call($$"""{"op":"resume","token":"{{token}}"}""")["ok"]!);
        Assert.Equal("1_1_2", (string)b.Call("""{"op":"enter_world","name":"Exile"}""")["instance"]!["area"]!);
    }

    [Fact]
    public async Task Two_Players_Share_Town_But_Not_Zones()
    {
        await using var host = NewHost();
        using var a = new Conn(host.Port);
        using var b = new Conn(host.Port);
        foreach (var (c, n) in new[] { (a, "playerA"), (b, "playerB") })
        {
            c.Call($$"""{"op":"register","name":"{{n}}","password":"pw1234"}""");
            c.Call($$"""{"op":"login","name":"{{n}}","password":"pw1234"}""");
            c.Call($$"""{"op":"create_character","name":"{{n}}c","class":"ranger"}""");
        }
        var ta = a.Call("""{"op":"enter_world","name":"playerAc"}""")["instance"]!;
        var tb = b.Call("""{"op":"enter_world","name":"playerBc"}""")["instance"]!;
        Assert.Equal((uint)ta["id"]!, (uint)tb["id"]!);
        Assert.Equal(2, (int)tb["players"]!);
        var za = a.Call("""{"op":"enter_area","area":"1_1_2"}""")["instance"]!;
        var zb = b.Call("""{"op":"enter_area","area":"1_1_2"}""")["instance"]!;
        Assert.NotEqual((uint)za["id"]!, (uint)zb["id"]!);
    }

    [Fact]
    public async Task Bad_Input_Is_Rejected_Not_Fatal()
    {
        await using var host = NewHost();
        using var a = new Conn(host.Port);
        Assert.Equal("bad_request", (string)a.Call("not json")["error"]!);
        Assert.Equal("unknown_op", (string)a.Call("""{"op":"explode"}""")["error"]!);
        Assert.Equal("not_in_world", (string)a.Call("""{"op":"enter_area","area":"x"}""")["error"]!);
        Assert.True((bool)a.Call("""{"op":"register","name":"ok_user","password":"pw1234"}""")["ok"]!);
    }
}
