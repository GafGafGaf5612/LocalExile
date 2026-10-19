using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// One class: CharacterSaves.Folder is static, so these tests must not run in parallel with other save tests.
public class PlayerInventoriesTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), $"saves-{Guid.NewGuid():N}");
    readonly string _character = $"Picker{Guid.NewGuid():N}"[..20];

    public PlayerInventoriesTests() => CharacterSaves.Folder = _folder;

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    // No item tables in this folder: the character starts with no kit, and items are kept by their type hash.
    PlayerInventories NewInventories() => PlayerInventories.For(_character, new ItemCatalog(_folder));

    static Item Wand() => new(0xd9b1a12f, ["Base"], 0x40a1);

    [Fact]
    public void Picking_up_moves_the_item_to_the_cursor_with_its_id_in_one_save()
    {
        var items = NewInventories();
        var placed = items.Add(PlayerInventories.MainInventory, 3, 1, Wand());

        var held = items.PickUp(PlayerInventories.MainInventory, placed.Id);

        Assert.NotNull(held);
        Assert.Equal(placed.Id, held.Id);
        Assert.Empty(items.Items(PlayerInventories.MainInventory));
        Assert.Equal([placed.Id], items.Items(PlayerInventories.Cursor).Select(i => i.Id));
        // What a crash right now would leave on disk: the item once, on the cursor.
        var saved = CharacterSaves.Load(_character)!.Inventories;
        Assert.False(saved.ContainsKey(PlayerInventories.MainInventory));
        Assert.Equal([placed.Id], saved[PlayerInventories.Cursor].Select(p => p.Id));
    }

    [Fact]
    public void Picking_up_while_holding_an_item_changes_nothing()
    {
        var items = NewInventories();
        var first = items.Add(PlayerInventories.MainInventory, 0, 0, Wand());
        var second = items.Add(PlayerInventories.MainInventory, 2, 0, Wand());
        items.PickUp(PlayerInventories.MainInventory, first.Id);

        Assert.Null(items.PickUp(PlayerInventories.MainInventory, second.Id));
        Assert.Equal([second.Id], items.Items(PlayerInventories.MainInventory).Select(i => i.Id));
        Assert.Equal([first.Id], items.Items(PlayerInventories.Cursor).Select(i => i.Id));
        Assert.Equal([second.Id], CharacterSaves.Load(_character)!.Inventories[PlayerInventories.MainInventory].Select(p => p.Id));
    }

    [Fact]
    public void Picking_up_an_item_that_is_not_there_changes_nothing()
    {
        var items = NewInventories();
        var placed = items.Add(PlayerInventories.MainInventory, 0, 0, Wand());

        Assert.Null(items.PickUp(PlayerInventories.MainInventory, placed.Id + 100));
        Assert.Null(items.PickUp(PlayerInventories.Flask, placed.Id));
        Assert.Equal([placed.Id], items.Items(PlayerInventories.MainInventory).Select(i => i.Id));
        Assert.Empty(items.Items(PlayerInventories.Cursor));
    }

    [Fact]
    public void Gold_an_older_save_kept_is_taken_once_and_the_rest_stays()
    {
        CharacterSaves.Write(_character, new CharacterSaves.Save([], Experience: 42, Gold: 750));

        Assert.Equal(750u, CharacterSaves.TakeGold(_character));
        Assert.Equal(0u, CharacterSaves.TakeGold(_character));
        var save = CharacterSaves.Load(_character)!;
        Assert.Null(save.Gold);
        Assert.Equal(42u, save.Experience);
        Assert.Equal(0u, CharacterSaves.TakeGold("NoSuchCharacter" + Guid.NewGuid().ToString("N")[..8]));
    }
}
