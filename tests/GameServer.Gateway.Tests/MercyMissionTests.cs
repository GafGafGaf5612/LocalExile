using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>a1q5 "Mercy Mission": Hailrake's Medicine Chest, its flag, Nessa taking it and her flask reward.</summary>
[Collection(TestSaves.FailWriteCollection)]
public class MercyMissionTests : IDisposable
{
    const uint ChestHash = 370857295, FlaskHash = 0x0f1a5c02, GemHash = 0x0e3e0001, RobeHash = 0x0a0b0003;
    readonly string folder = TestSaves.Catalog(new TestSaves.Type(ChestHash, MercyMission.MedicineChest, 2, 3), new TestSaves.Type(123, "Other"));
    readonly string character = "Mercy" + Guid.NewGuid().ToString("N")[..12];
    static Item Chest => new(ChestHash, ["Base"], 1);
    static Item Other => new(123, ["Base"], 1);

    public MercyMissionTests() => TestSaves.Ensure();
    public void Dispose() { Saves.FailWrite = null; PlayerInventories.Forget(character); Directory.Delete(folder, true); }

    PlayerInventories Inventory() => PlayerInventories.For(character, new ItemCatalog(folder));
    HashSet<string> SavedFlags() => CharacterSaves.Load(character)?.QuestFlags?.ToHashSet() ?? [];

    [Fact] public void HailrakeIsTheTidalIslandsBossAndTheChestItsDrop()
    {
        Assert.Equal("1_1_2a", MercyMission.Area);
        Assert.Equal("Metadata/Monsters/Cannibal/CannibalMaleBossHailrake", MercyMission.Hailrake);
        Assert.Equal("Metadata/Items/QuestItems/MedicineSet1", MercyMission.MedicineChest);
        Assert.Equal((1, 2), (MercyMission.RewardOffer, MercyMission.SupportRewardOffer));
    }

    [Fact] public void HailrakeDropsTheChestOnlyWhileNoCopyIsWithinReach()
    {
        var inventory = Inventory();
        var world = new InstanceState();
        Assert.True(MercyMission.HailrakeDrops(ChestHash, false, inventory, _ => 0, world.Ground));
        // Nessa has it: never again.
        Assert.False(MercyMission.HailrakeDrops(ChestHash, true, inventory, _ => 0, world.Ground));
        // A copy in the stash or on this instance's ground: not twice.
        Assert.False(MercyMission.HailrakeDrops(ChestHash, false, inventory, h => h == ChestHash ? 1 : 0, world.Ground));
        uint id = world.Ground.Add(new WorldItemObject(5, 5, Chest));
        Assert.False(MercyMission.HailrakeDrops(ChestHash, false, inventory, _ => 0, world.Ground));
        world.Ground.Take(id);
        // Carried, in the main inventory or on the cursor: not twice.
        var carried = inventory.Add(PlayerInventories.MainInventory, 0, 0, Chest);
        Assert.False(MercyMission.HailrakeDrops(ChestHash, false, inventory, _ => 0, world.Ground));
        inventory.Remove(PlayerInventories.MainInventory, carried.Id);
        // Other items do not count; a chest lost elsewhere (another instance, sold) drops again.
        inventory.Add(PlayerInventories.MainInventory, 0, 0, Other);
        Assert.True(MercyMission.HailrakeDrops(ChestHash, false, inventory, _ => 0, world.Ground));
    }

    [Fact] public void TheFlagFollowsTheCarriedChestUntilNessaHasIt()
    {
        Assert.Equal((true, false), MercyMission.Sync(flagged: false, done: false, carried: true));
        Assert.Equal(((bool?)null, false), MercyMission.Sync(flagged: true, done: false, carried: true));
        Assert.Equal((false, false), MercyMission.Sync(flagged: true, done: false, carried: false));
        Assert.Equal(((bool?)null, false), MercyMission.Sync(flagged: false, done: false, carried: false));
        // Nessa has one: the flag goes and a chest still carried (a copy from another instance) is taken.
        Assert.Equal((false, true), MercyMission.Sync(flagged: true, done: true, carried: true));
        Assert.Equal(((bool?)null, true), MercyMission.Sync(flagged: false, done: true, carried: true));
        Assert.Equal(((bool?)null, false), MercyMission.Sync(flagged: false, done: true, carried: false));
    }

    [Fact] public void DoneMeansNessaHasTheChestOrTheRewardIsTaken()
    {
        Assert.False(MercyMission.Done(_ => false));
        Assert.True(MercyMission.Done(f => f == MercyMission.NessaCongratulated));
        Assert.True(MercyMission.Done(f => f == MercyMission.QuestRewardTaken));
        Assert.False(MercyMission.Done(f => f == MercyMission.HaveMedicineChest));
    }

