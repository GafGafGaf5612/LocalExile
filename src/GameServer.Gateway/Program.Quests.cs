using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    // Sets or clears quest flags for the character (saved at once) and sends the player's flag set again.
    static async Task SetQuestFlags(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IEnumerable<string> flags, bool on = true)
    {
        var changed = new List<string>();
        lock (conn.Monsters)
            foreach (string flag in flags)
                if (on ? conn.QuestFlags.Add(flag) : conn.QuestFlags.Remove(flag)) changed.Add(flag);
        if (changed.Count == 0) return;
        // Only the changed flags: a flag another connection of the character set meanwhile stays in the save.
        CharacterSaves.ChangeQuestFlags(CharacterName(conn), changed, on);
        foreach (string flag in changed) Console.WriteLine($"[gateway] {remote} {CharacterName(conn)}: quest flag {flag} {(on ? "set" : "cleared")}");
        if (!conn.PlayerSent) return;
        var all = PlayerQuestFlags(conn);
        await Send(remote, stream, ciphers, InstanceEntities.PlayerQuestFlagsUpdate(PlayerId, all), $"0x02ba player quest flags: {all.Count}");
        // The exits these flags open appear (or go again when a flag is cleared).
        foreach (var (id, transition) in conn.Layout.AreaTransitions)
        {
            if (TransitionFlag(conn, transition) is not { } flag || !changed.Contains(flag)) continue;
            if (on)
            {
                await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(transition.Type), transition.Blob()),
                    $"0x02b9 create area transition 0x{id:x} {transition} (quest flag {flag})");
                await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
            }
            else await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove area transition 0x{id:x} (quest flag {flag} cleared)");
        }
        // The room objects these flags take away (a1q1: the force wall when Hillock dies).
        if (!on) return;
        List<uint> gone;
        lock (conn.Monsters)
        {
            gone = [.. conn.QuestRoomObjects.Where(o => changed.Contains(o.Value.Flag)).Select(o => o.Key)];
            foreach (uint id in gone) conn.QuestRoomObjects.Remove(id);
            conn.UpdateBarrier();
        }
        foreach (uint id in gone)
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove room object 0x{id:x} (quest flag set)");
    }

    // The rooms' server objects a quest manages (Quests.RoomObjectRemovedBy), where the room fit places them, while the
    // character lacks the flag that takes them away: The Twilight Strand's force wall (Tutorial_Blocker_3) blocks the way to
    // the gate until Hillock is dead (12-quests.md §6). Ids from QuestRoomObjectFirstId.
    static async Task SpawnQuestRoomObjects(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var notes = new List<string>();
        var objects = AreaPopulation.LoadObjects(conn.Layout.Id, conn.Seed, notes)
            .Select(o => (Object: o, Flag: Quests.RoomObjectRemovedBy(conn.Layout.Id, o.Type)))
            .Where(o => o.Flag is not null).ToList();
        if (objects.Count == 0) return;
        // The walls guard the exits their flag opens; the boss's side is the room's "mapboss" point (else the arrival).
        var boss = AreaPopulation.LoadPoints(conn.Layout.Id, conn.Seed, []).FirstOrDefault(p => p.Name == "mapboss");
        var from = conn.Layout.Near(boss is null ? (conn.Layout.SpawnX, conn.Layout.SpawnY) : (boss.X, boss.Y));
        uint id = QuestRoomObjectFirstId;
        foreach (var (o, flag) in objects)
        {
            if (conn.QuestFlags.Contains(flag!)) continue;
            var blocker = new BlockerObject(o.X, o.Y, o.Angle, o.Scale);
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(o.Type), blocker.Blob()),
                $"0x02b9 create room object 0x{id:x} {o.Type} at ({o.X}, {o.Y}), angle {o.Angle:0.###}, scale {o.Scale:0.##}, until quest flag {flag}");
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
            // The server's own wall: the walkable cells across the way (QuestBarriers).
            var guarded = conn.Layout.AreaTransitions.Where(t => TransitionFlag(conn, t.Transition) == flag)
                .Select(t => conn.Layout.Near((t.Transition.GridX, t.Transition.GridY))).ToList();
            var cells = conn.Layout.Walkable is { } walkable
                ? QuestBarriers.Cells(walkable, conn.Layout.GridW, conn.Layout.GridH, (o.X, o.Y), o.Angle, from, guarded)
                : [];
            Console.WriteLine($"[gateway] {remote} room object 0x{id:x}: {cells.Count} walkable cell(s) closed on the server between ({from.X}, {from.Y}) " +
                $"and {string.Join(", ", guarded)}" + (cells.Count == 0 ? "" : $", from {cells.Min()} to {cells.Max()}"));
            lock (conn.Monsters)
            {
                conn.QuestRoomObjects[id] = (flag!, cells);
                conn.UpdateBarrier();
            }
            id++;
        }
    }

    // The quest flag an area transition needs (Quests.FlagToEnter), or null. Such an exit is created only once the character
    // has the flag: The Twilight Strand's gate into Lioneye's Watch appears when Hillock dies (a1q1, 12-quests.md §6).
    static string? TransitionFlag(ClientState conn, AreaTransitionObject transition) =>
        AreaTransitionCatalog.IdOf(transition.DestinationHash16) is { } dest ? Quests.FlagToEnter(conn.Layout.Id, dest) : null;

    static bool TransitionShown(ClientState conn, AreaTransitionObject transition) =>
        TransitionFlag(conn, transition) is not { } flag || conn.QuestFlags.Contains(flag);
}
