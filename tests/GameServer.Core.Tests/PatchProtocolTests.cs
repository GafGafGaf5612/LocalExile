using GameServer.Core.Protocol.Patch;

namespace GameServer.Core.Tests;

public class PatchProtocolTests
{
    // Reply captured from patch.pathofexile.com:12995 for the hello 01 07 (2026-10-16).
    private const string RealWelcome =
        "02" + "0000000000000000000000000000000000000000000000000000000000000000" +
        "0022" + "680074007400700073003a002f002f00700061007400630068002e0070006f006500630064006e002e0063006f006d002f0033002e00320039002e0033002e0033002f00" +
        "0022" + "680074007400700073003a002f002f00700061007400630068002e0070006f006500630064006e002e0063006f006d002f0033002e00320039002e0033002e0033002f00";

    [Fact]
    public void Welcome_matches_the_real_patch_server_byte_for_byte()
    {
        const string root = "https://patch.poecdn.com/3.29.3.3/";
        Assert.Equal(RealWelcome, Convert.ToHexString(PatchProtocol.EncodeWelcome(new byte[32], root, root)).ToLowerInvariant());
    }

    [Fact]
    public void Default_config_advertises_only_the_local_web_root()
    {
        var config = PatchServerConfig.From(_ => null);
        Assert.Equal("http://127.0.0.1:8080/", config.WebRoot);
        Assert.Equal("http://127.0.0.1:8080/", config.BackupWebRoot);
        Assert.False(config.CdnPassthrough);

        byte[] welcome = PatchProtocol.EncodeWelcome(new byte[32], config.WebRoot, config.BackupWebRoot);
        string text = System.Text.Encoding.Unicode.GetString(welcome, 33, welcome.Length - 33);
        Assert.DoesNotContain("poecdn", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("poecdn", System.Text.Encoding.ASCII.GetString(welcome), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cdn_passthrough_needs_an_explicit_opt_in()
    {
        var mirrorOnly = PatchServerConfig.From(k => k == "Game_CDN_MIRROR" ? "https://patch.poecdn.com" : null);
        Assert.False(mirrorOnly.CdnPassthrough);
        Assert.DoesNotContain("poecdn", mirrorOnly.WebRoot);

        Assert.True(PatchServerConfig.From(k => k == "Game_CDN_PASSTHROUGH" ? "1" : null).CdnPassthrough);
        var flagged = PatchServerConfig.From(_ => null, passthroughFlag: true);
        Assert.True(flagged.CdnPassthrough);
        Assert.Equal("http://127.0.0.1:8080/", flagged.WebRoot); // the welcome still points at us, never the CDN
    }

    [Fact]
    public void Web_root_follows_the_listen_url_with_wildcards_as_loopback()
    {
        Assert.Equal("http://127.0.0.1:9000/", PatchServerConfig.From(_ => null, "http://0.0.0.0:9000").WebRoot);
        Assert.Equal("http://127.0.0.1:9001/", PatchServerConfig.From(_ => null, "http://+:9001").WebRoot);
    }

    [Fact]
    public void Hello_needs_two_bytes_and_returns_the_version()
    {
        Assert.Null(PatchProtocol.TryReadHello([0x01]));
        Assert.Equal((byte)7, PatchProtocol.TryReadHello([0x01, 0x07]));
        Assert.Throws<InvalidDataException>(() => PatchProtocol.TryReadHello([0x02, 0x07]));
    }
}
