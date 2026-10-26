using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// Mageblood's item markers and which flasks it applies (Program.Mageblood): the leftmost magic utility flasks only.
public class MagebloodTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data/tables/Flasks.json"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("Flasks.json");
    }

    static readonly Lazy<(ItemCatalog Items, Item Belt)> Setup = new(() =>
    {
        string data = Data();
        var items = new ItemCatalog(data);
        return (items, new UniqueCatalog(items, data).Create("Mageblood", new Random(1))!);
    });

    [Fact]
    public void Mageblood_is_recognised_by_its_unique_word_and_mod_flask_count()
    {
        var (items, belt) = Setup.Value;

        Assert.True(Program.IsMageblood(belt));
        Assert.False(Program.IsMageblood(ItemCatalog.Create(items.Find("Heavy Belt")!)));
        Assert.InRange(Program.MagebloodFlaskCount(belt), 2, 4);
    }

    [Fact]
    public void Only_the_leftmost_magic_utility_flasks_are_applied()
    {
        var (items, _) = Setup.Value;
        InventoryItem At(byte x, string type, ItemRarity rarity) =>
            new((uint)(x + 1), x, 0, ItemCatalog.Create(items.Find(type)!) with { Rarity = rarity });
        var bar = new[]
        {
            At(0, "Small Life Flask", ItemRarity.Normal),
            At(1, "Quicksilver Flask", ItemRarity.Magic),
            At(2, "Granite Flask", ItemRarity.Normal),
            At(3, "Quartz Flask", ItemRarity.Magic),
            At(4, "Ruby Flask", ItemRarity.Magic),
        };

        Assert.True(Program.IsMagicUtilityFlask(bar[1].Item));
        Assert.False(Program.IsMagicUtilityFlask(bar[0].Item));   // life flask
        Assert.False(Program.IsMagicUtilityFlask(bar[2].Item));   // normal rarity
        var applied = Program.MagebloodFlasks(bar, 2);
        Assert.Equal([(uint)2, (uint)4], applied.Select(i => i.Id));
    }
}
