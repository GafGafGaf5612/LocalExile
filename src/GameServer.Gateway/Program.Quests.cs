using System.Text.Json;
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
        // a1q4: the glyphs placed (or the dev flag set) open the Submerged Passage.
        if (changed.Contains(GlyphQuest.PlacedGlyphs)) await ChangeSubmergedPassage(remote, stream, ciphers, conn, on);
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
    static async Task SpawnOpeningTutorial(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (conn.Layout.Id != TutorialObjects.Strand || CharacterSaves.Load(CharacterName(conn))?.Tutorial is not { } tutorial) return;
        var objects = AreaPopulation.LoadObjects(conn.Layout.Id, conn.Seed, []);
        var exile = objects.FirstOrDefault(o => o.Type == TutorialObjects.Exile);
        conn.TutorialZombiePlacement = objects.FirstOrDefault(o => o.Type == TutorialObjects.Spawner);
        if (exile is not null && Npcs.Value.Find(TutorialObjects.Exile) is { Supported: true } npc)
            await SpawnNpc(remote, stream, ciphers, conn, npc with { TransitionState = (byte)(tutorial.Talked ? 2 : 1) }, exile.X, exile.Y, exile.Angle);
        if (conn.TutorialZombiePlacement is { } spawn)
        {
            if (tutorial.ZombieKilled)
                await DropTutorialGem(remote, stream, ciphers, conn, false, spawn.X, spawn.Y);
            else if (tutorial.Talked)
                await SpawnTutorialZombie(remote, stream, ciphers, conn);
            else
            {
                conn.TutorialSceneId = conn.NextObjectId++;
                await Send(remote, stream, ciphers, InstanceEntities.CreateObject(conn.TutorialSceneId, InstanceEntities.ObjectTypeHash(TutorialObjects.Spawner),
                    TutorialObjects.SceneBlob(spawn.X,spawn.Y,spawn.Angle)), "0x02b9 opening bite zombie scene");
                await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(conn.TutorialSceneId), null);
                await Send(remote, stream, ciphers, TutorialObjects.BiteAnimation(conn.TutorialSceneId,0), "0x01b2 opening zombie paused at bite start");
            }
        }
        // The recorder writes tile placements while the client generates the area. Wait briefly for this optional capture.
        _ = TrySpawnTutorialChest(remote, stream, ciphers, conn);
    }

    static async Task TrySpawnTutorialChest(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        try
        {
            for (int retry=0; retry<20 && !conn.Closed; retry++)
            {
                var named=AreaPopulation.LoadPoints(conn.Layout.Id,conn.Seed,[]).FirstOrDefault(p=>p.Name==TutorialObjects.Chest);
                if ((TutorialObjects.ChestPlacement(conn.Layout.Id,conn.Seed) ?? (named is null ? null : TutorialObjects.ChestAt(named))) is { } chest)
                {
                    conn.TutorialChest = chest; conn.TutorialChestId = conn.NextObjectId++;
                    bool opened = CharacterSaves.Load(CharacterName(conn))?.Tutorial?.ChestOpened == true;
                    await Send(remote,stream,ciphers,InstanceEntities.CreateObject(conn.TutorialChestId,InstanceEntities.ObjectTypeHash(chest.Type),
                        TutorialObjects.ChestBlob(chest,opened)), $"0x02b9 tutorial support chest at ({chest.X},{chest.Y}), opened={opened}");
                    await Send(remote,stream,ciphers,InstanceProtocol.EnterWakeZone(conn.TutorialChestId),null);
                    if (opened) await DropTutorialGem(remote,stream,ciphers,conn,true,chest.X,chest.Y);
                    return;
                }
                await Task.Delay(1000);
            }
            if (!conn.Closed) Console.WriteLine($"[gateway] {remote} tutorial chest has no captured placement for {conn.Layout.Id} seed {conn.Seed}; capture with -Walkmaps");
        }
        catch (Exception ex) when (ex is IOException or JsonException or ObjectDisposedException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] tutorial chest: {ex.Message}"); }
    }

    static async Task TalkToWoundedExile(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,uint id,SpawnedNpc npc)
    {
        if (CharacterSaves.Load(CharacterName(conn))?.Tutorial is not { Talked: false } || conn.TutorialSceneRunning) return;
        bool armed = ItemsOf(conn).Items(PlayerInventories.Weapon1).Count > 0;
        conn.InNpcTalk = true;
        await Send(remote,stream,ciphers,InstanceNpcs.Talk(id,armed ? (byte)2 : (byte)1),"0x0084 dying exile: class weapon check");
        if (!armed) return;
        conn.TutorialSceneRunning = true;
        CharacterSaves.ChangeTutorial(CharacterName(conn),t=>t with { Talked=true });
        await SetQuestFlags(remote,stream,ciphers,conn,["TalkedToWoundedExile"]);
        _ = RunOpeningScene(remote,stream,ciphers,conn,id,npc);
    }

    static async Task RunOpeningScene(string remote,NetworkStream stream,ConnectionCiphers ciphers,ClientState conn,uint id,SpawnedNpc npc)
    {
        try
        {
            double start=conn.Clock.Elapsed.TotalSeconds;
            // NPCTalk's actual script: triggerer.TalkToWoundedExile(8.2,22.8). Use the paused instance clock.
            async Task Until(double elapsed) { while (!conn.Closed && conn.Clock.Elapsed.TotalSeconds < start+elapsed) await Task.Delay(50); }
            await Until(8.2); if(conn.Closed) return;
            int component=npc.Npc.Components!.ToList().IndexOf("Transitionable");
            if(component>=0) await Send(remote,stream,ciphers,InstanceEntities.TransitionableUpdate(id,(byte)component,2),"0x02ba wounded exile bitten");
            if(conn.TutorialSceneId!=0) await Send(remote,stream,ciphers,TutorialObjects.BiteAnimation(conn.TutorialSceneId,1),"0x01b2 opening zombie bites exile");
            await Until(22.8); if(conn.Closed) return;
            if(conn.TutorialSceneId!=0) await Send(remote,stream,ciphers,InstanceProtocol.RemoveObject(conn.TutorialSceneId),null);
            if(conn.TalkingTo==id) { conn.InNpcTalk=false;conn.TalkingTo=0;await Send(remote,stream,ciphers,InstanceNpcs.Talk(0),null); }
            await SpawnTutorialZombie(remote,stream,ciphers,conn);
        }
        catch(Exception ex) when(ex is IOException or ObjectDisposedException)
        { if(!conn.Closed) Console.WriteLine($"[gateway] opening scene: {ex.Message}"); }
        finally { conn.TutorialSceneRunning=false; }
    }

    static async Task SpawnTutorialZombie(string remote,NetworkStream stream,ConnectionCiphers ciphers,ClientState conn)
    {
        if(Environment.GetEnvironmentVariable("Game_MONSTERS")=="0" || conn.TutorialZombiePlacement is not { } spawn) return;
        lock(conn.Monsters) if(conn.Monsters.Values.Any(m=>m.Variety?.Id==TutorialObjects.Zombie)) return;
        if(Monsters.Find(TutorialObjects.Zombie) is { Supported:true } zombie)
            await SpawnMonster(remote,stream,ciphers,conn,zombie,1,MonsterRarity.Normal,spawn.X,spawn.Y);
    }

    static async Task DropTutorialGem(string remote,NetworkStream stream,ConnectionCiphers ciphers,ClientState conn,bool support,int x,int y)
    {
        if(CharacterSaves.Load(CharacterName(conn))?.Tutorial is not { } tutorial || (support ? tutorial.SupportClaimed : tutorial.SkillClaimed)) return;
        var item=CharacterSaves.FromSaved(support?tutorial.SupportGem:tutorial.SkillGem,Items);
        var ground=new WorldItemObject(x,y,item);
        if(conn.Instance.PlaceTutorialItem(TutorialObjects.DropKey(CharacterName(conn),tutorial,support),ground) is not uint id) return;
        await Send(remote,stream,ciphers,InstanceEntities.CreateObject(id,InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath),ground.Blob()),
            $"0x02b9 tutorial {(support?"support":"skill")} gem {Items.GemName(item)} at ({x},{y})");
        await Send(remote,stream,ciphers,InstanceProtocol.EnterWakeZone(id),null);
    }

    static async Task OpenTutorialChest(string remote,NetworkStream stream,ConnectionCiphers ciphers,ClientState conn,InstanceTransfer.UseSkillOnObject use,TutorialObjects.Placement chest)
    {
        if(CharacterSaves.Load(CharacterName(conn))?.Tutorial is not { ChestOpened:false }) return;
        int action=Interlocked.Increment(ref conn.ActionCount);
        double now=conn.Clock.Elapsed.TotalSeconds;
        var (here,stop,arrive)=conn.Walk.WalkUpTo(chest.X,chest.Y,1,now);
        await Send(remote,stream,ciphers,InstanceProtocol.ActionStarted(PlayerId,here.X,here.Y,chest.X,chest.Y,use.SkillId,use.Sequence,use.Flags,targetObjectId:use.TargetId),null);
        if(arrive>now) await Task.Delay(TimeSpan.FromSeconds(arrive-now));
        if(conn.ActionCount!=action || conn.Closed) return;
        if(!GameServer.Core.World.PlayerWalk.InWalkUpRange(stop,(chest.X,chest.Y),1))
        {
            await Send(remote,stream,ciphers,InstanceProtocol.ActionEnded(PlayerId),null);
            return;
        }
        CharacterSaves.ChangeTutorial(CharacterName(conn),t=>t with { ChestOpened=true });
        await Send(remote,stream,ciphers,InstanceProtocol.RemoveObject(use.TargetId),null);
        await Send(remote,stream,ciphers,InstanceEntities.CreateObject(use.TargetId,InstanceEntities.ObjectTypeHash(chest.Type),TutorialObjects.ChestBlob(chest,true)),"0x02b9 opened support gem chest");
        await Send(remote,stream,ciphers,InstanceProtocol.EnterWakeZone(use.TargetId),null);
        await DropTutorialGem(remote,stream,ciphers,conn,true,chest.X,chest.Y);
        await Send(remote,stream,ciphers,InstanceProtocol.ActionEnded(PlayerId),null);
    }

    // Ids of the invisible SunkenPassageBlockers on the server's walk estimate (blockers only: the client spawns them).
    const uint PassageBlockerFirstId = 0xff000000;

    // a1q4 "Breaking Some Eggs" (GlyphQuest, 12-quests.md §2–4): The Mud Flats' three Rhoa Nests, the Glyph Wall and the
    // waterfall where the recorder captured them, and the client's invisible blockers on the server's walk estimate until
    // PlacedGlyphs. The recorder writes the capture while the client generates the area: wait briefly for it.
    static async Task SpawnGlyphQuest(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (conn.Layout.Id != GlyphQuest.Area) return;
        // Usually captured on an earlier visit: created at once, with the area. Else wait for the recorder in the background.
        if (LoadGlyphLayout(remote, conn) is { } now) { await CreateGlyphObjects(remote, stream, ciphers, conn, now); return; }
        _ = Task.Run(async () =>
        {
            for (int retry = 0; retry < 20 && !conn.Closed; retry++)
            {
                await Task.Delay(1000);
                if (LoadGlyphLayout(remote, conn) is { } later) { await CreateGlyphObjects(remote, stream, ciphers, conn, later); return; }
            }
            if (!conn.Closed) Console.WriteLine($"[gateway] {remote} a1q4: no captured Rhoa Nests or Glyph Wall for {conn.Layout.Id} seed {conn.Seed}; capture with -Walkmaps");
        });
    }

    static GlyphQuest.Layout? LoadGlyphLayout(string remote, ClientState conn)
    {
        try { return GlyphQuest.Load(conn.Layout.Id, conn.Seed); }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            Console.WriteLine($"[gateway] {remote} a1q4 capture: {ex.Message}");
            return null;
        }
    }

    static async Task CreateGlyphObjects(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, GlyphQuest.Layout layout)
    {
        try
        {
            conn.Glyphs = layout;
            bool placed = conn.QuestFlags.Contains(GlyphQuest.PlacedGlyphs);
            if (!placed) SetPassageBlockers(conn, true);
            foreach (var nest in layout.Nests)
            {
                uint id = Interlocked.Increment(ref conn.NextObjectId) - 1;
                bool opened = conn.Instance.ChestOpened(nest.At.X, nest.At.Y);
                lock (conn.GlyphNests) conn.GlyphNests[id] = nest;
                conn.SetBlocker(id, nest.At.X, nest.At.Y, GlyphQuest.NestSize);
                await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(nest.Type), GlyphQuest.NestBlob(nest.At, opened)),
                    $"0x02b9 Rhoa Nest 0x{id:x} {nest.Type} at ({nest.At.X}, {nest.At.Y}) with glyph {nest.Glyph + 1}, opened={opened}");
                await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
            }
            if (layout.Wall is { } wall)
            {
                uint id = Interlocked.Increment(ref conn.NextObjectId) - 1;
                await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(GlyphQuest.Wall), GlyphQuest.WallBlob(wall, placed)),
                    $"0x02b9 Glyph Wall 0x{id:x} at ({wall.X}, {wall.Y}), {(placed ? "glyphs placed" : "waiting for the glyphs")}");
                await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
                conn.GlyphWallId = id;
            }
            if (layout.Water is { } water)
            {
                uint id = Interlocked.Increment(ref conn.NextObjectId) - 1;
                await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(GlyphQuest.Water), GlyphQuest.WaterBlob(water.X, water.Y, water.Rotation, placed)),
                    $"0x02b9 waterfall 0x{id:x} at ({water.X}, {water.Y}), transitioned={placed}");
                await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
                conn.GlyphWaterId = id;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or ObjectDisposedException or KeyNotFoundException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] a1q4 objects: {ex.Message}"); }
    }

    static void SetPassageBlockers(ClientState conn, bool on)
    {
        if (conn.Glyphs is not { } layout) return;
        for (int i = 0; i < layout.Blockers.Count; i++)
            if (on) conn.SetBlocker(PassageBlockerFirstId + (uint)i, layout.Blockers[i].X, layout.Blockers[i].Y, GlyphQuest.BlockerSize);
            else conn.RemoveBlocker(PassageBlockerFirstId + (uint)i);
    }

    // PlacedGlyphs set: the wall and the waterfall play their transition (0x2ba to Transitionable), and the invisible blockers
    // leave the server's walk estimate (the client drops its own when it gets the flag). Cleared (dev): the other way.
    static async Task ChangeSubmergedPassage(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, bool open)
    {
        if (conn.Glyphs is null) return;
        SetPassageBlockers(conn, !open);
        byte state = open ? GlyphQuest.Transitioned : GlyphQuest.Untransitioned;
        foreach (var (id, what) in new[] { (conn.GlyphWallId, "Glyph Wall"), (conn.GlyphWaterId, "waterfall") })
            if (id != 0)
                await Send(remote, stream, ciphers, InstanceEntities.TransitionableUpdate(id, GlyphQuest.TransitionableIndex, state),
                    $"0x02ba {what} 0x{id:x} Transitionable → {state}");
    }

    // C→S 0x24 on a Rhoa Nest: walk up, open it (once per instance) and drop its glyph, unless the character has it already.
    static async Task OpenRhoaNest(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceTransfer.UseSkillOnObject use, GlyphQuest.Nest nest)
    {
        var at = nest.At;
        if (conn.Instance.ChestOpened(at.X, at.Y)) return;
        int action = Interlocked.Increment(ref conn.ActionCount);
        double now = conn.Clock.Elapsed.TotalSeconds;
        var (here, stop, arrive) = conn.Walk.WalkUpTo(at.X, at.Y, GlyphQuest.NestSize, now);
        await Send(remote, stream, ciphers, InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, at.X, at.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
            $"0x01ab player opens Rhoa Nest 0x{use.TargetId:x} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (conn.ActionCount != action || conn.Closed) return;
        if (!GameServer.Core.World.PlayerWalk.InWalkUpRange(stop, (at.X, at.Y), GlyphQuest.NestSize) || !conn.Instance.OpenChest(at.X, at.Y))
        {
            await Send(remote, stream, ciphers, InstanceProtocol.ActionEnded(PlayerId), null);
            return;
        }
        // Chest has no update reader: the opened nest replaces the closed one, under a new id (the same id again made the
        // client log "Replacing awake object", live 2026-10-25).
        uint opened = Interlocked.Increment(ref conn.NextObjectId) - 1;
        lock (conn.GlyphNests) { conn.GlyphNests.Remove(use.TargetId); conn.GlyphNests[opened] = nest; }
        conn.RemoveBlocker(use.TargetId);
        conn.SetBlocker(opened, at.X, at.Y, GlyphQuest.NestSize);
        await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(use.TargetId), null);
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(opened, InstanceEntities.ObjectTypeHash(nest.Type), GlyphQuest.NestBlob(at, true)),
            $"0x02b9 opened Rhoa Nest 0x{opened:x} (was 0x{use.TargetId:x})");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(opened), null);
        await DropGlyph(remote, stream, ciphers, conn, nest);
        await Send(remote, stream, ciphers, InstanceProtocol.ActionEnded(PlayerId), null);
    }

    // A nest's glyph, on the ground of the instance, while the character still needs it: no PlacedGlyphs or HaveGlyphN, none
    // held and none lying here already.
    static async Task DropGlyph(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, GlyphQuest.Nest nest)
    {
        int g = nest.Glyph;
        if (conn.QuestFlags.Contains(GlyphQuest.PlacedGlyphs) || conn.QuestFlags.Contains(GlyphQuest.HaveGlyph[g])) return;
        if (Items.Find(GlyphQuest.Glyphs[g]) is not { Supported: true } type)
        {
            Console.WriteLine($"[gateway] {remote} a1q4: {GlyphQuest.Glyphs[g]} is not in the item data");
            return;
        }
        var items = ItemsOf(conn);
        if (conn.Instance.Ground.All().Any(e => e.Item.Item.TypeHash == type.Hash)
            || new[] { PlayerInventories.MainInventory, PlayerInventories.Cursor }.Any(r => items.Items(r).Any(i => i.Item.TypeHash == type.Hash)))
            return;
        var ground = new WorldItemObject(nest.At.X, nest.At.Y, ItemCatalog.Create(type));
        uint id = conn.Instance.Ground.Add(ground);
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), ground.Blob()),
            $"0x02b9 {type.Name} 0x{id:x} from the Rhoa Nest at ({nest.At.X}, {nest.At.Y})");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
    }

    // C→S 0x24 on the Glyph Wall: walk up; seeing it sets SeenGlyphWall; with all three glyphs it takes them and sets
    // PlacedGlyphs (SetQuestFlags then opens the passage).
    static async Task UseGlyphWall(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceTransfer.UseSkillOnObject use)
    {
        if (conn.Glyphs?.Wall is not { } wall) return;
        int action = Interlocked.Increment(ref conn.ActionCount);
        double now = conn.Clock.Elapsed.TotalSeconds;
        var (here, stop, arrive) = conn.Walk.WalkUpTo(wall.X, wall.Y, GlyphQuest.WallSize, now);
        await Send(remote, stream, ciphers, InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, wall.X, wall.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
            $"0x01ab player uses the Glyph Wall from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (conn.ActionCount != action || conn.Closed) return;
        if (GameServer.Core.World.PlayerWalk.InWalkUpRange(stop, (wall.X, wall.Y), GlyphQuest.WallSize))
        {
            await SetQuestFlags(remote, stream, ciphers, conn, [GlyphQuest.SeenGlyphWall]);
            if (!conn.QuestFlags.Contains(GlyphQuest.PlacedGlyphs))
                await PlaceGlyphs(remote, stream, ciphers, conn);
        }
        else Console.WriteLine($"[gateway] {remote} Glyph Wall out of reach from ({stop.X}, {stop.Y})");
        await Send(remote, stream, ciphers, InstanceProtocol.ActionEnded(PlayerId), null);
    }

    static async Task PlaceGlyphs(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var items = ItemsOf(conn);
        var removed = GlyphQuest.Place(CharacterName(conn), items, i => GlyphQuest.GlyphOf(Items.ByHash(i.TypeHash)?.Id));
        if (removed is null)
        {
            Console.WriteLine($"[gateway] {remote} Glyph Wall: needs all three glyphs");
            return;
        }
        foreach (var row in removed.GroupBy(r => r.Row))
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row.Key), items.NextRevision(row.Key), [.. row.Select(r => r.Item.Id)], []),
                $"0x00bf inventory {PlayerInventories.InventoryId(row.Key)}: glyphs {string.Join(", ", row.Select(r => r.Item.Id))} placed in the Glyph Wall");
        await SetQuestFlags(remote, stream, ciphers, conn, [GlyphQuest.PlacedGlyphs]);
        await SetQuestFlags(remote, stream, ciphers, conn, GlyphQuest.HaveGlyph, on: false);
    }

}