    [Fact] public void NessaTakesTheChestWithItsFlagsOnceAndTheyStaySavedAfterReload()
    {
        var inventory = Inventory();
        CharacterSaves.ChangeQuestFlags(character, [MercyMission.HaveMedicineChest], true);
        var chest = inventory.Add(PlayerInventories.MainInventory, 3, 1, Chest);
        inventory.Add(PlayerInventories.MainInventory, 0, 0, Other);
        var handed = MercyMission.HandIn(character, inventory, ChestHash);
        Assert.Equal((PlayerInventories.MainInventory, chest.Id), (handed!.Value.Row, handed.Value.Item.Id));
        Assert.Null(MercyMission.HandIn(character, inventory, ChestHash));   // a second, quick choice finds none
        PlayerInventories.Forget(character);
        var reloaded = Inventory();
        Assert.DoesNotContain(MercyMission.Carried(reloaded), c => c.Item.Item.TypeHash == ChestHash);
        Assert.Single(reloaded.Items(PlayerInventories.MainInventory));
        Assert.Contains(MercyMission.NessaCongratulated, SavedFlags());
        Assert.DoesNotContain(MercyMission.HaveMedicineChest, SavedFlags());
    }

    [Fact] public void WithoutAChestNessaTakesNothingAndSetsNoFlag()
    {
        var inventory = Inventory();
        inventory.Add(PlayerInventories.MainInventory, 0, 0, Other);
        Assert.Null(MercyMission.HandIn(character, inventory, ChestHash));
        Assert.DoesNotContain(MercyMission.NessaCongratulated, SavedFlags());
        Assert.Single(inventory.Items(PlayerInventories.MainInventory));
    }

    [Fact] public void TheHeldChestCountsAsCarriedAndIsTakenFromTheCursor()
    {
        var inventory = Inventory();
        var chest = inventory.Add(PlayerInventories.MainInventory, 0, 0, Chest);
        var held = inventory.PickUp(PlayerInventories.MainInventory, chest.Id)!;
        Assert.Contains(MercyMission.Carried(inventory), c => c.Row == PlayerInventories.Cursor && c.Item.Id == held.Id);
        Assert.Equal(PlayerInventories.Cursor, MercyMission.HandIn(character, inventory, ChestHash)!.Value.Row);
        Assert.Empty(inventory.Items(PlayerInventories.Cursor));
    }

    [Fact] public void AFailedSaveKeepsTheChestAndSetsNoFlag()
    {
        var inventory = Inventory();
        inventory.Add(PlayerInventories.MainInventory, 0, 0, Chest);
        int writes = 0;
        Saves.FailWrite = key => key.Equals(character, StringComparison.OrdinalIgnoreCase) && ++writes == 2;
        Assert.ThrowsAny<Exception>(() => MercyMission.HandIn(character, inventory, ChestHash));
        Saves.FailWrite = null;
        PlayerInventories.Forget(character);
        Assert.Contains(MercyMission.Carried(Inventory()), c => c.Item.Item.TypeHash == ChestHash);
        Assert.DoesNotContain(MercyMission.NessaCongratulated, SavedFlags());
    }

    [Fact] public void AChestOnOfferToAVendorStaysAndNothingChanges()
    {
        var inventory = Inventory();
        var chest = inventory.Add(PlayerInventories.MainInventory, 0, 0, Chest);
        Assert.NotNull(inventory.Offer(PlayerInventories.MainInventory, chest.Id, 0, 0, _ => new PlayerInventories.Shape(2, 3, "")));
        Assert.Null(MercyMission.HandIn(character, inventory, ChestHash));
        Assert.DoesNotContain(MercyMission.NessaCongratulated, SavedFlags());
        inventory.CancelOffers();
        Assert.Contains(MercyMission.Carried(inventory), c => c.Item.Id == chest.Id);
    }

    [Fact] public void RemoveAllTakesEveryCarriedChestAndLeavesTheRest()
    {
        var inventory = Inventory();
        inventory.Add(PlayerInventories.MainInventory, 0, 0, Chest);
        inventory.Add(PlayerInventories.MainInventory, 2, 0, Chest);
        inventory.Add(PlayerInventories.MainInventory, 4, 0, Other);
        Assert.Equal(2, MercyMission.RemoveAll(inventory, ChestHash).Count);
        PlayerInventories.Forget(character);
        var reloaded = Inventory();
        Assert.Equal(123u, Assert.Single(reloaded.Items(PlayerInventories.MainInventory)).Item.TypeHash);
    }

    [Fact] public void TheMedicineBoatRoomPutsMapbossWhereTheCaptureSays()
    {
        // data/walkability/1_1_2a_seed1.rooms.json: medicine_boat_1.arm (138 × 207 cells) at (138, 345), code 3; its
        // "mapboss" point is at room cell (45, 127), angle 4.18879 (the .arm).
        var room = new TutorialObjects.RoomPlacement(138, 345, 138, 207, 3);
        var boss = room.Place(new AreaPopulation.RoomPoint("mapboss", 45, 127, 4.18879f));
        Assert.Equal((265, 437), (boss.X, boss.Y));
        var pack = room.Place(new AreaPopulation.RoomPoint("%area%-bosspack", 62, 154));
        Assert.Equal((292, 420), (pack.X, pack.Y));
    }

