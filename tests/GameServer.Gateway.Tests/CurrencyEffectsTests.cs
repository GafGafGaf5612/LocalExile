using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class CurrencyEffectsTests
{
    static ItemCatalog.BaseType SocketBase(string itemClass, byte width = 2, byte height = 3) =>
        new(0, "test", "test", 0x1234, width, height, 0x10, ["Base", "Sockets"], 0, Class: itemClass);

    [Theory]
    [InlineData("Body Armour", 1, 2)]
    [InlineData("Body Armour", 2, 3)]
    [InlineData("Body Armour", 24, 3)]
    [InlineData("Body Armour", 25, 4)]
    [InlineData("Body Armour", 49, 4)]
    [InlineData("Body Armour", 50, 6)]
    [InlineData("Bow", 100, 6)]
    [InlineData("Warstaff", 100, 6)]
    [InlineData("Helmet", 100, 4)]
    [InlineData("Gloves", 100, 4)]
    [InlineData("Boots", 100, 4)]
    [InlineData("Wand", 100, 3)]
    [InlineData("Shield", 100, 3)]
    [InlineData("Ring", 100, 0)]
    [InlineData("Belt", 100, 0)]
    [InlineData("Quiver", 100, 0)]
    public void Jewellers_respects_class_and_item_level(string itemClass, byte level, int expected)
    {
        Assert.Equal(expected, CurrencyEffects.MaximumSockets(Boots() with { ItemLevel = level }, SocketBase(itemClass)));
    }

    [Fact]
    public void Jewellers_refuses_gems_maximum_sockets_and_unsupported_items()
    {
        var random = new Random(4);
        Assert.Null(CurrencyEffects.Jewellers(Boots(SocketColour.Red) with { ItemLevel = 100 }, SocketBase("Boots"), (0, 0, 0), random));
        var full = Boots() with { ItemLevel = 100, Sockets = Enumerable.Repeat(new ItemSocket(SocketColour.Red), 4).ToArray() };
        Assert.Null(CurrencyEffects.Jewellers(full, SocketBase("Boots"), (0, 0, 0), random));
        Assert.Null(CurrencyEffects.Jewellers(Boots(), SocketBase("Ring"), (0, 0, 0), random));
        Assert.Null(CurrencyEffects.Jewellers(new Item(1, ["Base"], 0), SocketBase("Boots"), (0, 0, 0), random));
        Assert.Equal(2, CurrencyEffects.MaximumSockets(full, SocketBase("Wand", 1, 2)));
    }

    [Fact]
    public void Jewellers_rolls_valid_colours_and_link_groups_without_changing_other_properties()
    {
        var random = new Random(5);
        var counts = new HashSet<int>();
        var linkPatterns = new HashSet<string>();
        var colours = new HashSet<SocketColour>();
        var before = Boots() with { ItemLevel = 100, Quality = 20, Rarity = ItemRarity.Rare };
        for (int i = 0; i < 10000; i++)
        {
            var after = CurrencyEffects.Jewellers(before, SocketBase("Body Armour"), (20, 20, 20), random)!;
            counts.Add(after.Sockets.Count);
            linkPatterns.Add(string.Join(",", after.Links!));
            Assert.Equal(after.Sockets.Count, after.Links!.Sum(n => n));
            Assert.All(after.Links!, n => Assert.InRange(n, (byte)1, (byte)6));
            Assert.All(after.Sockets, s => { Assert.Null(s.Gem); colours.Add(s.Colour); });
            Assert.Equal(before, after with { Sockets = before.Sockets, Links = before.Links });
        }
        Assert.Equal(new[] { 1, 2, 3, 4, 6 }, counts.OrderBy(n => n));
        Assert.Equal(3, colours.Count);
        Assert.DoesNotContain(SocketColour.White, colours);
        Assert.Contains("4", linkPatterns);
        Assert.Contains("1,1,1,1", linkPatterns);
    }

    [Fact]
    public void Low_level_boots_at_the_level_cap_can_be_reforged_repeatedly()
    {
        // Live failure: BootsStr1 and GlovesStr1, item level 1, two empty sockets.
        foreach (var itemClass in new[] { "Boots", "Gloves" })
        {
            var item = Boots() with { ItemLevel = 1, Sockets = [new(SocketColour.Red), new(SocketColour.Blue)] };
            var type = SocketBase(itemClass, 2, 2);
            var random = new Random(7);
            for (int i = 0; i < 20; i++)
            {
                var result = CurrencyEffects.Jewellers(item, type, (6, 0, 0), random);
                Assert.NotNull(result);
                Assert.Equal(item.Sockets.Count == 2 ? 1 : 2, result.Sockets.Count);
                item = result;
            }
        }
    }

    [Fact]
    public void Jewellers_changes_existing_socket_count()
    {
        var before = Boots() with { ItemLevel = 100, Sockets = [new(SocketColour.Red), new(SocketColour.Blue)] };
        for (int i = 0; i < 100; i++)
            Assert.NotEqual(2, CurrencyEffects.Jewellers(before, SocketBase("Boots"), (0, 0, 0), new Random(i))!.Sockets.Count);
    }

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
