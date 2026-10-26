using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class CurrencyEffectsTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Fusing_changes_only_links_and_keeps_socketed_gems(int count)
    {
        var before = Boots(Enumerable.Repeat(SocketColour.White, count).ToArray()) with
        {
            ItemLevel = 1, Quality = 20, Rarity = ItemRarity.Rare,
            Links = Enumerable.Repeat((byte)1, count).ToArray(),
        };
        var random = new Random(9);
        for (int i = 0; i < 100; i++)
        {
            var after = CurrencyEffects.Fusing(before, SocketBase("Body Armour", 2, 4), random)!;
            Assert.NotNull(after);
            Assert.False(before.Links!.SequenceEqual(after.Links!));
            Assert.Same(before.Sockets, after.Sockets);
            Assert.NotNull(after.Sockets[0].Gem);
            Assert.Equal(count, after.Links!.Sum(n => n));
            Assert.All(after.Links!, n => Assert.InRange(n, (byte)1, (byte)count));
            Assert.Equal(before, after with { Links = before.Links });
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Small_fully_linked_items_can_be_unlinked_even_at_level_one(int count)
    {
        var before = Boots(Enumerable.Repeat(SocketColour.Red, count).ToArray());
        var after = CurrencyEffects.Fusing(before, SocketBase("Boots", 2, 2), new Random(10));
        Assert.NotNull(after);
        Assert.True(after.Links!.Count > 1);
    }

    [Fact]
    public void Fusing_refuses_invalid_targets_and_protects_completed_large_links()
    {
        var type = SocketBase("Body Armour", 2, 4);
        var random = new Random(11);
        Assert.Null(CurrencyEffects.Fusing(Boots(), type, random));
        Assert.Null(CurrencyEffects.Fusing(Boots(SocketColour.Red), type, random));
        Assert.Null(CurrencyEffects.Fusing(Boots(SocketColour.Red, SocketColour.Red), SocketBase("Ring"), random));
        Assert.Null(CurrencyEffects.Fusing(Boots(SocketColour.Red, SocketColour.Red) with { Components = ["Base"] }, type, random));
        foreach (int count in new[] { 5, 6 })
            Assert.Null(CurrencyEffects.Fusing(Boots(Enumerable.Repeat(SocketColour.Red, count).ToArray()), type, random));
        Assert.Null(CurrencyEffects.Fusing(Boots(Enumerable.Repeat(SocketColour.Red, 6).ToArray()) with { Links = null }, SocketBase("Boots"), random));
    }

    [Fact]
    public void Fusing_can_produce_large_links_and_quality_favours_them_without_being_consumed()
    {
        var before = Boots(Enumerable.Repeat(SocketColour.Blue, 6).ToArray()) with { Links = null };
        int CountLarge(byte quality)
        {
            int large = 0;
            var sizes = new HashSet<byte>();
            var random = new Random(12);
            for (int i = 0; i < 20000; i++)
            {
                var after = CurrencyEffects.Fusing(before with { Quality = quality }, SocketBase("Body Armour"), random)!;
                byte largest = after.Links!.Max();
                sizes.Add(largest);
                if (largest >= 4) large++;
                Assert.Equal(quality, after.Quality);
            }
            Assert.Contains((byte)5, sizes);
            Assert.Contains((byte)6, sizes);
            return large;
        }
        Assert.True(CountLarge(30) > CountLarge(0));
    }

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
        Assert.Null(CurrencyEffects.Jewellers(Boots(SocketColour.Red) with { ItemLevel = 100 }, SocketBase("Boots"), random));
        var full = Boots() with { ItemLevel = 100, Sockets = Enumerable.Repeat(new ItemSocket(SocketColour.Red), 4).ToArray() };
        Assert.Null(CurrencyEffects.Jewellers(full, SocketBase("Boots"), random));
        Assert.Null(CurrencyEffects.Jewellers(Boots(), SocketBase("Ring"), random));
        Assert.Null(CurrencyEffects.Jewellers(new Item(1, ["Base"], 0), SocketBase("Boots"), random));
        Assert.Equal(2, CurrencyEffects.MaximumSockets(full, SocketBase("Wand", 1, 2)));
    }

    [Fact]
    public void Jewellers_adds_white_sockets_and_keeps_retained_colours_and_link_groups()
    {
        var random = new Random(5);
        var counts = new HashSet<int>();
        var linkPatterns = new HashSet<string>();
        var colours = new HashSet<SocketColour>();
        var before = Boots() with { ItemLevel = 100, Quality = 20, Rarity = ItemRarity.Rare };
        for (int i = 0; i < 10000; i++)
        {
            var after = CurrencyEffects.Jewellers(before, SocketBase("Body Armour"), random)!;
            counts.Add(after.Sockets.Count);
            linkPatterns.Add(string.Join(",", after.Links!));
            Assert.Equal(after.Sockets.Count, after.Links!.Sum(n => n));
            Assert.All(after.Links!, n => Assert.InRange(n, (byte)1, (byte)6));
            Assert.All(after.Sockets, s => { Assert.Null(s.Gem); colours.Add(s.Colour); });
            Assert.Equal(before, after with { Sockets = before.Sockets, Links = before.Links });
        }
        Assert.Equal(new[] { 1, 2, 3, 4, 6 }, counts.OrderBy(n => n));
        Assert.Equal([SocketColour.White], colours);
        Assert.Contains("4", linkPatterns);
        Assert.Contains("1,1,1,1", linkPatterns);

        // Sockets that stay keep their colours; only the added ones are white.
        var kept = Boots(SocketColour.Red, SocketColour.Blue, SocketColour.Green) with
        {
            ItemLevel = 100, Sockets = [new(SocketColour.Red), new(SocketColour.Blue), new(SocketColour.Green)],
        };
        for (int i = 0; i < 200; i++)
        {
            var after = CurrencyEffects.Jewellers(kept, SocketBase("Body Armour"), new Random(i))!;
            for (int at = 0; at < Math.Min(after.Sockets.Count, kept.Sockets.Count); at++)
                Assert.Equal(kept.Sockets[at].Colour, after.Sockets[at].Colour);
            Assert.All(after.Sockets.Skip(kept.Sockets.Count), s => Assert.Equal(SocketColour.White, s.Colour));
        }
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
                var result = CurrencyEffects.Jewellers(item, type, random);
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
            Assert.NotEqual(2, CurrencyEffects.Jewellers(before, SocketBase("Boots"), new Random(i))!.Sockets.Count);
    }

    static Item Boots(params SocketColour[] colours) => new(0x1234, ["Base", "Sockets"], 0x10)
    {
        Sockets = [.. colours.Select((c, i) => new ItemSocket(c, i == 0 ? new Item(0x99, ["Base"], 0x20) : null))],
        Links = [(byte)colours.Length],
    };

    [Fact]
    public void A_chromatic_orb_forces_one_colour_and_keeps_gems_and_links()
    {
        var random = new Random(1);
        int whites = 0;
        for (int i = 0; i < 200; i++)
        {
            var before = Boots(SocketColour.Red, SocketColour.Red, SocketColour.Green);
            var after = CurrencyEffects.Chromatic(before, (20, 20, 0), random)!;

            // 3.29: one socket is forced non-white; the rest roll normally (white unless the item's chance colours them).
            Assert.Equal(3, after.Sockets.Count);
            Assert.Contains(after.Sockets, s => SocketColours.IsColoured(s.Colour));
            whites += after.Sockets.Count(s => s.Colour == SocketColour.White);
            Assert.Equal(before.Sockets[0].Gem, after.Sockets[0].Gem);
            Assert.Equal(before.Links, after.Links);
        }
        // Level 0: the two sockets that are not forced are white about 95% of the time each.
        Assert.True(whites > 250, $"{whites} white sockets");
    }

    [Fact]
    public void Higher_quality_raises_the_chromatic_non_white_chance()
    {
        var random = new Random(21);
        int Count(byte quality) => Enumerable.Range(0, 4000)
            .Sum(_ => CurrencyEffects.Chromatic(Boots(SocketColour.White, SocketColour.White, SocketColour.White, SocketColour.White, SocketColour.White, SocketColour.White)
                with { ItemLevel = 100, Quality = quality }, (20, 20, 20), random)!
                .Sockets.Count(s => SocketColours.IsColoured(s.Colour)));
        Assert.True(Count(20) > Count(0));
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
        // Strength 100: red weighs 112 against green's and blue's 12 (12 / (112 + 12 + 12) ≈ 10.6%).
        Assert.InRange(red / (double)total, 0.78, 0.87);
    }

    [Fact]
    public void Corruption_turns_one_white_socket_coloured()
    {
        var catalog = new ItemCatalog("data");
        var type = catalog.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!;
        var rolls = new ItemRolls(catalog);
        var random = new Random(22);
        var item = ItemCatalog.Create(type, [new(SocketColour.White), new(SocketColour.White), new(SocketColour.Blue)], [3])
            with { ItemLevel = 100, Rarity = ItemRarity.Rare };
        var after = CurrencyEffects.Vaal(item, type, catalog.Requirements(type), rolls, random)!;
        Assert.True(after.Corrupted);
        Assert.Equal(SocketColour.Blue, after.Sockets[2].Colour);
        // Exactly one of the two white sockets became a colour; the other stayed white.
        Assert.Equal(1, after.Sockets.Count(s => s.Colour == SocketColour.White));
        // No white socket left to colour: the sockets stay as they are.
        var allColoured = item with { Sockets = [new(SocketColour.Red), new(SocketColour.Green), new(SocketColour.Blue)] };
        var again = CurrencyEffects.Vaal(allColoured, type, catalog.Requirements(type), rolls, random)!;
        Assert.Equal(allColoured.Sockets.Select(s => s.Colour), again.Sockets.Select(s => s.Colour));
    }

    [Fact]
    public void An_item_without_sockets_is_not_changed()
    {
        Assert.Null(CurrencyEffects.Chromatic(new Item(0x1, ["Base"], 0), (0, 0, 0), new Random(3)));
    }
}
