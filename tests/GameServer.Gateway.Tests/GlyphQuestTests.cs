using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

[Collection(TestSaves.FailWriteCollection)]
public class GlyphQuestTests : IDisposable
{
    const uint Glyph1 = 3228085585, Glyph2 = 1721197556, Glyph3 = 278505504, Other = 99;
    readonly string folder = TestSaves.Catalog(new TestSaves.Type(Glyph1, GlyphQuest.Glyphs[0]), new TestSaves.Type(Glyph2, GlyphQuest.Glyphs[1]),
        new TestSaves.Type(Glyph3, GlyphQuest.Glyphs[2], Height: 2), new TestSaves.Type(Other, "Metadata/Items/Other"));
    readonly string character = "Mud" + Guid.NewGuid().ToString("N")[..12];
    public GlyphQuestTests() => TestSaves.Ensure();
    public void Dispose() { Saves.FailWrite = null; Directory.Delete(folder, true); }

    PlayerInventories Inventory() => PlayerInventories.For(character, new ItemCatalog(folder));
    static int? GlyphOf(Item i) => i.TypeHash switch { Glyph1 => 0, Glyph2 => 1, Glyph3 => 2, _ => null };
    static Item Make(uint hash) => new(hash, ["Base"], 1);
    HashSet<string> Flags() => [.. CharacterSaves.Load(character)?.QuestFlags ?? []];

    // The Mud Flats seed 1 as the recorder captured it (data/walkability/1_1_3_seed1.objects.json).
    void WriteCapture(string area = "1_1_3", int seed = 1) => File.WriteAllText(Path.Combine(folder, $"{area}_seed{seed}.objects.json"), JsonSerializer.Serialize(new
    {
        area, seed,
        objects = new object[]
        {
            new { objectType = GlyphQuest.Wall, model = "Metadata/Terrain/Beach/Transitions/BeachToWatercave_v2/Beach_to_watercave_v2_stone_switch.ao",
                x = 829, y = 375, rotation = 4.71238911f, z = -165.61325f, code = 3 },
            new { objectType = GlyphQuest.NestTypes[0], model = "Metadata/Terrain/Beach/Features/beach_rhoanest_v01_01_chest.ao", x = 793, y = 563, rotation = 0f, z = -57.5458f, code = 0 },
            new { objectType = GlyphQuest.NestTypes[0], model = "Metadata/Terrain/Beach/Features/beach_rhoanest_v01_01_chest.ao", x = 425, y = 609, rotation = 4.712389f, z = -57.5458f, code = 3 },
            new { objectType = GlyphQuest.NestTypes[0], model = "Metadata/Terrain/Beach/Features/beach_rhoanest_v01_01_chest.ao", x = 678, y = 770, rotation = 1.570796f, z = -57.5458f, code = 1 },
            new { objectType = "Metadata/Chests/Other", model = "Metadata/Other.ao", x = 1, y = 1, rotation = 0f, z = 0f, code = 0 },
        },
    }));

    [Fact]
    public void CapturedNestsGetOneGlyphEachTheSameEveryTime()
    {
        WriteCapture();
        var layout = GlyphQuest.Load("1_1_3", 1, folder)!;
        Assert.Equal(3, layout.Nests.Count);
        Assert.Equal(new[] { 0, 1, 2 }, layout.Nests.Select(n => n.Glyph).Order());
        Assert.All(layout.Nests, n => Assert.Equal(GlyphQuest.NestTypes[n.Glyph], n.Type));
        Assert.Equal(layout.Nests.Select(n => (n.At.X, n.At.Y, n.Glyph)), GlyphQuest.Load("1_1_3", 1, folder)!.Nests.Select(n => (n.At.X, n.At.Y, n.Glyph)));
        Assert.Equal(-57.5458f, layout.Nests[0].At.Z!.Value, 3);
        Assert.Equal((829, 375, 3), (layout.Wall!.X, layout.Wall.Y, layout.Wall.Code));
    }

    [Fact]
    public void CaptureOfAnotherAreaOrSeedIsNotUsed()
    {
        WriteCapture("1_1_3", 2);
        Assert.Null(GlyphQuest.Load("1_1_3", 1, folder));
        WriteCapture("1_1_2", 1);
        Assert.Null(GlyphQuest.Load("1_1_2", 1, folder));
    }

    [Fact]
    public void CaveRoomObjectsArePlacedThroughTheWallsRecord()
    {
        WriteCapture();
        var layout = GlyphQuest.Load("1_1_3", 1, folder)!;
        // The captured client doodads of to_cave.arm's SunkenPassageBlockers (dead_seagull_v01_03.ao), seed 1.
        Assert.Equal(19, layout.Blockers.Count);
        Assert.Contains((819, 304), layout.Blockers);
        Assert.Contains((849, 346), layout.Blockers);
        Assert.Contains((873, 358), layout.Blockers);
        // The waterfall beside the cave's own waterfall doodad at (899, 343); the room's angle 0 turned by code 3.
        Assert.Equal((906, 342), (layout.Water!.Value.X, layout.Water.Value.Y));
        Assert.Equal(3 * MathF.PI / 2, layout.Water.Value.Rotation, 4);
        // Every code is a turn or flip of the offset from the wall.
        var wall = layout.Wall!;
        Assert.Equal((wall.X + 33, wall.Y + 77), GlyphQuest.FromRoom(wall with { Code = 0 }, (71, 147)));
        Assert.Equal((wall.X + 77, wall.Y + 33), GlyphQuest.FromRoom(wall with { Code = 7 }, (71, 147)));
    }

