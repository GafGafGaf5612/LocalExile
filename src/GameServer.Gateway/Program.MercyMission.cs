using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

// a1q5 "Mercy Mission" (MercyMission, 12-quests.md §7): Hailrake's Medicine Chest, its quest flag, and Nessa taking it.
partial class Program
{
    // A quest flag as the client has it: the character's own, or one the player's flag set carries for the World panel.
    // (PR #229 adds a shared ClientHasQuestFlag for the same check; this is the minimal local version.)
    static bool MercyClientHas(ClientState conn, string flag) => Quests.Names(PlayerQuestFlags(conn)).Contains(flag);

    static bool MercyDone(ClientState conn) => MercyMission.Done(f => MercyClientHas(conn, f));

    static ItemCatalog.BaseType? MedicineChestType() => Items.Find(MercyMission.MedicineChest) is { Supported: true } type ? type : null;

    static bool IsMedicineChest(Item item) => Items.ByHash(item.TypeHash)?.Id == MercyMission.MedicineChest;

    // Hailrake died on The Tidal Island: the Medicine Chest where he fell (the item's drop_monsters), unless Nessa has it
    // already or a copy is carried, in the stash or on this instance's ground (MercyMission.HailrakeDrops).
    static async Task DropQuestItems(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string varietyId, (int X, int Y) at)
    {
        if (conn.Layout.Id != MercyMission.Area || varietyId != MercyMission.Hailrake) return;
        if (MedicineChestType() is not { } type)
        {
            Console.WriteLine($"[gateway] {remote} a1q5: {MercyMission.MedicineChest} is not in the item data");
            return;
        }
        if (!MercyMission.HailrakeDrops(type.Hash, MercyDone(conn), ItemsOf(conn), PersonalStashOf(conn).CountOf, conn.Instance.Ground))
        {
            Console.WriteLine($"[gateway] {remote} Hailrake: no {type.Name}, the character has one or Nessa has it");
            return;
        }
        var (x, y) = conn.Layout.Walkable is { } walks && !walks(at.X, at.Y) ? conn.Layout.Near(at) : at;
        var ground = new WorldItemObject(x, y, ItemCatalog.Create(type));
        uint id = conn.Instance.Ground.Add(ground);
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), ground.Blob()),
            $"0x02b9 {type.Name} 0x{id:x} dropped by Hailrake at ({x}, {y})");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
    }

    // C→S 0x24 on a ground item: the Medicine Chest is picked up as any item, then its flag follows (SyncMercyMission).
    // Once Nessa has a chest, another one (left on an instance's ground) goes unpicked: the .it remove_flag. False when the
    // item is not a Medicine Chest.
    static async Task<bool> PickUpQuestItem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, WorldItemObject ground,
        InstanceTransfer.UseSkillOnObject use)
    {
        if (!IsMedicineChest(ground.Item)) return false;
        if (MercyDone(conn))
        {
            if (conn.Instance.Ground.Take(id) is not null)
                await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove Medicine Chest 0x{id:x}: Nessa has one");
            return true;
        }
        await PickUpGroundItem(remote, stream, ciphers, conn, id, ground, use);
        await SyncMercyMission(remote, stream, ciphers, conn);
        return true;
    }

    // HaveMedicineChest follows the chest the character carries (MercyMission.Sync), whatever took it away (a drop, a sale,
    // the stash); once Nessa has one, a chest still carried goes (the .it remove_flag).
    static async Task SyncMercyMission(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (MedicineChestType() is not { } type) return;
        var items = ItemsOf(conn);
        bool done = MercyDone(conn);
        bool carried = MercyMission.Carried(items).Any(c => c.Item.Item.TypeHash == type.Hash);
        var (set, remove) = MercyMission.Sync(conn.QuestFlags.Contains(MercyMission.HaveMedicineChest), done, carried);
        if (remove)
            foreach (var row in MercyMission.RemoveAll(items, type.Hash).GroupBy(r => r.Row))
                await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row.Key), items.NextRevision(row.Key),
                    [.. row.Select(r => r.Item.Id)], []), $"0x00bf inventory {PlayerInventories.InventoryId(row.Key)}: Medicine Chest removed, Nessa has one");
        if (set is bool on) await SetQuestFlags(remote, stream, ciphers, conn, [MercyMission.HaveMedicineChest], on);
    }

    // The sync about once a second while the player is in this area (started with the area, like the player tick).
    static async Task RunMercyMission(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        try
        {
            while (!conn.Closed)
            {
                if (conn.PlayerSent) await SyncMercyMission(remote, stream, ciphers, conn);
                await Task.Delay(1000);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The connection closed while sending.
        }
    }

    // C→S 0x82 on an NPCTalk row that sets NessaCongratulated (Nessa's "The Medicine Chest"): she takes the chest with the
    // flag (MercyMission.HandIn, one save). Without a chest the talk does not happen: the option list comes back. True
    // when the talk may go on.
    static async Task<bool> HandInQuestItems(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, NpcCatalog.Talk talk)
    {
        if (talk.StartFlag != MercyMission.NessaCongratulated || MercyClientHas(conn, MercyMission.NessaCongratulated)) return true;
        var items = ItemsOf(conn);
        if (MedicineChestType() is not { } type || MercyMission.HandIn(CharacterName(conn), items, type.Hash) is not var (row, chest))
        {
            Console.WriteLine($"[gateway] {remote} Nessa: no Medicine Chest carried");
            await SyncMercyMission(remote, stream, ciphers, conn);
            await Send(remote, stream, ciphers, InstanceNpcs.Talk(conn.TalkingTo), $"0x0084 npc 0x{conn.TalkingTo:x}: options again (no Medicine Chest)");
            return false;
        }
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), [chest.Id], []),
            $"0x00bf inventory {PlayerInventories.InventoryId(row)}: Medicine Chest {chest.Id} given to Nessa");
        await SetQuestFlags(remote, stream, ciphers, conn, [MercyMission.NessaCongratulated]);
        await SetQuestFlags(remote, stream, ciphers, conn, [MercyMission.HaveMedicineChest], on: false);
        return true;
    }
}
