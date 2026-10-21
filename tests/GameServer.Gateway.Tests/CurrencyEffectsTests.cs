using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class CurrencyEffectsTests
{
    static Item Boots(params SocketColour[] colours) => new(0x1234, ["Base", "Sockets"], 0x10)
    {
        Sockets = [.. colours.Select((c, i) => new ItemSocket(c, i == 0 ? new Item(0x99, ["Base"], 0x20) : null))],
        Links = [(byte)colours.Length],
    };

    [Fact]
    public void A_chromatic_orb_changes_the_colours_and_keeps_gems_and_links()
    {
        var random = new Random(1);
        for (int i = 0; i < 200; i++)
        {
            var before = Boots(SocketColour.Red, SocketColour.Red, SocketColour.Green);
            var after = CurrencyEffects.Chromatic(before, (20, 20, 0), random)!;

            Assert.NotEqual(before.Sockets.Select(s => s.Colour), after.Sockets.Select(s => s.Colour));
            Assert.All(after.Sockets, s => Assert.NotEqual(SocketColour.White, s.Colour));
            Assert.Equal(before.Sockets[0].Gem, after.Sockets[0].Gem);
            Assert.Equal(before.Links, after.Links);
        }
    }

    [Fact]
    public void The_colours_follow_the_attribute_requirements()
    {
        var random = new Random(2);
        int red = 0, total = 0;
        for (int i = 0; i < 2000; i++)
        {
            var after = CurrencyEffects.Chromatic(Boots(SocketColour.Blue), (100, 0, 0), random)!;
            total++;
            if (after.Sockets[0].Colour == SocketColour.Red) red++;
        }
        // Strength 100: red weighs 112 against green's 12 (blue, the old colour, cannot come back on one socket).
        Assert.InRange(red / (double)total, 0.85, 0.95);
    }

    [Fact]
    public void An_item_without_sockets_is_not_changed()
    {
        Assert.Null(CurrencyEffects.Chromatic(new Item(0x1, ["Base"], 0), (0, 0, 0), new Random(3)));
    }
}
