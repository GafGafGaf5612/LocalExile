using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

// Experience and flask charges from kills wait in memory (hundreds of kills per second must not each wait for the disk)
// and are saved within a few seconds, at a level-up, when the connection ends, or with any other write of the save.
public class UnsavedProgressTests
{
    readonly string _character = $"Xp{Guid.NewGuid():N}"[..20];

    public UnsavedProgressTests() => TestSaves.Ensure();

    // The experience in the database itself, not the one Load adds from memory.
    uint? SavedExperience() => JsonSerializer.Deserialize<CharacterSaves.Save>(Saves.Get(SaveDatabase.Table.Characters, _character)!)!.Experience;

    [Fact]
    public void Experience_waits_in_memory_until_it_is_saved()
    {
        CharacterSaves.Write(_character, new CharacterSaves.Save([], Experience: 100));

        CharacterSaves.AddExperience(_character, 0, xp => xp + 50);
        CharacterSaves.AddExperience(_character, 0, xp => xp + 25);

        Assert.Equal(100u, SavedExperience());
        Assert.Equal(175u, CharacterSaves.Load(_character)!.Experience);   // a new connection sees it at once
        CharacterSaves.SaveExperience();
        Assert.Equal(175u, SavedExperience());
    }

    [Fact]
    public void Any_other_write_of_the_save_saves_the_waiting_experience_too()
    {
        CharacterSaves.Write(_character, new CharacterSaves.Save([], Experience: 100));
        CharacterSaves.AddExperience(_character, 0, xp => xp + 10);

        CharacterSaves.ChangeQuestFlags(_character, ["a1q1-KilledHillock"], on: true);

        Assert.Equal(110u, SavedExperience());
    }

    [Fact]
    public void Experience_set_by_hand_is_saved_at_once_and_later_gains_add_to_it()
    {
        CharacterSaves.Write(_character, new CharacterSaves.Save([], Experience: 100));
        CharacterSaves.AddExperience(_character, 0, xp => xp + 10);

        CharacterSaves.SetExperience(_character, 5000);
        Assert.Equal(5000u, SavedExperience());
        Assert.Equal((5000u, 5001u), CharacterSaves.AddExperience(_character, 0, xp => xp + 1));
    }

    [Fact]
    public void A_deleted_characters_waiting_experience_never_reaches_a_new_one_of_its_name()
    {
        CharacterSaves.Write(_character, new CharacterSaves.Save([], Experience: 100));
        CharacterSaves.AddExperience(_character, 0, xp => xp + 999);
        Assert.True(Saves.Db.SetAside(_character));   // Auth deletes it

        CharacterSaves.SaveExperience(_character);
        Assert.Null(Saves.Get(SaveDatabase.Table.Characters, _character));   // not brought back

        CharacterSaves.AddExperience(_character, 0, xp => xp + 7);
        PlayerInventories.SetClass(_character, 1);   // a new character of that name connects
        CharacterSaves.Write(_character, new CharacterSaves.Save([]));
        Assert.Null(CharacterSaves.Load(_character)!.Experience);
    }

    [Fact]
    public void Flask_charges_from_kills_wait_in_memory_and_are_saved_by_the_next_save()
    {
        var items = PlayerInventories.For(_character, new ItemCatalog(Path.Combine(Path.GetTempPath(), "none")));
        var flask = items.Add(PlayerInventories.Flask, 0, 0, new Item(0xf1a5c0, ["Base"], 0x1) { Charges = 0 });

        items.ChangeUnsaved(PlayerInventories.Flask, flask.Id, i => i with { Charges = 30 });
        int SavedCharges() => CharacterSaves.Load(_character)!.Inventories[PlayerInventories.Flask].Single().Item.Charges;
        Assert.Equal(0, SavedCharges());

        PlayerInventories.SaveAllUnsaved();
        Assert.Equal(30, SavedCharges());

        // Or with the next change that is saved.
        items.ChangeUnsaved(PlayerInventories.Flask, flask.Id, i => i with { Charges = 45 });
        items.Add(PlayerInventories.MainInventory, 0, 0, new Item(0xd9b1a12f, ["Base"], 0x40a1));
        Assert.Equal(45, SavedCharges());
    }

    [Fact]
    public void Experience_from_many_kills_at_once_all_counts_and_saves_once()
    {
        CharacterSaves.Write(_character, new CharacterSaves.Save([], Experience: 0));

        Parallel.For(0, 500, _ => CharacterSaves.AddExperience(_character, 0, xp => xp + 2));

        Assert.Equal(0u, SavedExperience());
        CharacterSaves.SaveExperience(_character);
        Assert.Equal(1000u, SavedExperience());
    }
}