    // QuestRewardOffers 1 and its QuestRewards rows, as the game data has them (columns __col1 index, __col5 rarity).
    string RewardTables()
    {
        string data = Path.Combine(folder, "rewards");
        Directory.CreateDirectory(Path.Combine(data, "tables"));
        void Write(string name, object value) => File.WriteAllText(Path.Combine(data, "tables", name + ".json"), JsonSerializer.Serialize(value));
        Write("QuestFlags", new[] { new { Id = "a1q1-QuestRewardTaken" }, new { Id = MercyMission.QuestRewardTaken } });
        Write("QuestRewardOffers", new object[] { new { QuestFlag = 0 }, new { QuestFlag = 1 } });
        Write("QuestRewards", new object[]
        {
            new { RewardOffer = 1, __col1 = 0, Reward = 0, RewardLevel = 4, RewardStack = 1, Characters = Array.Empty<int>(), __col5 = (int?)0 },
            new { RewardOffer = 1, __col1 = 1, Reward = 2, RewardLevel = 8, RewardStack = 1, Characters = Array.Empty<int>(), __col5 = (int?)1 },
            new { RewardOffer = 0, __col1 = 0, Reward = 1, RewardLevel = 1, RewardStack = 1, Characters = Array.Empty<int>(), __col5 = (int?)null },
        });
        return data;
    }

    // A catalog whose rows are a flask (Charges, Flask), a gem and a body armour.
    string ItemTables()
    {
        string data = TestSaves.Catalog(new TestSaves.Type(FlaskHash, "Metadata/Items/Flasks/FlaskLife2"),
            new TestSaves.Type(GemHash, "Metadata/Items/Gems/SkillGemTest"), new TestSaves.Type(RobeHash, "Metadata/Items/Armours/BodyArmours/Robe"));
        File.WriteAllText(Path.Combine(data, "item-types.json"), JsonSerializer.Serialize(new
        {
            types = new object[]
            {
                new { type = "Metadata/Items/Flasks/FlaskLife2", components = new[] { "Base", "Quality", "LocalStats", "Charges", "Mods", "Usable", "Flask" }, read = 7 },
                new { type = "Metadata/Items/Gems/SkillGemTest", components = new[] { "Base" }, read = 1 },
                new { type = "Metadata/Items/Armours/BodyArmours/Robe", components = new[] { "Base" }, read = 1 },
            },
        }));
        return data;
    }

    [Fact] public void NessasRewardIsANormalFlaskAtItsItemLevelAndMagicArmoursStayRefused()
    {
        string tables = RewardTables(), itemData = ItemTables();
        try
        {
            var catalog = QuestRewardCatalog.Load(tables);
            var offer = catalog.Find(MercyMission.RewardOffer)!;
            Assert.Equal(MercyMission.QuestRewardTaken, offer.TakenFlag);
            var items = new ItemCatalog(itemData);
            var flask = QuestRewardCatalog.Create(offer.Choices[0], items);
            Assert.NotNull(flask);
            Assert.Equal((FlaskHash, (byte)4), (flask!.TypeHash, flask.ItemLevel));
            Assert.Equal(0, offer.Choices[0].Rarity);
            // A magic item at level 8 (offer 8's armours) needs affixes: still refused.
            Assert.Null(QuestRewardCatalog.Create(offer.Choices[1], items));
            // Gems: level 1 only, as before.
            var gem = QuestRewardCatalog.Create(catalog.Find(0)!.Choices[0], items);
            Assert.Equal((GemHash, (byte)1), (gem!.TypeHash, gem.ItemLevel));
            Assert.Null(QuestRewardCatalog.Create(catalog.Find(0)!.Choices[0] with { Level = 4 }, items));
            // A non-flask normal item above level 1 is not a modelled reward either.
            Assert.Null(QuestRewardCatalog.Create(new QuestRewardCatalog.Choice(9, 2, 4, 1, []) { Rarity = 0 }, items));
        }
        finally { Directory.Delete(itemData, true); }
    }

    [Fact] public void TheFlaskRewardIsClaimedOnceWithItsTakenFlag()
    {
        var inventory = Inventory();
        Item flask = new(123, ["Base"], 1) { ItemLevel = 4 };
        var claimed = QuestRewardCatalog.Claim(character, MercyMission.QuestRewardTaken, inventory, flask, _ => new(1, 2, ""));
        Assert.NotNull(claimed);
        Assert.Null(QuestRewardCatalog.Claim(character, MercyMission.QuestRewardTaken, inventory, flask, _ => new(1, 2, "")));
        PlayerInventories.Forget(character);
        Assert.Equal((byte)4, Assert.Single(Inventory().Items(PlayerInventories.MainInventory)).Item.ItemLevel);
        Assert.Contains(MercyMission.QuestRewardTaken, SavedFlags());
    }
}
