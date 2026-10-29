using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// 3.29 socket colours: sockets are white by default, with an item-level (and, for Chromatic, quality) chance to roll a
// colour proportional to the base's attribute requirements. New sockets from currency and the bench stay white.
public class SocketColoursTests
{
    [Fact]
    public void New_items_generate_two_linked_sockets_and_most_are_white()
    {
        var (catalog, _, _, _) = BenchCraftingTests.Setup.Value;
        var type = catalog.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!;

        var (sockets, links) = catalog.DefaultSockets(type, itemLevel: 1);
        Assert.Equal(2, sockets.Count);
        Assert.Equal(new byte[] { 2 }, links);

        // 5% per socket at item level 1: a few colours over 4000 sockets, never most of them.
        int coloured = Enumerable.Range(0, 2000)
            .Sum(_ => catalog.DefaultSockets(type, itemLevel: 1).Sockets.Count(s => SocketColours.IsColoured(s.Colour)));
        Assert.InRange(coloured, 1, 800);
    }

    [Fact]
    public void The_colour_chance_rises_with_item_level()
    {
        var (catalog, _, _, _) = BenchCraftingTests.Setup.Value;
        var type = catalog.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!;
        int Count(int itemLevel) => Enumerable.Range(0, 2000)
            .Sum(_ => catalog.DefaultSockets(type, itemLevel).Sockets.Count(s => SocketColours.IsColoured(s.Colour)));

        int low = Count(1), high = Count(100);
        Assert.True(high > low * 2, $"level 1: {low}, level 100: {high}");
    }

    [Fact]
    public void A_coloured_socket_follows_the_attribute_requirements()
    {
        var (catalog, _, _, _) = BenchCraftingTests.Setup.Value;
        var type = catalog.Find("Metadata/Items/Armours/BodyArmours/BodyStr10")!; // 124 Str: nearly all coloured sockets are red
        int red = 0, coloured = 0;
        for (int i = 0; i < 4000; i++)
            foreach (var socket in catalog.DefaultSockets(type, itemLevel: 100).Sockets)
                if (SocketColours.IsColoured(socket.Colour))
                {
                    coloured++;
                    if (socket.Colour == SocketColour.Red) red++;
                }
        Assert.True(coloured > 0);
        Assert.True(red > coloured * 0.8, $"{red} red of {coloured}");
    }

    [Fact]
    public void Bench_socket_count_crafts_add_white_sockets_and_keep_retained_colours()
    {
        var (catalog, _, bench, _) = BenchCraftingTests.Setup.Value;
        var type = catalog.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!;
        var recipe = bench.All.Single(r => r.Sockets == 4);
        var item = ItemCatalog.Create(type, [new(SocketColour.Red)], [1]) with { ItemLevel = 100 };

        var output = bench.Apply(recipe.Row, item, false, new Random(2))!.Item;
        Assert.Equal(4, output.Sockets.Count);
        Assert.Equal(SocketColour.Red, output.Sockets[0].Colour);
        Assert.All(output.Sockets.Skip(1), s => Assert.Equal(SocketColour.White, s.Colour));
    }

    [Fact]
    public void The_non_white_bench_crafts_guarantee_their_minimum()
    {
        var (catalog, _, bench, _) = BenchCraftingTests.Setup.Value;
        var type = catalog.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!;
        var item = ItemCatalog.Create(type,
            Enumerable.Repeat(new ItemSocket(SocketColour.White), 6).ToArray(), [6]) with { ItemLevel = 1 };
        foreach (var (name, need) in new[]
                 {
                     ("At Least Two Non-White Sockets", 2), ("At Least Three Non-White Sockets", 3),
                     ("At Least Four Non-White Sockets", 4),
                 })
        {
            var recipe = bench.All.Single(r => r.Name == name);
            for (int seed = 0; seed < 20; seed++)
            {
                var output = bench.Apply(recipe.Row, item, false, new Random(seed))!.Item;
                Assert.True(output.Sockets.Count(s => SocketColours.IsColoured(s.Colour)) >= need, $"{name}, seed {seed}");
            }
        }
    }

    [Fact]
    public void The_quality_bonus_applies_to_the_chromatic_and_bench_chances()
    {
        Assert.True(SocketColours.NonWhiteChance(100, 20, chromatic: true) > SocketColours.NonWhiteChance(100));
        Assert.True(SocketColours.NonWhiteChance(100) > SocketColours.NonWhiteChance(1));
    }
}