    [Fact]
    public void NestAndWallBlobsCarryHeightOpenedAndTransitionState()
    {
        var nest = new GlyphQuest.Placement(GlyphQuest.NestTypes[1], "Metadata/A.ao", 10, 20, 0, -57.5f, 0);
        byte[] closed = GlyphQuest.NestBlob(nest, false), opened = GlyphQuest.NestBlob(nest, true);
        // flags, base data, x, y, rotation, u16, flags0 0x04, flags1 0x02, height steps and z, two visibility bytes.
        Assert.Equal(0x04, closed[16]);
        Assert.Equal((int)MathF.Round(-57.5f / 7.8125f), BitConverter.ToInt32(closed, 18));
        int chest = 2 + 16 + 8 + 2 + 1 + 4 + 2 * "Metadata/A.ao".Length;
        Assert.Equal(0, closed[chest]);
        Assert.Equal(1, opened[chest]);
        // Chest 3, Stats 10, ObjectMagicProperties 4, InteractionAction, MinimapIcon, tasks.
        Assert.Equal(chest + 3 + 10 + 4 + 3, closed.Length);

        var wall = nest with { Type = GlyphQuest.Wall };
        byte[] waiting = GlyphQuest.WallBlob(wall, false), done = GlyphQuest.WallBlob(wall, true);
        Assert.Equal(chest + 4, waiting.Length);   // Transitionable, MinimapIcon, InteractionAction, tasks
        Assert.Equal(GlyphQuest.Untransitioned, waiting[chest]);
        Assert.Equal(GlyphQuest.Transitioned, done[chest]);
        byte[] water = GlyphQuest.WaterBlob(1, 2, 0, true);
        Assert.Equal(0, water[16]);   // terrain height
        Assert.Equal(GlyphQuest.Transitioned, water[^2]);
    }

    [Fact]
    public void TheSubmergedPassageIsClosedUntilTheGlyphsArePlaced()
    {
        Assert.Equal(GlyphQuest.PlacedGlyphs, Quests.FlagToEnter("1_1_3", "1_1_4_1"));
        Assert.Null(Quests.FlagToEnter("1_1_3", "1_1_3a"));
        Assert.Equal(1, GlyphQuest.GlyphOf("Metadata/Items/QuestItems/Glyphs/Glyph2"));
        Assert.Null(GlyphQuest.GlyphOf("Metadata/Items/Other"));
    }

    [Fact]
    public void PickingUpAGlyphSavesItsFlagsWithTheItem()
    {
        var inventory = Inventory();
        GlyphQuest.Claim(character, 2, () => inventory.Add(0, 0, 0, Make(Glyph3)));
        Assert.Superset(new HashSet<string> { GlyphQuest.KnowsAboutGlyphs, "a1q4-HaveGlyph3" }, Flags());
        Assert.DoesNotContain("a1q4-HaveGlyph1", Flags());
        Assert.Single(inventory.Items(0));
    }

    [Fact]
    public void TheWallTakesAllThreeGlyphsOrNothing()
    {
        var inventory = Inventory();
        inventory.Add(0, 0, 0, Make(Glyph1));
        inventory.Add(0, 1, 0, Make(Other));
        inventory.Add(0, 2, 0, Make(Glyph3));
        CharacterSaves.ChangeQuestFlags(character, ["a1q4-HaveGlyph1", "a1q4-HaveGlyph3"], true);
        Assert.Null(GlyphQuest.Place(character, inventory, GlyphOf));
        Assert.Equal(3, inventory.Items(0).Count);
        Assert.DoesNotContain(GlyphQuest.PlacedGlyphs, Flags());

        inventory.Add(PlayerInventories.Cursor, 0, 0, Make(Glyph2));
        var removed = GlyphQuest.Place(character, inventory, GlyphOf)!;
        Assert.Equal(3, removed.Count);
        Assert.Contains(removed, r => r.Row == PlayerInventories.Cursor);
        Assert.Equal(new[] { Other }, inventory.Items(0).Select(i => i.Item.TypeHash));
        Assert.Contains(GlyphQuest.PlacedGlyphs, Flags());
        Assert.DoesNotContain(Flags(), f => f.StartsWith("a1q4-HaveGlyph"));
        // As saved: a reload has the same items.
        PlayerInventories.Forget(character);
        Assert.Equal(new[] { Other }, Inventory().Items(0).Select(i => i.Item.TypeHash));
    }

    [Fact]
    public void AFailedPlacementKeepsTheGlyphsAndTheFlags()
    {
        var inventory = Inventory();
        inventory.Add(0, 0, 0, Make(Glyph1));
        inventory.Add(0, 1, 0, Make(Glyph2));
        inventory.Add(0, 2, 0, Make(Glyph3));
        int writes = 0;
        Saves.FailWrite = key => key == character && ++writes == 3;
        Assert.ThrowsAny<Exception>(() => GlyphQuest.Place(character, inventory, GlyphOf));
        Saves.FailWrite = null;
        PlayerInventories.Forget(character);
        Assert.Equal(3, Inventory().Items(0).Count);
        Assert.DoesNotContain(GlyphQuest.PlacedGlyphs, Flags());
    }

    [Fact]
    public void AQuestChestOpensOncePerInstance()
    {
        var instance = new InstanceState();
        Assert.False(instance.ChestOpened(5, 6));
        Assert.True(instance.OpenChest(5, 6));
        Assert.False(instance.OpenChest(5, 6));
        Assert.True(instance.ChestOpened(5, 6));
        Assert.False(new InstanceState().ChestOpened(5, 6));
    }
}
