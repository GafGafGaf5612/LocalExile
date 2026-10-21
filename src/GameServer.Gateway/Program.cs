using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using WalkEstimator = GameServer.Core.World.WalkEstimator;
using Progression = GameServer.Core.World.Progression;
using DamageModel = GameServer.Core.World.DamageModel;
using DamageType = GameServer.Core.World.DamageType;
using SaveFiles = GameServer.Core.Saves.SaveFiles;
using DamagePacket = GameServer.Core.World.DamagePacket;
using DamageRange = GameServer.Core.World.DamageRange;
using DamageOverTime = GameServer.Core.World.DamageOverTime;

// GameServer.Gateway — the instance (game) server the client connects to after login 0x14, or after 0x27 on an area
// change (Agents/docs/protocol/02, 09).
// Reads the plaintext opening (0x0F ticket + 0x0E hello), asks GameServer.Auth which area/instance the ticket belongs to
// (InstanceControl "resolve"), answers 0x11 so both directions switch to Salsa20 keyed by the ticket's instance key,
// sends the area (0x1d), answers the client's 0x7d with the player and the area's area transitions, and handles ticks,
// movement and area transition clicks (0x24: change area, or with Ctrl the instance chooser; Agents/docs/protocol/09-area-change.md).
// Port: Game_GATEWAY_PORT (default 6112). Instance keys: data/instance-secret.bin (Game_INSTANCE_SECRET), shared with
// GameServer.Auth. Auth control: 127.0.0.1:Game_AUTH_CONTROL_PORT (20495); without it every ticket gets Lioneye's Watch.

int port = int.TryParse(Environment.GetEnvironmentVariable("Game_GATEWAY_PORT"), out var p) ? p : 6112;
var keyring = InstanceKeyring.LoadOrCreate(Environment.GetEnvironmentVariable("Game_INSTANCE_SECRET") ?? Path.Combine("data", "instance-secret.bin"));

// The HNC side channel needs a 64-byte table from the client; read it from the local client instead of shipping it.
string clientExe = Environment.GetEnvironmentVariable("Game_CLIENT_EXE") ?? FindUp(Path.Combine("Client", "Game.exe")) ?? "Client/Game.exe";
if (!File.Exists(clientExe))
{
    Console.Error.WriteLine($"[gateway] client executable not found ({clientExe}); set Game_CLIENT_EXE");
    return 1;
}
HncSender.LoadMask(File.ReadAllBytes(clientExe));
if (GameDataFiles.Warning() is { } missingData) Console.WriteLine(missingData);

// Characters and stashes: the save database (Saves). Saves from before it (data/saves, data/stashes) go into it once; one
// that can't be read stops the Gateway, so no character starts over.
try
{
    Saves.Open(Saves.DefaultPath);
    Saves.ImportJson(Saves.Db, "data", CharacterSaves.Opts);
    Console.WriteLine($"[gateway] saves: {Saves.Db.Path}");
}
catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException)
{
    Console.Error.WriteLine($"[gateway] the save database can't be opened: {e.Message}");
    return 1;
}

// Every item sent carries its level requirement (13-items.md §2d): the client shows and checks the byte it receives.
// Gems have their own (GrantedEffectsPerLevel), which the client reads from the SkillGem component instead. Maps have
// none, as in the game: their drop level is where they drop, not who may run them.
Item.LevelRequirementOf = item => !item.Components.Contains("SkillGem") && Items.ByHash(item.TypeHash) is { Class: not MapDevice.MapClass } t
    ? ItemModStats.LevelRequirement(item, t.DropLevel, Items.Mods) : 0;

// Local play only by default; Game_BIND=0.0.0.0 to accept other machines (then also set Game_GATEWAY_HOST).
var bind = IPAddress.Parse(Environment.GetEnvironmentVariable("Game_BIND") ?? "127.0.0.1");
var listener = new TcpListener(bind, port);
// Before any client connects: Auth forgets the players of this Gateway's last run, which may have stopped without
// sending "leave" for them (their instances would never expire).
var started = await InstanceControl.CallAsync(new ControlRequest { Op = "gateway_started" });
if (!started.Ok) Console.WriteLine($"[gateway] Auth did not take gateway_started: {started.Error}");
listener.Start();
Console.WriteLine($"[gateway] listening on {bind}:{port}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
// What waits in memory to be saved (experience, flask charges from kills) is saved every few seconds, and at the end.
var saving = SaveUnsavedProgress(cts.Token);
AppDomain.CurrentDomain.ProcessExit += (_, _) => SaveUnsavedProgressNow("the Gateway stops");

while (!cts.IsCancellationRequested)
{
    TcpClient client;
    try
    {
        client = await listener.AcceptTcpClientAsync(cts.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }

    _ = HandleAsync(client, keyring);
}

listener.Stop();
await saving;
SaveUnsavedProgressNow("the Gateway stopped");
Console.WriteLine("[gateway] stopped");
return 0;

// Experience and flask charges from kills wait in memory (CharacterSaves.AddExperience, PlayerInventories.ChangeUnsaved)
// and are saved here at least every few seconds: a crash or a power cut loses at most those seconds of them.
static async Task SaveUnsavedProgress(CancellationToken stop)
{
    using var timer = new PeriodicTimer(CharacterSaves.ExperienceSaveInterval);
    try
    {
        while (await timer.WaitForNextTickAsync(stop)) SaveUnsavedProgressNow(null);
    }
    catch (OperationCanceledException) { }
}

static void SaveUnsavedProgressNow(string? why)
{
    try
    {
        CharacterSaves.SaveExperience();
        PlayerInventories.SaveAllUnsaved();
        if (why is not null) Console.WriteLine($"[gateway] unsaved progress saved ({why})");
    }
    catch (Exception e)
    {
        // Kept in memory: the next save tries again.
        Console.WriteLine($"[gateway] unsaved progress not saved{(why is null ? "" : $" ({why})")}: {e.GetType().Name}: {e.Message}");
    }
}

static string? FindUp(string relative)
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
    return null;
}

static async Task HandleAsync(TcpClient client, InstanceKeyring keyring)
{
    string remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
    // Nagle off: it would hold a small 0x1c back until the previous write is acknowledged (up to ~200 ms, issue #46).
    client.NoDelay = true;
    Console.WriteLine($"[gateway] CONNECT {remote} at {DateTime.Now:HH:mm:ss.fff}");
    long total = 0;
    uint ticket = 0;
    ClientState? left = null;
    try
    {
        using (client)
        using (var stream = client.GetStream())
        {
            var buffer = new byte[8192];
            var pending = new List<byte>();
            InstanceOpening? opening = null;
            while (opening is null)
            {
                int n = await stream.ReadAsync(buffer);
                if (n == 0) return;
                total += n;
                pending.AddRange(buffer.AsSpan(0, n));
                opening = InstanceProtocol.TryReadOpening([.. pending]);
            }
            ticket = opening.Ticket;
            Console.WriteLine($"[gateway] {remote} opening: ticket=0x{opening.Ticket:x8} digest={Convert.ToHexString(opening.Digest)} " +
                              $"address=0x{opening.Address:x8} u32={opening.U32_128} realm=0x{opening.Realm:x2} flag={opening.Flag}");

            // Which area and instance this ticket was issued for (Auth's instance registry).
            var resolved = await InstanceControl.CallAsync(new ControlRequest { Op = "resolve", Ticket = ticket });
            if (!resolved.Ok || string.IsNullOrEmpty(resolved.Character))
            {
                // Without the character, its progress would go to another save: refuse instead.
                Console.WriteLine($"[gateway] {remote} resolve failed ({resolved.Error ?? "no character"}); connection refused");
                return;
            }
            ushort areaHash = resolved.Area;
            uint seed = resolved.Seed;
            ulong instanceId = resolved.Instance;
            var layout = AreaLayouts.For(areaHash, seed);
            AreaLayouts.Announce(layout, seed);
            Console.WriteLine($"[gateway] {remote} ticket 0x{ticket:x8}: {resolved.Character ?? "?"} → instance {instanceId}, {layout.Describe()}");

            ConnectionCiphers ciphers = keyring.Ciphers(opening.Ticket);
            await stream.WriteAsync(InstanceProtocol.StartEncryption());
            Console.WriteLine($"[gateway] {remote} => 0x0011 (start encryption)");

            // Text is the character's league: the map overlay shows it under the area name ("{0} League", 0x1406b2030).
            var area = new AreaInstance(InstanceId: (uint)instanceId, AreaHash: areaHash, Seed: seed, Level: 0, Text: resolved.League ?? "");
            await Send(remote, stream, ciphers, area.Encode(), $"0x001d {area}");

            // Anything after the opening was already encrypted by the client.
            var conn = new ClientState(layout, ticket, seed) { Character = resolved.Character, CosmeticPortalVariation = resolved.Character is { Length: > 0 } savedCharacter ? CharacterSaves.PortalCosmetic(savedCharacter) : (ushort)0, Account = resolved.Account, League = resolved.League, Hardcore = resolved.Hardcore, CameFrom = AreaLayouts.CameFrom.TryRemove(ticket, out var from) ? from : null, Instance = Instances.Enter(instanceId, resolved.Created), LastTown = resolved.LastTown, ClassRow = Skills.RowOfClass(resolved.Class), Pools = CharacterStats.For(Skills.ClassFor(Skills.RowOfClass(resolved.Class)), resolved.Level), Hnc = new HncSender(keyring.Key(opening.Ticket)), Keyring = keyring };
            left = conn;
            conn.Commands = Commands(remote, stream, ciphers, conn);
            // Waypoint travel arrives next to the waypoint; so does a login into a hideout (a Survivors character's start).
            conn.ArriveAtWaypoint = WaypointArrivals.TryRemove(ticket, out _) || (conn.CameFrom is null && HideoutCatalog.ByArea(layout.Hash) is not null);
            PlayerInventories.SetClass(CharacterName(conn), conn.ClassRow, conn.League);   // before its items are first used (start kit)
            // Load the items now: a new character's start kit is built only while it has no save, and the save is written
            // at 0x7d by the area visit (RecordVisit), before the player object first needs the items.
            var startItems = ItemsOf(conn);
            // Towns and hideouts refill the flasks, before the inventories go to the client.
            if ((WaypointCatalog.Areas.Find(layout.Hash) is { IsTown: true } || HideoutCatalog.Hideouts.Any(h => h.AreaHash16 == layout.Hash))
                && FillFlasks(startItems, int.MaxValue).Count > 0)
                Console.WriteLine($"[gateway] {remote} flasks refilled in town");
            // A sale still open when the last connection ended (a disconnect, a crash) ended as a cancel: every offered
            // item is in its cell (it never left it).
            if (startItems.CancelOffers().Count is > 0 and var cancelled)
                Console.WriteLine($"[gateway] {remote} {cancelled} item(s) offered in an unfinished sale are back in their cells");
            int level = LoadProgress(conn, resolved.Level);
            if (level != resolved.Level)
            {
                // The saved experience is the character's level: Auth's (the ticket's) was not updated at the last level-up.
                var fixedLevel = await InstanceControl.CallAsync(new ControlRequest { Op = "set_level", Ticket = ticket, Level = level });
                Console.WriteLine($"[gateway] {remote} {CharacterName(conn)}: level {level} from the saved experience (the account had {resolved.Level})" +
                    (fixedLevel.Ok ? "" : $"; not saved to the account: {fixedLevel.Error}"));
            }
            conn.PlayerLife = conn.PlayerMaxLife;
            conn.PlayerMana = conn.Pools.Mana;
            Console.WriteLine($"[gateway] {remote} {CharacterName(conn)}: {Skills.ClassFor(conn.ClassRow).ObjectType} (Characters row {conn.ClassRow}), level {conn.Pools.Level} (experience {conn.Experience}, {conn.Passives.Count} passive(s), life {conn.Pools.Life}, mana {conn.Pools.Mana}, attack speed +{conn.Pools.AttackSpeedPct}%), league {conn.League ?? "?"}");
            // The read loop answers ticks; everything else runs in order on the handler task (ClientInbox, issue #46).
            var inbox = new ClientInbox();
            using var reading = new CancellationTokenSource();
            var handlers = RunHandlers(remote, stream, ciphers, conn, inbox, reading);
            try
            {
                byte[] rest = [.. pending.Skip(InstanceProtocol.OpeningLength)];
                if (rest.Length > 0) await OnClientData(remote, stream, ciphers, rest, conn, inbox, Stopwatch.GetTimestamp());
                // Closed early by C→S 0x153 (exit to character selection) or a handler error: the handler task cancels
                // the read, and leaving the loop closes the connection.
                while (!conn.Closed)
                {
                    int n = await stream.ReadAsync(buffer, reading.Token);
                    if (n == 0) break;
                    total += n;
                    await OnClientData(remote, stream, ciphers, buffer.AsSpan(0, n).ToArray(), conn, inbox, Stopwatch.GetTimestamp());
                }
            }
            catch (OperationCanceledException) when (reading.IsCancellationRequested) { }
            finally
            {
                conn.Closed = true;
                inbox.Complete();
                await handlers;
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[gateway] {remote} error: {ex.GetType().Name}: {ex.Message}");
    }
    if (left is not null)
    {
        // The connection ends (a logout, an area change, a crash of the client): what waits in memory is saved now.
        try
        {
            CharacterSaves.SaveExperience(CharacterName(left));
            ItemsOf(left).SaveUnsaved();
        }
        catch (Exception e)
        {
            Console.WriteLine($"[gateway] {remote} unsaved progress not saved at disconnect (kept for the next save): {e.GetType().Name}: {e.Message}");
        }
        // The instance keeps its monsters as they are now, for the next connection (#12).
        if (left.MonstersPlaced)
            lock (left.Monsters) left.Instance.Monsters = SavedMonster.Save(left.Monsters.Values, left.Clock.Elapsed.TotalSeconds);
        Instances.Leave(left.Instance);
    }
    if (ticket != 0) await InstanceControl.CallAsync(new ControlRequest { Op = "leave", Ticket = ticket });
    Console.WriteLine($"[gateway] DISCONNECT {remote} total={total}");
}

// The read loop's part: decrypts one read, frames it (InstanceProtocol.SplitClientMessages), answers its 0x1b ticks with
// 0x1c at once and queues the rest for RunHandlers. readAt is when the read completed: a tick answered more than
// ClientInbox.SlowAfter later is logged.
static async Task OnClientData(string remote, NetworkStream stream, ConnectionCiphers ciphers, byte[] data, ClientState conn,
    ClientInbox inbox, long readAt)
{
    ciphers.Receive.Process(data);
    byte[] all = [.. conn.Carry, .. data];
    var messages = InstanceProtocol.SplitClientMessages(all, out conn.Carry, out byte[] unknown);
    foreach (var bytes in inbox.Post(messages, unknown))
    {
        if (conn.Ticks++ % 50 == 0) Console.WriteLine($"[gateway] {remote} <= 0x001b tick #{conn.Ticks}: {Convert.ToHexString(bytes)} (answered with 0x001c)");
        await Send(remote, stream, ciphers, InstanceProtocol.Pong(serverDelayMs: ClientInbox.ServerDelayMs, ackIndex: inbox.NextAckIndex(), sideChannel: conn.Hnc!.NextIdleU32()), null);
        var waited = Stopwatch.GetElapsedTime(readAt);
        if (waited > ClientInbox.SlowAfter)
            Console.WriteLine($"[gateway] {remote} slow tick #{conn.Ticks}: 0x001c sent {waited.TotalMilliseconds:0} ms after the 0x001b was read");
    }
}

// The connection's handler task: every message but the ticks, one read at a time and in order (ClientInbox). A handler
// that runs longer than ClientInbox.SlowAfter is logged. An error or 0x153 closes the connection by cancelling the read.
static async Task RunHandlers(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
    ClientInbox inbox, CancellationTokenSource reading)
{
    await Task.Yield();   // the read loop starts at once; the handlers run beside it
    try
    {
        await foreach (var batch in inbox.ReadAllAsync())
        {
            if (conn.Closed) break;
            long start = Stopwatch.GetTimestamp();
            await OnClientMessages(remote, stream, ciphers, batch.Messages, batch.Unknown, conn);
            var took = Stopwatch.GetElapsedTime(start);
            if (took > ClientInbox.SlowAfter)
            {
                var ids = batch.Messages.Select(m => $"0x{m.Id:x4}").Concat(batch.Unknown.Length > 1 ? [$"unframed 0x{batch.Unknown[0] << 8 | batch.Unknown[1]:x4}"] : []);
                Console.WriteLine($"[gateway] {remote} slow handler: {string.Join(", ", ids)} took {took.TotalMilliseconds:0} ms (ticks answered meanwhile)");
            }
            if (conn.Closed) break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[gateway] {remote} error: {ex.GetType().Name}: {ex.Message}");
    }
    conn.Closed = true;
    reading.Cancel();
}

// Handles the framed messages of one read, ticks taken out (OnClientData):
// 0x7d (area generated) → the local player id (0x22), the player object (0x2b9), inventories, passive slots, the area transitions,
// and with Game_GATEWAY_ENTER=1 enter area (0x21) + wake zone; 0x25 (move) → 0x1ab echo;
// 0x24 on an area transition → change area (0x2b2, then 0x27 with the new instance) or, with Ctrl, the instance chooser (0xae).
// Anything else is logged in hex: those are the next messages to map.
static async Task OnClientMessages(string remote, NetworkStream stream, ConnectionCiphers ciphers,
    IReadOnlyList<(ushort Id, byte[] Bytes)> messages, byte[] unknown, ClientState conn)
{
    foreach (var (id, bytes) in messages)
    {
        if (id == InstanceProtocol.UseSkillAtLocationId)
        {
            // Echo the action so the client runs it (Move on a ground click). The server does no validation yet.
            var use = InstanceProtocol.ReadUseSkillAtLocation(bytes);
            Interlocked.Increment(ref conn.ActionCount);   // a new action replaces the current one: a pending hit no longer lands
            double at = conn.Clock.Elapsed.TotalSeconds;
            // A gem skill cast at the ground (Fireball) stops the player where it stands; anything else (Move) walks there.
            bool cast = Skills.IsGemSkill(use.SkillId);
            var (px, py) = cast ? conn.Walk.Position(at) : conn.Walk.Order(use.X, use.Y, at);
            if (cast) conn.Walk.Order(px, py, at);
            await Send(remote, stream, ciphers,
                InstanceProtocol.ActionStarted(PlayerId, px, py, use.X, use.Y, use.SkillId, use.Slot, use.Flags),
                $"0x01ab action started {use} at ({px}, {py}){(cast ? " (gem skill cast)" : "")}");
            if (cast) _ = CastGemSkill(remote, stream, ciphers, conn, use.SkillId, (px, py), (use.X, use.Y));
            else await FollowRighteousFire(remote, stream, ciphers, conn);
            string mode = Environment.GetEnvironmentVariable("Game_MOVE_MODE") ?? "echo";
            if (mode == "teleport")
                await Send(remote, stream, ciphers, InstanceProtocol.Teleport(PlayerId, use.X, use.Y), $"0x01b7 teleport to ({use.X}, {use.Y})");
            else if (mode == "move")
            {
                float speed = float.TryParse(Environment.GetEnvironmentVariable("Game_MOVE_SPEED"), System.Globalization.CultureInfo.InvariantCulture, out var sp) ? sp : 37f;
                await Send(remote, stream, ciphers, InstanceProtocol.MoveTo(PlayerId, use.X, use.Y, speed), $"0x01ba move to ({use.X}, {use.Y}) speed {speed}");
            }
        }
        else if (InstanceProtocol.IsLockstepMovement(id))
        {
            // The client moves this way only in lockstep, which the Gateway does not answer: say so once per connection.
            if (conn.WarnedLockstep) continue;
            conn.WarnedLockstep = true;
            Console.WriteLine($"[gateway] {remote} <= 0x{id:x4} lockstep movement: the client's Network Mode is lockstep (\"auto\" picks it on localhost); " +
                "only Predictive is supported, the player stays hidden and cannot move");
            await Send(remote, stream, ciphers, InstanceChat.MessageBox(LockstepWarning), "0x000d message box: lockstep warning");
        }
        else if (id == InstancePause.RequestId)
        {
            bool pause = InstancePause.ReadRequest(bytes);
            // Survivors: closing the Escape menu while an upgrade waits keeps the area paused until it is picked.
            if (!pause && conn.Run?.Offer is not null && SurvivorsPauses)
            {
                Console.WriteLine($"[gateway] {remote} <= 0x01d4 resume: a Survivors pick is open, the area stays paused");
                await Send(remote, stream, ciphers, InstancePause.State(true), "0x01d3 paused (a Survivors pick is open)");
                await conn.Commands!.Reply("Survivors: choose an upgrade first (/1, /2 or /3).");
            }
            else await SetPaused(remote, stream, ciphers, conn, pause);
        }
        else if (id == InstanceProtocol.UseInstantSkillId)
        {
            // An instant skill (skill_is_instant: Haste, Righteous Fire) has no action: the walk and a pending hit go on,
            // and the client waits for no echo.
            var (target, skill) = InstanceProtocol.ReadUseInstantSkill(bytes);
            var spot = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            Console.WriteLine($"[gateway] {remote} <= 0x0191 instant skill 0x{skill:x4} on 0x{target:x} at ({spot.X}, {spot.Y})");
            if (Skills.IsGemSkill(skill)) _ = UseInstantSkill(remote, stream, ciphers, conn, skill, target, spot);
        }
        else if (id == InstanceTransfer.UseSkillOnObjectId)
        {
            var use = InstanceTransfer.ReadUseSkillOnObject(bytes);
            // In the background: the player may first walk to the target, and the read loop must keep answering ticks.
            _ = OnUseObject(remote, stream, ciphers, conn, use);
        }
        else if (id == InstanceProtocol.BindSkillId)
        {
            var (slot, skill) = InstanceProtocol.ReadBindSkill(bytes);
            string? entry = SkillBarEntry(conn, skill);
            Console.WriteLine($"[gateway] {remote} <= 0x004e bind skill 0x{skill:x4} ({(entry is null ? "unknown, not saved" : entry == "" ? "empty" : entry)}) to slot {slot}: {Convert.ToHexString(bytes)}");
            if (entry is not null) CharacterSaves.SetSkillBar(CharacterName(conn), slot, entry);
        }
        else if (id == InstanceEntities.AllocatePassiveId)
        {
            var (u2, node, u6) = InstanceEntities.ReadAllocatePassive(bytes);
            Console.WriteLine($"[gateway] {remote} <= 0x0034 allocate passive {node} ({Passives.Find(node)?.Name ?? "unknown"}), " +
                $"mastery effect {u2}, u8 {u6}");
            await ChangePassives(remote, stream, ciphers, conn, [node], allocate: true, "Passive Skill Tree",
                u2 == 0 ? null : u2);
        }
        else if (id == InstanceEntities.RefundPassiveId)
        {
            var (node, expansion, gold) = InstanceEntities.ReadRefundPassive(bytes);
            Console.WriteLine($"[gateway] {remote} <= 0x0036 refund passive {node} ({(node <= ushort.MaxValue ? Passives.Find((ushort)node)?.Name : null) ?? "unknown"})" +
                $"{(expansion ? ", expansion node" : "")}, {(gold ? "gold respec" : "refund points")}: {Convert.ToHexString(bytes)}");
            if (!expansion && node <= ushort.MaxValue)
            {
                if (gold)
                    await ChangePassives(remote, stream, ciphers, conn, [(ushort)node], allocate: false, "Respecialisation", goldRespec: true);
                else
                    await ChangePassives(remote, stream, ciphers, conn, [(ushort)node], allocate: false, "Refund points", refundPoints: true);
            }
            else
            {
                // Cluster jewel nodes do not exist here: put the tree back.
                Console.WriteLine($"[gateway] {remote} refund refused: expansion nodes are not supported");
                await SendPassives(remote, stream, ciphers, conn);
            }
        }
        else if (id == InstanceEntities.RefundAllPassivesId)
        {
            // "Refund All" (after the client's Yes): every passive but the ascendancy ones, for their full gold cost.
            var nodes = PassiveState(conn).Nodes.Where(n => Passives.Find(n) is not { IsAscendancy: true }).ToList();
            Console.WriteLine($"[gateway] {remote} <= 0x0037 refund all passives ({nodes.Count})");
            await ChangePassives(remote, stream, ciphers, conn, nodes, allocate: false, "Respecialisation, Refund All", goldRespec: true);
        }
        else if (id == InstanceEntities.PanelToggleId)
            Console.WriteLine($"[gateway] {remote} <= 0x0123 panel {(bytes[2] != 0 ? "opened" : "closed")}");
        else if (id == InstanceItems.DropItemId)
            await DropItem(remote, stream, ciphers, conn);
        else if (id == InstanceItems.CommandId)
        {
            var command = InstanceItems.ReadCommand(bytes);
            if (command.Id == InstanceItems.DestroyCursorItemCommand) await DestroyHeldItem(remote, stream, ciphers, conn);
            else if (command.Id == InstanceItems.WarpToHideoutCommand) await WarpToHideout(remote, stream, ciphers, conn, "/hideout");
            else Console.WriteLine($"[gateway] {remote} <= 0x0016 command {command} (not handled)");
        }
        else if (id == InstanceWaypoints.VisitHideoutId)
        {
            var visit = InstanceWaypoints.ReadVisitHideout(bytes);
            Console.WriteLine($"[gateway] {remote} <= 0x017a visit the hideout of {visit.Character} (from object 0x{visit.Waypoint:x}, u8 {visit.Flag})");
            // Only the player's own hideout exists (no parties or other players' hideouts yet).
            await WarpToHideout(remote, stream, ciphers, conn, "World panel Hideout button");
        }
        else if (id == InstanceItems.UnsocketGemId)
            await UnsocketGem(remote, stream, ciphers, conn, InstanceItems.ReadUnsocketGem(bytes));
        else if (id == InstanceItems.SocketGemId)
            await SocketGem(remote, stream, ciphers, conn, InstanceItems.ReadSocketGem(bytes));
        else if (id == InstanceItems.PlaceItemId)
            await PlaceItem(remote, stream, ciphers, conn, InstanceItems.ReadPlaceItem(bytes));
        else if (id == InstanceItems.ItemActionId)
            await HeldOntoItem(remote, stream, ciphers, conn, InstanceItems.ReadItemAction(bytes), Convert.ToHexString(bytes));
        else if (id == InstanceItems.SplitStackId)
            await SplitStack(remote, stream, ciphers, conn, InstanceItems.ReadSplitStack(bytes), Convert.ToHexString(bytes));
        else if (id == InstanceItems.UseItemId)
            await UseItem(remote, stream, ciphers, conn, InstanceItems.ReadUseItem(bytes), Convert.ToHexString(bytes));
        else if (id == InstanceItems.UseFlaskId)
            await UseFlask(remote, stream, ciphers, conn, InstanceItems.ReadUseFlask(bytes));
        else if (id == InstanceItems.UseItemOnId)
            await UseItemOn(remote, stream, ciphers, conn, InstanceItems.ReadUseItemOn(bytes), Convert.ToHexString(bytes));
        else if (id == InstanceItems.UseItemTypeOnId)
        {
            // Shift held: the currency stays on the cursor and is named by type; use a stack of it from the inventories,
            // the main inventory first.
            var again = InstanceItems.ReadUseItemTypeOn(bytes);
            var items = ItemsOf(conn);
            byte[] rows = [PlayerInventories.MainInventory, .. Enumerable.Range(0, InstanceInventories.PlayerDefaults.Length).Select(r => (byte)r)];
            var stack = rows.Select(r => (Row: r, Item: items.Items(r).FirstOrDefault(i => i.Item.TypeHash == again.TypeHash))).FirstOrDefault(s => s.Item is not null);
            if (stack.Item is null) Console.WriteLine($"[gateway] {remote} <= 0x0072 use type 0x{again.TypeHash:x8} again: none left");
            else await UseItemOn(remote, stream, ciphers, conn, new InstanceItems.UseItemOn(PlayerInventories.InventoryId(stack.Row), stack.Item.Id,
                again.TargetInventoryId, again.TargetId), Convert.ToHexString(bytes));
        }
        else if (id == InstanceItems.InventoryRequestId)
            Console.WriteLine($"[gateway] {remote} <= 0x{id:x4} (inventory request, not handled yet): {Convert.ToHexString(bytes)}");
        else if (id == InstanceItems.PickUpItemId)
            await PickUpItem(remote, stream, ciphers, conn, InstanceItems.ReadPickUpItem(bytes));
        else if (id == InstanceProtocol.ExitToCharacterSelectId)
        {
            // Esc → "Exit to Character Selection" or the hardcore death screen's Exit. Closing the instance connection
            // is the answer: the client keeps its login connection and goes back to the character list (09 §6).
            Console.WriteLine($"[gateway] {remote} <= 0x0153 exit to character selection: closing the instance connection");
            conn.Closed = true;
            return;
        }
        else if (id == InstanceProtocol.ResurrectId)
            _ = Resurrect(remote, stream, ciphers, conn, InstanceProtocol.ReadResurrectInTown(bytes));
        else if (id == InstanceNpcs.DialogueClosedId)
            await CloseNpcDialogue(remote, stream, ciphers, conn);
        else if (id == InstanceNpcs.TradeOfferId)
        {
            var offer = InstanceNpcs.ReadTradeOffer(bytes);
            // Ctrl + click on an inventory item sends x = y = 0xff (0x14030e3e0 → 0x142166660): any free offer cell.
            if (conn.Sale is { } sale && offer.X == 0xff && offer.Y == 0xff) await OfferFromInventory(remote, stream, ciphers, conn, sale, offer.InventoryId, offer.ItemId);
            else if (conn.Sale is { } open) await OfferForSale(remote, stream, ciphers, conn, open, offer.InventoryId, offer.ItemId, offer.X, offer.Y);
            else Console.WriteLine($"[gateway] {remote} <= 0x00b2 trade offer {offer}: no sale open");
        }
        else if (id is InstanceNpcs.TradeTakeBackId or InstanceNpcs.TradeMoveAllId)
        {
            var (item, inventory) = InstanceNpcs.ReadTradeMove(bytes);
            Console.WriteLine($"[gateway] {remote} <= 0x{id:x4} trade move item {item} (inventory {inventory})");
            if (conn.Sale is not { } sale) Console.WriteLine($"[gateway] {remote} trade move: no sale open");
            else if (ItemsOf(conn).Offered().Any(i => i.Id == item)) await TakeFromSale(remote, stream, ciphers, conn, sale, item);
            else await OfferFromInventory(remote, stream, ciphers, conn, sale, inventory, item);
        }
        else if (id == InstanceNpcs.TradeAcceptId)
        {
            uint revision = InstanceNpcs.ReadTradeAccept(bytes);
            Console.WriteLine($"[gateway] {remote} <= 0x00b6 trade accept (their offer revision {revision}, ours {conn.Sale?.TheirsRevision})");
            await CloseSale(remote, stream, ciphers, conn, accepted: conn.Sale is { } s && s.TheirsRevision == revision);
        }
        else if (id == InstanceNpcs.TradeUnacceptId)
            Console.WriteLine($"[gateway] {remote} <= 0x00b7 trade un-accept: {Convert.ToHexString(bytes)}");
        else if (id == InstanceNpcs.TradeAnswerId)
            await AnswerSale(remote, stream, ciphers, conn, InstanceNpcs.ReadTradeAnswer(bytes));
        else if (id == InstanceNpcs.BuyId)
            await BuyFromShop(remote, stream, ciphers, conn, InstanceNpcs.ReadBuy(bytes));
        else if (id == InstanceStash.RequestTabId)
        {
            var (tab, kind) = InstanceStash.ReadRequestTab(bytes);
            await SendStashTab(remote, stream, ciphers, conn, tab, kind);
        }
        else if (id == InstanceStash.ReorderTabsId)
        {
            var (kind, order) = InstanceStash.ReadReorderTabs(bytes);
            Console.WriteLine($"[gateway] {remote} <= 0x00cc stash {kind} tab order: {string.Join(", ", order.Select(o => $"{o.Index}->{o.Position}"))} (not kept)");
        }
        else if (id == InstanceStash.MoveToTabId)
            await MoveToStash(remote, stream, ciphers, conn, InstanceStash.ReadMoveToTab(bytes));
        else if (id == InstanceStash.MoveToAffinityTabId)
            await MoveToAffinityTab(remote, stream, ciphers, conn, InstanceStash.ReadMoveToAffinityTab(bytes));
        else if (id == InstanceStash.SetAffinitiesId)
            await SetTabAffinities(remote, stream, ciphers, conn, InstanceStash.ReadSetAffinities(bytes));
        else if (id == InstanceNpcs.CtrlBuyId)
            await CtrlBuyFromShop(remote, stream, ciphers, conn, InstanceNpcs.ReadCtrlBuy(bytes));
        else if (id == InstanceStash.TakeFromTabId)
        {
            var (tab, item, a, b, c, d) = InstanceStash.ReadTakeFromTab(bytes);
            Console.WriteLine($"[gateway] {remote} <= 0x00e3 take item {item} from stash tab {tab} ({a:x2} {b:x2} {c:x2} {d:x2})");
            await TakeFromStash(remote, stream, ciphers, conn, tab, item);
        }
        else if (id == InstanceMapDevice.ActivateId)
            await ActivateMapDevice(remote, stream, ciphers, conn, InstanceMapDevice.ReadActivate(bytes));
        else if (id == InstanceStash.MoveTabId)
            Console.WriteLine($"[gateway] {remote} <= 0x00d1 stash tab into a folder (not handled): {Convert.ToHexString(bytes)}");
        else if (id == InstanceNpcs.OptionId)
            await ChooseNpcOption(remote, stream, ciphers, conn, InstanceNpcs.ReadOption(bytes));
        else if (id == InstanceChat.ChatId)
        {
            var chat = InstanceChat.ReadChat(bytes);
            Console.WriteLine($"[gateway] {remote} <= 0x0015 chat \"{chat.Text}\" (u8 {chat.U8})");
            if (chat.IsCommand) await conn.Commands!.RunAsync(chat.Text);
        }
        else if (id == InstanceTransfer.ChooseInstanceId)
        {
            byte row = bytes[2];
            if (conn.Chooser is not { } chooser) { Console.WriteLine($"[gateway] {remote} <= 0x00af row {row} without a list"); continue; }
            conn.Chooser = null;
            bool isNew = row == InstanceTransfer.NewInstanceRow;
            if (!isNew && row >= chooser.Ids.Count) { Console.WriteLine($"[gateway] {remote} <= 0x00af row {row} out of range"); continue; }
            ulong instance = isNew ? 0 : chooser.Ids[row];
            Console.WriteLine($"[gateway] {remote} <= 0x00af chose {(isNew ? "a new instance" : $"instance {instance}")} of 0x{chooser.Area:x4}");
            await ChangeArea(remote, stream, ciphers, conn, new PendingTransfer(chooser.Area, instance, isNew, AtWaypoint: chooser.AtWaypoint), $"chooser row {row}");
        }
        else if (id == InstanceWaypoints.TravelId)
            _ = TravelByWaypoint(remote, stream, ciphers, conn, InstanceWaypoints.ReadTravel(bytes));
        else if (id == InstanceProtocol.AreaGeneratedId)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x007d area generated: {Convert.ToHexString(bytes)}");
            if (!conn.PlayerSent)
            {
                // First visit with scripts/walkmaps running: the map is written while the client generates the area.
                if (conn.Layout.Walkable is null && await AreaLayouts.WaitFor(conn.Layout, conn.Seed) is { } fresh)
                {
                    conn.Use(fresh);
                    Console.WriteLine($"[gateway] {remote} walk map captured: {fresh.Describe()}");
                }
                if (conn.Layout.AreaTransitions.Length == 0 && await AreaLayouts.WaitForTransitions(conn.Layout, conn.Seed) is { } withTransitions)
                {
                    conn.Use(withTransitions);
                    Console.WriteLine($"[gateway] {remote} area transitions captured: {withTransitions.Describe()}");
                }
                // Waypoint travel: next to the destination's waypoint instead of the area's spawn. A generated area's
                // waypoint is only known from the walk-map recorder's capture for this seed (live.ps1 -Walkmaps).
                if (conn.ArriveAtWaypoint && conn.Layout.Waypoint is { } arrival)
                {
                    var (ax, ay) = conn.Layout.Near((arrival.GridX, arrival.GridY));
                    conn.Use(conn.Layout with { SpawnX = ax, SpawnY = ay });
                    Console.WriteLine($"[gateway] {remote} arrived by waypoint: spawn ({ax}, {ay}) next to the waypoint at ({arrival.GridX}, {arrival.GridY})");
                }
                // Otherwise next to the exit back; Resurrect at Checkpoint and the monster packs' clearance use it too.
                else if (conn.Layout.Arrival(conn.CameFrom) is var (ex, ey) && (ex, ey) != (conn.Layout.SpawnX, conn.Layout.SpawnY))
                {
                    conn.Use(conn.Layout with { SpawnX = ex, SpawnY = ey });
                    Console.WriteLine($"[gateway] {remote} arrival from 0x{conn.CameFrom:x4}: ({ex}, {ey})");
                }
                else if (conn.ArriveAtWaypoint)
                    Console.WriteLine($"[gateway] {remote} arrived by waypoint, but {conn.Layout.Id} seed {conn.Seed} has no captured waypoint " +
                        $"(run live.ps1 -Walkmaps): spawn ({conn.Layout.SpawnX}, {conn.Layout.SpawnY}){(conn.Layout.Walkable is null ? ", no walk map either: the player may stand outside the map" : "")}");
                RecordVisit(conn);
                await SendPlayer(remote, stream, ciphers, conn);
                conn.PlayerSent = true;
                await SendWaypoints(remote, stream, ciphers, conn);
                uint gold = PersonalStashOf(conn).Gold;
                await Send(remote, stream, ciphers, InstanceEntities.Gold(gold), $"0x02a9 gold: {gold} (account, {conn.League ?? "Standard"})");
                // The saved skill bar (S→C 0x53), right after the player and the area.
                await SendSkillBar(remote, stream, ciphers, conn);
                // A Survivors character's map is a run (RunSurvivors) instead of the area's packs.
                bool survivorsRun = IsSurvivorsRun(conn);
                if (survivorsRun) _ = RunSurvivors(remote, stream, ciphers, conn);
                else await PopulateArea(remote, stream, ciphers, conn);
                await SendGroundItems(remote, stream, ciphers, conn);
                await PlaceStartWeapon(remote, stream, ciphers, conn);
                await SpawnTownNpcs(remote, stream, ciphers, conn);
                await SpawnTownStash(remote, stream, ciphers, conn);
                await SpawnHideoutDoodads(remote, stream, ciphers, conn);
                if (!survivorsRun) await SpawnMapReturnPortal(remote, stream, ciphers, conn);
                await GiveSurvivorsMap(remote, stream, ciphers, conn);
                _ = RunMonsterAi(remote, stream, ciphers, conn);
                _ = RunPlayerTick(remote, stream, ciphers, conn);
                _ = RunFlaskRecovery(remote, stream, ciphers, conn);
            }
        }
    }
    if (unknown.Length > 0)
    {
        ushort uid = unknown.Length > 1 ? (ushort)(unknown[0] << 8 | unknown[1]) : unknown[0];
        Console.WriteLine($"[gateway] {remote} <= unframed 0x{uid:x4} ({unknown.Length} bytes): {Convert.ToHexString(unknown.AsSpan(0, Math.Min(unknown.Length, 256)))}");
        // C→S 0x35: the mastery effect chosen in the passive tree (node, effect HASH16). It allocates the mastery node
        // and records the effect; the client waits for the 0x33 tree and the updated stats.
        if (uid == InstanceEntities.MasteryEffectId && unknown.Length >= 6)
        {
            var (node, effect) = InstanceEntities.ReadMasteryEffect(unknown);
            await ChangePassives(remote, stream, ciphers, conn, [node], allocate: true, "Passive Skill Tree mastery", effect);
        }
        // The client's answer to 0x2b2 (0x142148690): it is ready to leave, so move it now.
        else if (uid == InstanceTransfer.LeaveAreaAckId && conn.PendingTransfer is { } t) await Transfer(remote, stream, ciphers, conn, t);
    }
}

// Creates one monster: registers its type at this level (0x20, once per connection), creates it (0x2b9) and puts it in
// the wake zone (0x1a9), and keeps it for the AI. /spawn_mob and the area's packs (PopulateArea) use it.
static async Task<uint> SpawnMonster(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, MonsterCatalog.Variety variety,
    byte level, MonsterRarity rarity, int x, int y, string? name = null, bool friendly = false, bool log = true, uint? damagedLife = null,
    bool hunt = false)
{
    uint id = conn.NextObjectId++;
    // Full life: the client derives the maximum from the level (MonsterCatalog.BaseLife); mods are not sent yet.
    uint life = (uint)(Monsters.BaseLife(variety, level) ?? 100);
    // The type supplies the animated object; Game_MONSTER_AO=1 also sends the variety's first AOFiles path (Animated
    // bit0), which is not needed.
    string? ao = Environment.GetEnvironmentVariable("Game_MONSTER_AO") == "1" ? variety.AnimatedObject : null;
    ushort team = friendly ? (ushort)0 : (ushort)1;
    // Its attack: the variety's first granted effect (Hungry Corpse: Melee) at its level, with the variety's attack time,
    // reach and the level's damage as the skill's final stats (11-skills.md §7).
    var damage = Monsters.BaseDamage(variety, level) ?? (1, 1);
    var attack = new MonsterAttack(SkillCatalog.FirstSkillId, variety.AttackMs / 1000.0, variety.AttackRange,
        DamagePacket.Of(DamageType.Physical, damage.Min, damage.Max));
    var skill = variety.GrantedEffects is [int effect, ..]
        ? Skills.FindByRow(attack.SkillId, effect, level, attack.SkillStats()) : null;
    var monster = new MonsterObject(x, y, life, variety.ComponentsOrBase, ao, rarity, name, variety.StateMachine, variety.TransitionStates, team,
        skill is null ? null : [skill], skill is null ? null : attack.Stats());
    // A monster type needs a level: 0x20 registers "<Id>@<level>" (once per connection), the 0x2b9 type is its hash.
    if (conn.MonsterTypes.Add((variety.Row, level)))
        await Send(remote, stream, ciphers, InstanceEntities.MonsterTypes([((ushort)variety.Row, level)]),
            $"0x0020 monster type {variety.Id}@{level} (row {variety.Row})");
    uint type = InstanceEntities.ObjectTypeHash(MonsterObject.TypePath(variety.Id, level));
    // The world is active by now, so 0x2b9 is created at once; 0x1a9 makes it drawn and updated.
    await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, type, monster.Blob()), !log ? null :
        $"0x02b9 create {rarity}{(friendly ? " friendly" : "")} monster 0x{id:x} {variety}{(name is null ? "" : $" \"{name}\"")} type=0x{type:x8} at ({x}, {y}), life {life}, components {string.Join(", ", variety.ComponentsOrBase)}");
    await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), !log ? null : $"0x01a9 monster 0x{id:x} enters wake zone");
    lock (conn.Monsters)
        conn.Monsters[id] = new SpawnedMonster($"{variety.Name}@{level}", conn.NewWalk(x, y, variety.MovementSpeed),
            variety.AggroRange, Math.Max(2, variety.ObjectSize + 1), life)
        {
            Attack = skill is null ? null : attack, Hostile = !friendly, Variety = variety, Level = level, Rarity = rarity,
            Life = Math.Min(damagedLife ?? life, life), Hunts = hunt, Aggro = hunt,
        };
    // A kept monster that was hit: its life as a hit sets it (0x1b1).
    if (damagedLife < life)
        await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(id, damagedLife.Value), !log ? null : $"0x01b1 monster 0x{id:x} life {damagedLife}/{life}");
    return id;
}

// The area's monsters, once the player is in (Agents/docs/protocol/14-monster-packs.md): MonsterPacks scattered over the
// walk map, the room points' packs and the unique boss at "mapboss" (AreaPopulation). Towns have none; neither does an
// area without a walk map. Game_MONSTERS=0 turns it off.
static async Task PopulateArea(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    var layout = conn.Layout;
    if (Environment.GetEnvironmentVariable("Game_MONSTERS") == "0") return;
    // An instance entered before keeps its monsters as the last connection left them: where they were, with their
    // damage; the dead stay dead (#12).
    if (conn.Instance.Monsters is { } saved)
    {
        conn.MonstersPlaced = true;
        foreach (var s in saved)
            if (Monsters.Find(s.Variety.ToString()) is { } kept)
                await SpawnMonster(remote, stream, ciphers, conn, kept, s.Level, s.Rarity, s.X, s.Y, friendly: !s.Hostile, log: false, damagedLife: s.Life);
        Console.WriteLine($"[gateway] {remote} area {layout.Id} monsters: {saved.Count} kept from the instance");
        return;
    }
    // The packs need the walk map; until it exists nothing is placed, so nothing is kept either.
    if (layout.Walkable is not { } walkable) return;
    conn.MonstersPlaced = true;
    if (Packs.For(layout.Id) is not { } area || (area.Scattered.Count == 0 && area.Bosses.Count == 0)) return;
    if (Monsters.Error is { } error) { Console.WriteLine($"[gateway] {remote} area monsters: {error}"); return; }
    // Emerge monsters (Gravel Eater …) stay hidden without the Emerge action the server does not send yet.
    bool Spawnable(int row) => Monsters.Find(row.ToString()) is { Supported: true } v && !v.Id.Contains("Emerge", StringComparison.OrdinalIgnoreCase);
    var notes = new List<string>();
    var points = AreaPopulation.LoadPoints(layout.Id, conn.Seed, notes);
    var spawns = AreaPopulation.Plan(area, layout.GridW, layout.GridH, walkable, (layout.SpawnX, layout.SpawnY),
        [.. layout.AreaTransitions.Select(t => (t.Transition.GridX, t.Transition.GridY))], points,
        HashCode.Combine(layout.Hash, conn.Seed), Spawnable, notes);
    foreach (var n in notes) Console.WriteLine($"[gateway] {remote} area {layout.Id} monsters: {n}");
    byte level = (byte)Math.Clamp(area.AreaLevel, 1, 100);
    foreach (var s in spawns)
    {
        var variety = Monsters.Find(s.Variety.ToString())!;
        bool notable = s.Rarity is MonsterRarity.Unique or MonsterRarity.Rare;
        await SpawnMonster(remote, stream, ciphers, conn, variety, level, s.Rarity, s.X, s.Y, log: notable);
    }
    Console.WriteLine($"[gateway] {remote} area {layout.Id} monsters: created {spawns.Count} at level {level}: " +
        string.Join(", ", spawns.GroupBy(s => s.Pack).Select(g => $"{g.Key} × {g.Count()}")) +
        $"; unique: {string.Join(", ", spawns.Where(s => s.Rarity == MonsterRarity.Unique).Select(s => $"{Monsters.Find(s.Variety.ToString())!.Name} at ({s.X}, {s.Y})").DefaultIfEmpty("none"))}");
}

// An area transition click. Plain: change area (0x2b2 now, the move itself once the client acknowledges with 0x2cb).
// With Ctrl: list the destination's instances (0xae) for the client's instance chooser.
static async Task OnUseObject(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceTransfer.UseSkillOnObject use)
{
    SpawnedMonster? target;
    lock (conn.Monsters) target = conn.Monsters.GetValueOrDefault(use.TargetId);
    if (target is not null)
    {
        if (use.SkillId != SkillCatalog.FirstSkillId) await CastOn(remote, stream, ciphers, conn, use, target);
        else await Attack(remote, stream, ciphers, conn, use, target);
        return;
    }
    // A self-cast skill (Righteous Fire) targets the player itself: echo it in place, then cast.
    if (use.TargetId == PlayerId && Skills.IsGemSkill(use.SkillId))
    {
        Interlocked.Increment(ref conn.ActionCount);
        double at = conn.Clock.Elapsed.TotalSeconds;
        var self = conn.Walk.Position(at);
        conn.Walk.Order(self.X, self.Y, at);
        await Send(remote, stream, ciphers,
            InstanceProtocol.ActionStarted(PlayerId, self.X, self.Y, self.X, self.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: PlayerId),
            $"0x01ab player skill 0x{use.SkillId:x4} cast on itself at ({self.X}, {self.Y})");
        await CastGemSkill(remote, stream, ciphers, conn, use.SkillId, self, self, PlayerId);
        return;
    }
    if (conn.Instance.Ground.Get(use.TargetId) is { } ground)
    {
        await PickUpGroundItem(remote, stream, ciphers, conn, use.TargetId, ground, use);
        return;
    }
    if (use.TargetId == WaypointCatalog.ObjectId && conn.Layout.Waypoint is { } waypoint)
    {
        await UseWaypoint(remote, stream, ciphers, conn, use, waypoint);
        return;
    }
    SpawnedNpc? npc;
    lock (conn.Npcs) npc = conn.Npcs.GetValueOrDefault(use.TargetId);
    if (npc is not null)
    {
        await TalkToNpc(remote, stream, ciphers, conn, use, npc);
        return;
    }
    if (conn.ReturnPortal is { } back && use.TargetId == back.Id)
    {
        await UseMapReturnPortal(remote, stream, ciphers, conn, use, (back.X, back.Y));
        return;
    }
    (int X, int Y, int Index)? portal;
    lock (conn.MapPortals) portal = conn.MapPortals.TryGetValue(use.TargetId, out var at) ? at : null;
    if (portal is { } p)
    {
        await UseMapPortal(remote, stream, ciphers, conn, use, p);
        return;
    }
    HideoutCatalog.Doodad? doodad;
    lock (conn.HideoutDoodads) doodad = conn.HideoutDoodads.GetValueOrDefault(use.TargetId);
    if (doodad is not null)
    {
        await UseMapDevice(remote, stream, ciphers, conn, use, doodad);
        return;
    }
    if (conn.StashObject != 0 && use.TargetId == conn.StashObject)
    {
        await OpenStash(remote, stream, ciphers, conn, use);
        return;
    }
    if (conn.GuildStashObject != 0 && use.TargetId == conn.GuildStashObject)
    {
        await OpenGuildStash(remote, stream, ciphers, conn, use);
        return;
    }
    var (_, transition) = conn.Layout.AreaTransitions.FirstOrDefault(e => e.Id == use.TargetId);
    if (transition is null)
    {
        Console.WriteLine($"[gateway] {remote} <= 0x0024 use object {use} (not an area transition)");
        return;
    }
    ushort dest = transition.DestinationHash16;
    // A quest can close an exit: The Twilight Strand's way into Lioneye's Watch needs Hillock dead (a1q1).
    if (AreaTransitionCatalog.IdOf(dest) is { } destId && Quests.FlagToEnter(conn.Layout.Id, destId) is { } needed && !conn.QuestFlags.Contains(needed))
    {
        Console.WriteLine($"[gateway] {remote} <= 0x0024 area transition 0x{use.TargetId:x} to {destId}: closed until quest flag {needed}");
        return;
    }
    // The client walks to the area transition first; follow it and act once the player is there (a move meanwhile cancels).
    int action = Interlocked.Increment(ref conn.ActionCount);
    double now = conn.Clock.Elapsed.TotalSeconds;
    var (here, stop, arrive) = conn.Walk.Approach(transition.GridX, transition.GridY, AreaTransitionReach, now);
    Console.WriteLine($"[gateway] {remote} <= 0x0024 area transition 0x{use.TargetId:x} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
    if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
    if (conn.ActionCount != action) return;
    if (Dist(stop, (transition.GridX, transition.GridY)) > AreaTransitionReach + 2)
    {
        Console.WriteLine($"[gateway] {remote} area transition 0x{use.TargetId:x} out of reach from ({stop.X}, {stop.Y})");
        return;
    }
    // A gate within the area (a map's boss arena): next to its pair, in the same instance.
    if (transition.TeleportTo is { } pair)
    {
        var (tx, ty) = conn.Layout.Near(pair);
        conn.Walk.Teleport(tx, ty, conn.Clock.Elapsed.TotalSeconds);
        await Send(remote, stream, ciphers, InstanceProtocol.Teleport(PlayerId, tx, ty),
            $"0x01b7 player through the gate 0x{use.TargetId:x} to ({tx}, {ty}), next to its pair at ({pair.X}, {pair.Y})");
        return;
    }
    if (use.Ctrl)
    {
        await ShowInstances(remote, stream, ciphers, conn, dest, atWaypoint: false);
        return;
    }
    await ChangeArea(remote, stream, ciphers, conn, new PendingTransfer(dest, 0, false), use.ToString());
}

// The destination's instances (0xae) for the client's instance chooser; its pick (0xaf) changes area.
static async Task ShowInstances(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, ushort dest, bool atWaypoint)
{
    var list = await InstanceControl.CallAsync(new ControlRequest { Op = "list", Ticket = conn.Ticket, Area = dest });
    var instances = list.Instances ?? [];
    var entries = instances
        .Select(i => new InstanceTransfer.InstanceListEntry((ulong)i.CreatedUnix, (uint)i.SecondsLeft, 0, i.Players)).ToList();
    conn.Chooser = new ChooserList(dest, [.. instances.Select(i => i.Id)], atWaypoint);
    await Send(remote, stream, ciphers, InstanceTransfer.InstanceList(dest, entries),
        $"0x00ae instance list for 0x{dest:x4}: {entries.Count} instance(s) [{string.Join(", ", instances.Select(i => $"{i.Id} ({i.SecondsLeft} s left): {string.Join("/", i.Players)}"))}]");
}

// A waypoint click (C→S 0x24 on the waypoint, Agents/docs/protocol/16-waypoints.md): walk there, activate it if it is
// not yet, then open the World panel for travel (S→C 0x7b kind 0). The panel's pick arrives as C→S 0x4d.
static async Task UseWaypoint(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
    InstanceTransfer.UseSkillOnObject use, WaypointObject waypoint)
{
    int action = Interlocked.Increment(ref conn.ActionCount);
    double now = conn.Clock.Elapsed.TotalSeconds;
    var (here, stop, arrive) = conn.Walk.Approach(waypoint.GridX, waypoint.GridY, WaypointReach, now);
    Console.WriteLine($"[gateway] {remote} <= 0x0024 waypoint ({use}) from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
    if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
    if (conn.ActionCount != action) return;
    if (Dist(stop, (waypoint.GridX, waypoint.GridY)) > WaypointReach + 2)
    {
        Console.WriteLine($"[gateway] {remote} waypoint out of reach from ({stop.X}, {stop.Y})");
        return;
    }
    await ActivateWaypoint(remote, stream, ciphers, conn, "clicked");
    await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(InstanceWaypoints.WorldPanelUi, WaypointCatalog.ObjectId),
        "0x007b open the World panel for waypoint travel");
}

// Creates one NPC (0x2b9, its NPCs.dat Id is an .ot type) and puts it in the wake zone; kept for clicks and /npcs.
static async Task SpawnNpc(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, NpcCatalog.Npc npc,
    int x, int y, float rotation)
{
    uint id = conn.NextObjectId++;
    var obj = new NpcObject(x, y, npc.Components!, rotation, npc.StateMachine, npc.TransitionState);
    uint type = InstanceEntities.ObjectTypeHash(npc.Id);
    await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, type, obj.Blob()),
        $"0x02b9 create npc 0x{id:x} {npc} type=0x{type:x8} at ({x}, {y}) rotation {rotation:0.##}, components {string.Join(", ", npc.Components!)}");
    await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), $"0x01a9 npc 0x{id:x} enters wake zone");
    lock (conn.Npcs) conn.Npcs[id] = new SpawnedNpc(npc, x, y);
}

// The town's NPCs (NpcCatalog.In) at their cells.
static async Task SpawnTownNpcs(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    var placements = NpcCatalog.In(conn.Layout.Hash);
    if (placements.Count == 0 || Environment.GetEnvironmentVariable("Game_NPCS") == "0") return;
    if (Npcs.Value.Error is { } error) { Console.WriteLine($"[gateway] {remote} npcs: {error}"); return; }
    foreach (var p in placements)
    {
        if (Npcs.Value.Find(p.Id) is not { Supported: true } npc) { Console.WriteLine($"[gateway] {remote} npc {p.Id}: unknown or unsupported components"); continue; }
        if (conn.Layout.Walkable is { } w && !w(p.X, p.Y)) Console.WriteLine($"[gateway] {remote} npc {npc.Name}: ({p.X}, {p.Y}) is not walkable");
        await SpawnNpc(remote, stream, ciphers, conn, npc, p.X, p.Y, p.Rotation);
    }
}

// A click on an NPC (C→S 0x24 on its object): the client walks to it first; follow it and answer once the player is there.
static async Task TalkToNpc(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
    InstanceTransfer.UseSkillOnObject use, SpawnedNpc npc)
{
    int action = Interlocked.Increment(ref conn.ActionCount);
    double now = conn.Clock.Elapsed.TotalSeconds;
    var (here, stop, arrive) = conn.Walk.Approach(npc.X, npc.Y, NpcReach, now);
    Console.WriteLine($"[gateway] {remote} <= 0x0024 npc {npc.Npc} ({use}) from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
    if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
    if (conn.ActionCount != action) return;
    uint id;
    lock (conn.Npcs) id = conn.Npcs.FirstOrDefault(n => ReferenceEquals(n.Value, npc)).Key;
    conn.TalkingTo = id;
    conn.InNpcTalk = false;
    await Send(remote, stream, ciphers, InstanceNpcs.Talk(id), $"0x0084 talk to npc 0x{id:x} {npc.Npc.Name}");
}

// C→S 0x85: the dialogue window was closed or, while a talk plays, Continue was pressed. The client stays in its dialogue
// state (and the player cannot move) until the server answers: back to the option list (0x84 0xff) after a talk, else
// 0x84 with object 0, which ends the dialogue.
static async Task CloseNpcDialogue(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    uint id = conn.TalkingTo;
    if (id != 0 && conn.InNpcTalk)
    {
        conn.InNpcTalk = false;
        await Send(remote, stream, ciphers, InstanceNpcs.Talk(id), $"0x0084 npc 0x{id:x}: back to the options (0x85 after a talk)");
        return;
    }
    conn.TalkingTo = 0;
    conn.InNpcTalk = false;
    await Send(remote, stream, ciphers, InstanceNpcs.Talk(0), "0x0084 object 0: npc dialogue closed (0x85)");
}

// C→S 0x82: an option picked in the NPC dialogue window (an NPCTalk row of the NPC talked to).
static async Task ChooseNpcOption(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, byte index)
{
    uint id = conn.TalkingTo;
    SpawnedNpc? npc;
    lock (conn.Npcs) npc = conn.Npcs.GetValueOrDefault(id);
    if (npc is null) { Console.WriteLine($"[gateway] {remote} <= 0x0082 npc option {index}: no NPC dialogue open"); return; }
    var talk = Npcs.Value.TalkOf(npc.Npc, index);
    Console.WriteLine($"[gateway] {remote} <= 0x0082 npc {npc.Npc.Name} option {index}: " +
        (talk is null ? "no NPCTalk row" : $"NPCTalk row {talk.Row} \"{talk.Option}\" ({talk.Category}), text audio [{string.Join(", ", talk.TextAudio)}]"));
    if (talk is null) return;
    switch (talk.Category)
    {
        case "Gossip" or "Quests" or "QuestReward" when talk.TextAudio.Count > 0:
            // 0x84 with the row's index: the client opens the dialogue window on that NPCTalk row (its text and audio).
            conn.InNpcTalk = true;
            await Send(remote, stream, ciphers, InstanceNpcs.Talk(id, (byte)talk.Index),
                $"0x0084 npc 0x{id:x} {npc.Npc.Name} talks: NPCTalk row {talk.Row} \"{talk.Option}\" (index {talk.Index})");
            break;
        case "BuyItems":
            await OpenNpcShop(remote, stream, ciphers, conn, id, npc);
            break;
        case "SellItems":
            await OpenNpcSale(remote, stream, ciphers, conn, npc);
            break;
        // Bestel's Respecialisation (script: SendOpenScreen( CharacterPassiveTreeScreen, 1 )): the character's passive tree,
        // S→C 0x7b kind 0x5f with 1. Its refunds come back as C→S 0x36 (per node) and 0x37 (Refund All), paid in gold.
        case "RespecPassivePointsForGold":
            conn.InNpcTalk = false;
            await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(InstanceNpcs.PassiveTreeUi, 1), "0x007b open the passive tree (respec)");
            break;
        // Nessa's Identify Items (script: triggerer.IdentifyItems(), then the IdentifyAll sound): every unidentified item
        // in the main inventory, free. The sound effect is not sent (its message is not mapped).
        case "IdentifyItems":
            await IdentifyInventory(remote, stream, ciphers, conn, npc);
            break;
        default:
            Console.WriteLine($"[gateway] {remote} npc option category {talk.Category} is not implemented yet");
            break;
    }
}

// An NPC's Identify Items: the main inventory's unidentified items become identified (their explicit mods and rare names
// are sent from now on). Removing and adding the same ids in one 0xbf replaces them on the client (see UseItemOn).
static async Task IdentifyInventory(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, SpawnedNpc npc)
{
    const byte row = PlayerInventories.MainInventory;
    var items = ItemsOf(conn);
    var identified = items.IdentifyAll(row);
    if (identified.Count == 0)
    {
        Console.WriteLine($"[gateway] {remote} npc {npc.Npc.Name} identify items: nothing unidentified");
        return;
    }
    uint inventory = PlayerInventories.InventoryId(row);
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(inventory, items.NextRevision(row), [.. identified.Select(i => i.Id)], identified),
        $"0x00bf inventory {inventory}: {npc.Npc.Name} identified {identified.Count} item(s)");
    foreach (var i in identified)
        Console.WriteLine($"[gateway] {remote} npc {npc.Npc.Name} identified item {i.Id}: {string.Join("; ", i.Item.Mods.Select(Items.Mods.Describe))}");
    // ClientStrings IdentifyItemsChatMessage / IdentifyItemsChatMessageSingle, as a chat line (S→C 0x17).
    string text = identified.Count == 1 ? "1 Item identified" : $"{identified.Count} Items identified";
    await Send(remote, stream, ciphers, InstanceChat.ChatLine(text), $"0x0017 chat line \"{text}\"");
    // The client decides itself whether the option is usable (NPCTalkCategory IdentifyItems, 0x141e754b0: gray with
    // "No items in your inventory can be identified" when nothing is unidentified), but only when the options are
    // listed: list them again.
    await Send(remote, stream, ciphers, InstanceNpcs.Talk(conn.TalkingTo), $"0x0084 npc 0x{conn.TalkingTo:x}: options again");
}

// C→S 0x88: an item bought from the open shop tab. The client picked the free cell; the item leaves the tab (0xbf on
// manager 1) and enters the inventory (0xbf), keeping its id. Free for now: prices are not charged yet.
static async Task BuyFromShop(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceNpcs.Buy buy)
{
    OpenShop? shop;
    InventoryItem? item = null;
    lock (conn.Npcs)
    {
        shop = conn.Shop;
        if (shop is not null && shop.Items.Remove(buy.ItemId, out var found)) item = found;
    }
    if (shop is null || item is null || PlayerInventories.Row(buy.InventoryId) is not byte row)
    {
        Console.WriteLine($"[gateway] {remote} <= 0x0088 buy {buy}: {(shop is null ? "no shop open" : item is null ? "not in the shop" : "unknown inventory")}");
        if (shop is not null && item is not null) lock (conn.Npcs) shop.Items[item.Id] = item;
        return;
    }
    var items = ItemsOf(conn);
    var stash = StashOf(conn);
    // The price: the item's price list (its first entry), paid from the main inventory and then the stash, together with
    // the item's arrival (one transaction). Not enough: nothing happens (the client shows the price in red).
    (uint Currency, int Amount)? price = item.Item.Price is [var (currency, amount), ..] ? (currency, (int)amount) : null;
    if (ItemMoves.Buy(items, stash, row, item with { X = buy.X, Y = buy.Y, Item = item.Item with { Price = [] } }, price) is not var (paid, bought))
    {
        lock (conn.Npcs) shop.Items[item.Id] = item;
        Console.WriteLine($"[gateway] {remote} <= 0x0088 buy item {item.Id}: costs {price!.Value.Amount} × {Items.ByHash(price.Value.Currency)?.Name ?? $"0x{price.Value.Currency:x8}"}, not enough in the inventory and the stash");
        return;
    }
    if (paid?.Inventory is { } fromInventory)
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
            items.NextRevision(PlayerInventories.MainInventory), [.. fromInventory.Removed, .. fromInventory.Changed.Select(c => c.Id)], fromInventory.Changed),
            $"0x00bf inventory 1: paid (stacks emptied [{string.Join(", ", fromInventory.Removed)}], changed [{string.Join(", ", fromInventory.Changed.Select(c => $"{c.Id}={c.Item.StackSize}"))}])");
    await SendStashPayment(remote, stream, ciphers, conn, stash, paid?.Stash);
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(shop.Tab, ++shop.Revision, [item.Id], [], NpcShops.Manager),
        $"0x00bf shop tab {shop.Tab}: item {item.Id} sold");
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(buy.InventoryId, items.NextRevision(row), [], [bought]),
        $"0x00bf inventory {buy.InventoryId}: bought item {item.Id} from {shop.Npc.Npc.Name} at ({buy.X}, {buy.Y}) ({buy})");
}

// What a price took from the stash (ItemMoves.Price.Stash), to the client: one 0xbf per tab this connection has the
// contents of (the others come whole when it asks, 0xc7).
static async Task SendStashPayment(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, AccountStash stash, AccountStash.Payment? paid)
{
    foreach (var (tab, (removed, changed)) in paid?.Tabs ?? new Dictionary<int, (List<uint>, List<InventoryItem>)>())
        if (TabsSentOf(conn, stash).Contains(tab))
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(stash.InventoryOf(tab), stash.NextRevision(tab), [.. removed, .. changed.Select(c => c.Id)], changed, stash.IsGuild ? InstanceStash.GuildManager : (byte)0),
                $"0x00bf stash tab {tab}: paid (stacks emptied [{string.Join(", ", removed)}], changed [{string.Join(", ", changed.Select(c => $"{c.Id}={c.Item.StackSize}"))}])");
}

// C→S 0x8a: Ctrl + click on an item in the open shop tab: bought into the main inventory (a stack onto stacks of its type,
// then the first free cell), the price paid from the inventory and then the stash in the same transaction
// (ItemMoves.BuyToInventory). Without room or enough to pay, nothing happens.
static async Task CtrlBuyFromShop(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceNpcs.CtrlBuy buy)
{
    OpenShop? shop;
    InventoryItem? item = null;
    lock (conn.Npcs)
    {
        shop = conn.Shop;
        if (shop is not null && shop.Items.Remove(buy.ItemId, out var found)) item = found;
    }
    if (shop is null || item is null)
    {
        Console.WriteLine($"[gateway] {remote} <= 0x008a Ctrl + click buy {buy}: {(shop is null ? "no shop open" : "not in the shop")}");
        return;
    }
    var items = ItemsOf(conn);
    var stash = StashOf(conn);
    (uint Currency, int Amount)? price = item.Item.Price is [var (currency, amount), ..] ? (currency, (int)amount) : null;
    if (ItemMoves.BuyToInventory(items, stash, item.Item with { Price = [] }, price, ShapeOf, out string why) is not var (paid, into))
    {
        lock (conn.Npcs) shop.Items[item.Id] = item;
        Console.WriteLine($"[gateway] {remote} <= 0x008a Ctrl + click buy item {item.Id}: refused ({why})");
        return;
    }
    // The payment and the item land in the same inventory: one 0xbf with every stack that shrank, emptied, grew or arrived.
    var fromInventory = paid?.Inventory;
    var gone = new List<uint>([.. fromInventory?.Removed ?? [], .. fromInventory?.Changed.Select(c => c.Id) ?? [], .. into.Changed.Select(c => c.Id)]);
    var now = new Dictionary<uint, InventoryItem>();
    foreach (var i in (IEnumerable<InventoryItem>)[.. fromInventory?.Changed ?? [], .. into.Changed, .. into.Added]) now[i.Id] = i;
    foreach (var removed in fromInventory?.Removed ?? []) now.Remove(removed);
    await SendStashPayment(remote, stream, ciphers, conn, stash, paid?.Stash);
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(shop.Tab, ++shop.Revision, [item.Id], [], NpcShops.Manager),
        $"0x00bf shop tab {shop.Tab}: item {item.Id} sold");
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
        items.NextRevision(PlayerInventories.MainInventory), [.. gone.Distinct()], [.. now.Values]),
        $"0x00bf inventory 1: bought item {item.Id} from {shop.Npc.Npc.Name} with Ctrl + click" + (price is var (_, a) ? $" for {a}" : ""));
}

// An item goes into the sell offer (C→S 0xb2, or 0x2a on the offer inventory) from the inventory it is in (the cursor, or
// the main inventory on a Ctrl-click); the NPC answers with one Scroll Fragment per offered item in its own offer. As in
// 3.29 the item keeps its inventory cell until the sale ends (PlayerInventories.Offer), and the client keeps it there too:
// no 0xbf takes it out of the inventory, so the client can draw its greyed imprint. A held item goes back to its cell
// (0xbf: off the cursor, into the inventory) first.
static async Task OfferForSale(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, NpcSale sale,
    uint fromInventory, uint itemId, byte x, byte y)
{
    var items = ItemsOf(conn);
    if (PlayerInventories.Row(fromInventory) is not byte row || items.Offer(row, itemId, x, y, ShapeOf) is not var (waits, offered))
    {
        Console.WriteLine($"[gateway] {remote} sell offer: item {itemId} is not in inventory {fromInventory} (or no cell to wait in)");
        return;
    }
    if (waits != row)
    {
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(fromInventory, items.NextRevision(row), [offered.Id], []),
            $"0x00bf inventory {fromInventory}: remove item {offered.Id} (offered from the cursor)");
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(waits), items.NextRevision(waits), [], [offered]),
            $"0x00bf inventory {PlayerInventories.InventoryId(waits)}: item {offered.Id} waits at ({offered.X}, {offered.Y}) while offered");
    }
    var inOffer = offered with { X = x, Y = y };
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(sale.Mine, ++sale.MineRevision, [], [inOffer], NpcShops.Manager),
        $"0x00bf sell offer: item {inOffer.Id} at ({x}, {y})");
    await SendSaleReward(remote, stream, ciphers, conn, sale);
}

// An offered item taken back (C→S 0xb3, or 0x28 on the offer inventory) is in its inventory cell again: it never left the
// client's inventory, so only the offer changes.
static async Task TakeFromSale(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, NpcSale sale, uint itemId)
{
    var items = ItemsOf(conn);
    if (items.TakeBack(itemId) is not var (row, back))
    {
        Console.WriteLine($"[gateway] {remote} sell offer item {itemId}: not offered");
        return;
    }
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(sale.Mine, ++sale.MineRevision, [itemId], [], NpcShops.Manager),
        $"0x00bf sell offer: remove item {itemId} (back in inventory {PlayerInventories.InventoryId(row)} at ({back.X}, {back.Y}))");
    await SendSaleReward(remote, stream, ciphers, conn, sale);
}

// The NPC's offer: one Scroll Fragment (each its own stack of 1) per offered item, replacing what it offered before.
static async Task SendSaleReward(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, NpcSale sale)
{
    var items = ItemsOf(conn);
    int count = items.Offered().Count;
    var fragment = Items.Find(NpcShops.SellReward);
    var reward = new List<InventoryItem>();
    if (fragment is not null)
        for (int i = 0; i < Math.Min(count, NpcShops.Offer.Width * NpcShops.Offer.Height); i++)
            reward.Add(new InventoryItem(items.NewItemId(), (byte)(i % NpcShops.Offer.Width), (byte)(i / NpcShops.Offer.Width), ItemCatalog.Create(fragment)));
    List<uint> before;
    lock (conn.Npcs)
    {
        before = [.. sale.Reward.Select(r => r.Id)];
        sale.Reward = reward;
    }
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(sale.Theirs, ++sale.TheirsRevision, before, reward, NpcShops.Manager),
        $"0x00bf {sale.Npc.Npc.Name}'s offer: {reward.Count} x {NpcShops.SellReward}");
}

// C→S 0xb1: Cancel (u8 0). The offered items go back to the inventory and S→C 0xb9 closes the window.
static async Task AnswerSale(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, (uint Other, byte Answer) a)
{
    Console.WriteLine($"[gateway] {remote} <= 0x00b1 trade answer {a.Answer} (trade {a.Other})");
    await CloseSale(remote, stream, ciphers, conn, accepted: a.Answer != 0);
}

// An item Shift-clicked in an inventory while the sell window is open goes into a free cell of the offer.
static async Task OfferFromInventory(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, NpcSale sale, uint inventory, uint itemId)
{
    var items = ItemsOf(conn);
    byte? row = PlayerInventories.Row(inventory);
    var it = row is byte r ? items.Items(r).FirstOrDefault(i => i.Id == itemId) : null;
    if (it is null || ShapeOf(it.Item) is not { } shape) { Console.WriteLine($"[gateway] {remote} sell offer: item {itemId} not in inventory {inventory}"); return; }
    List<(InventoryItem Item, PlayerInventories.Shape? Shape)> offered = [.. items.Offered().Select(o => (o, ShapeOf(o.Item)))];
    (byte X, byte Y)? cell = null;
    for (int x = 0; x + shape.Width <= NpcShops.Offer.Width && cell is null; x++)
        for (int y = 0; y + shape.Height <= NpcShops.Offer.Height && cell is null; y++)
            if (!offered.Any(o => o.Shape is { } s && o.Item.X < x + shape.Width && x < o.Item.X + s.Width && o.Item.Y < y + shape.Height && y < o.Item.Y + s.Height))
                cell = ((byte)x, (byte)y);
    if (cell is not { } c) { Console.WriteLine($"[gateway] {remote} sell offer: no room for item {itemId}"); return; }
    await OfferForSale(remote, stream, ciphers, conn, sale, inventory, itemId, c.X, c.Y);
}

// Ends the sale. Accepted: the offered items are gone and the NPC's offer goes into the inventory, in one save; when the
// offer doesn't fit (the offered items' cells free up first, so it nearly always does), nothing changes and the window
// stays open. Cancelled: every offered item is in its cell again (it kept it). Then S→C 0xb9 closes the window.
static async Task CloseSale(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, bool accepted)
{
    NpcSale? sale;
    lock (conn.Npcs) sale = conn.Sale;
    if (sale is null) Console.WriteLine($"[gateway] {remote} trade: no sale open");
    accepted &= sale is not null;
    var items = ItemsOf(conn);
    if (accepted)
    {
        int count = items.Offered().Count;
        var sold = items.OfferedInPlace();   // where the client still has them: they leave its inventory now
        var fragment = Items.Find(NpcShops.SellReward);
        PlayerInventories.Stacking? added = null;
        string? refused = count == 0 ? null
            : fragment is null ? $"{NpcShops.SellReward} is not in the game data"
            : (added = items.Sell(ItemCatalog.Create(fragment) with { StackSize = (ushort)count }, ShapeOf)) is null ? "not enough room in the inventory for the offer"
            : null;
        if (refused is not null)
        {
            Console.WriteLine($"[gateway] {remote} sale accepted: refused ({refused}); the window stays open");
            await conn.Commands!.Reply(fragment is null ? "This sale can't be made: the game data is missing." : "Not enough room in your inventory.");
            return;
        }
        Console.WriteLine($"[gateway] {remote} sale accepted: {count} item(s) sold for {count} × {NpcShops.SellReward}");
        foreach (var group in sold.GroupBy(s => s.Row).Where(g => g.Key != PlayerInventories.MainInventory || added is not { Any: true }))
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(group.Key), items.NextRevision(group.Key),
                [.. group.Select(s => s.Item.Id)], []), $"0x00bf inventory {PlayerInventories.InventoryId(group.Key)}: {group.Count()} item(s) sold");
        // The main inventory's sold items and the reward in one 0xbf.
        if (added is { Any: true })
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                items.NextRevision(PlayerInventories.MainInventory),
                [.. sold.Where(s => s.Row == PlayerInventories.MainInventory).Select(s => s.Item.Id), .. added.RemovedIds], added.AddedItems),
                $"0x00bf inventory 1: sold items out, {added.Changed.Count} stack(s) grown, {added.Added.Count} new (the NPC's offer)");
    }
    else if (sale is not null)
    {
        // The offered items never left the client's inventory: nothing to send back.
        int back = items.CancelOffers().Count;
        if (back > 0) Console.WriteLine($"[gateway] {remote} sale cancelled: {back} offered item(s) stay where they are");
    }
    lock (conn.Npcs) if (conn.Sale == sale) conn.Sale = null;
    await Send(remote, stream, ciphers, InstanceNpcs.TradeClose(0, accepted), $"0x00b9 sell window closed ({(accepted ? "accepted" : "cancelled")})");
    // The client is still talking to the NPC (session +0xd7c0 = 2, +0xd7c4 = the NPC, live 2026-10-19) and the player cannot
    // move until 0x84 with object 0 ends the talk. Cancel sends 0x85 for it, Accept sends nothing, so end it here.
    conn.TalkingTo = 0;
    conn.InNpcTalk = false;
    await Send(remote, stream, ciphers, InstanceNpcs.Talk(0), "0x0084 object 0: npc dialogue closed (sale over)");
}

// Sell Items: two empty offer inventories (manager 1: the player's offer, the NPC's), then S→C 0xb8 with no other party
// (an NPC sale) opens the sell window. Being tested.
static async Task OpenNpcSale(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, SpawnedNpc npc)
{
    uint mine = NpcShops.FirstTabId + 0x10, theirs = mine + 1;
    ItemsOf(conn).CancelOffers();   // none should be left; a new sale must not count them
    lock (conn.Npcs) conn.Sale = new NpcSale(npc, mine, theirs);
    await Send(remote, stream, ciphers, InstanceInventories.AddInventory(mine, NpcShops.Offer, NpcShops.Manager), $"0x00c0 sell offer {mine} (manager {NpcShops.Manager})");
    await Send(remote, stream, ciphers, InstanceInventories.AddInventory(theirs, NpcShops.Offer, NpcShops.Manager), $"0x00c0 {npc.Npc.Name}'s offer {theirs} (manager {NpcShops.Manager})");
    conn.InNpcTalk = false;
    await Send(remote, stream, ciphers, InstanceNpcs.TradeOpen(0, mine, theirs, InstanceNpcs.TradeActive | InstanceNpcs.TradeNoMouseOver, npc.Npc.Name, npc.Npc.Row),
        $"0x00b8 open the sell window with {npc.Npc.Name} (offers {mine}, {theirs})");
}

// Purchase Items: the NPC's stock as one shop tab (S→C 0xc0, inventory manager 1), then S→C 0x8c opens the buy window on it.
static async Task OpenNpcShop(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint npcObject, SpawnedNpc npc)
{
    if (Items.Error is { } error) { Console.WriteLine($"[gateway] {remote} shop: {error}"); return; }
    var notes = new List<string>();
    var items = ItemsOf(conn);
    var stock = NpcShops.Fill(npc.Npc.Id, Items, items.NewItemId, notes);
    foreach (var n in notes) Console.WriteLine($"[gateway] {remote} shop {npc.Npc.Name}: {n}");
    uint tab = NpcShops.FirstTabId;
    lock (conn.Npcs) conn.Shop = new OpenShop(npc, tab, stock.ToDictionary(i => i.Id));
    await Send(remote, stream, ciphers, InstanceInventories.AddInventory(tab, NpcShops.Tab, NpcShops.Manager, stock),
        $"0x00c0 shop tab {tab} (manager {NpcShops.Manager}) of {npc.Npc.Name}: {stock.Count} items");
    conn.InNpcTalk = false;
    await Send(remote, stream, ciphers, InstanceNpcs.Dialogue(npcObject, (ushort)Math.Max(0, npc.Npc.ShopRow), false, [tab], []),
        $"0x008c open the buy window of {npc.Npc.Name} (NPCShop row {npc.Npc.ShopRow}) on tab {tab}");}

// Activates this area's waypoint for the character (saved, added to the saved ones) and tells the client: a new 0x4c with
// the area's bit set plays the waypoint's activation. Towns activate on entry, other waypoints when the player comes near
// or clicks them.
static async Task ActivateWaypoint(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string why)
{
    if (WaypointCatalog.Areas.Find(conn.Layout.Hash) is not { } entry) return;
    lock (conn.Waypoints) if (!conn.Waypoints.Add(entry.Id)) return;
    CharacterSaves.AddWaypoints(CharacterName(conn), [entry.Id]);
    await SendWaypointList(remote, stream, ciphers, conn, $"waypoint of {entry.Name} ({entry.Id}) activated: {why}");
}

// After the player is in: a town's waypoint activates on entry, then the character's waypoints (S→C 0x4c).
static async Task SendWaypoints(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    if (WaypointCatalog.Areas.Entries.Count == 0) return;
    if (WaypointCatalog.Areas.Find(conn.Layout.Hash) is { IsTown: true } town)
    {
        bool added;
        lock (conn.Waypoints) added = conn.Waypoints.Add(town.Id);
        if (added) CharacterSaves.AddWaypoints(CharacterName(conn), [town.Id]);
    }
    await SendWaypointList(remote, stream, ciphers, conn, "the character's waypoints");
}

static async Task SendWaypointList(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string why)
{
    var entries = WaypointCatalog.Areas.Entries;
    List<int> bits;
    lock (conn.Waypoints) bits = [.. Enumerable.Range(0, entries.Count).Where(i => conn.Waypoints.Contains(entries[i].Id))];
    // The pins need their areas' access quest flags first (the player object carried the ones known at creation).
    if (conn.PlayerSent)
    {
        var flags = PlayerQuestFlags(conn);
        await Send(remote, stream, ciphers, InstanceEntities.PlayerQuestFlagsUpdate(PlayerId, flags), $"0x02ba player quest flags: {flags.Count}");
    }
    await Send(remote, stream, ciphers, InstanceWaypoints.Waypoints(bits), $"0x004c waypoints: {bits.Count} activated ({why})");
}

// The player's quest flags as the client keeps them (QuestFlags rows, 16-waypoints.md §3): the flags the character's
// quests set (12-quests.md §5; the client checks them too: Lioneye's Watch is accessible only with a1q1-KilledHillock),
// and what the World panel needs (16-waypoints.md §4): the visit flags of every area entered or whose waypoint is
// activated (they reveal the neighbouring pins and so the connection lines), and the access flags of the activated
// waypoints' areas (their pins become travel targets).
static List<int> PlayerQuestFlags(ClientState conn)
{
    lock (conn.Waypoints)
        return [.. WaypointCatalog.Areas.Entries.Where(e => conn.Waypoints.Contains(e.Id)).SelectMany(e => e.AccessFlags ?? [])
            .Concat(conn.Visited.Concat(conn.Waypoints).SelectMany(id => WaypointCatalog.Visits.For(id)))
            .Concat(Quests.Rows(conn.QuestFlags))
            .Distinct().Order()];
}

// Entering an area marks it visited (saved, added to the saved ones) before the player object carries the quest flags.
static void RecordVisit(ClientState conn)
{
    if (WaypointCatalog.Visits.For(conn.Layout.Id).Count == 0) return;
    lock (conn.Waypoints) if (!conn.Visited.Add(conn.Layout.Id)) return;
    CharacterSaves.AddVisited(CharacterName(conn), [conn.Layout.Id]);
}

// C→S 0x4d: a waypoint picked on the World panel. An activated waypoint changes area (the player arrives next to its
// waypoint); with Ctrl, the instance chooser first.
static async Task TravelByWaypoint(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceWaypoints.Travel travel)
{
    var entry = WaypointCatalog.Areas.Find(travel.AreaHash16);
    bool activated;
    lock (conn.Waypoints) activated = entry is not null && conn.Waypoints.Contains(entry.Id);
    Console.WriteLine($"[gateway] {remote} <= 0x004d waypoint travel to 0x{travel.AreaHash16:x4} ({entry?.Name ?? "no waypoint"}), " +
        $"u32 0x{travel.U32:x}{(travel.Ctrl ? ", Ctrl" : "")}{(activated ? "" : ": not activated, ignored")}");
    if (!activated) return;
    if (travel.Ctrl) await ShowInstances(remote, stream, ciphers, conn, travel.AreaHash16, atWaypoint: true);
    else await ChangeArea(remote, stream, ciphers, conn, new PendingTransfer(travel.AreaHash16, 0, false, AtWaypoint: true), $"waypoint to {entry!.Name}");
}

// The player uses a skill on a monster (C→S 0x24, e.g. the default attack on a left click). The client walks into range
// first, so the server walks its estimate too (WalkEstimator.Approach, reach = the weapon's MaxAttackDistance in cells,
// centre to centre) and only then starts the swing: it echoes the action aimed at the monster (0x1ab, flags | 0x80 +
// target id) and, at the contact point (half the attack time), lowers the monster's life (0x1b1) by the unarmed damage
// (Characters.dat MinDamage..MaxDamage raised by the increased melee physical damage stats). Any later action (a move, another click) cancels both.
// One attack per attack time (WeaponSpeed ms scaled by the player's attack speed stats, a quarter early allowed for
// jitter); earlier repeats of 0x24 are ignored. At 0 the client's Life component runs its death; the corpse is
// removed 10 s later.
static async Task Attack(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
    InstanceTransfer.UseSkillOnObject use, SpawnedMonster m)
{
    var weapon = Skills.UnarmedFor(conn.ClassRow);
    double now = conn.Clock.Elapsed.TotalSeconds, duration;
    lock (conn.Monsters) duration = weapon.AttackSeconds(CharacterPlayerStats(conn));
    // The client sends one 0x24 per swing while the button is held; jitter makes the next one arrive a little before a
    // full attack time, so accept it up to a quarter of an attack early.
    lock (conn.Monsters)
        if (now < conn.NextAttackAt - duration / 4)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0024 skill 0x{use.SkillId:x4} seq 0x{use.Sequence:x4} ignored: {conn.NextAttackAt - now:0.000} s before the next attack");
            return;
        }
    int action = Interlocked.Increment(ref conn.ActionCount);
    (int X, int Y) target;
    lock (conn.Monsters) target = m.Walk.Position(now);
    var (here, stop, arrive) = conn.Walk.Approach(target.X, target.Y, weapon.Range, now);
    Console.WriteLine($"[gateway] {remote} <= 0x0024 skill 0x{use.SkillId:x4} seq 0x{use.Sequence:x4} on 0x{use.TargetId:x}: player ({here.X}, {here.Y}), " +
        $"monster ({target.X}, {target.Y}), distance {Dist(here, target):0.0}; swing from ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
    if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
    if (conn.ActionCount != action) { Console.WriteLine($"[gateway] {remote} attack on 0x{use.TargetId:x} cancelled before the swing"); return; }
    now = conn.Clock.Elapsed.TotalSeconds;
    lock (conn.Monsters)
    {
        target = m.Walk.Position(now);
        conn.NextAttackAt = Math.Max(now, conn.NextAttackAt) + duration;
    }
    if (Dist(stop, target) > weapon.Range + 2)
    {
        Console.WriteLine($"[gateway] {remote} attack on 0x{use.TargetId:x} out of reach: {Dist(stop, target):0.0} > {weapon.Range}");
        return;
    }
    await Send(remote, stream, ciphers,
        InstanceProtocol.ActionStarted(PlayerId, stop.X, stop.Y, target.X, target.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
        $"0x01ab player skill 0x{use.SkillId:x4} on monster 0x{use.TargetId:x} ({m.Label}, life {m.Life}/{m.MaxLife}), attack time {duration:0.000} s");
    if (m.Life == 0) return;
    await Task.Delay(TimeSpan.FromSeconds(duration / 2));   // the contact point, roughly
    uint life;
    int accuracy, evasion;
    lock (conn.Monsters)
    {
        if (m.Life == 0) return;
        if (conn.ActionCount != action)
        {
            Console.WriteLine($"[gateway] {remote} attack on 0x{use.TargetId:x} cancelled by a later action");
            return;
        }
        accuracy = conn.Pools.Accuracy;
        evasion = Monsters.BaseEvasion(m.Level) ?? 0;
    }
    int hitChance = DamageModel.AccuracyHitChance(evasion, accuracy);
    if (!DamageModel.Hits(hitChance, Random.Shared.Next(100)))
    {
        Console.WriteLine($"[gateway] {remote} attack on 0x{use.TargetId:x} missed: accuracy {accuracy} vs evasion {evasion}, " +
            $"{hitChance}% chance");
        return;
    }
    int attackEffect = SkillEffectId(conn, SkillCatalog.FirstSkillId) is { } attackId ? Skills.EffectRow(attackId) : -1;
    var player = CharacterPlayerStats(conn);
    var hit = weapon.HitDamage(player)
        .Convert(ConversionFor(conn, attackEffect, 1, attack: true));
    // Impale: the stacks already on the target add their stored damage to this hit, this hit uses one of their five
    // hits, and the hit's own physical damage may store a new stack for the hits after it.
    double physical = hit[DamageType.Physical].Max;
    double stored = m.Impale.Bonus;
    if (stored > 0) hit = hit.Add(DamageType.Physical, new DamageRange(stored, stored));
    m.Impale.AfterHit();
    int impaleChance = Stats.Key("impale_on_hit_%_chance") is uint impaleKey ? player.GetValueOrDefault(impaleKey) : 0;
    bool impaled = impaleChance > 0 && Random.Shared.Next(100) < impaleChance && m.Impale.Apply(physical);
    // Read before the hit is applied: a killing blow clears the target's impales.
    int impaleStacks = m.Impale.Stacks;
    double impaleStored = m.Impale.Bonus;
    int damage = hit.RollTotal(Random.Shared);
    bool critical = conn.Pools.AttackCriticalStrikeChance > 0
        && Random.Shared.Next(10_000) < conn.Pools.AttackCriticalStrikeChance;
    if (critical) damage = DamageModel.CriticalDamage(damage, conn.Pools.CriticalStrikeMultiplier);
    lock (conn.Monsters)
    {
        if (m.Life == 0) return;
        if (conn.ActionCount != action)
        {
            Console.WriteLine($"[gateway] {remote} attack on 0x{use.TargetId:x} cancelled by a later action");
            return;
        }
        m.Life = (uint)Math.Max(0, (long)m.Life - damage);
        life = m.Life;
        conn.LastEnemyHitAt = conn.Clock.Elapsed.TotalSeconds;
        if (life == 0) { m.Aggro = false; m.Dead = true; m.Impale.Clear(); }
    }
    // At 0 the client's Life tick (0x141e6a190) stops notifying its listeners, so the mini life bar would keep the last
    // value: hide it with the stat its listener watches (hide_mini_life_bar).
    if (life == 0)
        await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(use.TargetId, [(StatKeys.HideMiniLifeBar, 1)]),
            $"0x01b3 monster 0x{use.TargetId:x} hide_mini_life_bar = 1");
    await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(use.TargetId, life),
        $"0x01b1 monster 0x{use.TargetId:x} hit for {damage}{(critical ? " (critical)" : "")}" +
        (stored > 0 ? $", impale +{stored:0.#} ({impaleStacks} stack(s))" : "") +
        (impaled ? $", impaled ({impaleStacks} stack(s), {impaleStored:0.#} stored)" : "") +
        $", life {life}/{m.MaxLife}{(life == 0 ? " (dies)" : "")}");
    if (life == 0) await MonsterKilled(remote, stream, ciphers, conn, use.TargetId, m);
}

// A gem skill used on a monster (C→S 0x24 with a skill id other than the default attack's): echo the action from where the
// player stands, aimed at the monster, so the client plays the cast. CastGemSkill then pays the cost and runs the
// skill's effect (projectile, aura or self-cast).
static async Task CastOn(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
    InstanceTransfer.UseSkillOnObject use, SpawnedMonster m)
{
    Interlocked.Increment(ref conn.ActionCount);
    double now = conn.Clock.Elapsed.TotalSeconds;
    (int X, int Y) target;
    lock (conn.Monsters) target = m.Walk.Position(now);
    var here = conn.Walk.Position(now);
    conn.Walk.Order(here.X, here.Y, now);
    await Send(remote, stream, ciphers,
        InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, target.X, target.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
        $"0x01ab player skill 0x{use.SkillId:x4} cast on monster 0x{use.TargetId:x} ({m.Label}) from ({here.X}, {here.Y}) to ({target.X}, {target.Y}), flags 0x{use.Flags:x4}");
    _ = CastGemSkill(remote, stream, ciphers, conn, use.SkillId, here, target, use.TargetId);
}

// A gem skill's cast: Righteous Fire puts its buff on, any other skill launches its projectile (if it has one). False when
// it was refused (cooldown, mana), so its use is not confirmed to the client.
static async Task<bool> CastGemSkill(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
    ushort skillId, (int X, int Y) from, (int X, int Y) to, uint targetId = 0)
{
    var gem = GemSkillEffects(conn.Character).FirstOrDefault(g => g.Id == skillId);
    // Vaal souls are not gathered yet (Roadmap "Gems later"), so a Vaal skill never has the souls it needs.
    if (gem.Name is not null && Skills.VaalSouls(gem.Effect, gem.Level) is int souls and > 0)
    {
        Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: needs {souls} Vaal souls, has 0");
        return false;
    }
    // A skill with a cooldown (GEPL Cooldown: auras 1200 ms) is refused until it is over: spamming Haste toggled it.
    if (gem.Name is not null && Skills.CooldownMs(gem.Effect, gem.Level) is int cooldown and > 0)
    {
        double now = conn.Clock.Elapsed.TotalSeconds, readyAt;
        lock (conn.Monsters)
        {
            readyAt = conn.SkillReadyAt.GetValueOrDefault(gem.Effect);
            if (now >= readyAt) conn.SkillReadyAt[gem.Effect] = now + cooldown / 1000.0;
        }
        if (now < readyAt)
        {
            Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: on cooldown for {(readyAt - now) * 1000:0} ms more");
            return false;
        }
    }
    if (gem.Name is not null && Skills.Reservation(gem.Effect, gem.Level) is { } reservation)
    {
        await ToggleAura(remote, stream, ciphers, conn, skillId, gem.Effect, gem.Name, reservation, gem.Level, gem.Quality);
        return true;
    }
    if (gem.Name is not null)
    {
        if (!PayMana(conn, gem.Effect, gem.Level, gem.Quality, out uint cost))
        {
            Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: not enough mana ({conn.PlayerMana} < {cost})");
            return false;
        }
        if (cost > 0)
            await SendPlayerMana(remote, stream, ciphers, conn,
                $"0x01b1 player mana {conn.PlayerMana} (-{cost} for {gem.Name})");
    }
    if (gem.Name is not null && IsRighteousFire(gem.Effect))
        await ActivateRighteousFire(remote, stream, ciphers, conn, skillId, gem.Effect, gem.Level, gem.Quality);
    else
        await LaunchProjectile(remote, stream, ciphers, conn, skillId, from, to, targetId);
    return true;
}

// An instant skill (C→S 0x191): its effect, then S→C 0x1ce, which has the client run the skill's action without animation
// and consume a use of it: the skill bar's cooldown starts (the tooltip showed the cooldown, the bar did not run it).
static async Task UseInstantSkill(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
    ushort skillId, uint targetId, (int X, int Y) at)
{
    if (await CastGemSkill(remote, stream, ciphers, conn, skillId, at, at, targetId))
        await Send(remote, stream, ciphers, InstanceProtocol.InstantAction(PlayerId, skillId, at.X, at.Y, targetId),
            $"0x01ce player skill 0x{skillId:x4}: instant action (its use and cooldown)");
}

// Aura skills toggle: the first cast reserves their mana and puts their buff on the player, the next cast turns them
// off. The reserved mana is sent in the Life pool and in every later 0x1b1 mana update; the client does not derive it.
static async Task ToggleAura(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
    ushort skillId, int effect, string name, (int Flat, int Percent) reservation, int level, int quality)
{
    string? effectId = Skills.EffectId(effect);
    if (effectId is null || !AuraCatalog.Supported(effectId))
    {
        Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {name}: reservation is known but this aura's effect is not implemented yet");
        return;
    }
    AuraCatalog.Aura? ended = null;
    AuraCatalog.Aura? started = null;
    lock (conn.Monsters)
    {
        if (conn.Auras.Remove(skillId, out var old))
            ended = old;
        else if (conn.Auras.Values.FirstOrDefault(a => a.EffectId == effectId) is { } same)
        {
            // One aura of a kind at a time: a second gem of the same aura can't start it.
            Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {name}: refused, {same.Name} already runs (skill 0x{same.SkillId:x4})");
            return;
        }
        else
        {
            var aura = new AuraCatalog.Aura(skillId, effect, effectId, name, conn.NextBuff++,
                AuraCatalog.BuffRow(effectId), reservation.Flat, reservation.Percent, level, quality);
            conn.Auras[skillId] = aura;
            int reserved = AuraCatalog.ReservedMana(conn.Pools, conn.Auras.Values);
            if (reserved > conn.Pools.Mana)
            {
                conn.Auras.Remove(skillId);
                Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {name}: needs {reserved} mana reserved, " +
                    $"maximum is {conn.Pools.Mana}");
                return;
            }
            started = aura;
            conn.PlayerMana = (uint)Math.Min(conn.PlayerMana, (uint)(conn.Pools.Mana - reserved));
        }
        conn.Pools = PoolsFor(conn, conn.Pools.Level);
        conn.ApplyMovementSpeed();
    }

    if (ended is not null)
    {
        await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, ended.Buff),
            $"0x01c2 player buff {ended.Buff}: {ended.Name} ends");
    }
    if (started is not null)
    {
        await Send(remote, stream, ciphers,
            InstanceEntities.AddBuff(PlayerId, started.Buff, started.BuffRow, PlayerId, started.SkillId),
            $"0x01c1 player buff {started.Buff}: {started.Name} (BuffDefinitions row {started.BuffRow})");
    }
    await SendCharacterStats(remote, stream, ciphers, conn, started is not null ? $"aura {name} on" : $"aura {ended!.Name} off");
    await SendPlayerMana(remote, stream, ciphers, conn,
        $"0x01b1 player mana {conn.PlayerMana}/{conn.Pools.Mana}, {ReservedMana(conn)} reserved");
}

// Experimental: a projectile gem skill's projectile. After the cast time the server creates the Projectiles.dat object
// named after the effect (Fireball → Metadata/Projectiles/Fireball) just ahead of the caster, wakes it and moves it to the
// aimed point and on to its range with 0x1ba at the row's ProjectileSpeed (world units per second, 250/23 per grid
// cell), stopping at the first wall or monster on its path; a monster takes the skill's damage packet
// (SkillCatalog.HitDamage, after conversion). It explodes there; then the object is removed.
static async Task LaunchProjectile(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
    ushort skillId, (int X, int Y) from, (int X, int Y) to, uint targetId = 0)
{
    var gem = GemSkillEffects(conn.Character).FirstOrDefault(g => g.Id == skillId);
    if (gem.Name is null || Skills.ProjectileFor(gem.Effect) is not { } projectile)
    {
        Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4}: no projectile");
        return;
    }
    int action = conn.ActionCount;
    // Released at the cast animation's contact keyframe, which the client plays stretched to the cast time.
    double release = Animations.SpellRelease(Skills.ClassFor(conn.ClassRow).AnimatedObject);
    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, CastMs(conn, gem.Effect) * release)));
    if (conn.ActionCount != action || conn.Closed) { Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} cancelled before release"); return; }
    await FlyProjectile(remote, stream, ciphers, conn, projectile, from, to, m =>
    {
        // The player's stats (passives, items, auras) under the skill's own, as GemSkills sends them. Keep the hit's
        // types separate so conversion and per-type mitigation apply.
        var stats = FinalPlayerStats(conn).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        foreach (var (k, v) in Skills.EffectStats(gem.Effect, gem.Level, gem.Quality)) stats[k] = v;
        var hit = SpellDamage.Packet(Skills.HitDamage(gem.Effect, gem.Level, gem.Quality),
                name => Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0)
            .Convert(ConversionFor(conn, gem.Effect, gem.Level, attack: false));
        int damage = hit.RollTotal(Random.Shared);
        bool critical = conn.Pools.SpellCriticalStrikeChance > 0
            && Random.Shared.Next(10_000) < conn.Pools.SpellCriticalStrikeChance;
        if (critical) damage = DamageModel.CriticalDamage(damage, conn.Pools.SpellCriticalStrikeMultiplier);
        return (damage, critical);
    });
}

// A projectile's flight (LaunchProjectile): created just ahead of from, aimed at to, it flies on
// to its range and stops at the first wall or monster on its path; a monster hit takes damage(monster).
static async Task FlyProjectile(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
    (string Id, int Speed) projectile, (int X, int Y) from, (int X, int Y) to, Func<SpawnedMonster, (int Damage, bool Critical)> damageTo,
    bool log = true)
{
    uint id = conn.NextObjectId++;
    double dx = to.X - from.X, dy = to.Y - from.Y, aim = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    // Game_PROJECTILE_SPEED (cells per second) overrides the speed, e.g. to watch a slow projectile while testing.
    float cellsPerSecond = float.TryParse(Environment.GetEnvironmentVariable("Game_PROJECTILE_SPEED"), inv, out var ps) && ps > 0
        ? ps : projectile.Speed / (250f / 23f);
    // It leaves a little ahead of the caster (Game_PROJECTILE_AHEAD cells, default 4) and flies toward the clicked point
    // or monster and on to Game_PROJECTILE_RANGE cells (default 120, ~1.2 s at the real speed), clamped to the area.
    double ahead = double.TryParse(Environment.GetEnvironmentVariable("Game_PROJECTILE_AHEAD"), inv, out var pa) ? pa : 4;
    double range = double.TryParse(Environment.GetEnvironmentVariable("Game_PROJECTILE_RANGE"), inv, out var pr) && pr > 0 ? pr : 120;
    ahead = Math.Min(ahead, aim - 1);
    from = ((int)Math.Round(from.X + dx / aim * ahead), (int)Math.Round(from.Y + dy / aim * ahead));
    double length = Math.Max(range - ahead, 1);
    if (TerrainHeights.Size(conn.Layout.Id, conn.Seed) is (int w, int h))
        foreach (var (d, lo, hi) in new[] { (dx, -from.X, w - 1 - from.X), (dy, -from.Y, h - 1 - from.Y) })
            if (d != 0) length = Math.Min(length, Math.Max(0, (d > 0 ? hi : lo) / (d / aim)));
    // Collision: the first living monster within Game_PROJECTILE_HIT_RADIUS cells (default 4: the row's CollisionRadius
    // 2 plus a monster's body) of the projectile when it passes, using where the monster will be by then. Checked once at
    // launch, half a cell at a time along the path; a monster that walks into the path later is not hit. Walls and
    // doodads: the first cell the area's projectile grid blocks (ProjectileGrid; without one nothing blocks).
    double hitRadius = double.TryParse(Environment.GetEnvironmentVariable("Game_PROJECTILE_HIT_RADIUS"), inv, out var phr) && phr > 0 ? phr : 4;
    double launched = conn.Clock.Elapsed.TotalSeconds;
    uint hitId = 0;
    bool hitWall = false;
    lock (conn.Monsters)
        for (double s = 0; s <= length && hitId == 0 && !hitWall; s += 0.5)
        {
            double px = from.X + dx / aim * s, py = from.Y + dy / aim * s, at = launched + s / cellsPerSecond;
            if (ProjectileGrid.Blocks(conn.Layout.Id, conn.Seed, (int)Math.Round(px), (int)Math.Round(py)) == true)
            {
                hitWall = true;
                length = Math.Max(0, s - 0.5);
                break;
            }
            foreach (var (mid, mon) in conn.Monsters)
            {
                if (mon.Dead || mon.Life == 0) continue;
                var (mx, my) = mon.Walk.Position(at);
                if ((mx - px) * (mx - px) + (my - py) * (my - py) <= hitRadius * hitRadius) { hitId = mid; length = s; break; }
            }
        }
    to = ((int)Math.Round(from.X + dx / aim * length), (int)Math.Round(from.Y + dy / aim * length));
    dx = to.X - from.X; dy = to.Y - from.Y;
    double cells = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
    // Positioned rotation = atan2(dx, -dy) (the player's own moves, traced live 2026-10-18: facing (+15, -18) read 0.668)
    // Game_PROJECTILE_DIRECTION=1 sends the flight direction in Positioned's f1 0x04 block (ProjectileObject.Direction).
    // Live 2026-10-18 it put the fireball under the ground, so it is off by default: the four floats are not a direction.
    var direction = Environment.GetEnvironmentVariable("Game_PROJECTILE_DIRECTION") != "1" ? ((float, float, float, float)?)null
        : ((float)(dx / cells), (float)(dy / cells), 0f, 0f);
    // Hand height: the terrain's height at both ends (scripts/walkmaps capture) plus Game_PROJECTILE_HEIGHT steps of
    // 7.8125 world units (default 8, ~60 units). Without a capture the projectile follows the terrain.
    int up = int.TryParse(Environment.GetEnvironmentVariable("Game_PROJECTILE_HEIGHT"), out int u) ? u : 8;
    int? startHeight = TerrainHeights.At(conn.Layout.Id, conn.Seed, from.X, from.Y) is int t0 ? t0 + up : null;
    int? endHeight = TerrainHeights.At(conn.Layout.Id, conn.Seed, to.X, to.Y) is int t1 ? t1 + up : startHeight;
    if (startHeight is null) TerrainHeights.Forget(conn.Layout.Id, conn.Seed);
    var obj = new ProjectileObject(projectile.Id, from.X, from.Y, (float)Math.Atan2(dx, -dy), Direction: direction, Height: startHeight);
    uint type = InstanceEntities.ObjectTypeHash(projectile.Id);
    await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, type, obj.Blob()), !log ? null :
        $"0x02b9 create projectile 0x{id:x} {projectile.Id} type=0x{type:x8} at ({from.X}, {from.Y})");
    await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), !log ? null : $"0x01a9 projectile 0x{id:x} enters wake zone");
    // 0x1ba's float is the move's duration in seconds, not a speed (InstanceProtocol.MoveTo).
    await Send(remote, stream, ciphers, InstanceProtocol.MoveTo(id, to.X, to.Y, (float)(cells / cellsPerSecond), u28: endHeight is int e ? (uint)e : TerrainHeight), !log ? null :
        $"0x01ba projectile 0x{id:x} to ({to.X}, {to.Y}), {cells:0.0} cells at {cellsPerSecond:0.0} cells/s, height {startHeight?.ToString() ?? "terrain"} → {endHeight?.ToString() ?? "terrain"}" +
        (hitId != 0 ? $", will hit monster 0x{hitId:x}" : hitWall ? ", will hit a wall" : ""));
    await Task.Delay(TimeSpan.FromSeconds(cells / cellsPerSecond));
    if (conn.Closed) return;
    SpawnedMonster? m = null;
    if (hitId != 0) lock (conn.Monsters) m = conn.Monsters.GetValueOrDefault(hitId);
    if (m is not null && m.Life > 0)
    {
        var (damage, critical) = damageTo(m);
        await HitMonster(remote, stream, ciphers, conn, hitId, m, damage, $"{projectile.Id}{(critical ? " (critical)" : "")}", log);
    }
    // The impact: on a monster or a wall the projectile's model becomes the explosion (Game_PROJECTILE_IMPACT_AO overrides
    // it; "none" turns it off), which plays its default animation; the object goes 1.5 s later. At the end of its range it
    // just goes.
    string impact = Environment.GetEnvironmentVariable("Game_PROJECTILE_IMPACT_AO") ?? "Metadata/Effects/Spells/fireball/explode_new2.ao";
    if ((hitId != 0 || hitWall) && impact != "none")
    {
        await Send(remote, stream, ciphers, InstanceEntities.AnimatedModel(id, ProjectileObject.AnimatedComponentIndex, impact),
            $"0x02ba projectile 0x{id:x} explodes ({impact})");
        await Task.Delay(1500);
        if (conn.Closed) return;
    }
    await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove projectile 0x{id:x}");
}

// A hit on a monster for damage: its life goes down (0x1b1) and at 0 it dies (MonsterKilled). A monster another hit has
// killed meanwhile is left alone, so it dies only once (hits that land together).
static async Task HitMonster(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster m,
    int damage, string by, bool log = true)
{
    uint life;
    lock (conn.Monsters)
    {
        if (m.Dead) return;
        m.Life = (uint)Math.Max(0, (long)m.Life - damage);
        life = m.Life;
        conn.LastEnemyHitAt = conn.Clock.Elapsed.TotalSeconds;
        if (life == 0) { m.Aggro = false; m.Dead = true; }
    }
    if (life == 0)
        await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(id, [(StatKeys.HideMiniLifeBar, 1)]),
            !log ? null : $"0x01b3 monster 0x{id:x} hide_mini_life_bar = 1");
    await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(id, life),
        !log && life > 0 ? null : $"0x01b1 monster 0x{id:x} hit by {by} for {damage}, life {life}/{m.MaxLife}{(life == 0 ? " (dies)" : "")}");
    if (life == 0) await MonsterKilled(remote, stream, ciphers, conn, id, m);
}

// The player's granted skills: the default attack with the unarmed weapon's stats and every stat the server set on the
// player since (a skill's stats include the character's; the client reads the skill's).
static IReadOnlyList<GrantedSkill> PlayerSkills(ClientState conn)
{
    var player = FinalPlayerStats(conn).ToList();
    return [.. Skills.PlayerDefaults(player), .. GemSkills(conn.Character, player)];
}

// The player's stats as the server sets them: what /set_stat set, then the attack's final values computed from them
// (the client derives nothing itself: 11-skills.md §4).
// FinalPlayerStats as a full 0x1b3, which only sets the stats it lists: every stat sent before that is gone now (a
// stat only an unequipped item or a refunded passive gave) goes with 0, so the client does not keep its old value.
static List<(uint, int)> PlayerStatsWithCleared(ClientState conn)
{
    var stats = FinalPlayerStats(conn).ToList();
    var now = stats.Select(s => s.Item1).ToHashSet();
    lock (conn.Monsters)
    {
        stats.AddRange(conn.SentStatKeys.Where(k => !now.Contains(k)).Select(k => (k, 0)));
        conn.SentStatKeys = now;
    }
    return stats;
}

static IEnumerable<(uint, int)> FinalPlayerStats(ClientState conn)
{
    var stats = CharacterPlayerStats(conn);
    foreach (var (k, v) in Skills.UnarmedFor(conn.ClassRow).Stats(stats)) stats[k] = v;
    return stats.Select(kv => (kv.Key, kv.Value));
}

// What /set_stat set, then the class's attributes and pools and the passives' stats for the stats it did not set.
static Dictionary<uint, int> CharacterPlayerStats(ClientState conn)
{
    Dictionary<uint, int> stats;
    lock (conn.Monsters) stats = new(conn.PlayerStats);
    foreach (var (k, v) in BaseStats(conn)) stats.TryAdd(k, v);
    // The passives' conditional attack speed is already one final value in Pools; /set_stat's raw attack-speed stats
    // stay additive. main_hand_attack_speed_+% is the total the client swings with, so Unarmed reads only it.
    int devAttackSpeed = 0;
    lock (conn.Monsters)
        foreach (uint key in StatKeys.AttackSpeedInputs)
            devAttackSpeed += conn.PlayerStats.GetValueOrDefault(key);
    stats[StatKeys.MainHandAttackSpeedPct] = conn.Pools.AttackSpeedPct + devAttackSpeed;
    // The character panel's sums of base forms (PanelStats); a stat /set_stat set keeps its value.
    HashSet<uint> devSet;
    lock (conn.Monsters) devSet = [.. conn.PlayerStats.Keys];
    int Get(string name) => Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0;
    var (mainLocal, offLocal) = HandLocalStats(conn);
    foreach (var (stat, value) in PanelStats.Compute(Get, n => mainLocal.GetValueOrDefault(n), n => offLocal.GetValueOrDefault(n))
        .Concat(PanelStats.FromPools(Get, conn.Pools)))
        if (Stats.Key(stat) is uint key && !devSet.Contains(key)) stats[key] = value;
    // The Offence tab's per-hand damage rows (issue #75): the hit the default attack actually swings with, so the panel
    // agrees with the damage the server applies. Only the main hand: the server attacks with it. Those rows are gated by
    // skill_uses_main_hand, which nothing sent before, so the numbers alone would leave them blank.
    if (Stats.Key("skill_uses_main_hand") is uint usesMainHand && !devSet.Contains(usesMainHand)) stats[usesMainHand] = 1;
    var attack = Skills.UnarmedFor(conn.ClassRow).HitDamage(stats);
    foreach (var (stat, value) in PanelStats.HandDamage(attack, "main_hand"))
        if (Stats.Key(stat) is uint key && !devSet.Contains(key)) stats[key] = value;
    // The conditions as they are now; the player tick sends the chance again when they change.
    int suppression = Suppression(conn).Chance;
    foreach (uint key in SuppressionChanceKeys) stats[key] = suppression;
    return stats;
}

// Conversion for one hit: the player's stats plus the skill's own stats (base/skill conversion). Player stats win on
// a key collision; the normal case is that the skill adds stats the player does not have.
static GameServer.Core.World.DamageConversion ConversionFor(ClientState conn, int effectRow, int level, bool attack)
{
    var stats = CharacterPlayerStats(conn);
    if (effectRow >= 0)
        foreach (var (key, value) in Skills.EffectStats(effectRow, level)) stats.TryAdd(key, value);
    return DamageStats.Conversion(stats, Stats, attack);
}

// The character's own stats: the class's attributes and the pools (ClassAttributes), then every other stat of the
// allocated passives, summed (increased life and the attributes are already in the pools; the rest, such as increased
// spell damage, the client only shows).
static IEnumerable<(uint, int)> BaseStats(ClientState conn)
{
    var seen = new HashSet<uint>();
    foreach (var (k, v) in ClassAttributes(conn.ClassRow, conn.Pools)) if (seen.Add(k)) yield return (k, v);
    List<ushort> nodes;
    lock (conn.Monsters) nodes = [.. conn.Passives];
    var extra = CharacterStatSources(conn, nodes);
    if (conn.God)
    {
        foreach (var (stat, value) in GodStats)
            if (Stats.Key(stat) is uint key) extra[key] = extra.GetValueOrDefault(key) + value;
        // the final value the client shows: 100% of maximum life per second, per minute
        if (Stats.Key("life_regeneration_rate_per_minute") is uint lr) extra[lr] = (int)Math.Min(int.MaxValue, (long)conn.Pools.Life * GodLifeRegenPctPerSecond / 100 * 60);
    }
    List<AuraCatalog.Aura> auras;
    lock (conn.Monsters) auras = [.. conn.Auras.Values];
    foreach (var (k, v) in AuraCatalog.RawStats(auras, conn.Pools.AuraEffectPct, Skills, Stats))
        extra[k] = extra.GetValueOrDefault(k) + v;
    foreach (var (k, v) in extra) if (seen.Add(k)) yield return (k, v);
}

// The class's attributes (Characters.dat BaseStrength / BaseDexterity / BaseIntelligence) as final stats: strength,
// dexterity and intelligence for the character panel, and virtual_*_for_requirements, which item requirements are
// checked against (0x1402ce5f0 via the Player cache 0x141e80340, keys 0x3d1b-0x3d1d; global_no_attribute_requirements
// skips the check, global_item_attribute_requirements_+% scales it).
// With the level-based pools (CharacterStats): level, maximum_life / maximum_mana (and their base_ forms),
// accuracy_rating, physical_damage_reduction_rating (armour) and evasion_rating (base_evasion_rating 15), as final
// values, and the attributes' increases: armour/evasion_rating_+%, maximum_energy_shield_+% (intelligence) and
// melee_physical_damage_+% (strength). The Offence panel's accuracy row reads main_hand_accuracy_rating
// (CharacterPanelStats MH_Accuracy); its Chance to
// Hit rows read main_hand_chance_to_hit_% and main_hand_chance_to_hit_evasive_monsters_% directly. The reference is a
// monster of the character's level (the normal and high-dexterity evasion rows of DefaultMonsterStats).
static IEnumerable<(uint, int)> ClassAttributes(int classRow, CharacterStats.Pools pools)
{
    int referenceEvasion = Monsters.BaseEvasion(pools.Level) ?? 0;
    int referenceEvasiveEvasion = Monsters.BaseEvasiveEvasion(pools.Level) ?? 0;
    int chanceToHit = DamageModel.AccuracyHitChance(referenceEvasion, pools.Accuracy);
    int evasiveChanceToHit = DamageModel.AccuracyHitChance(referenceEvasiveEvasion, pools.Accuracy);
    foreach (var (stat, value) in new[] { ("level", pools.Level), ("base_maximum_life", (int)pools.Life), ("maximum_life", (int)pools.Life),
        ("base_maximum_mana", (int)pools.Mana), ("maximum_mana", (int)pools.Mana), ("accuracy_rating", pools.Accuracy),
        ("main_hand_accuracy_rating", pools.Accuracy), ("off_hand_accuracy_rating", pools.Accuracy),
        ("main_hand_chance_to_hit_%", chanceToHit), ("off_hand_chance_to_hit_%", chanceToHit),
        ("main_hand_chance_to_hit_evasive_monsters_%", evasiveChanceToHit),
        ("off_hand_chance_to_hit_evasive_monsters_%", evasiveChanceToHit),
        ("character_sheet_reference_evasion", referenceEvasion),
        ("character_sheet_reference_high_dex_evasion", referenceEvasiveEvasion),
        ("physical_damage_reduction_rating", pools.Armour), ("physical_damage_reduction_rating_+%", pools.ArmourPct),
        ("base_evasion_rating", CharacterStats.BaseEvasion), ("evasion_rating", pools.Evasion), ("evasion_rating_+%", pools.EvasionPct),
        ("maximum_energy_shield_+%", pools.EnergyShieldPct), ("melee_physical_damage_+%", pools.MeleePhysicalDamagePct),
        ("movement_velocity_+%", pools.MovementVelocityPct),
        ("combined_base_block_%", pools.BlockChanceBase), ("attack_block_%", pools.BlockChance),
        // The character panel prints attack block as "capped% (uncapped%)"; the client does not derive the second value.
        ("uncapped_attack_block_%", pools.BlockChanceUncapped),
        // A separate spell block row is only drawn when display_spell_block_chance is non-zero.
        ("spell_block_%", pools.SpellBlockChance), ("display_spell_block_chance", pools.SpellBlockChance),
        ("base_spell_block_%", 0),
        // Resistance display: the client shows fire_damage_resistance_% as the capped value and
        // uncapped_fire_damage_resistance_% (and the other three) in parentheses as the overcap. The passives' base
        // resistances are already summed into the pools, so zero the all-elements input to avoid counting it twice. The
        // maximum-resistance passives stay in BaseStats: the client adds them to its own 75 base. Do not send maximum_*
        // here; those keys are additive maximum-resistance modifiers, so sending 75 would count the base twice.
        ("base_resist_all_elements_%", 0),
        ("base_fire_damage_resistance_%", pools.FireResistance),
        ("fire_damage_resistance_%", DamageModel.Capped(pools.FireResistance, pools.FireMaxResistance)),
        ("uncapped_fire_damage_resistance_%", pools.FireResistance),
        ("base_cold_damage_resistance_%", pools.ColdResistance),
        ("cold_damage_resistance_%", DamageModel.Capped(pools.ColdResistance, pools.ColdMaxResistance)),
        ("uncapped_cold_damage_resistance_%", pools.ColdResistance),
        ("base_lightning_damage_resistance_%", pools.LightningResistance),
        ("lightning_damage_resistance_%", DamageModel.Capped(pools.LightningResistance, pools.LightningMaxResistance)),
        ("uncapped_lightning_damage_resistance_%", pools.LightningResistance),
        ("base_chaos_damage_resistance_%", pools.ChaosResistance),
        ("chaos_damage_resistance_%", DamageModel.Capped(pools.ChaosResistance, pools.ChaosMaxResistance)),
        ("uncapped_chaos_damage_resistance_%", pools.ChaosResistance),
        // The character panel reads the final suppression values, not only the raw passive input.
        ("spell_suppression_chance_%", pools.SpellSuppressionChance),
        ("base_spell_suppression_chance_%", pools.SpellSuppressionChance),
        ("combined_spell_suppression_chance_%", pools.SpellSuppressionChance),
        ("spell_damage_%_suppressed", pools.SpellSuppressionEffect),
        ("base_spell_damage_%_suppressed", pools.SpellSuppressionEffect),
        // The Offence panel's crit rows read these final values (permyriad for chance, percent for multiplier).
        ("main_hand_critical_strike_chance", pools.AttackCriticalStrikeChance),
        ("main_hand_critical_strike_multiplier", pools.CriticalStrikeMultiplier),
        ("spell_critical_strike_chance", pools.SpellCriticalStrikeChance),
        ("spell_critical_strike_multiplier", pools.SpellCriticalStrikeMultiplier) })
        if (Stats.Key(stat) is uint key) yield return (key, value);
    // The class's attributes with the passives' (CharacterStats.For); a pools record made without a class (0) falls back
    // to the class's own.
    var c = Skills.ClassFor(classRow);
    var (str, dex, intel) = pools.Strength + pools.Dexterity + pools.Intelligence > 0
        ? (pools.Strength, pools.Dexterity, pools.Intelligence) : (c.Strength, c.Dexterity, c.Intelligence);
    foreach (var (stat, value) in new[] { ("strength", str), ("dexterity", dex), ("intelligence", intel),
        ("virtual_strength_for_requirements", str), ("virtual_dexterity_for_requirements", dex),
        ("virtual_intelligence_for_requirements", intel) })
        if (Stats.Key(stat) is uint key) yield return (key, value);
}

// The skills of the gems socketed in the equipped items: each gem's GrantedEffects row at the gem's level with its own stats
// (SkillCatalog.EffectStats: damage, cast time, mana cost) after the player's, with ids after the default attack's that
// no basic action uses (SkillCatalog.SkillIds).
static IEnumerable<GrantedSkill> GemSkills(string? character, IEnumerable<(uint, int)>? player = null)
{
    var playerStats = player?.ToList() ?? [];
    foreach (var (id, effect, name, level, quality) in GemSkillEffects(character))
    {
        var stats = new Dictionary<uint, int>();
        foreach (var (k, v) in playerStats) stats[k] = v;
        var own = Skills.EffectStats(effect, level, quality);
        foreach (var (k, v) in own) stats[k] = v;
        // Increased cast speed shortens the cast the client plays (the final values: 11-skills.md §4).
        if (CastSpeedPct(stats) is int castSpeed and not 0 && Skills.CastMs(effect) is int castMs and > 0)
        {
            int ms = (int)Math.Round(castMs / (1 + castSpeed / 100.0));
            if (Stats.Key("virtual_base_spell_cast_time_ms") is uint vk) stats[vk] = ms;
            if (Stats.Key("hundred_times_casts_per_second") is uint hk) stats[hk] = (int)Math.Round(100_000.0 / Math.Max(1, ms));
        }
        // A spell's tooltip DPS is its own hit (SpellDamage) × casts per second, not the default attack's values that
        // came with the player's stats.
        int Get(string name) => Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0;
        if (SpellDamage.Hit(Get) is { Max: > 0 } hit && Get("hundred_times_casts_per_second") is int cps and > 0)
        {
            var (perUse, perSecond) = SpellDamage.Display(hit, cps / 100.0);
            stats[StatKeys.DisplayHundredTimesDamagePerSkillUse] = perUse;
            stats[StatKeys.HundredTimesDamagePerSecond] = perSecond;
        }
        if (Skills.FindByRow(id, effect, level, [.. stats.Select(kv => (kv.Key, kv.Value))]) is { } skill)
        {
            Console.WriteLine($"[gateway] gem skill 0x{skill.Id:x4} {name} level {level} quality {quality} (effect {effect}): GEPL {skill.EffectsPerLevelRow}, " +
                $"GESSPL {skill.StatSetsPerLevelRow}, " + string.Join(", ", own.Select(kv => $"{Stats.Name(kv.Item1)} {kv.Item2}")));
            yield return skill;
        }
    }
}

// What a skill bar slot holds, as saved (CharacterSaves): a gem or the default attack by GrantedEffects Id (skill ids
// change with the socketed gems), a basic action as "Action/<ActionTypes Id>" ("Action/Move": "Move only" on a mouse
// button), "" for a cleared slot. Null for an id the server does not know.
static string? SkillBarEntry(ClientState conn, ushort skillId) =>
    skillId == 0 ? "" : SkillEffectId(conn, skillId) ?? (Skills.ActionName(skillId) is { } a ? "Action/" + a : null);

// The saved skill bar as the player's current skill ids, sent with S→C 0x53. The default attack (a Survivors character:
// Move) goes on slot 0 (left mouse button) only when nothing at all was saved for that slot; nothing is sent without a saved bar, so the client keeps its own
// default.
static async Task SendSkillBar(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    FillSkillBar(conn);
    if (CharacterSaves.Load(CharacterName(conn))?.SkillBar is not { Count: > 0 } bar) return;
    var ids = new ushort[InstanceProtocol.SkillBarSlots];
    var byEffect = GemSkillEffects(conn.Character).Select(g => (g.Id, Effect: Skills.EffectId(g.Effect)))
        .Append((Id: SkillCatalog.FirstSkillId, Effect: SkillEffectId(conn, SkillCatalog.FirstSkillId)))
        .Where(e => e.Effect is not null).GroupBy(e => e.Effect!).ToDictionary(g => g.Key, g => g.First().Id);
    foreach (var (slot, entry) in bar)
    {
        if (slot < 0 || slot >= ids.Length) continue;
        if (entry.StartsWith("Action/") && Skills.ActionSkillId(entry["Action/".Length..]) is ushort action) ids[slot] = action;
        else if (byEffect.TryGetValue(entry, out ushort id)) ids[slot] = id;
    }
    if (!bar.ContainsKey(SkillBarLayout.LeftButton))
        ids[SkillBarLayout.LeftButton] = Survivors.IsLeague(conn.League) && Skills.ActionSkillId("Move") is ushort move ? move : SkillCatalog.FirstSkillId;
    await Send(remote, stream, ciphers, InstanceProtocol.SkillBar(ids),
        $"0x0053 skill bar: {string.Join(", ", ids.Select((id, i) => id == 0 ? null : $"slot {i} 0x{id:x4}").OfType<string>())}");
}

// Puts the skills that are nowhere on the saved bar into its empty slots (SkillBarLayout), leaving every other slot as
// it is: on a new bar the default attack on the left mouse button (a Survivors character: Move there and the default
// attack after the gem skills), each gem skill of the equipment on the right mouse button or the next empty slot. True when the bar changed (saved).
static bool FillSkillBar(ClientState conn)
{
    string character = CharacterName(conn);
    var bar = CharacterSaves.Load(character)?.SkillBar ?? [];
    string attack = SkillEffectId(conn, SkillCatalog.FirstSkillId)!;
    var gems = GemSkillEffects(conn.Character).Select(g => Skills.EffectId(g.Effect)).OfType<string>().ToList();
    var added = SkillBarLayout.Fill(bar, attack, gems, entry => entry == attack || entry.StartsWith("Action/") || gems.Contains(entry),
        moveOnLeft: Survivors.IsLeague(conn.League));
    foreach (var (slot, entry) in added) CharacterSaves.SetSkillBar(character, (byte)slot, entry);
    if (added.Count > 0)
        Console.WriteLine($"[gateway] {character} skill bar: {string.Join(", ", added.Select(a => $"{a.Entry} on slot {a.Slot}"))}");
    return added.Count > 0;
}

// The GrantedEffects Id behind a skill id of the player (the default attack or a gem skill), or null.
static string? SkillEffectId(ClientState conn, ushort skillId) =>
    skillId == SkillCatalog.FirstSkillId ? Environment.GetEnvironmentVariable("Game_PLAYER_ATTACK") ?? "PlayerMelee"
    : GemSkillEffects(conn.Character).FirstOrDefault(g => g.Id == skillId) is { Name: not null } g ? Skills.EffectId(g.Effect) : null;

// Why the held item may not go into an equipment row: the character is below its level requirement
// (ItemModStats.LevelRequirement). The client also checks it (0x1421eca50) against the level we send in the item data,
// but the server does not trust that.
static string? LevelTooLow(ClientState conn, byte row, Item? held)
{
    if (held is null || !(ItemModStats.EquipmentRows.Contains(row) || row is 14 or 15)) return null;
    if (Items.ByHash(held.TypeHash) is not { } t) return null;
    int needs = ItemModStats.LevelRequirement(held, t.DropLevel, Items.Mods);
    return conn.Pools.Level < needs ? $"{t.Name} requires level {needs} (character is {conn.Pools.Level})" : null;
}

// A worn item whose requirements are not met (CharacterStatSources): its mods and gems do nothing.
static bool IsInactive(ClientState conn, uint itemId) => InactiveItems.GetValueOrDefault(CharacterName(conn))?.Contains(itemId) == true;

static string CharacterName(ClientState conn) => string.IsNullOrEmpty(conn.Character) ? "Exile" : conn.Character;

// The skills of the active gems socketed in the equipped items in skill-list order: skill id, GrantedEffects row, gem
// name, and the gem's level (from its experience) and quality. A transfigured or Trarthan gem grants its variant's skill, a
// Vaal gem its Vaal skill and its base skill (ItemCatalog.GemEffects). Each skill keeps its id while its gem stays socketed
// (GemSkillIds): the client binds skill bar keys by skill id.
static IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality)> GemSkillEffects(string? character)
{
    string name = string.IsNullOrEmpty(character) ? "Exile" : character;
    var inactive = InactiveItems.GetValueOrDefault(name);
    var gems = PlayerInventories.For(name, Items).SocketedGemPlaces()
        .SelectMany(g => Items.GemEffects(g.Gem).Select(effect => (g.ItemId, g.Socket, g.Gem, Effect: effect))).ToList();
    var ids = GemSkillIdsOf.GetOrAdd(name, _ => new GemSkillIds())
        .Assign([.. gems.Select(g => new GemSkillIds.Gem(g.ItemId, g.Socket, g.Effect))], GemSkillIdPool.Value);
    // Gems in an inactive item grant nothing, but keep their id.
    for (int i = 0; i < gems.Count; i++)
        if (ids[i] is ushort id && inactive?.Contains(gems[i].ItemId) != true)
            yield return (id, gems[i].Effect, Items.GemName(gems[i].Gem), Items.GemLevel(gems[i].Gem), gems[i].Gem.Quality);
}

static double Dist((int X, int Y) a, (int X, int Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

// A new level from /set_level: the level's starting experience, then the level change (ApplyLevel).
static async Task SetLevel(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, int level)
{
    lock (conn.Monsters) conn.Experience = CharacterStats.ExperienceFor(level);
    CharacterSaves.SetExperience(CharacterName(conn), conn.Experience);
    await Send(remote, stream, ciphers, InstanceEntities.PlayerExperienceUpdate(PlayerId, conn.Experience),
        $"0x02ba player Player: experience {conn.Experience} (level {level})");
    await ApplyLevel(remote, stream, ciphers, conn, level);
}

// A new level in place, after the Player component's experience (0x2ba; the client derives the level from it and runs
// its level change): the level's stats (0x1b3: level, life, mana, accuracy, ...), the skills with them (0x2ba Actor),
// full life, as a level up gives, and the passive skill points (0x3d). Passives beyond a lower level's points are
// taken back, last allocated first.
static async Task ApplyLevel(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, int level)
{
    int before = conn.Pools.Level;
    List<ushort>? trimmed = null;
    Dictionary<ushort, ushort> effects;
    lock (conn.Monsters)
    {
        int had = conn.Passives.Count;
        TrimPassives(conn, level);
        if (conn.Passives.Count != had) trimmed = [.. conn.Passives];
        effects = conn.MasteryEffects.Where(kv => conn.Passives.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        conn.Pools = PoolsFor(conn, level);
        conn.ApplyMovementSpeed();
    }
    // Only a lower level (/set_level) drops passives; otherwise the saved ones stay as they are.
    if (trimmed is not null) CharacterSaves.SetPassives(CharacterName(conn), trimmed, effects);
    Console.WriteLine($"[gateway] {remote} {CharacterName(conn)}: level {before} → {level} (life {conn.Pools.Life}, mana {conn.Pools.Mana}, {Progression.PassivePoints(level)} passive point(s))");
    await SendCharacterStats(remote, stream, ciphers, conn, $"level {level}");
    int mana = UnreservedMana(conn);
    lock (conn.Monsters) (conn.PlayerLife, conn.PlayerMana) = (conn.PlayerMaxLife, (uint)mana);
    await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerMaxLife), $"0x01b1 player life {conn.PlayerMaxLife}");
    await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana {conn.PlayerMana}");
    await SendPassives(remote, stream, ciphers, conn);
}

// The player's stats (0x1b3) and the skills computed with them (0x2ba Actor), after the level or the passives changed.
static async Task SendCharacterStats(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string why)
{
    await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, PlayerStatsWithCleared(conn)), $"0x01b3 player stats ({why})");
    await SendSkills(remote, stream, ciphers, conn);
}

// The player's skills again, and the removal of the ones the client still has whose gems left the equipment, in one
// Actor 0x20 update (PlayerSkillsChange): the 0x40 list neither removes skills nor tells the character panel.
static async Task SendSkills(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    var skills = PlayerSkills(conn);
    var gone = conn.ClientSkillIds.Except(skills.Select(s => s.Id)).Order().ToList();
    var added = skills.Select(s => s.Id).Where(id => !conn.ClientSkillIds.Contains(id)).ToList();
    conn.ClientSkillIds = [.. skills.Select(s => s.Id)];
    await Send(remote, stream, ciphers, InstanceEntities.PlayerSkillsChange(PlayerId, skills, gone),
        $"0x02ba player Actor: {skills.Count} skill(s) with {skills.Sum(k => k.Stats?.Count ?? 0)} stats" +
        (gone.Count > 0 ? $", remove skill(s) {string.Join(", ", gone.Select(id => $"0x{id:x4}"))}" : ""));
    if (gone.Count > 0) await EndSkillEffects(remote, stream, ciphers, conn, gone);
    // The update can take a bound skill off the skill bar (live 2026-10-21: Haste's slot emptied when its aura went on,
    // and came back with the area change's 0x53), and a gem socketed again does not get its slot back by itself, so the
    // saved bar goes out again. Not when a skill the saved bar never had was added: the client binds a new skill to a free
    // slot itself (C→S 0x4e), and the bar sent now would clear it there. A new gem skill gets an empty slot of the saved
    // bar first (FillSkillBar: the right mouse button, then Q W E R T …), so it goes out with the bar.
    FillSkillBar(conn);
    var saved = CharacterSaves.Load(CharacterName(conn))?.SkillBar?.Values;
    if (added.All(id => SkillEffectId(conn, id) is { } effect && saved?.Contains(effect) == true))
        await SendSkillBar(remote, stream, ciphers, conn);
}

// Skills the player no longer has (their gems left the equipment): their auras and Righteous Fire end with them, and the
// reserved mana comes back.
static async Task EndSkillEffects(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IReadOnlyCollection<ushort> gone)
{
    List<AuraCatalog.Aura> ended;
    lock (conn.Monsters)
    {
        ended = [.. conn.Auras.Values.Where(a => gone.Contains(a.SkillId))];
        foreach (var aura in ended) conn.Auras.Remove(aura.SkillId);
        if (ended.Count > 0)
        {
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
        }
    }
    foreach (var aura in ended)
        await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, aura.Buff), $"0x01c2 player buff {aura.Buff}: {aura.Name} ends (its gem left)");
    if (ended.Count > 0)
    {
        await SendCharacterStats(remote, stream, ciphers, conn, $"aura {string.Join(", ", ended.Select(a => a.Name))} off (gem left)");
        await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana {conn.PlayerMana}/{conn.Pools.Mana}, {ReservedMana(conn)} reserved");
    }
    if (conn.RighteousFire is { } rf && gone.Contains(rf.Skill)) await EndRighteousFire(remote, stream, ciphers, conn, "its gem left");
}

// The character's experience and passives from its save; returns its level. The saved experience decides the level: the
// account's (the ticket's) is only a copy for the character list, which a failed update at a level-up leaves behind. A save
// without experience (an older one) starts at the account's level. Passives beyond the level's points are dropped.
static int LoadProgress(ClientState conn, int level)
{
    var save = CharacterSaves.Load(CharacterName(conn));
    level = CharacterSaves.LevelOf(save, level);
    // Without the tree the saved passives can't be checked; dropping them would lose them at the next save.
    if (save?.Passives is { Count: > 0 } && !Passives.Loaded)
        throw new UnknownSavedItemException("allocated passives, but the passive tree (data/tables/PassiveSkills.json) is not loaded");
    // Gold is the account's (AccountStash.Gold): gold an older save kept with the character moves into the account, in one
    // transaction with the save letting go of it.
    if (save?.Gold is > 0 and uint oldGold)
    {
        uint now = Saves.Atomically(() =>
        {
            uint gold = PersonalStashOf(conn).AddGold(oldGold);
            CharacterSaves.TakeGold(CharacterName(conn));
            return gold;
        });
        Console.WriteLine($"[gateway] {CharacterName(conn)}: {oldGold} gold from the character save moved to the account, now {now}");
    }
    lock (conn.Monsters)
    {
        conn.Passives = [.. (save?.Passives ?? []).Where(n => Passives.Find(n) is { IsAscendancy: false })];
        conn.Waypoints.UnionWith(save?.Waypoints ?? []);
        conn.Visited.UnionWith(save?.Visited ?? []);
        conn.QuestFlags = [.. save?.QuestFlags ?? [], .. Quests.EveryCharacter];
        conn.ExtraPassivePoints = save?.ExtraPassivePoints ?? 0;
        conn.RefundPoints = save?.RefundPoints ?? 0;
        conn.Experience = save?.Experience ?? CharacterStats.ExperienceFor(level);
        TrimPassives(conn, level);
        conn.MasteryEffects = (save?.MasteryEffects ?? []).Where(kv => conn.Passives.Contains(kv.Key)
            && Passives.FindEffect(kv.Value) is { } effect && Passives.IsEffectFor(kv.Key, effect))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        conn.Pools = PoolsFor(conn, level);
        conn.ApplyMovementSpeed();
    }
    return level;
}

// The pools of the character's class at this level with its passives (call under the Monsters lock).
// The character's stats from its allocated passives and the global mods of its active equipped items (ItemModStats),
// by client stat key. Worn items whose requirements the class, the passives and the other active items do not meet are
// inactive (ItemModStats.Inactive): remembered per character for the weapon profile and the gem skills.
static Dictionary<uint, int> CharacterStatSources(ClientState conn, IEnumerable<ushort>? nodes = null, int? level = null)
{
    var items = ItemsOf(conn);
    var passives = Passives.Stats(nodes ?? conn.Passives, conn.MasteryEffects);
    int Get(string name) => Stats.Key(name) is uint k ? passives.GetValueOrDefault(k) : 0;
    var c = Skills.ClassFor(conn.ClassRow);
    var (ps, pd, pi) = CharacterStats.Attributes(Get);
    int god = conn.God ? 1000 : 0;
    var worn = ItemModStats.EquipmentRows.SelectMany(items.Items)
        .Select(i => (i, t: Items.ByHash(i.Item.TypeHash)))
        .Select(x => x.t is { } t
            ? new ItemModStats.Worn(x.i.Id, x.i.Item, Items.Requirements(t).Str, Items.Requirements(t).Dex, Items.Requirements(t).Int,
                ItemModStats.LevelRequirement(x.i.Item, t.DropLevel, Items.Mods))
            : new ItemModStats.Worn(x.i.Id, x.i.Item, 0, 0, 0, 0)).ToList();
    var before = InactiveItems.GetValueOrDefault(CharacterName(conn));
    var inactive = ItemModStats.Inactive(worn, (c.Strength + ps + god, c.Dexterity + pd + god, c.Intelligence + pi + god),
        level ?? conn.Pools.Level, i => ItemModStats.Attributes(i, Items.Mods), before);
    InactiveItems[CharacterName(conn)] = inactive;
    if (inactive.Count > 0 && (before is null || !before.SetEquals(inactive)))
        Console.WriteLine($"[gateway] {CharacterName(conn)}: inactive items (requirements not met): " +
            string.Join(", ", worn.Where(w => inactive.Contains(w.Id)).Select(w => $"{w.Id} {Items.ByHash(w.Item.TypeHash)?.Name} (str {w.Strength}, dex {w.Dexterity}, int {w.Intelligence}, level {w.Level})")));
    var active = worn.Where(w => !inactive.Contains(w.Id)).Select(w => w.Item);
    return ItemModStats.Plus(passives, ItemModStats.Global(active, Items.Mods, Stats));
}

static CharacterStats.Pools PoolsFor(ClientState conn, int level)
{
    // The stat sources first: they decide which worn items are inactive, which the weapon profile reads.
    var sources = CharacterStatSources(conn, level: level);
    var weapons = WeaponProfileFor(conn);
    var bonus = PassiveBonus(sources, weapons);
    var gear = EquippedBaseDefences(conn);
    bonus = bonus with { Armour = bonus.Armour + gear.Armour, Evasion = bonus.Evasion + gear.Evasion };
    List<AuraCatalog.Aura> auras;
    lock (conn.Monsters) auras = [.. conn.Auras.Values];
    var aura = AuraCatalog.Bonus(auras, bonus.AuraEffectPct, Skills, Stats);
    bonus = bonus with
    {
        FireResistance = bonus.FireResistance + aura.FireResistance,
        ColdResistance = bonus.ColdResistance + aura.ColdResistance,
        LightningResistance = bonus.LightningResistance + aura.LightningResistance,
        ChaosResistance = bonus.ChaosResistance + aura.ChaosResistance,
        FireMaxResistance = bonus.FireMaxResistance + aura.FireMaxResistance,
        ColdMaxResistance = bonus.ColdMaxResistance + aura.ColdMaxResistance,
        LightningMaxResistance = bonus.LightningMaxResistance + aura.LightningMaxResistance,
        ChaosMaxResistance = bonus.ChaosMaxResistance + aura.ChaosMaxResistance,
        Accuracy = bonus.Accuracy + aura.Accuracy,
        CriticalStrikeChancePct = bonus.CriticalStrikeChancePct + aura.CriticalStrikeChancePct,
        SpellCriticalStrikeChancePct = bonus.SpellCriticalStrikeChancePct + aura.SpellCriticalStrikeChancePct,
        AttackSpeedPct = bonus.AttackSpeedPct + aura.AttackSpeedPct,
        // Survivors: Swiftness, for the run.
        MovementVelocityPct = bonus.MovementVelocityPct + aura.MovementVelocityPct + (conn.Run?.MovementSpeedPct ?? 0)
    };
    if (conn.God)
        bonus = bonus with { Strength = bonus.Strength + 1000, Dexterity = bonus.Dexterity + 1000, Intelligence = bonus.Intelligence + 1000,
            Life = bonus.Life + 10_000, AttackSpeedPct = bonus.AttackSpeedPct + 1000 };
    var pools = CharacterStats.For(Skills.ClassFor(conn.ClassRow), level, bonus);
    pools = pools with
    {
        AttackBlockLucky = pools.AttackBlockLucky || conn.ForceAttackBlockLucky,
        SpellBlockLucky = pools.SpellBlockLucky || conn.ForceSpellBlockLucky,
        SpellSuppressionLucky = pools.SpellSuppressionLucky || conn.ForceSpellSuppressionLucky,
        SpellSuppressionUnlucky = pools.SpellSuppressionUnlucky || conn.ForceSpellSuppressionUnlucky,
        // WeaponTypes.dat gives the main hand's base critical chance (permyriad, e.g. 830 = 8.30%); unarmed is 0.
        AttackCriticalStrikeChance = (int)Math.Round(
            DamageModel.CriticalStrikeChance(weapons.Critical, pools.CriticalStrikeChancePct) * 100),
        SpellCriticalStrikeChance = (int)Math.Round(
            DamageModel.CriticalStrikeChance(DamageModel.BaseSpellCriticalStrikeChancePerMyriad,
                pools.SpellCriticalStrikeChancePct) * 100)
    };
    int shieldBlock = weapons.Shield ? ShieldBaseBlock(conn) : 0;
    int rawAttack = pools.BlockChanceUncapped + shieldBlock;
    double blockFactor = (1 + bonus.BlockChanceInc / 100.0) * (1 + bonus.BlockChanceMore / 100.0);
    int totalAttack = (int)Math.Round(Math.Max(0, rawAttack) * blockFactor);
    int totalSpell = (int)Math.Round(Math.Max(0, pools.SpellBlockChance) * blockFactor);
    pools = pools with
    {
        BlockChanceBase = shieldBlock,
        BlockChanceUncapped = Math.Max(0, totalAttack),
        BlockChance = Math.Min(Math.Max(0, totalAttack), pools.BlockChanceMax),
        SpellBlockChance = Math.Min(Math.Max(0, totalSpell), pools.SpellBlockChanceMax)
    };
    // "+1% Chance to Block Spell Damage per 5% Chance to Block Attack Damage": the attack value is the final capped
    // chance, including the shield's base. The effect stacks additively and is capped by the spell block maximum.
    if (pools.SpellBlockPer5AttackBlock != 0 && pools.BlockChance > 0)
        pools = pools with
        {
            SpellBlockChance = Math.Min(pools.SpellBlockChance + pools.BlockChance / 5 * pools.SpellBlockPer5AttackBlock,
                pools.SpellBlockChanceMax)
        };
    // Versatile Combatant: +2% spell block for each 1% of attack block above its (keystone-reduced) maximum.
    if (pools.VersatileCombatant)
    {
        int overcap = Math.Max(0, pools.BlockChanceUncapped - pools.BlockChanceMax);
        if (overcap > 0)
            pools = pools with
            {
                SpellBlockChance = Math.Min(pools.SpellBlockChance + 2 * overcap, pools.SpellBlockChanceMax)
            };
    }
    return pools;
}

// What the allocated passives add to the pools: attributes (base_* and additional_*, all attributes and the pairs),
// flat and increased life, mana and accuracy, flat and increased armour and evasion, energy shield and melee physical
// damage, the base resistances (base_resist_all_elements_% applies to fire, cold and lightning), the shield-specific
// block nodes when a shield is equipped, critical strike chance and multiplier, and reservation efficiency and aura
// effect.
static CharacterStats.Bonus PassiveBonus(Dictionary<uint, int> stats, WeaponProfile weapons)
{
    int Get(string name) => Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0;
    int allRes = Get("base_resist_all_elements_%");
    // The game's block stats are conditional on the equipped weapons:
    // - dual_wield_or_shield_block_%: dual wielding or a shield;
    // - block_while_dual_wielding_% / _claws: dual wielding (the latter with two claws);
    // - staff_block_% / additional_staff_block_%: a staff;
    // - shield_block_% / block_chance_%_while_holding_shield: a shield.
    int blockWhileDualOrShield = weapons is { Shield: true } or { DualWield: true } ? Get("dual_wield_or_shield_block_%") : 0;
    int shieldBlock = weapons.Shield ? Get("shield_block_%") + Get("block_chance_%_while_holding_shield") : 0;
    int staffBlock = weapons.Staff ? Get("staff_block_%") + Get("additional_staff_block_%") : 0;
    int dualBlock = weapons.DualWield
        ? Get("block_while_dual_wielding_%") + (weapons.DualClaws ? Get("block_while_dual_wielding_claws_%") : 0) + 20
        : 0; // 20 is the inherent dual-wield block chance (PoB Misc.lua)
    int bowBlock = weapons.Bow ? Get("with_bow_additional_block_%") : 0;
    // Spell block has the matching weapon-specific stats; the staff-or-shield stat is shared.
    int staffOrShieldSpell = weapons is { Staff: true } or { Shield: true } ? Get("spell_block_chance_%_while_holding_staff_or_shield") : 0;
    int shieldSpellBlock = weapons.Shield ? Get("shield_spell_block_%") + Get("block_spells_chance_%_while_holding_shield") : 0;
    int staffSpellBlock = weapons.Staff ? Get("spell_block_with_staff_%") : 0;
    int dualSpellBlock = weapons.DualWield ? Get("spell_block_while_dual_wielding_%") : 0;
    int bowSpellBlock = weapons.Bow ? Get("spell_block_with_bow_%") : 0;
    // Attack speed passives, conditional on the equipped weapon shape as in PoB's ModFlags: generic, melee,
    // one-/two-handed melee, all one-handed, the weapon families, shields and dual wielding. Minion, totem and
    // timed/conditional stats are not player attack speed here.
    int attackSpeed = Get("attack_speed_+%") + Get("attack_and_cast_speed_+%")
        + (weapons.Melee ? Get("melee_attack_speed_+%") : 0)
        + (weapons.OneHandMelee ? Get("one_handed_melee_attack_speed_+%") : 0)
        + (weapons.TwoHandMelee ? Get("two_handed_melee_attack_speed_+%") : 0)
        + (weapons.OneHand ? Get("one_handed_attack_speed_+%") : 0)
        + (weapons.Unarmed ? Get("unarmed_melee_attack_speed_+%") : 0)
        + (weapons.Shield ? Get("attack_speed_+%_while_holding_shield") + Get("shield_attack_speed_+%") : 0)
        + (weapons.DualWield ? Get("attack_speed_while_dual_wielding_+%") : 0)
        + (weapons.Axe ? Get("axe_attack_speed_+%") : 0)
        + (weapons.Sword ? Get("sword_attack_speed_+%") : 0)
        + (weapons.Mace ? Get("mace_attack_speed_+%") : 0)
        + (weapons.Claw ? Get("claw_attack_speed_+%") : 0)
        + (weapons.Dagger ? Get("dagger_attack_speed_+%") : 0)
        + (weapons.Wand ? Get("wand_attack_speed_+%") : 0)
        + (weapons.Bow ? Get("bow_attack_speed_+%") : 0)
        + (weapons.Staff ? Get("staff_attack_speed_+%") : 0);
    // PoB Misc.lua: 10% more attack speed while dual wielding; unarmed attacks count as dual wielding. The main hand's
    // local attack speed scales the weapon's own rate before the global increases, so it multiplies like a more.
    int attackSpeedMore = (int)Math.Round(((weapons.DualWield || weapons.Unarmed ? 1.10 : 1.0) * (1 + weapons.LocalAttackSpeedPct / 100.0) - 1) * 100);
    // Unconditional critical strike chance and multiplier passives: generic, attack/melee/weapon-shape and spell.
    // Timed or enemy-state conditions are not modelled.
    int criticalChance = Get("critical_strike_chance_+%") + Get("attack_critical_strike_chance_+%")
        + (weapons.Melee ? Get("melee_critical_strike_chance_+%") : 0)
        + (weapons.OneHandMelee ? Get("one_handed_melee_critical_strike_chance_+%") : 0)
        + (weapons.TwoHandMelee ? Get("two_handed_melee_critical_strike_chance_+%") : 0)
        + (weapons.DualWield ? Get("critical_strike_chance_while_dual_wielding_+%") : 0)
        + (weapons.Shield ? Get("critical_strike_chance_while_wielding_shield_+%") : 0)
        + (weapons.Axe ? Get("axe_critical_strike_chance_+%") : 0)
        + (weapons.Sword ? Get("sword_critical_strike_chance_+%") : 0)
        + (weapons.Mace ? Get("mace_critical_strike_chance_+%") : 0)
        + (weapons.Claw ? Get("claw_critical_strike_chance_+%") : 0)
        + (weapons.Dagger ? Get("dagger_critical_strike_chance_+%") : 0)
        + (weapons.Wand ? Get("wand_critical_strike_chance_+%") : 0)
        + (weapons.Bow ? Get("bow_critical_strike_chance_+%") + Get("global_critical_strike_chance_+%_while_holding_bow") : 0)
        + (weapons.Staff ? Get("staff_critical_strike_chance_+%") + Get("global_critical_strike_chance_+%_while_holding_staff") : 0);
    int spellCriticalChance = Get("critical_strike_chance_+%") + Get("spell_critical_strike_chance_+%");
    int criticalMultiplier = Get("base_critical_strike_multiplier_+")
        + (weapons.Melee ? Get("melee_critical_strike_multiplier_+") + Get("melee_weapon_critical_strike_multiplier_+") : 0)
        + (weapons.OneHandMelee ? Get("one_handed_melee_critical_strike_multiplier_+") : 0)
        + (weapons.TwoHandMelee ? Get("two_handed_melee_critical_strike_multiplier_+") : 0)
        + (weapons.DualWield ? Get("critical_strike_multiplier_while_dual_wielding_+") : 0)
        + (weapons.Axe ? Get("axe_critical_strike_multiplier_+") : 0)
        + (weapons.Sword ? Get("sword_critical_strike_multiplier_+") : 0)
        + (weapons.Mace ? Get("mace_critical_strike_multiplier_+") : 0)
        + (weapons.Claw ? Get("claw_critical_strike_multiplier_+") : 0)
        + (weapons.Dagger ? Get("dagger_critical_strike_multiplier_+") + Get("critical_strike_multiplier_with_dagger_+") : 0)
        + (weapons.Wand ? Get("wand_critical_strike_multiplier_+") : 0)
        + (weapons.Bow ? Get("bow_critical_strike_multiplier_+") + Get("global_critical_strike_multiplier_+_while_holding_bow") : 0)
        + (weapons.Staff ? Get("staff_critical_strike_multiplier_+") + Get("global_critical_strike_multiplier_+_while_holding_staff") : 0);
    int spellCriticalMultiplier = Get("base_critical_strike_multiplier_+")
        + Get("base_spell_critical_strike_multiplier_+") + Get("spell_critical_strike_multiplier_+");
    // Accuracy passives: generic flat and increased, the weapon-specific flat and increased stats, and the Accuracy
    // Mastery's conversions. Conditional stats (nearby ally, marked enemy, low life, frenzy charges, unique/close
    // enemies) are not modelled here. Unarmed attacks count as dual wielding, as they do for attack speed.
    int accuracyFlat = Get("accuracy_rating") + weapons.LocalAccuracy
        + (weapons.Sword ? Get("sword_accuracy_rating") : 0)
        + (weapons.Axe ? Get("axe_accuracy_rating") : 0)
        + (weapons.Mace ? Get("mace_accuracy_rating") : 0)
        + (weapons.Claw ? Get("claw_accuracy_rating") : 0)
        + (weapons.Dagger ? Get("dagger_accuracy_rating") : 0)
        + (weapons.Wand ? Get("wand_accuracy_rating") : 0)
        + (weapons.Staff ? Get("staff_accuracy_rating") : 0)
        + (weapons.Bow ? Get("bow_accuracy_rating") : 0);
    int accuracyPct = Get("accuracy_rating_+%")
        + ((weapons.DualWield || weapons.Unarmed) ? Get("accuracy_rating_while_dual_wielding_+%") : 0)
        + (weapons.OneHandMelee ? Get("one_handed_melee_accuracy_rating_+%") : 0)
        + (weapons.TwoHandMelee ? Get("two_handed_melee_accuracy_rating_+%") : 0)
        + (weapons.Axe ? Get("axe_accuracy_rating_+%") : 0)
        + (weapons.Sword ? Get("sword_accuracy_rating_+%") : 0)
        + (weapons.Mace ? Get("mace_accuracy_rating_+%") : 0)
        + (weapons.Claw ? Get("claw_accuracy_rating_+%") : 0)
        + (weapons.Dagger ? Get("dagger_accuracy_rating_+%") : 0)
        + (weapons.Wand ? Get("wand_accuracy_rating_+%") : 0)
        + (weapons.Staff ? Get("staff_accuracy_rating_+%") : 0)
        + (weapons.Bow ? Get("bow_accuracy_rating_+%") : 0);
    int allMax = Get("additional_maximum_all_resistances_%"), allElemMax = Get("additional_maximum_all_elemental_resistances_%");
    // Reservation efficiency: generic efficiency applies to both pools; the mana/life-specific stats only to theirs.
    // Aura effect is the generic non-curse/aura effect; the Reservation mastery's self-aura effect is included.
    int manaReservation = Get("base_mana_reservation_efficiency_+%") + Get("base_reservation_efficiency_+%")
        + Get("mana_reservation_efficiency_+%") + Get("reservation_efficiency_+%");
    int lifeReservation = Get("base_life_reservation_efficiency_+%") + Get("base_reservation_efficiency_+%")
        + Get("life_reservation_efficiency_+%") + Get("reservation_efficiency_+%");
    int auraEffect = Get("non_curse_aura_effect_+%") + Get("aura_effect_+%")
        + Get("aura_effect_on_self_from_your_skills_+%");
    int auraArea = Get("base_aura_area_of_effect_+%");
    // Defences: the tree's flat armour/evasion and their increased modifiers. "Defences" and the combined armour and
    // evasion increase apply to both ratings, as in PoB's calcLib.mod.
    int defencesPct = Get("global_defences_+%") + Get("combined_global_defences_+%")
        + Get("evasion_and_physical_damage_reduction_rating_+%")
        + Get("combined_armour_and_evasion_rating_+%");
    int armourPct = Get("physical_damage_reduction_rating_+%") + defencesPct;
    int evasionPct = Get("evasion_rating_+%") + defencesPct;
    var (strength, dexterity, intelligence) = CharacterStats.Attributes(Get);
    return new CharacterStats.Bonus(
        Strength: strength, Dexterity: dexterity, Intelligence: intelligence,
        Life: Get("base_maximum_life"), LifePct: Get("maximum_life_+%"), Mana: Get("base_maximum_mana"), ManaPct: Get("maximum_mana_+%"),
        Armour: Get("base_physical_damage_reduction_rating"), ArmourPct: armourPct,
        Evasion: Get("base_evasion_rating"), EvasionPct: evasionPct,
        SharedDefencesPct: defencesPct, IronReflexes: Get("keystone_iron_reflexes") != 0,
        EnergyShieldPct: Get("maximum_energy_shield_+%"), MeleePhysicalDamagePct: Get("melee_physical_damage_+%"),
        FireResistance: allRes + Get("base_fire_damage_resistance_%"),
        ColdResistance: allRes + Get("base_cold_damage_resistance_%"),
        LightningResistance: allRes + Get("base_lightning_damage_resistance_%"),
        ChaosResistance: Get("base_chaos_damage_resistance_%"),
        FireMaxResistance: allMax + allElemMax + Get("base_maximum_fire_damage_resistance_%"),
        ColdMaxResistance: allMax + allElemMax + Get("base_maximum_cold_damage_resistance_%"),
        LightningMaxResistance: allMax + allElemMax + Get("base_maximum_lightning_damage_resistance_%"),
        ChaosMaxResistance: allMax + Get("base_maximum_chaos_damage_resistance_%"),
        SpellSuppressionChance: Get("base_spell_suppression_chance_%"),
        SpellSuppressionEffect: Get("base_spell_damage_%_suppressed"),
        SpellSuppressionLucky: Get("spell_suppression_chance_is_lucky") != 0,
        SpellSuppressionUnlucky: Get("spell_suppression_chance_is_unlucky") != 0,
        SpellSuppressionWhileFullLife: Get("spell_suppression_chance_%_while_on_full_life"),
        SpellSuppressionWhileMoving: Get("spell_suppression_chance_%_while_moving"),
        SpellSuppressionIfHitRecently: Get("spell_suppression_chance_%_if_enemy_hit_recently"),
        MovementVelocityPct: Get("base_movement_velocity_+%"),
        BlockChance: Get("additional_block_%") + Get("attack_block_%") + Get("monster_base_block_%")
            + blockWhileDualOrShield + shieldBlock + staffBlock + dualBlock + bowBlock,
        SpellBlockChance: Get("base_spell_block_%") + staffOrShieldSpell + shieldSpellBlock + staffSpellBlock + dualSpellBlock + bowSpellBlock,
        BlockChanceMax: Get("maximum_block_%"), SpellBlockChanceMax: Get("base_maximum_spell_block_%"),
        BlockChanceInc: Get("block_chance_+%"), BlockChanceMore: Get("block_chance_+%_final"),
        SpellBlockPer5AttackBlock: Get("spell_block_%_per_5_attack_block"),
        LifeGainedOnBlock: Get("life_gained_on_block"), ManaGainedOnBlock: Get("mana_gained_on_block"),
        VersatileCombatant: Get("keystone_versatile_combatant") != 0,
        AttackBlockLucky: Get("base_block_luck") != 0 || Get("attack_block_luck") != 0 || Get("base_attack_block_luck") != 0,
        SpellBlockLucky: Get("base_block_luck") != 0 || Get("spell_block_luck") != 0 || Get("base_spell_block_luck") != 0,
        AttackSpeedPct: attackSpeed, AttackSpeedMorePct: attackSpeedMore,
        CriticalStrikeChancePct: criticalChance, SpellCriticalStrikeChancePct: spellCriticalChance,
        CriticalStrikeMultiplier: criticalMultiplier, SpellCriticalStrikeMultiplier: spellCriticalMultiplier,
        Accuracy: accuracyFlat, AccuracyPct: accuracyPct,
        AccuracyPerLevel: Get("accuracy_rating_per_level"),
        DexterityAccuracyTriple: Get("dexterity_accuracy_bonus_grants_accuracy_rating_+3_per_dexterity_instead") != 0,
        AccuracyEqualsIntelligence: Get("gain_accuracy_rating_equal_to_intelligence") != 0,
        ManaReservationEfficiencyPct: manaReservation, LifeReservationEfficiencyPct: lifeReservation,
        AuraEffectPct: auraEffect, AuraAreaPct: auraArea);
}

// The character's passive skill points (one per level after the first, plus the extra points from
// /add_passive_points; the client computes the same from the level in 0x33 and its extra_skill_points field), the
// allocated nodes and the selected mastery effects. The client allows that many nodes plus the points the allocated
// nodes grant themselves.
static (ushort Points, ushort RefundPoints, List<ushort> Nodes, Dictionary<ushort, ushort> Masteries) PassiveState(ClientState conn)
{
    lock (conn.Monsters)
        return ((ushort)Math.Min(ushort.MaxValue, Progression.PassivePoints(conn.Pools.Level) + conn.ExtraPassivePoints),
            conn.RefundPoints, [.. conn.Passives], new(conn.MasteryEffects));
}

// Drops the last allocated passives beyond the level's points (call under the Monsters lock).
static void TrimPassives(ClientState conn, int level)
{
    int points = Progression.PassivePoints(level) + conn.ExtraPassivePoints;
    while (conn.Passives.Count > 0 && conn.Passives.Count > points + Passives.PointsGranted(conn.Passives))
        conn.Passives.RemoveAt(conn.Passives.Count - 1);
}

// The character's passive skill tree (0x33): its level (from which the client counts the points), its class (the
// tree's start) and the allocated nodes.
static async Task SendPassives(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    var (bytes, label) = PassivesMessage(conn);
    await Send(remote, stream, ciphers, bytes, label);
}

static (byte[] Bytes, string Label) PassivesMessage(ClientState conn)
{
    var (points, refundPoints, nodes, masteries) = PassiveState(conn);
    int unspent = points + Passives.PointsGranted(nodes) - nodes.Count;
    return (InstanceEntities.CharacterPassives((byte)conn.Pools.Level, (byte)Skills.ClassFor(conn.ClassRow).IntegerId, nodes,
            questPoints: conn.ExtraPassivePoints, refundPoints: refundPoints, masteryEffects: masteries),
        $"0x0033 passive skill tree: level {conn.Pools.Level}, {points} skill point(s) ({conn.ExtraPassivePoints} extra), " +
        $"{refundPoints} refund point(s), {nodes.Count} allocated, {unspent} unspent");
}

// Allocates (or, with allocate false, refunds) passive nodes: checked against the tree's nodes and the character's
// points, then saved, applied to the stats and sent back (0x3d). The client asks only for nodes connected to its tree.
// A gold respec (goldRespec) refunds only allocated nodes and charges their cost (PassiveTree.GoldRespecCost); a
// refund-point refund (refundPoints) spends one saved point per node. /reset_passives sets neither.
static async Task<string?> ChangePassives(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
    IReadOnlyList<ushort> nodes, bool allocate, string why, ushort? masteryEffect = null, bool goldRespec = false,
    bool refundPoints = false)
{
    string? error = null;
    List<ushort> now;
    Dictionary<ushort, ushort> effectsNow;
    string? effectLabel = null;
    long cost = 0;
    uint gold = 0;
    ushort refundsNow = 0;
    int refundsSpent = 0;
    lock (conn.Monsters)
    {
        var next = new List<ushort>(conn.Passives);
        var effects = new Dictionary<ushort, ushort>(conn.MasteryEffects);
        foreach (var n in nodes)
        {
            if (Passives.Find(n) is not { } node) { error = $"unknown passive {n}"; break; }
            if (node.IsAscendancy) { error = $"{node.Name} ({n}) is an ascendancy passive"; break; }
            if (masteryEffect is { } raw)
            {
                if (!allocate) { error = "a mastery effect cannot be refunded on its own"; break; }
                var effect = Passives.EffectsFor(n).FirstOrDefault(e => e.Hash == raw || e.Row == raw);
                if (effect is null) { error = $"{node.Name} ({n}) has no mastery effect {raw}"; break; }
                if (!next.Contains(n)) next.Add(n);
                effects[n] = effect.Hash;
                effectLabel = $"{effect.Id} ({effect.Hash})";
            }
            else if (allocate) { if (!next.Contains(n)) next.Add(n); }
            else if ((goldRespec || refundPoints) && !next.Contains(n)) { error = $"{node.Name} ({n}) is not allocated"; break; }
            else
            {
                next.Remove(n);
                effects.Remove(n);
                if (refundPoints) refundsSpent++;
            }
        }
        if (error is null && next.Count > Progression.PassivePoints(conn.Pools.Level) + conn.ExtraPassivePoints + Passives.PointsGranted(next))
            error = $"not enough passive skill points ({Progression.PassivePoints(conn.Pools.Level) + conn.ExtraPassivePoints} at level {conn.Pools.Level}, {next.Count} wanted)";
        if (error is null && refundPoints && refundsSpent > conn.RefundPoints)
            error = $"not enough refund points ({refundsSpent} needed, {conn.RefundPoints} held)";
        if (goldRespec) cost = Passives.GoldRespecCost(nodes, conn.Pools.Level);
        now = next;
        effectsNow = effects;
        refundsNow = (ushort)(conn.RefundPoints - (refundPoints && error is null ? refundsSpent : 0));
    }
    // Saved before it applies, in one transaction: the gold (paid last, from the account's gold: nothing is taken when the
    // change is refused), the passives, their mastery effects and the refund points.
    if (error is null)
        error = Saves.Atomically(() =>
        {
            if (goldRespec)
            {
                if (PersonalStashOf(conn).TrySpendGold(cost) is not uint left) return $"not enough gold ({cost} needed, {PersonalStashOf(conn).Gold} held)";
                gold = left;
            }
            CharacterSaves.SetPassives(CharacterName(conn), now, effectsNow, refundPoints ? refundsNow : null);
            return null;
        });
    lock (conn.Monsters)
    {
        if (error is null)
        {
            conn.Passives = now;
            conn.MasteryEffects = effectsNow;
            conn.RefundPoints = refundsNow;
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
            conn.PlayerLife = Math.Min(conn.PlayerLife, conn.PlayerMaxLife);
            conn.PlayerMana = Math.Min(conn.PlayerMana, (uint)UnreservedMana(conn));
        }
        now = [.. conn.Passives];
        effectsNow = new(conn.MasteryEffects);
        refundsNow = conn.RefundPoints;
    }
    Console.WriteLine($"[gateway] {remote} passives {(allocate ? "allocate" : "refund")} [{string.Join(", ", nodes.Select(n => $"{n} {Passives.Find(n)?.Name}"))}]" +
        (effectLabel is null ? "" : $" mastery {effectLabel}") + $" ({why}): " +
        (error ?? $"{now.Count} allocated, life {conn.Pools.Life}, mana {conn.Pools.Mana}" +
            (goldRespec ? $", {cost} gold paid, {gold} left" : "") +
            (refundPoints ? $", {refundsSpent} refund point(s) spent, {refundsNow} left" : "")));
    if (error is null)
    {
        if (goldRespec)
        {
            await Send(remote, stream, ciphers, InstanceEntities.Gold(gold), $"0x02a9 gold: {gold} (respec, {cost} paid)");
        }
        await SendCharacterStats(remote, stream, ciphers, conn, "passives");
        await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana {conn.PlayerMana} (passives)");
    }
    // Sent either way: on a refusal it puts the client's tree back to the server's.
    await SendPassives(remote, stream, ciphers, conn);
    return error;
}

// Experience from a kill: added (up to the last level), sent as the Player component's experience (0x2ba; the client
// derives the level and plays the level up from it), saved, and at a new level the level's stats, life and passive
// points (ApplyLevel) and the account's level (Auth set_level, for the character list).
static async Task GainExperience(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint gain, string why)
{
    // Added to the character's total (an older connection's or another task's gain is never written over), kept in memory
    // and saved every few seconds; a new level is saved at once.
    uint start;
    int oldLevel;
    lock (conn.Monsters) (start, oldLevel) = (conn.Experience, conn.Pools.Level);
    var (before, after) = CharacterSaves.AddExperience(CharacterName(conn), start, xp => Progression.AddExperience(xp, gain, CharacterStats.LevelStarts));
    if (after == before) return;
    lock (conn.Monsters) conn.Experience = Math.Max(conn.Experience, after);
    int level = Progression.LevelFor(after, CharacterStats.LevelStarts);
    await Send(remote, stream, ciphers, InstanceEntities.PlayerExperienceUpdate(PlayerId, after),
        $"0x02ba player Player: experience {before} + {after - before} = {after} (level {level}; {why})");
    if (level <= oldLevel) return;
    CharacterSaves.SaveExperience(CharacterName(conn));   // a new level is saved at once, before the account hears of it
    var r = await InstanceControl.CallAsync(new ControlRequest { Op = "set_level", Ticket = conn.Ticket, Level = level });
    if (!r.Ok) Console.WriteLine($"[gateway] {remote} level {level} not saved to the account: {r.Error}");
    await ApplyLevel(remote, stream, ciphers, conn, level);
    // Survivors: every level gained in a run is a pick.
    if (conn.Run is { Over: false } run)
    {
        lock (conn.Monsters) run.LevelledUp(level, level - oldLevel);
        await conn.Commands!.Reply($"Survivors: level {level}!");
        await OpenSurvivorsPick(remote, stream, ciphers, conn);
    }
}

// A monster died (its life reached 0 from the player's attack or skill): the player gains its experience when near
// enough, it drops its items around where it fell (ItemDrops), and its corpse is removed 10 s later.
static async Task MonsterKilled(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster m)
{
    double now = conn.Clock.Elapsed.TotalSeconds;
    (int X, int Y) at, player;
    bool alive;
    lock (conn.Monsters) { at = m.Walk.Position(now); player = conn.Walk.Position(now); alive = conn.PlayerLife > 0; }
    _ = Task.Delay(10_000).ContinueWith(async _ =>
    {
        bool removed;
        lock (conn.Monsters) removed = conn.Monsters.Remove(id);
        if (removed && !conn.Closed)
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove dead monster 0x{id:x}");
    });
    if (m.Variety is not { } variety || !m.Hostile) return;
    if (conn.Run is not null) await SurvivorsKill(remote, stream, ciphers, conn, id, m, at);
    await SetQuestFlags(remote, stream, ciphers, conn, Quests.OnMonsterKilled(conn.Layout.Id, variety.Id));
    if (alive && Dist(at, player) <= ExperienceRange && Monsters.BaseExperience(variety, m.Level) is int baseXp)
    {
        uint xp = Progression.MonsterExperience(baseXp, variety.ExperienceMultiplier, m.Rarity, conn.Pools.Level, m.Level);
        // Survivors: the league's curve (Survivors.ExperienceMultiplier), rares 10 times a normal monster, and Wisdom.
        if (conn.Run is { } run && Monsters.BaseExperience(variety, (byte)Math.Clamp(conn.Pools.Level, 1, 100)) is int levelXp)
            xp = (uint)Math.Round(xp * Survivors.ExperienceMultiplier(conn.Pools.Level, CharacterStats.LevelStarts, levelXp)
                * Survivors.RarityExperience(m.Rarity) / Progression.RarityMultiplier(m.Rarity) * run.ExperienceMultiplier);
        await GainExperience(remote, stream, ciphers, conn, xp, $"{m.Rarity} {m.Label} 0x{id:x}");
    }
    await GainFlaskCharges(remote, stream, ciphers, conn, Flasks.KillCharges(m.Rarity, Environment.GetEnvironmentVariable("Game_FLASK_KILL_CHARGES")),
        $"{m.Rarity} {m.Label} 0x{id:x} killed");
    // A Survivors run kills hundreds: normal monsters drop their items a quarter as often there (Survivors.NormalLootOneIn);
    // gold drops as anywhere.
    await DropLoot(remote, stream, ciphers, conn, m, at,
        items: conn.Run is null || m.Rarity != MonsterRarity.Normal || Random.Shared.Next(Survivors.NormalLootOneIn) == 0);
}

// The monster's items (ItemDrops.Roll) as WorldItem objects scattered around where it died, on walkable cells when the
// area's walk map is known. Like dropped items they lie in the instance (InstanceStore).
static async Task DropLoot(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, SpawnedMonster m, (int X, int Y) at,
    bool items = true)
{
    if (Environment.GetEnvironmentVariable("Game_DROPS") == "0") return;
    List<ItemDrops.Drop> drops = items ? [.. Drops.Roll(m.Level, m.Rarity, Random.Shared)] : [];
    // And a pile of gold (ItemDrops.Gold), picked up into the stash's gold.
    if (Drops.Gold(m.Level, m.Rarity, Random.Shared) is { } gold) drops.Add(gold);
    foreach (var drop in drops)
    {
        // A ring around the corpse, 3 to 6 cells out.
        double angle = Random.Shared.NextDouble() * Math.Tau, r = 3 + Random.Shared.NextDouble() * 3;
        int x = at.X + (int)Math.Round(Math.Cos(angle) * r), y = at.Y + (int)Math.Round(Math.Sin(angle) * r);
        if (conn.Layout.Walkable is { } walks && !walks(x, y)) (x, y) = at;
        var ground = new WorldItemObject(x, y, drop.Item);
        uint id = conn.Instance.Ground.Add(ground);
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), ground.Blob()),
            $"0x02b9 create world item 0x{id:x} {drop.Type.Name} (item level {drop.Item.ItemLevel}) dropped by {m.Label} at ({x}, {y})");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
    }
}

// Survivors (Survivors.cs, Agents/docs/survivors.md): a Survivors character in a map's area plays a run there.
static bool IsSurvivorsRun(ClientState conn) => Survivors.IsLeague(conn.League) && MapDevices.Value.IsMapArea(conn.Layout.Hash);

// The run: the chat counts down from 5, then every Survivors.StepMs the waves come around the player, its skills fire,
// it regenerates and the picks open; the boss comes after Survivors.RunSeconds. It ends with the player's death
// (Resurrect takes it home), the boss's (SurvivorsWon) or the connection's: the map is spent either way.
static async Task RunSurvivors(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    var run = new Survivors.Run(conn.Pools.Level, Random.Shared);
    conn.Run = run;
    Task Say(string text) => conn.Commands!.Reply(text);
    try
    {
        if (conn.Layout.Walkable is null || Monsters.Error is not null || Packs.For(conn.Layout.Id) is not { } area)
        {
            Console.WriteLine($"[gateway] {remote} Survivors: {conn.Layout.Id} has no walk map or monster packs ({Monsters.Error ?? "run live.ps1 -Walkmaps"}): no run");
            await Say("Survivors: this map has no walk map or monsters yet, so there is no run here.");
            run.State = Survivors.Run.Phase.Lost;
            return;
        }
        // Emerge monsters stay hidden without their action (PopulateArea).
        MonsterCatalog.Variety? Spawnable(int row) =>
            Monsters.Find(row.ToString()) is { Supported: true } v && !v.Id.Contains("Emerge", StringComparison.OrdinalIgnoreCase) ? v : null;
        foreach (var (pack, weight) in area.Scattered)
            foreach (var (row, w) in pack.Entries)
                if (Spawnable(row) is { } v) run.Pool.Add((v, Math.Max(1, weight * w)));
        run.BossVariety = area.Bosses.Select(Spawnable).FirstOrDefault(v => v is not null);
        if (run.Pool.Count == 0)
        {
            await Say("Survivors: none of this map's monsters can be created yet, so there is no run here.");
            run.State = Survivors.Run.Phase.Lost;
            return;
        }
        Console.WriteLine($"[gateway] {remote} Survivors run in {conn.Layout.Id}: {run.Pool.Count} monster kinds " +
            $"({string.Join(", ", run.Pool.Select(p => p.Variety.Name).Distinct())}), boss {run.BossVariety?.Name ?? "a rare of them"}");
        await Say("Survivors: one life, no portals. Your skills fire on their own; every level gives an upgrade, picked with /1, /2 or /3.");
        for (int n = Survivors.CountdownSeconds; n >= 0; n--)
        {
            await Say(n == 0 ? "Survivors: 0 - survive!" : $"Survivors: {n}");
            // The countdown holds while the area is paused, as the run's own clock does.
            if (n > 0) { await Task.Delay(1000); await WhilePaused(conn); }
            if (conn.Closed) return;
        }
        lock (conn.Monsters)
        {
            run.State = Survivors.Run.Phase.Waves;
            run.CharacterLevel = conn.Pools.Level;
        }
        double last = conn.Clock.Elapsed.TotalSeconds;
        while (!conn.Closed && !run.Over)
        {
            await Task.Delay(Survivors.StepMs);
            // The Clock stands still while paused (the Escape menu, an open pick): so does the run's survival time.
            if (conn.Paused) continue;
            double now = conn.Clock.Elapsed.TotalSeconds, dt = now - last;
            last = now;
            double t = run.Elapsed += dt;
            if (conn.PlayerLife == 0)
            {
                run.State = Survivors.Run.Phase.Lost;
                Console.WriteLine($"[gateway] {remote} Survivors run lost at {Survivors.Time(t)}: {run.Kills} kills, {run.Describe()}");
                await Say($"Survivors: you fell after {Survivors.Time(t)} with {run.Kills} kills. The map is spent; resurrect to go home.");
                break;
            }
            await SurvivorsClock(remote, stream, ciphers, conn, run, t);
            await SurvivorsWaves(remote, stream, ciphers, conn, run, t);
            await SurvivorsSkills(remote, stream, ciphers, conn, run, now, t, dt);
            await SurvivorsRegen(remote, stream, ciphers, conn, run, dt);
        }
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
    {
        // The connection closed while sending.
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[gateway] {remote} Survivors run stopped: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
    }
    if (run.Ring != 0 && !conn.Closed)
        await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(run.Ring), $"0x02bb remove effect 0x{run.Ring:x} (Flame Ring, the run is over)");
    run.Ring = 0;
}

// Every minute the chat tells the time and a rare joins the waves; at Survivors.RunSeconds the boss comes.
static async Task SurvivorsClock(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, Survivors.Run run, double t)
{
    if (run.State != Survivors.Run.Phase.Waves) return;
    if (t >= Survivors.RunSeconds)
    {
        run.State = Survivors.Run.Phase.Boss;
        byte bossLevel = (byte)Math.Min(100, Survivors.MonsterLevel(run.CharacterLevel, t) + 2);
        run.Boss = await SpawnWaveMonster(remote, stream, ciphers, conn, run.BossVariety ?? run.PickMonster(),
            run.BossVariety is null ? MonsterRarity.Rare : MonsterRarity.Unique, bossLevel, 20, 28);
        Console.WriteLine($"[gateway] {remote} Survivors: the boss 0x{run.Boss:x} ({run.BossVariety?.Name ?? "a rare"}) at level {bossLevel}");
        await conn.Commands!.Reply($"Survivors: {Survivors.Time(t)}! The boss comes: kill it to win.");
        return;
    }
    int minutes = (int)(t / 60);
    if (minutes <= run.Minutes) return;
    run.Minutes = minutes;
    await conn.Commands!.Reply($"Survivors: {Survivors.Time(t)} survived, {run.Kills} kills. A rare monster joins the hunt.");
    await SpawnWaveMonster(remote, stream, ciphers, conn, run.PickMonster(), MonsterRarity.Rare, Survivors.MonsterLevel(run.CharacterLevel, t));
}

// The waves: the hunting monsters left far behind go, and new ones come on a ring around the player (a few per step)
// until there are Survivors.Crowd of them (half as many while the boss lives).
static async Task SurvivorsWaves(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, Survivors.Run run, double t)
{
    if (run.State is not (Survivors.Run.Phase.Waves or Survivors.Run.Phase.Boss)) return;
    double now = conn.Clock.Elapsed.TotalSeconds;
    int alive = 0;
    var behind = new List<uint>();
    lock (conn.Monsters)
    {
        var here = conn.Walk.Position(now);
        foreach (var (id, m) in conn.Monsters)
        {
            if (!m.Hunts || m.Dead) continue;
            if (id != run.Boss && Dist(here, m.Walk.Position(now)) > Survivors.LeftBehind) behind.Add(id);
            else alive++;
        }
        foreach (uint id in behind) conn.Monsters.Remove(id);
    }
    foreach (uint id in behind) await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), null);
    int crowd = Survivors.Crowd(t) / (run.State == Survivors.Run.Phase.Boss ? 2 : 1);
    byte level = Survivors.MonsterLevel(run.CharacterLevel, t);
    for (int i = 0; i < Math.Min(Survivors.SpawnsPerStep, crowd - alive); i++)
        await SpawnWaveMonster(remote, stream, ciphers, conn, run.PickMonster(), Survivors.RollRarity(t, Random.Shared), level);
}

// A hunting monster on a walkable cell of a ring around the player. Returns its object id, 0 when no cell was found.
static async Task<uint> SpawnWaveMonster(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
    MonsterCatalog.Variety variety, MonsterRarity rarity, byte level, int minRadius = Survivors.SpawnMinRadius, int maxRadius = Survivors.SpawnMaxRadius)
{
    var (px, py) = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
    for (int i = 0; i < 24; i++)
    {
        double angle = Random.Shared.NextDouble() * Math.Tau, r = minRadius + Random.Shared.NextDouble() * (maxRadius - minRadius);
        int x = px + (int)Math.Round(Math.Cos(angle) * r), y = py + (int)Math.Round(Math.Sin(angle) * r);
        if (x < 0 || y < 0 || (conn.Layout.GridW > 0 && x >= conn.Layout.GridW) || (conn.Layout.GridH > 0 && y >= conn.Layout.GridH)) continue;
        if (conn.Layout.Walkable is { } w && !w(x, y)) continue;
        return await SpawnMonster(remote, stream, ciphers, conn, variety, level, rarity, x, y, log: rarity != MonsterRarity.Normal, hunt: true);
    }
    return 0;
}

// The Fireball projectile (Metadata/Projectiles/Fireball) the run's Fireball flies with, null without the tables.
static (string Id, int Speed)? SurvivorsFireball() => Skills.ProjectileFor(Skills.EffectRow("Fireball"));

// A skill hit's damage: a share of a normal monster's life at the character's level (the pool's first monster with its
// life multiplier taken out), with the run's Might, ±10%, doubled at Precision's chance.
static int SurvivorsDamage(Survivors.Run run, double share)
{
    var v = run.Pool[0].Variety;
    double life = (Monsters.BaseLife(v, (byte)Math.Clamp(run.CharacterLevel, 1, 100)) ?? 20) * 100.0 / Math.Max(1, v.LifeMultiplier);
    return run.Precise((int)Math.Max(1, Math.Round(life * share * run.DamageMultiplier * (0.9 + 0.2 * Random.Shared.NextDouble()))));
}

// The living enemies within range of a cell, nearest first: (id, monster, where it is).
static List<(uint Id, SpawnedMonster M, (int X, int Y) At)> EnemiesNear(ClientState conn, (int X, int Y) at, double range, double now)
{
    lock (conn.Monsters)
        return [.. conn.Monsters.Where(kv => kv.Value is { Dead: false, Hostile: true, Life: > 0 })
            .Select(kv => (kv.Key, kv.Value, At: kv.Value.Walk.Position(now)))
            .Where(e => Dist(e.At, at) <= range).OrderBy(e => Dist(e.At, at))];
}

// The run's skills, each when it is ready: Fireball at the nearest enemies, the Flame Ring burning around the player
// (Righteous Fire's circle, following it), Fire Burst exploding among the enemies.
static async Task SurvivorsSkills(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, Survivors.Run run,
    double now, double t, double dt)
{
    if (conn.PlayerLife == 0) return;
    var here = conn.Walk.Position(now);
    if (run.Level(Survivors.Upgrade.Fireball) is int fireball and > 0 && t >= run.ReadyAt.GetValueOrDefault(Survivors.Upgrade.Fireball)
        && SurvivorsFireball() is { } projectile)
    {
        var targets = EnemiesNear(conn, here, Survivors.FireballRange * run.Reach, now)
            .Take(Survivors.FireballProjectiles(fireball) + run.ExtraProjectiles).ToList();
        if (targets.Count > 0)
        {
            run.ReadyAt[Survivors.Upgrade.Fireball] = t + Survivors.FireballSeconds(fireball, run.Haste);
            foreach (var (_, _, at) in targets)
                _ = FlyProjectile(remote, stream, ciphers, conn, projectile, here, at,
                    _ => (SurvivorsDamage(run, Survivors.FireballDamage(fireball)), false), log: false);
        }
    }
    if (run.Level(Survivors.Upgrade.FlameRing) is int ring and > 0)
    {
        if (run.Ring == 0)
        {
            run.Ring = conn.NextObjectId++;
            var ringObject = new EffectObject(here.X, here.Y, RighteousFireRingAo) { Flags0 = EffectObject.FixedRotation };
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(run.Ring, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), ringObject.Blob()),
                $"0x02b9 create effect 0x{run.Ring:x} {RighteousFireRingAo} at ({here.X}, {here.Y}) (Survivors Flame Ring)");
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(run.Ring), null);
            await FollowRighteousFire(remote, stream, ciphers, conn);
        }
        double perSecond = SurvivorsDamage(run, Survivors.FlameRingDamage(ring));
        var burnt = new List<(uint Id, SpawnedMonster M, int Damage)>();
        foreach (var (id, m, _) in EnemiesNear(conn, here, Survivors.FlameRingRadius(ring, run.Reach), now))
            lock (conn.Monsters)
            {
                m.Burn += perSecond * dt;
                if (m.Burn < 1) continue;
                int damage = (int)m.Burn;
                m.Burn -= damage;
                burnt.Add((id, m, damage));
            }
        foreach (var (id, m, damage) in burnt) await HitMonster(remote, stream, ciphers, conn, id, m, damage, "the Flame Ring", log: false);
    }
    if (run.Level(Survivors.Upgrade.FireBurst) is int burst and > 0 && t >= run.ReadyAt.GetValueOrDefault(Survivors.Upgrade.FireBurst))
    {
        var near = EnemiesNear(conn, here, Survivors.FireBurstRange * run.Reach, now);
        if (near.Count > 0)
        {
            run.ReadyAt[Survivors.Upgrade.FireBurst] = t + Survivors.FireBurstSeconds(burst, run.Haste);
            var at = near[Random.Shared.Next(near.Count)].At;
            await SurvivorsExplosion(remote, stream, ciphers, conn, at);
            foreach (var (id, m, _) in EnemiesNear(conn, at, Survivors.FireBurstRadius(burst, run.Reach), now))
                await HitMonster(remote, stream, ciphers, conn, id, m, SurvivorsDamage(run, Survivors.FireBurstDamage(burst)), "Fire Burst", log: false);
        }
    }
    // Fire Nova: Fireballs out on every side, evenly spaced, from where the player stands.
    if (run.Level(Survivors.Upgrade.FireNova) is int nova and > 0 && t >= run.ReadyAt.GetValueOrDefault(Survivors.Upgrade.FireNova)
        && SurvivorsFireball() is { } novaball)
    {
        run.ReadyAt[Survivors.Upgrade.FireNova] = t + Survivors.FireNovaSeconds(nova, run.Haste);
        int count = Survivors.FireNovaProjectiles(nova) + 2 * run.ExtraProjectiles;
        double turn = Random.Shared.NextDouble() * Math.Tau;
        for (int i = 0; i < count; i++)
        {
            double angle = turn + Math.Tau * i / count;
            var to = (here.X + (int)Math.Round(Math.Cos(angle) * Survivors.FireNovaAim), here.Y + (int)Math.Round(Math.Sin(angle) * Survivors.FireNovaAim));
            _ = FlyProjectile(remote, stream, ciphers, conn, novaball, here, to,
                _ => (SurvivorsDamage(run, Survivors.FireNovaDamage(nova)), false), log: false);
        }
    }
    // Meteor: the enemy with the most life near the player takes a heavy hit, and those right around it too.
    if (run.Level(Survivors.Upgrade.Meteor) is int meteor and > 0 && t >= run.ReadyAt.GetValueOrDefault(Survivors.Upgrade.Meteor))
    {
        var near = EnemiesNear(conn, here, Survivors.MeteorRange * run.Reach, now);
        if (near.Count > 0)
        {
            run.ReadyAt[Survivors.Upgrade.Meteor] = t + Survivors.MeteorSeconds(meteor, run.Haste);
            var at = near.MaxBy(e => e.M.Life).At;
            await SurvivorsExplosion(remote, stream, ciphers, conn, at);
            foreach (var (id, m, _) in EnemiesNear(conn, at, Survivors.MeteorRadius(meteor, run.Reach), now))
                await HitMonster(remote, stream, ciphers, conn, id, m, SurvivorsDamage(run, Survivors.MeteorDamage(meteor)), "Meteor", log: false);
        }
    }
}

// Fire Burst's explosion: the Fireball's impact model as an effect object at the cell, gone 1.5 s later.
static async Task SurvivorsExplosion(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, (int X, int Y) at)
{
    const string Explosion = "Metadata/Effects/Spells/fireball/explode_new2.ao";
    uint id = conn.NextObjectId++;
    await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
        new EffectObject(at.X, at.Y, Explosion).Blob()), null);
    await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
    _ = Task.Delay(1500).ContinueWith(async _ => { if (!conn.Closed) await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), null); });
}

// Regeneration's life per second (a share of maximum life), sent when it adds a whole point.
static async Task SurvivorsRegen(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, Survivors.Run run, double dt)
{
    if (run.Level(Survivors.Upgrade.Regeneration) is not (int regen and > 0)) return;
    uint life;
    lock (conn.Monsters)
    {
        if (conn.PlayerLife == 0 || conn.PlayerLife >= conn.PlayerMaxLife) { run.RegenCarry = 0; return; }
        run.RegenCarry += conn.PlayerMaxLife * regen / 100.0 * dt;
        if (run.RegenCarry < 1) return;
        int add = (int)run.RegenCarry;
        run.RegenCarry -= add;
        conn.PlayerLife = (uint)Math.Min(conn.PlayerMaxLife, conn.PlayerLife + add);
        life = conn.PlayerLife;
    }
    await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, life), null);
}

// Heals the player by a share of its maximum life (Vampirism, Mending).
static async Task SurvivorsHeal(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, double share, string why)
{
    uint life;
    lock (conn.Monsters)
    {
        if (conn.PlayerLife == 0 || conn.PlayerLife >= conn.PlayerMaxLife) return;
        conn.PlayerLife = (uint)Math.Min(conn.PlayerMaxLife, conn.PlayerLife + Math.Max(1, (long)Math.Round(conn.PlayerMaxLife * share)));
        life = conn.PlayerLife;
    }
    await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, life), $"0x01b1 player life {life}/{conn.PlayerMaxLife} ({why})");
}

// A monster of the run died: the kill counts, Vampirism heals, now and then a Beach Map drops, and the boss's death wins
// the run.
static async Task SurvivorsKill(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster m, (int X, int Y) at)
{
    if (conn.Run is not { } run || run.State is not (Survivors.Run.Phase.Waves or Survivors.Run.Phase.Boss)) return;
    lock (conn.Monsters) run.Kills++;
    if (run.Level(Survivors.Upgrade.Vampirism) is int vampirism and > 0)
        await SurvivorsHeal(remote, stream, ciphers, conn, vampirism / 100.0, "Vampirism");
    if (Survivors.DropsMap(m.Rarity, Random.Shared)) await DropSurvivorsMaps(remote, stream, ciphers, conn, at, 1, $"{m.Label}'s drop");
    if (id == run.Boss) await SurvivorsWon(remote, stream, ciphers, conn, run, at);
}

// Beach Maps on the ground around a cell.
static async Task DropSurvivorsMaps(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, (int X, int Y) at, int count, string why)
{
    if (Items.Find(Survivors.MapItem) is not { Supported: true } type) return;
    for (int i = 0; i < count; i++)
    {
        double angle = Random.Shared.NextDouble() * Math.Tau;
        int x = at.X + (int)Math.Round(Math.Cos(angle) * 4), y = at.Y + (int)Math.Round(Math.Sin(angle) * 4);
        if (conn.Layout.Walkable is { } walks && !walks(x, y)) (x, y) = at;
        var ground = new WorldItemObject(x, y, ItemCatalog.Create(type));
        uint id = conn.Instance.Ground.Add(ground);
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), ground.Blob()),
            $"0x02b9 create world item 0x{id:x} {type.Name} at ({x}, {y}) (Survivors: {why})");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
    }
}

// The boss is dead: the run is won. The waves go, two Beach Maps drop where the boss fell and a portal home opens next
// to the player.
static async Task SurvivorsWon(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, Survivors.Run run, (int X, int Y) at)
{
    run.State = Survivors.Run.Phase.Won;
    double t = run.Elapsed;
    List<uint> gone;
    lock (conn.Monsters)
    {
        gone = [.. conn.Monsters.Where(kv => kv.Value is { Hunts: true, Dead: false }).Select(kv => kv.Key)];
        foreach (uint id in gone) conn.Monsters.Remove(id);
    }
    foreach (uint id in gone) await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), null);
    Console.WriteLine($"[gateway] {remote} Survivors run won at {Survivors.Time(t)}: {run.Kills} kills, {run.Describe()}");
    await conn.Commands!.Reply($"Survivors: victory at {Survivors.Time(t)} with {run.Kills} kills! Take your maps and go home through the portal.");
    await DropSurvivorsMaps(remote, stream, ciphers, conn, at, Survivors.BossMaps, "the boss's reward");
    await SpawnMapReturnPortal(remote, stream, ciphers, conn, NextToPlayer(conn));
}

// Opens the next pick if one waits and none is open: the three choices in chat, and the area pauses until one is taken
// (Game_SURVIVORS_PAUSE=0 keeps it running).
static async Task OpenSurvivorsPick(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    if (conn.Run is not { } run) return;
    IReadOnlyList<Survivors.Upgrade>? offer;
    int waiting;
    lock (conn.Monsters) { offer = run.OpenPick(); waiting = run.PendingPicks; }
    if (offer is null) return;
    Console.WriteLine($"[gateway] {remote} Survivors pick: {string.Join(", ", offer)} ({waiting} more waiting)");
    await conn.Commands!.Reply(string.Join('\n', offer.Select((u, i) => Survivors.ChoiceLine(i + 1, u, run))
        .Prepend($"Survivors: choose an upgrade with /1, /2 or /3{(waiting > 0 ? $" ({waiting} more after this one)" : "")}")));
    if (!conn.Paused && SurvivorsPauses)
        await SetPaused(remote, stream, ciphers, conn, true);
}

// /1, /2, /3 (/pick n): takes that choice of the open pick, then opens the next one or resumes the area.
static async Task ChooseSurvivorsPick(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, int choice)
{
    if (conn.Run is not { } run) { await conn.Commands!.Reply("Survivors: there is no run here."); return; }
    Survivors.Upgrade? taken;
    lock (conn.Monsters) taken = run.Choose(choice);
    if (taken is not { } u) { await conn.Commands!.Reply(run.Offer is null ? "Survivors: no upgrade to choose now." : "Survivors: choose 1, 2 or 3."); return; }
    Console.WriteLine($"[gateway] {remote} Survivors pick {choice}: {u} → {run.Describe()}");
    if (u == Survivors.Upgrade.Mending) await SurvivorsHeal(remote, stream, ciphers, conn, 0.3, "Mending");
    if (u == Survivors.Upgrade.Swiftness)
    {
        // The movement speed is a pool stat (PoolsFor): the client and the walk estimate take it now.
        var pools = PoolsFor(conn, conn.Pools.Level);
        lock (conn.Monsters)
        {
            conn.Pools = pools;
            conn.ApplyMovementSpeed();
        }
        await SendCharacterStats(remote, stream, ciphers, conn, $"Survivors Swiftness {run.Level(u)}");
    }
    await conn.Commands!.Reply($"Survivors: {Survivors.Info(u).Name}{(u == Survivors.Upgrade.Mending ? "" : $" {run.Level(u)}")}. Now: {run.Describe()}.");
    if (run.PendingPicks > 0) await OpenSurvivorsPick(remote, stream, ciphers, conn);
    else if (conn.Paused) await SetPaused(remote, stream, ciphers, conn, false);
}

// In the hideout, a Survivors character without a map (none in the main inventory or the map device, and no map open)
// gets a Beach Map, so it can never be stuck.
static async Task GiveSurvivorsMap(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    if (!Survivors.IsLeague(conn.League) || HideoutCatalog.ByArea(conn.Layout.Hash) is null) return;
    if (MapDevice.OpenOf(CharacterName(conn)) is not null || Items.Find(Survivors.MapItem) is not { Supported: true } type) return;
    var items = ItemsOf(conn);
    bool IsMap(InventoryItem i) => Items.ByHash(i.Item.TypeHash)?.Class == MapDevice.MapClass;
    if (items.Items(PlayerInventories.MainInventory).Any(IsMap) || items.Items(PlayerInventories.MapDevice).Any(IsMap)) return;
    if (items.FreeCell(PlayerInventories.MainInventory, type.Width, type.Height, ShapeOf) is not { } cell)
    {
        await conn.Commands!.Reply("Survivors: make room in your inventory for a new Beach Map.");
        return;
    }
    var added = items.Put(PlayerInventories.MainInventory, new InventoryItem(items.NewItemId(), cell.X, cell.Y, ItemCatalog.Create(type)));
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
        items.NextRevision(PlayerInventories.MainInventory), [], [added]), $"0x00bf inventory 1: Survivors gives {type.Name} (no map left)");
    await conn.Commands!.Reply("Survivors: you had no map left, so here is a Beach Map.");
}

// /god: what it adds on top of the character's stats (GodStats) and pools (PoolsFor): 1000% increased attack and cast
// speed, 100% of maximum life and 10 000 mana regenerated per second, +10 000 maximum life and +1000 to each attribute.

static async Task SetGod(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, bool on)
{
    lock (conn.Monsters)
    {
        conn.God = on;
        conn.Pools = PoolsFor(conn, conn.Pools.Level);
        conn.ApplyMovementSpeed();
        conn.PlayerLife = on ? conn.PlayerMaxLife : Math.Min(conn.PlayerLife, conn.PlayerMaxLife);
        conn.PlayerMana = on ? (uint)UnreservedMana(conn) : Math.Min(conn.PlayerMana, (uint)UnreservedMana(conn));
    }
    Console.WriteLine($"[gateway] {remote} god mode {(on ? "on" : "off")}: life {conn.Pools.Life}, mana {conn.Pools.Mana}, {ReservedMana(conn)} reserved");
    await SendCharacterStats(remote, stream, ciphers, conn, on ? "god mode on" : "god mode off");
    await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerLife), $"0x01b1 player life {conn.PlayerLife}");
    await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana {conn.PlayerMana}");
    // UI built on entering caches the attributes (see SendPlayer): send them once more.
    await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, [.. BaseStats(conn)]), "0x01b3 player attributes");
}

// The player's life per second from regeneration: the stats' flat life regeneration per minute (/god, /set_stat).
static double LifeRegenPerSecond(ClientState conn)
{
    var stats = CharacterPlayerStats(conn);
    int Get(string name) => Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0;
    // flat life per minute plus a percentage of maximum life per minute (/god: 6000 = 100% per second)
    return (Get("base_life_regeneration_rate_per_minute") + conn.PlayerMaxLife * Get("life_regeneration_rate_per_minute_%") / 100.0) / 60;
}

// The player's mana per second from regeneration: Character.ot's mana_regeneration_rate_per_minute_% (105: 1.75% of
// maximum mana per second) plus the stats' flat base_mana_regeneration_rate_per_minute (/god), raised by
// mana_regeneration_rate_+%.
static double ManaRegenPerSecond(ClientState conn)
{
    var stats = CharacterPlayerStats(conn);
    int Get(string name) => Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0;
    double perMinute = conn.Pools.Mana * (BaseManaRegenPctPerMinute + Get("mana_regeneration_rate_per_minute_%")) / 100.0
        + Get("base_mana_regeneration_rate_per_minute");
    return perMinute * (1 + Get("mana_regeneration_rate_+%") / 100.0) / 60;
}

// The mana the running auras reserve, and what is left for casting and regeneration.
static int ReservedMana(ClientState conn)
{
    lock (conn.Monsters) return AuraCatalog.ReservedMana(conn.Pools, conn.Auras.Values);
}

static int UnreservedMana(ClientState conn) => Math.Max(0, (int)conn.Pools.Mana - ReservedMana(conn));

// 0x1b1 for the mana pool: every update carries the reserved amount, or the client would clear it.
static Task SendPlayerMana(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
    string? log = null) =>
    Send(remote, stream, ciphers,
        InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerMana, (uint)ReservedMana(conn), pool: ManaPool), log);

// A gem skill's mana cost at the gem's level (its mana_cost stat), paid when the cast starts. False when the player lacks
// the mana.
static bool PayMana(ClientState conn, int effect, int level, int quality, out uint cost)
{
    cost = Stats.Key("mana_cost") is uint k ? (uint)Skills.EffectStats(effect, level, quality).Where(kv => kv.Item1 == k).Select(kv => kv.Item2).FirstOrDefault() : 0;
    lock (conn.Monsters)
    {
        if (conn.PlayerMana < cost) return false;
        conn.PlayerMana -= cost;
        return true;
    }
}

// A cast's time in ms: the effect's CastTime scaled by the player's increased cast speed.
static int CastMs(ClientState conn, int effect) => (int)Math.Round(Skills.CastMs(effect) / (1 + CastSpeedPct(CharacterPlayerStats(conn)) / 100.0));

// cast_speed_+% is the total (PanelStats: base_cast_speed_+% and attack_and_cast_speed_+% are already in it).
static int CastSpeedPct(IReadOnlyDictionary<uint, int> stats) =>
    Stats.Key("cast_speed_+%") is uint k ? stats.GetValueOrDefault(k) : 0;

// Righteous Fire (GrantedEffects RighteousFire): casting it puts the righteous_fire buff (BuffDefinitions row 12: the
// flames around the player, BuffVisuals 15) on the player with S→C 0x1c1. While it lasts the player burns for 90% of
// maximum life per second, nonlethal (base_nonlethal_fire_damage_%_of_maximum_life_taken_per_minute 5400), and enemies
// within its radius (active_skill_base_area_of_effect_radius 18 cells at level 1, plus their size) take 70% of the
// player's maximum life per second as fire damage (base_righteous_fire_%_of_max_life_to_deal_to_nearby_per_minute 4200)
// and carry the righteous_fire_aura buff (row 13). It ends at death or on leaving the area; casting it again does nothing.
static bool IsRighteousFire(int effect) => Skills.EffectId(effect) == "RighteousFire";

// Righteous Fire's ring follows the player: after each move order it glides (0x1ba) to where the walk ends, arriving with
// the player.
static async Task FollowRighteousFire(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    // The Survivors' Flame Ring is the same circle and follows the same way.
    foreach (uint ring in new[] { conn.RighteousFire?.Ring ?? 0, conn.Run?.Ring ?? 0 }.Where(r => r != 0))
    {
        double now = conn.Clock.Elapsed.TotalSeconds, arrives = conn.Walk.ArrivesAt;
        var to = conn.Walk.Position(Math.Max(now, arrives));
        await Send(remote, stream, ciphers, InstanceProtocol.MoveTo(ring, to.X, to.Y, (float)Math.Max(0.05, arrives - now), u28: TerrainHeight), null);
    }
}

static async Task ActivateRighteousFire(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, ushort skillId,
    int effect, int level, int quality)
{
    ushort buff, aura;
    uint ring;
    (int X, int Y) at;
    lock (conn.Monsters)
    {
        if (conn.RighteousFire is not null || conn.PlayerLife == 0) return;
        buff = conn.NextBuff++;
        aura = conn.NextBuff++;
        ring = conn.NextObjectId++;
        at = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        var stats = Skills.EffectStats(effect, level, quality).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        int Get(string name, int fallback) => Stats.Key(name) is uint k && stats.TryGetValue(k, out int v) ? v : fallback;
        conn.RighteousFire = new RighteousFireState(buff, aura, ring, skillId,
            DamagePctPerMinute: Get("base_righteous_fire_%_of_max_life_to_deal_to_nearby_per_minute", 4200),
            BurnPctPerMinute: Get("base_nonlethal_fire_damage_%_of_maximum_life_taken_per_minute", 5400),
            Radius: Get("active_skill_base_area_of_effect_radius", 18));
    }
    await Send(remote, stream, ciphers, InstanceEntities.AddBuff(PlayerId, buff, RighteousFireBuffRow, PlayerId, skillId),
        $"0x01c1 player buff {buff}: righteous_fire (BuffDefinitions row {RighteousFireBuffRow}) from skill 0x{skillId:x4}, " +
        $"radius {conn.RighteousFire!.Radius}, {conn.RighteousFire.DamagePctPerMinute / 60.0:0.#}% of life per second to enemies, {conn.RighteousFire.BurnPctPerMinute / 60.0:0.#}% to the player");
    // The caster burns in its own fire: righteous_fire_aura too, whose BuffVisuals (19) give a player model the
    // flaming hands (EPKFiles2 ri_fire/player.epk) and the ignite flames; the first buff only draws the ring at its feet.
    await Send(remote, stream, ciphers, InstanceEntities.AddBuff(PlayerId, aura, RighteousFireAuraBuffRow, PlayerId, skillId),
        $"0x01c1 player buff {aura}: righteous_fire_aura (BuffDefinitions row {RighteousFireAuraBuffRow})");
    // The big burning circle is not a buff visual: it is MiscAnimated RighteousFireRing, an effect object of its own
    // (EffectObject), kept under the player as it walks (FollowRighteousFire).
    // FixedRotation: the glides that keep it under the player must not turn it (it spun as the player turned).
    var ringObject = new EffectObject(at.X, at.Y, RighteousFireRingAo) { Flags0 = EffectObject.FixedRotation };
    await Send(remote, stream, ciphers, InstanceEntities.CreateObject(ring, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), ringObject.Blob()),
        $"0x02b9 create effect 0x{ring:x} {RighteousFireRingAo} at ({at.X}, {at.Y})");
    await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(ring), null);
}

static async Task EndRighteousFire(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string why)
{
    RighteousFireState? rf;
    List<(uint Monster, ushort Buff)> auras;
    lock (conn.Monsters)
    {
        rf = conn.RighteousFire;
        conn.RighteousFire = null;
        auras = rf is null ? [] : [.. rf.Auras.Select(kv => (kv.Key, kv.Value))];
    }
    if (rf is null) return;
    await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, rf.Buff), $"0x01c2 player buff {rf.Buff}: righteous_fire ends ({why})");
    await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, rf.SelfAura), null);
    await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(rf.Ring), $"0x02bb remove effect 0x{rf.Ring:x} (Righteous Fire's ring)");
    foreach (var (monster, buff) in auras)
        await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(monster, buff), null);
}

// The player's own tick (PlayerTickMs): life regeneration and Righteous Fire's burning, on the player and on the enemies
// around it. Life changes are sent with 0x1b1; monsters killed by the fire die as from a hit (MonsterKilled).
static async Task RunPlayerTick(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    double carry = 0, manaCarry = 0;   // fractions of life and mana between ticks
    int suppression = Suppression(conn).Chance;   // the panel's Spell Suppression chance as of the last tick
    while (!conn.Closed)
    {
        await Task.Delay(PlayerTickMs);
        if (conn.Closed) break;
        if (conn.Paused) continue;
        double seconds = PlayerTickMs / 1000.0;
        double regen = LifeRegenPerSecond(conn) * seconds;
        var rf = conn.RighteousFire;
        uint before, after;
        bool dead;
        lock (conn.Monsters)
        {
            before = conn.PlayerLife;
            dead = before == 0;
            double burnPerSecond = rf is null ? 0 : conn.PlayerMaxLife * rf.BurnPctPerMinute / 6000.0;
            var burnDot = new DamageOverTime(DamageType.Fire, burnPerSecond, seconds);
            double burn = DamageModel.Mitigate(burnDot.TotalRemainingDamage,
                Resistance(conn, DamageType.Fire), MaxResistance(conn, DamageType.Fire));
            double change = carry + regen - burn;
            int whole = (int)Math.Truncate(change);
            carry = change - whole;
            // The burn is nonlethal: it stops at 1 life.
            after = dead ? 0 : (uint)Math.Clamp((long)before + whole, Math.Min(1, before), conn.PlayerMaxLife);
            if (!dead) conn.PlayerLife = after;
            if (after == conn.PlayerMaxLife && carry > 0) carry = 0;
        }
        if (dead)
        {
            carry = 0;
            if (rf is not null) await EndRighteousFire(remote, stream, ciphers, conn, "death");
            continue;
        }
        if (after != before) await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, after), null);
        uint manaBefore, manaAfter, manaCap;
        double manaRegen = ManaRegenPerSecond(conn) * seconds;
        lock (conn.Monsters)
        {
            manaBefore = conn.PlayerMana;
            double change = manaCarry + manaRegen;
            int whole = (int)Math.Truncate(change);
            manaCarry = change - whole;
            manaCap = (uint)UnreservedMana(conn);
            conn.PlayerMana = manaAfter = (uint)Math.Clamp((long)manaBefore + whole, 0, manaCap);
            if (manaAfter == manaCap) manaCarry = 0;
        }
        if (manaAfter != manaBefore) await SendPlayerMana(remote, stream, ciphers, conn, null);
        if (rf is not null) await BurnAround(remote, stream, ciphers, conn, rf, seconds);
        // The character panel's Spell Suppression chance again (0x1b3 with only its stats) when a condition changed
        // it: full life lost or regained, a walk started or ended, an enemy hit 4 s ago.
        if (Suppression(conn).Chance is var chance && chance != suppression)
        {
            suppression = chance;
            await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, [.. SuppressionChanceKeys.Select(k => (k, chance))]),
                $"0x01b3 player Spell Suppression chance {chance}%");
        }
        if (conn.Layout.Waypoint is { } waypoint
            && Dist(conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds), (waypoint.GridX, waypoint.GridY)) <= WaypointActivationRange)
            await ActivateWaypoint(remote, stream, ciphers, conn, "the player came near");
        // Gold within GoldAutoLootRange is picked up without a click.
        var at = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        foreach (var (id, gold) in conn.Instance.Ground.Within(at.X, at.Y, GoldAutoLootRange, IsGold))
            await PickUpGold(remote, stream, ciphers, conn, id, gold, "auto-looted");
    }
}

// The flasks' recovery, apart from the player tick so the orb fills smoothly: while a flask recovers, every
// FlaskStepMs the life and mana it gave since the last step are added, and each whole point goes to the client at once
// (S→C 0x1b1; a Small Life Flask's 23 life per second is a +1 about every 43 ms). Effects end when their time is up,
// their pool is full, or the player dies (13-items.md §8).
static async Task RunFlaskRecovery(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    double lifeCarry = 0, manaCarry = 0, last = conn.Clock.Elapsed.TotalSeconds;
    while (!conn.Closed)
    {
        await Task.Delay(FlaskStepMs);
        double now = conn.Clock.Elapsed.TotalSeconds;
        if (!conn.FlaskEffects.Any)
        {
            (lifeCarry, manaCarry, last) = (0, 0, now);
            continue;
        }
        var (life, mana, ended) = conn.FlaskEffects.Advance(last, now);
        last = now;
        uint lifeBefore, lifeAfter, manaBefore, manaAfter, manaCap;
        bool dead;
        lock (conn.Monsters)
        {
            lifeBefore = conn.PlayerLife;
            dead = lifeBefore == 0;
            lifeCarry += life;
            int lifeWhole = (int)Math.Truncate(lifeCarry);
            lifeCarry -= lifeWhole;
            conn.PlayerLife = lifeAfter = dead ? 0 : (uint)Math.Min((long)lifeBefore + lifeWhole, conn.PlayerMaxLife);
            manaBefore = conn.PlayerMana;
            manaCarry += mana;
            int manaWhole = (int)Math.Truncate(manaCarry);
            manaCarry -= manaWhole;
            manaCap = (uint)UnreservedMana(conn);
            conn.PlayerMana = manaAfter = dead ? manaBefore : (uint)Math.Min((long)manaBefore + manaWhole, manaCap);
        }
        if (dead)
        {
            // Flask effects end with death (CancelOnDeath).
            await EndFlaskEffects(remote, stream, ciphers, conn, [.. ended, .. conn.FlaskEffects.End(_ => true)], "death");
            continue;
        }
        if (lifeAfter != lifeBefore) await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, lifeAfter), null);
        if (manaAfter != manaBefore) await SendPlayerMana(remote, stream, ciphers, conn, null);
        // A flask's effect also ends once its pool is full (the flask_effect buffs are not "not removed when full").
        bool lifeFull = lifeAfter == conn.PlayerMaxLife, manaFull = manaAfter == manaCap;
        ended.AddRange(conn.FlaskEffects.End(e => (e.LifePerSecond == 0 || lifeFull) && (e.ManaPerSecond == 0 || manaFull)));
        if (ended.Count > 0)
            await EndFlaskEffects(remote, stream, ciphers, conn, ended, $"life {lifeAfter}/{conn.PlayerMaxLife}, mana {manaAfter}/{manaCap}");
    }
}

// The ended flask effects' buffs come off the player (S→C 0x1c2).
static async Task EndFlaskEffects(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, List<Flasks.Effect> ended, string why)
{
    foreach (var e in ended)
        if (FlaskBuffs) await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, e.Buff), $"0x01c2 player buff {e.Buff}: flask {e.Slot} effect ends ({why})");
        else Console.WriteLine($"[gateway] {remote} flask {e.Slot} effect ends ({why})");
}

// Righteous Fire's damage to the hostile monsters within its radius for one tick, with their aura buff on while they
// are inside.
static async Task BurnAround(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, RighteousFireState rf, double seconds)
{
    double now = conn.Clock.Elapsed.TotalSeconds;
    double perSecond = conn.PlayerMaxLife * rf.DamagePctPerMinute / 6000.0;
    var damageOverTime = new DamageOverTime(DamageType.Fire, perSecond, seconds);
    var hits = new List<(uint Id, SpawnedMonster M, uint Life, int Damage)>();
    var entered = new List<(uint Id, ushort Buff)>();
    var left = new List<(uint Id, ushort Buff)>();
    lock (conn.Monsters)
    {
        var here = conn.Walk.Position(now);
        foreach (var (id, m) in conn.Monsters)
        {
            bool inside = !m.Dead && m.Life > 0 && m.Hostile && Dist(here, m.Walk.Position(now)) <= rf.Radius + m.StopRange;
            if (!inside)
            {
                if (rf.Auras.Remove(id, out ushort gone)) left.Add((id, gone));
                continue;
            }
            if (!rf.Auras.ContainsKey(id)) { rf.Auras[id] = conn.NextBuff; entered.Add((id, conn.NextBuff++)); }
            var (damage, carry) = damageOverTime.Tick(seconds, m.Burn);
            m.Burn = carry;
            if (damage == 0) continue;
            m.Life = (uint)Math.Max(0, (long)m.Life - damage);
            m.Aggro = m.Life > 0;
            if (m.Life == 0) { m.Dead = true; if (rf.Auras.Remove(id, out ushort b)) left.Add((id, b)); }
            hits.Add((id, m, m.Life, damage));
        }
    }
    foreach (var (id, buff) in entered)
        await Send(remote, stream, ciphers, InstanceEntities.AddBuff(id, buff, RighteousFireAuraBuffRow, PlayerId, rf.Skill), null);
    foreach (var (id, buff) in left)
        await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(id, buff), null);
    foreach (var (id, m, life, damage) in hits)
    {
        if (life == 0)
            await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(id, [(StatKeys.HideMiniLifeBar, 1)]), null);
        await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(id, life),
            life == 0 ? $"0x01b1 monster 0x{id:x} ({m.Label}) burnt to death by Righteous Fire" : null);
        if (life == 0) await MonsterKilled(remote, stream, ciphers, conn, id, m);
    }
}

// To the character's hideout (21-hideouts.md §3): "/hideout" (C→S 0x16 command 45) or the World panel's Hideout button
// (C→S 0x17a). Its own instance (Auth re-enters the live one), arriving next to its waypoint.
static async Task WarpToHideout(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string why)
{
    if (HideoutCatalog.Default is not { } hideout)
    {
        Console.WriteLine($"[gateway] {remote} {why}: no data/hideouts.json (run scripts/dev/extract-data.ps1)");
        return;
    }
    if (conn.Layout.Hash == hideout.AreaHash16) { Console.WriteLine($"[gateway] {remote} {why}: already in the {hideout.Name}"); return; }
    await ChangeArea(remote, stream, ciphers, conn, new PendingTransfer(hideout.AreaHash16, 0, false, AtWaypoint: true), $"{why}: {hideout.Name}");
}

// 0x2b2 now; the move itself (0x27) once the client acknowledges with 0x2cb.
static async Task ChangeArea(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, PendingTransfer t, string why)
{
    if (conn.PendingTransfer is not null) return; // the client repeats 0x24 until something happens
    conn.PendingTransfer = t;
    await Send(remote, stream, ciphers, InstanceTransfer.ChangeArea(t.Area), $"0x02b2 change area to 0x{t.Area:x4} ({why})");
    // If the client never acknowledges, move it anyway after a moment.
    _ = Task.Delay(2000).ContinueWith(async _ => { if (conn.PendingTransfer is { } p) await Transfer(remote, stream, ciphers, conn, p); });
}

// Auth moves the player and issues the destination ticket; 0x27 sends the client there (it reconnects to this Gateway).
static async Task Transfer(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, PendingTransfer t)
{
    conn.PendingTransfer = null;
    var r = await InstanceControl.CallAsync(new ControlRequest
    {
        Op = "transfer", Ticket = conn.Ticket, Area = t.Area, Instance = t.Instance, New = t.New,
    });
    t.Done?.Invoke(r);
    if (!r.Ok)
    {
        Console.WriteLine($"[gateway] {remote} transfer failed: {r.Error}");
        return;
    }
    if (t.AtWaypoint) WaypointArrivals[r.Ticket] = 0;
    else if (!t.AtSpawn) AreaLayouts.CameFrom[r.Ticket] = conn.Layout.Hash;
    string host = Environment.GetEnvironmentVariable("Game_GATEWAY_HOST") ?? "127.0.0.1";
    ushort port = ushort.TryParse(Environment.GetEnvironmentVariable("Game_GATEWAY_PORT"), out var gp) ? gp : (ushort)6112;
    await Send(remote, stream, ciphers, InstanceTransfer.ConnectInstance(host, port, r.Ticket, r.Area, conn.Keyring.Key(r.Ticket)),
        $"0x0027 connect to instance {r.Instance} of area 0x{r.Area:x4} (seed {r.Seed}) at {host}:{port}, ticket 0x{r.Ticket:x8}");
}

// The permanent resistance penalty from Kitava: -30% after the Act 5 encounter and another -30% after the final
// Act 10 Kitava. It is a quest penalty, not a buff/aura: the client keeps it through its quest flags, and the server
// mirrors it from the same flags. (In the 3.29.3b tables the flags are a5q6-CompletedBoss and a10q3-SeenBoss /
// a10q3-UsedRewardBook; a9q5-KilledBoss is the Act 9 boss and does not grant it.)
static int ResistancePenalty(ClientState conn)
{
    bool act5, act10;
    lock (conn.Monsters)
    {
        act5 = conn.QuestFlags.Contains("a5q6-CompletedBoss") || conn.QuestFlags.Contains("SinOnKitavaDefeat");
        act10 = conn.QuestFlags.Contains("a10q3-SeenBoss") || conn.QuestFlags.Contains("a10q3-UsedRewardBook");
    }
    return (act5 ? -30 : 0) + (act10 ? -30 : 0);
}

// The player's final resistance for one damage type: the passive/gear modifier plus the Kitava quest penalty.
static int Resistance(ClientState conn, DamageType type)
{
    int modifier = type switch
    {
        DamageType.Fire => conn.Pools.FireResistance,
        DamageType.Cold => conn.Pools.ColdResistance,
        DamageType.Lightning => conn.Pools.LightningResistance,
        DamageType.Chaos => conn.Pools.ChaosResistance,
        _ => 0,
    };
    return type == DamageType.Physical ? modifier : modifier + ResistancePenalty(conn);
}

// The per-type maximum from Character.ot's 75 plus the passive/gear maximum-resistance modifiers, hard-capped at 90.
static int MaxResistance(ClientState conn, DamageType type) => type switch
{
    DamageType.Fire => conn.Pools.FireMaxResistance,
    DamageType.Cold => conn.Pools.ColdMaxResistance,
    DamageType.Lightning => conn.Pools.LightningMaxResistance,
    DamageType.Chaos => conn.Pools.ChaosMaxResistance,
    _ => DamageModel.DefaultMaxResistance,
};

// Spell Suppression: chance in 0..100 with the conditional passives that hold now (full life, moving, an enemy hit in the
// last 4 s: PoB's "recently"), the damage reduction on a successful suppress (40% by default) and its lucky or unlucky
// roll (PoB's suppressRolls).
static (int Chance, int Effect, bool Lucky, bool Unlucky) Suppression(ClientState conn)
{
    double now = conn.Clock.Elapsed.TotalSeconds;
    bool fullLife, hitRecently;
    lock (conn.Monsters)
    {
        fullLife = conn.PlayerLife > 0 && conn.PlayerLife >= conn.PlayerMaxLife;
        hitRecently = now - conn.LastEnemyHitAt <= RecentlySeconds;
    }
    return (CharacterStats.SuppressionChance(conn.Pools, fullLife, moving: now < conn.Walk.ArrivesAt, hitRecently),
        Math.Max(0, conn.Pools.SpellSuppressionEffect),
        conn.Pools.SpellSuppressionLucky, conn.Pools.SpellSuppressionUnlucky);
}

static DamageType? ParseDamageType(string text) => text.ToLowerInvariant() switch
{
    "fire" => DamageType.Fire,
    "cold" => DamageType.Cold,
    "lightning" or "light" => DamageType.Lightning,
    "chaos" => DamageType.Chaos,
    "physical" or "phys" => DamageType.Physical,
    _ => null,
};

// The chat commands of one connection (ChatCommands.cs).
static ChatCommands Commands(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    return new ChatCommands
    {
        Send = (bytes, log) => Send(remote, stream, ciphers, bytes, log),
        // S→C 0x17 chat lines in the player's chat panel, one per line of the text (an empty line as a space).
        Reply = async text =>
        {
            foreach (string line in text.Split('\n').Select(l => l.Length == 0 ? " " : l))
                await Send(remote, stream, ciphers, InstanceChat.ChatLine(line), $"0x0017 chat line \"{line}\"");
        },
    }
    .Add("raw", "/raw <hex> (send one S→C message)", async args =>
    {
        byte[] m = ChatCommands.ParseHex(args);
        await Send(remote, stream, ciphers, m, $"0x{m[0] << 8 | m[1]:x4} raw ({m.Length} bytes)");
    })
    // Survivors (Agents/docs/survivors.md): the open pick's choices, and the run's state.
    .Add("1", "/1 (Survivors: take the first upgrade offered)", _ => ChooseSurvivorsPick(remote, stream, ciphers, conn, 1))
    .Add("2", "/2 (Survivors: take the second upgrade offered)", _ => ChooseSurvivorsPick(remote, stream, ciphers, conn, 2))
    .Add("3", "/3 (Survivors: take the third upgrade offered)", _ => ChooseSurvivorsPick(remote, stream, ciphers, conn, 3))
    .Add("pick", "/pick <1 to 3> (Survivors: take that upgrade)", args => ChooseSurvivorsPick(remote, stream, ciphers, conn, int.Parse(args[0])))
    .Add("run", "/run (Survivors: the run's time, kills and upgrades)", async _ =>
    {
        if (conn.Run is not { } run) { await conn.Commands!.Reply("Survivors: there is no run here."); return; }
        await conn.Commands!.Reply($"Survivors: {run.State} at {Survivors.Time(run.Elapsed)}, {run.Kills} kills, level {run.CharacterLevel}. " +
            $"Upgrades: {run.Describe()}{(run.Offer is null ? "" : " (a pick is open: /1, /2 or /3)")}");
    })
    .Add("set_speed", "/set_speed <percent> (100 = normal)", async args =>
    {
        int percent = int.Parse(args[0]);
        if (percent is < 10 or > 1000) throw new ArgumentException("10 to 1000");
        int totalPct = conn.Pools.MovementVelocityPct + percent - 100;
        conn.Walk.SetSpeed(WalkSpeed * (100 + totalPct) / 100, conn.Clock.Elapsed.TotalSeconds);
        lock (conn.Monsters) conn.PlayerStats[StatKeys.MovementVelocityPct] = totalPct;
        await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, [(StatKeys.MovementVelocityPct, totalPct)]),
            $"0x01b3 stats: movement_velocity_+% (key {StatKeys.MovementVelocityPct}) = {totalPct} ({conn.Pools.MovementVelocityPct} passive + {percent - 100} dev)");
    })
    .Add("set_stat", "/set_stat <stat name or client key> <value> (e.g. attack_speed_+% 100)", async args =>
    {
        uint stat = Stats.Key(args[0]) ?? throw new ArgumentException($"unknown stat {args[0]}");
        int value = int.Parse(args[1]);
        lock (conn.Monsters) conn.PlayerStats[stat] = value;
        await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, PlayerStatsWithCleared(conn)),
            $"0x01b3 stats: {Stats.Name(stat)} (key 0x{stat:x}) = {value}, and the attack's final stats");
        // Skills compute with their own stats (the attack and the character panel read those), so send them again.
        await SendSkills(remote, stream, ciphers, conn);
    })
    .Add("spawn_mob", "/spawn_mob <MonsterVarieties row, Id or name>[@level] [friendly] [normal|magic|rare|unique] [name]", async args =>
    {
        // "friendly": on the player's team (Positioned team 0); monsters are hostile (team 1) otherwise.
        bool friendly = args.Any(a => a.Equals("friendly", StringComparison.OrdinalIgnoreCase));
        args = [.. args.Where(a => !a.Equals("friendly", StringComparison.OrdinalIgnoreCase))];
        if (args.Length == 0) throw new ArgumentException("which monster");
        if (Monsters.Error is { } error) { await conn.Commands!.Reply(error); return; }
        // Words up to a rarity keyword name the monster; words after it are a custom name. Only the four names count:
        // Enum.TryParse would also accept "100" as a rarity number and send a byte the client cannot read (Buffer Underflow).
        int r = Array.FindIndex(args, a => a.ToLowerInvariant() is "normal" or "magic" or "rare" or "unique");
        var rarity = r < 0 ? MonsterRarity.Normal : Enum.Parse<MonsterRarity>(args[r], ignoreCase: true);
        string? name = r < 0 || r == args.Length - 1 ? null : string.Join(' ', args[(r + 1)..]);
        if (r == 0) throw new ArgumentException("which monster");
        string query = string.Join(' ', r < 0 ? args : args[..r]);
        byte level = 1;
        if (query.LastIndexOf('@') is int at and > 0)
        {
            level = byte.Parse(query[(at + 1)..]);
            if (level is < 1 or > 100) throw new ArgumentException("level 1 to 100");
            query = query[..at];
        }
        else if (r < 0 && args.Length > 1 && int.TryParse(args[^1], out int bare) && bare is >= 1 and <= 100)
        {
            // A bare number after the monster is the level most people mean (the syntax itself wants @).
            level = (byte)bare;
            query = query[..query.LastIndexOf(' ')];
        }
        if (Monsters.Find(query) is not { } variety) { await conn.Commands!.Reply($"No monster matches \"{query}\"."); return; }
        if (!variety.Supported)
        {
            await conn.Commands!.Reply($"{variety} is not supported yet: its type has components " +
                $"{string.Join(", ", variety.ComponentsOrBase.Except(MonsterObject.BaseComponents))} that are not serialized.");
            return;
        }
        var (x, y) = conn.SpawnNear(conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds));
        await SpawnMonster(remote, stream, ciphers, conn, variety, level, rarity, x, y, name, friendly);
    })
    .Add("item", "/item <BaseItemTypes row, Id or name> [count] [magic|rare] (into the inventory, e.g. /item Small Life Flask 2, /item Iron Ring rare)", async args =>
    {
        if (args.Length == 0) throw new ArgumentException("which item?");
        ItemRarity rolled = ItemRarity.Normal;
        if (args.Length > 1 && args[^1].ToLowerInvariant() is "magic" or "rare") { rolled = Enum.Parse<ItemRarity>(args[^1], true); args = args[..^1]; }
        int count = 1, requested = 1;
        if (args.Length > 1 && int.TryParse(args[^1], out int n)) { requested = n; count = Math.Clamp(n, 1, 60); args = args[..^1]; }
        if (Items.Error is { } error) { await conn.Commands!.Reply(error); return; }
        if (Items.Find(string.Join(' ', args)) is not { } type) { await conn.Commands!.Reply($"No item type \"{string.Join(' ', args)}\""); return; }
        if (!type.Supported)
        {
            await conn.Commands!.Reply($"{type} has components the server cannot write yet: {string.Join(", ", type.Components.Where(c => !Item.Supported.Contains(c)))}");
            return;
        }
        var items = ItemsOf(conn);
        if (type.Stackable)
        {
            // A stackable type: [count] is how many, onto the stacks already there first (up to 20 full stacks).
            int amount = Math.Clamp(requested, 1, type.MaxStack * 20);
            var stacked = items.AddStacked(PlayerInventories.MainInventory, ItemCatalog.Create(type) with { StackSize = (ushort)Math.Min(amount, ushort.MaxValue) }, ShapeOf);
            if (!stacked.Any) { await conn.Commands!.Reply("No room in the inventory"); return; }
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                items.NextRevision(PlayerInventories.MainInventory), stacked.RemovedIds, stacked.AddedItems),
                $"0x00bf inventory 1: /item {type} × {amount - stacked.Left} ({stacked.Changed.Count} stack(s) grown, {stacked.Added.Count} new)");
            if (stacked.Left > 0) await conn.Commands!.Reply($"No room for {stacked.Left} of them");
            return;
        }
        var (sockets, links) = Items.DefaultSockets(type);
        var added = new List<InventoryItem>();
        for (int i = 0; i < count; i++)
        {
            if (items.FreeCell(PlayerInventories.MainInventory, type.Width, type.Height, ShapeOf) is not { } cell) break;
            var created = ItemCatalog.Create(type, sockets, links) with { ItemLevel = (byte)Math.Clamp(conn.Pools.Level, 1, 100) };
            if (rolled != ItemRarity.Normal) created = Rolls.Roll(created, rolled, Random.Shared);
            added.Add(items.Put(PlayerInventories.MainInventory, new InventoryItem(items.NewItemId(), cell.X, cell.Y, created)));
        }
        if (added.Count == 0) { await conn.Commands!.Reply("No room in the inventory"); return; }
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
            items.NextRevision(PlayerInventories.MainInventory), [], added), $"0x00bf inventory 1: /item {type} × {added.Count}");
    })
    .Add("gem", "/gem <name> [level] [quality] (a gem into the inventory at that level and quality, e.g. /gem Fireball 20 20, /gem Ice Nova of Frostbolts, /gem Vaal Fireball 5)", async args =>
    {
        // The trailing numbers: the level, then the quality.
        var numbers = new List<int>();
        while (args.Length > 1 && numbers.Count < 2 && int.TryParse(args[^1], out int n)) { numbers.Insert(0, n); args = args[..^1]; }
        if (args.Length == 0) throw new ArgumentException("which gem?");
        if (Items.Error is { } error) { await conn.Commands!.Reply(error); return; }
        string query = string.Join(' ', args);
        if (Items.FindGem(query) is not var (type, variant) || Items.Gem(type) is not { } gem) { await conn.Commands!.Reply($"No gem \"{query}\""); return; }
        if (!type.Supported)
        {
            await conn.Commands!.Reply($"{variant.Name} has components the server cannot write yet: {string.Join(", ", type.Components.Where(c => !Item.Supported.Contains(c)))}");
            return;
        }
        int level = numbers.Count > 0 ? numbers[0] : 1, quality = numbers.Count > 1 ? numbers[1] : 0;
        // Corruption (one level over the cap, quality up to 23%) is not modelled yet: the uncorrupted limits.
        if (level < 1 || level > gem.MaxLevel) { await conn.Commands!.Reply($"{variant.Name} has levels 1 to {gem.MaxLevel}"); return; }
        if (quality is < 0 or > 20) { await conn.Commands!.Reply("Quality is 0 to 20"); return; }
        var items = ItemsOf(conn);
        if (items.FreeCell(PlayerInventories.MainInventory, type.Width, type.Height, ShapeOf) is not { } cell) { await conn.Commands!.Reply("No room in the inventory"); return; }
        var created = ItemCatalog.Create(type) with { GemExperience = gem.ExperienceFor(level), GemVariant = variant.Index, Quality = (byte)quality };
        var added = items.Put(PlayerInventories.MainInventory, new InventoryItem(items.NewItemId(), cell.X, cell.Y, created));
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
            items.NextRevision(PlayerInventories.MainInventory), [], [added]),
            $"0x00bf inventory 1: /gem {variant.Name} (variant {variant.Index}) level {level}, {quality}% quality, experience {created.GemExperience}");
    })
    .Add("map_portal", "/map_portal [type 1-4] [created 0|1] [open after ms, -1 never] [model 0-2] (dev: one MultiplexPortal next to you, listed in the map device's 0x8f)", async args =>
    {
        if (args.Length > 3) conn.MapPortalStyle = byte.Parse(args[3]);
        byte type = args.Length > 0 ? byte.Parse(args[0]) : MapPortalObject.MapDevicePortal;
        byte created = args.Length > 1 ? byte.Parse(args[1]) : (byte)1;
        int openMs = args.Length > 2 ? int.Parse(args[2]) : -1;
        var (x, y) = conn.SpawnNear(conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds));
        uint id = conn.NextObjectId++;
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(MapPortalObject.MetadataPath),
            new MapPortalObject(x, y, 0, type, created, Rotation: conn.PortalRotationRadians).Blob()),
            $"0x02b9 create test portal 0x{id:x} type {type}, transitionable {created}, at ({x}, {y})");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
        lock (conn.MapPortals) conn.MapPortals[id] = (x, y, -1);
        if (openMs >= 0) OpenMapPortalLater(remote, stream, ciphers, conn, id, openMs);
        uint deviceId;
        lock (conn.HideoutDoodads) deviceId = conn.HideoutDoodads.Keys.FirstOrDefault();
        if (deviceId != 0) SendMapPortalsLater(remote, stream, ciphers, conn, deviceId, MapDevice.OpenOf(CharacterName(conn))?.Area.Hash16 ?? conn.Layout.Hash, MapPortalListMs);
        await conn.Commands!.Reply($"Portal 0x{id:x}: type {type}, created {created}, " + (openMs >= 0 ? $"opens after {openMs} ms" : "not opened"));
    })
    .Add("cosmetic", "/cosmetic portal <name, number or part of a name> | /cosmetic list [filter] | /cosmetic off (the portal cosmetic: the shop effect replaces the base portal visual)", async args =>
    {
        if (args.Length == 0)
        {
            await conn.Commands!.Reply($"{Cosmetics.Portals.Count} portal effects. " +
                "/cosmetic list <part of a name> lists the ones that have it, /cosmetic portal <number or name> applies one.");
            return;
        }
        string what = args[0].ToLowerInvariant();
        if (what is not ("list" or "portal" or "off")) throw new ArgumentException("list, portal or off");
        if (what == "off")
        {
            conn.CosmeticPortalVariation = 0;
            CharacterSaves.SetPortalCosmetic(CharacterName(conn), 0);
            bool back = await ResendMapPortals(remote, stream, ciphers, conn);
            await conn.Commands!.Reply(back
                ? "Portal cosmetic off: the open portals are back to the base effect."
                : "Portal cosmetic off: the next portal uses the base effect.");
            return;
        }
        string filter = string.Join(' ', args.Skip(1).Where(a => !a.Equals("portal", StringComparison.OrdinalIgnoreCase)));
        if (what == "list")
        {
            var shown = filter.Length == 0 ? Cosmetics.Portals : CosmeticCatalog.Matches(Cosmetics.Portals, filter);
            await conn.Commands!.Reply(shown.Count == 0
                ? Cosmetics.Portals.Count == 0 ? "No portal effects: data/tables is missing." : $"No portal effects match \"{filter}\"."
                : $"{shown.Count} portal effects: " + string.Join(", ", shown.Take(12).Select(c => c.ToString())) + (shown.Count > 12 ? ", ..." : ""));
            return;
        }
        if (filter.Length == 0)
        {
            // Nothing named: the first page of the list, so the numbers that pick one are right there.
            await conn.Commands!.Reply($"{Cosmetics.Portals.Count} portal effects: " + string.Join(", ", Cosmetics.Portals.Take(12)) +
                (Cosmetics.Portals.Count > 12 ? ", ..." : "") + " — use a number, a name or a piece of one.");
            return;
        }
        var matches = CosmeticCatalog.Matches(Cosmetics.Portals, filter);
        if (matches.Count == 0) throw new ArgumentException($"no portal effect matches \"{filter}\" (try /cosmetic list {filter})");
        if (matches.Count > 1)
        {
            // Several contain what was typed: name them with the numbers that pick one, rather than guessing which was
            // meant. The next /cosmetic with one of the numbers (or a longer fragment) applies it.
            await conn.Commands!.Reply($"{matches.Count} portal effects match \"{filter}\": " +
                string.Join(", ", matches.Take(12).Select(c => c.ToString())) + (matches.Count > 12 ? ", ..." : "") +
                ". Add more of the name, or use one of the numbers.");
            return;
        }
        var cosmetic = matches[0];
        // A portal cosmetic is the portal's own variation (+0x26 of its 0x008f entry), not a second object: the client
        // swaps the base portal effect for the shop one.
        conn.CosmeticPortalVariation = (ushort)cosmetic.Index;
        CharacterSaves.SetPortalCosmetic(CharacterName(conn), conn.CosmeticPortalVariation);   // survives a relog
        bool applied = await ResendMapPortals(remote, stream, ciphers, conn);
        await conn.Commands!.Reply($"portal: {cosmetic.Name} (variation {cosmetic.Index})" +
            (applied ? ", applied to the open portals" : ", the next portal uses it"));
    })
    .Add("portal_rotation", "/portal_rotation <degrees> (dev: turn the map portals around the vertical axis; rebuilds the ones standing)", async args =>
    {
        if (args.Length == 0) throw new ArgumentException("degrees");
        conn.PortalRotationDegrees = float.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
        int rebuilt = await RebuildMapPortals(remote, stream, ciphers, conn);
        await conn.Commands!.Reply(rebuilt == 0
            ? $"Map portals will be created at {conn.PortalRotationDegrees:0.#}°."
            : $"{rebuilt} map portal(s) rebuilt at {conn.PortalRotationDegrees:0.#}°.");
    })
    .Add("mod", "/mod [implicit|explicit|enchant|scourge|hidden] <Mods row, Id or part of it> [values…] | /mod clear [section] (the held item; missing values are rolled)", async args =>
    {
        if (args.Length == 0) throw new ArgumentException("which mod?");
        ModSection? section = null;
        if (args.Length > 1 && ParseSection(args[0]) is { } s) { section = s; args = args[1..]; }
        if (args[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            var only = args.Length > 1 ? ParseSection(args[1]) : section;
            await ChangeHeldItem(remote, stream, ciphers, conn, i => i with { Mods = [.. i.Mods.Where(m => only is { } o && m.Section != o)] },
                $"mods cleared{(only is { } o ? $" ({o})" : "")}");
            return;
        }
        if (Items.Mods.Find(args[0]) is not { } mod) { await conn.Commands!.Reply($"No mod \"{args[0]}\" in Mods.dat"); return; }
        var values = args[1..].Select(int.Parse).ToList();
        var added = mod.With(section ?? mod.DefaultSection, values, Random.Shared);
        await ChangeHeldItem(remote, stream, ciphers, conn, i => i with { Mods = [.. i.Mods, added] }, $"+ {Items.Mods.Describe(added)}");
    })
    .Add("roll", "/roll <magic|rare> [item level] (new affixes and name for the held item)", async args =>
    {
        if (args.Length == 0 || !Enum.TryParse<ItemRarity>(args[0], true, out var rarity) || rarity is not (ItemRarity.Magic or ItemRarity.Rare))
            throw new ArgumentException("magic or rare");
        byte? level = args.Length > 1 ? byte.Parse(args[1]) : null;
        await ChangeHeldItem(remote, stream, ciphers, conn, i => Rolls.Roll(level is byte l ? i with { ItemLevel = l } : i, rarity, Random.Shared),
            $"rolled {rarity}");
    })
    .Add("mods", "/mods (the held item's rarity and mods)", async args =>
    {
        if (ItemsOf(conn).Items(PlayerInventories.Cursor) is not [var held, ..]) { await conn.Commands!.Reply("Hold an item first"); return; }
        await conn.Commands!.Reply($"{(held.Item.NameWords.Count > 0 ? Rolls.NameOf(held.Item.NameWords) + ", " : "")}" +
            $"{Items.ByHash(held.Item.TypeHash)?.Name} ilvl {held.Item.ItemLevel}, {held.Item.Rarity}" +
            $"{(held.Item.Identified ? "" : ", unidentified")}: " +
            (held.Item.Mods.Count == 0 ? "no mods" : string.Join("; ", held.Item.Mods.Select(Items.Mods.Describe))));
    })
    .Add("rarity", "/rarity <normal|magic|rare|unique> [unid] (the held item)", async args =>
    {
        if (args.Length == 0 || !Enum.TryParse<ItemRarity>(args[0], true, out var rarity)) throw new ArgumentException("normal, magic, rare or unique");
        bool identified = !args.Skip(1).Any(a => a.StartsWith("unid", StringComparison.OrdinalIgnoreCase));
        await ChangeHeldItem(remote, stream, ciphers, conn, i => i with { Rarity = rarity, Identified = identified },
            $"{rarity}{(identified ? "" : ", unidentified")}");
    })
    .Add("set_level", "/set_level <1 to 100> (saved to the account; the level's experience and stats are sent at once)", async args =>
    {
        if (args.Length == 0 || !int.TryParse(args[0], out int level) || level is < 1 or > 100) throw new ArgumentException("level 1 to 100");
        var r = await InstanceControl.CallAsync(new ControlRequest { Op = "set_level", Ticket = conn.Ticket, Level = level });
        if (!r.Ok) { await conn.Commands!.Reply($"Level not set: {r.Error}"); return; }
        await SetLevel(remote, stream, ciphers, conn, level);
    })
    .Add("add_xp", "/add_xp <amount> (experience as from a kill: levels up, saved)", async args =>
    {
        if (args.Length == 0 || !uint.TryParse(args[0], out uint xp) || xp == 0) throw new ArgumentException("an amount of experience");
        await GainExperience(remote, stream, ciphers, conn, xp, "/add_xp");
    })
    .Add("passives", "/passives (passive skill points and the allocated passives)", async _ =>
    {
        var (points, refundPoints, nodes, _) = PassiveState(conn);
        int unspent = points + Passives.PointsGranted(nodes) - nodes.Count;
        await conn.Commands!.Reply($"Level {conn.Pools.Level}, {conn.Experience} experience: {points} passive skill point(s), {unspent} unspent, {refundPoints} refund point(s).\n" +
            string.Join("\n", nodes.Select(n => $"{n} {Passives.Find(n)?.Name}").DefaultIfEmpty("No passives allocated.")));
    })
    .Add("gold", "/gold <amount> (dev: add gold, or take it with a negative amount; the account's, saved)", async args =>
    {
        if (args.Length == 0 || !long.TryParse(args[0], out long delta)) throw new ArgumentException("an amount of gold (negative takes gold)");
        uint gold = PersonalStashOf(conn).AddGold(delta);
        await Send(remote, stream, ciphers, InstanceEntities.Gold(gold), $"0x02a9 gold: {gold} (/gold {delta})");
        await conn.Commands!.Reply($"Gold: {gold}");
    })
    .Add("add_passive_points", "/add_passive_points <n> (dev: add n extra passive skill points, saved)", async args =>
    {
        if (args.Length == 0 || !int.TryParse(args[0], out int add) || add <= 0) throw new ArgumentException("a positive number");
        ushort total;
        lock (conn.Monsters)
        {
            conn.ExtraPassivePoints = (ushort)Math.Min(ushort.MaxValue, conn.ExtraPassivePoints + add);
            total = conn.ExtraPassivePoints;
        }
        CharacterSaves.SetExtraPassivePoints(CharacterName(conn), total);
        await SendPassives(remote, stream, ciphers, conn);
        await conn.Commands!.Reply($"{add} extra passive point(s) added; {total} extra total.");
    })
    .Add("add_refund_points", "/add_refund_points <n> (dev: add n refund points, saved)", async args =>
    {
        if (args.Length == 0 || !int.TryParse(args[0], out int add) || add <= 0) throw new ArgumentException("a positive number");
        ushort total;
        lock (conn.Monsters)
        {
            conn.RefundPoints = (ushort)Math.Min(ushort.MaxValue, conn.RefundPoints + add);
            total = conn.RefundPoints;
        }
        CharacterSaves.SetRefundPoints(CharacterName(conn), total);
        await SendPassives(remote, stream, ciphers, conn);
        await conn.Commands!.Reply($"{add} refund point(s) added; {total} total.");
    })
    .Add("add_passive", "/add_passive <PassiveSkillGraphId> (dev: allocate one passive node)", async args =>
    {
        if (args.Length == 0 || !ushort.TryParse(args[0], out ushort node)) throw new ArgumentException("a PassiveSkillGraphId");
        string? error = await ChangePassives(remote, stream, ciphers, conn, [node], allocate: true, "/add_passive");
        await conn.Commands!.Reply(error ?? $"Passive {node} allocated.");
    })
    .Add("reset_passives", "/reset_passives (refunds every passive, saved)", async _ =>
    {
        var (_, _, nodes, _) = PassiveState(conn);
        await ChangePassives(remote, stream, ciphers, conn, nodes, allocate: false, "/reset_passives");
        await conn.Commands!.Reply($"{nodes.Count} passive(s) refunded.");
    })
    .Add("resistances", "/resistances (your uncapped and capped fire/cold/lightning/chaos resistance)", async _ =>
    {
        var p = conn.Pools;
        int penalty = ResistancePenalty(conn);
        int fire = p.FireResistance + penalty, cold = p.ColdResistance + penalty;
        int lightning = p.LightningResistance + penalty, chaos = p.ChaosResistance + penalty;
        await conn.Commands!.Reply(
            $"Kitava penalty {penalty}%. " +
            $"Fire {fire}% (passives {p.FireResistance}%, max {p.FireMaxResistance}%, capped {DamageModel.Capped(fire, p.FireMaxResistance)}%), " +
            $"Cold {cold}% (passives {p.ColdResistance}%, max {p.ColdMaxResistance}%, capped {DamageModel.Capped(cold, p.ColdMaxResistance)}%), " +
            $"Lightning {lightning}% (passives {p.LightningResistance}%, max {p.LightningMaxResistance}%, capped {DamageModel.Capped(lightning, p.LightningMaxResistance)}%), " +
            $"Chaos {chaos}% (passives {p.ChaosResistance}%, max {p.ChaosMaxResistance}%, capped {DamageModel.Capped(chaos, p.ChaosMaxResistance)}%).");
    })
    .Add("hit", "/hit <fire|cold|lightning|chaos|physical> <damage> (dev: damage yourself through resistances or armour)", async args =>
    {
        if (args.Length < 2) throw new ArgumentException("a damage type and an amount");
        if (ParseDamageType(args[0]) is not { } type) throw new ArgumentException("fire, cold, lightning, chaos or physical");
        if (!int.TryParse(args[1], out int amount) || amount <= 0) throw new ArgumentException("a positive amount");
        int resistance = Resistance(conn, type);
        int maxResistance = MaxResistance(conn, type);
        int dealt = type == DamageType.Physical
            ? DamageModel.MitigatePhysical(amount, conn.Pools.Armour)
            : DamageModel.Mitigate(amount, resistance, maxResistance);
        string mitigation = type == DamageType.Physical
            ? $"{DamageModel.ArmourReduction(conn.Pools.Armour, amount)}% armour"
            : $"{DamageModel.Capped(resistance, maxResistance)}% resistance";
        uint life;
        bool wasDead;
        lock (conn.Monsters)
        {
            wasDead = conn.PlayerLife == 0;
            if (!wasDead)
            {
                conn.PlayerLife = (uint)Math.Max(0, (long)conn.PlayerLife - dealt);
                life = conn.PlayerLife;
            }
            else life = 0;
        }
        if (wasDead)
        {
            await conn.Commands!.Reply("You are already dead.");
            return;
        }
        if (life == 0)
            await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, [(StatKeys.HideMiniLifeBar, 1)]),
                "0x01b3 player hide_mini_life_bar = 1 (dev hit)");
        await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, life),
            $"0x01b1 player dev {type} hit for {dealt} ({amount} before {mitigation}), life {life}/{conn.PlayerMaxLife}");
        if (life == 0)
            await Send(remote, stream, ciphers, InstanceProtocol.DeathScreen(DeathScreenFlags(conn)), $"0x0055 death screen ({conn.League ?? "?"} league, dev hit)");
        await conn.Commands!.Reply($"{type}: {amount} → {dealt} after {mitigation}; life {life}/{conn.PlayerMaxLife}.");
    })
    .Add("suppression", "/suppression (Spell Suppression chance, roll and damage reduction)", async _ =>
    {
        var (chance, effect, lucky, unlucky) = Suppression(conn);
        double effective = DamageModel.EffectiveSuppressionChance(chance, lucky, unlucky);
        await conn.Commands!.Reply($"Spell Suppression: {chance}% chance (cap {DamageModel.SpellSuppressionChanceCap}%, effective {effective:0.#}%" +
            $"{(lucky ? ", lucky" : "")}{(unlucky ? ", unlucky" : "")}), {effect}% less damage on a suppressed spell hit.");
    })
    .Add("suppression_luck", "/suppression_luck <lucky|unlucky|off> (dev: force a lucky/unlucky suppression roll)", async args =>
    {
        string mode = args.Length == 0 ? "" : args[0].ToLowerInvariant();
        bool lucky = mode == "lucky", unlucky = mode == "unlucky";
        if (mode is not ("lucky" or "unlucky" or "off" or "")) throw new ArgumentException("lucky, unlucky or off");
        lock (conn.Monsters)
        {
            conn.ForceSpellSuppressionLucky = lucky;
            conn.ForceSpellSuppressionUnlucky = unlucky;
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
        }
        await SendCharacterStats(remote, stream, ciphers, conn, $"suppression luck {mode}");
        await conn.Commands!.Reply($"Spell Suppression roll: {(lucky ? "lucky" : unlucky ? "unlucky" : "normal")}.");
    })
    .Add("accuracy", "/accuracy [monster level] (accuracy rating and attack hit chance; default: your level)", async args =>
    {
        int level = conn.Pools.Level;
        if (args.Length > 0)
        {
            if (!int.TryParse(args[0], out int requested) || requested is < 1 or > Progression.MaxLevel)
                throw new ArgumentException($"monster level 1 to {Progression.MaxLevel}");
            level = requested;
        }
        int evasion = Monsters.BaseEvasion(level) ?? 0;
        int evasiveEvasion = Monsters.BaseEvasiveEvasion(level) ?? 0;
        int chance = DamageModel.AccuracyHitChance(evasion, conn.Pools.Accuracy);
        int evasiveChance = DamageModel.AccuracyHitChance(evasiveEvasion, conn.Pools.Accuracy);
        await conn.Commands!.Reply($"Accuracy {conn.Pools.Accuracy} ({conn.Pools.AccuracyPct}% increased): " +
            $"{chance}% chance to hit a level {level} monster (evasion {evasion}), " +
            $"{evasiveChance}% against evasive monsters (evasion {evasiveEvasion}). Spells always hit.");
    })
    .Add("defences", "/defences [monster level] (armour, evasion and avoidance; default: your level)", async args =>
    {
        int level = conn.Pools.Level;
        if (args.Length > 0)
        {
            if (!int.TryParse(args[0], out int requested) || requested is < 1 or > Progression.MaxLevel)
                throw new ArgumentException($"monster level 1 to {Progression.MaxLevel}");
            level = requested;
        }
        int monsterAccuracy = Monsters.BaseAccuracy(level) ?? 0;
        int monsterDamage = Monsters.BaseDamage(level) ?? 0;
        int hitChance = DamageModel.AccuracyHitChance(conn.Pools.Evasion, monsterAccuracy);
        int reduction = DamageModel.ArmourReduction(conn.Pools.Armour, monsterDamage);
        await conn.Commands!.Reply($"Armour {conn.Pools.Armour} ({conn.Pools.ArmourPct}% increased): " +
            $"{reduction}% physical reduction against a level {level} monster hit ({monsterDamage}), " +
            $"evasion {conn.Pools.Evasion} ({conn.Pools.EvasionPct}% increased): {100 - hitChance}% chance to evade " +
            $"(monster accuracy {monsterAccuracy}, {hitChance}% chance to be hit).");
    })
    .Add("auras", "/auras (active auras, their reservation and the character's efficiency)", async _ =>
    {
        List<AuraCatalog.Aura> auras;
        int reserved;
        int maxMana;
        int efficiency;
        int auraEffect;
        lock (conn.Monsters)
        {
            auras = [.. conn.Auras.Values];
            reserved = AuraCatalog.ReservedMana(conn.Pools, auras);
            maxMana = (int)conn.Pools.Mana;
            efficiency = conn.Pools.ManaReservationEfficiencyPct;
            auraEffect = conn.Pools.AuraEffectPct;
        }
        string list = auras.Count == 0 ? "none" : string.Join(", ", auras.Select(a =>
            $"{a.Name} ({a.ManaFlat} flat, {a.ManaPercent / 100.0:0.##}%)"));
        await conn.Commands!.Reply($"Auras: {list}. Mana {conn.PlayerMana}/{maxMana}, {reserved} reserved " +
            $"({maxMana - reserved} unreserved); reservation efficiency {efficiency}%, aura effect {auraEffect}%.");
    })
    .Add("spellhit", "/spellhit <fire|cold|lightning|chaos|physical> <damage> (dev: spell hit through resistance and Spell Suppression)", async args =>
    {
        if (args.Length < 2) throw new ArgumentException("a damage type and an amount");
        if (ParseDamageType(args[0]) is not { } type) throw new ArgumentException("fire, cold, lightning, chaos or physical");
        if (!int.TryParse(args[1], out int amount) || amount <= 0) throw new ArgumentException("a positive amount");
        int resistance = Resistance(conn, type), maxResistance = MaxResistance(conn, type);
        int dealt = type == DamageType.Physical
            ? DamageModel.MitigatePhysical(amount, conn.Pools.Armour)
            : DamageModel.Mitigate(amount, resistance, maxResistance);
        string mitigation = type == DamageType.Physical
            ? $"{DamageModel.ArmourReduction(conn.Pools.Armour, amount)}% armour"
            : $"{DamageModel.Capped(resistance, maxResistance)}% resistance";
        var (chance, effect, lucky, unlucky) = Suppression(conn);
        bool suppressed = DamageModel.Suppresses(chance, Random.Shared.Next(100), lucky, unlucky);
        if (suppressed) dealt = DamageModel.Suppress(dealt, effect);
        uint life;
        bool wasDead;
        lock (conn.Monsters)
        {
            wasDead = conn.PlayerLife == 0;
            if (!wasDead)
            {
                conn.PlayerLife = (uint)Math.Max(0, (long)conn.PlayerLife - dealt);
                life = conn.PlayerLife;
            }
            else life = 0;
        }
        if (wasDead)
        {
            await conn.Commands!.Reply("You are already dead.");
            return;
        }
        if (life == 0)
            await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, [(StatKeys.HideMiniLifeBar, 1)]),
                "0x01b3 player hide_mini_life_bar = 1 (dev spell hit)");
        await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, life),
            $"0x01b1 player dev spell {type} hit for {dealt} ({amount} before {mitigation}, suppression {(suppressed ? "yes" : "no")}), life {life}/{conn.PlayerMaxLife}");
        if (life == 0)
            await Send(remote, stream, ciphers, InstanceProtocol.DeathScreen(DeathScreenFlags(conn)), $"0x0055 death screen ({conn.League ?? "?"} league, dev spell hit)");
        double effective = DamageModel.EffectiveSuppressionChance(chance, lucky, unlucky);
        await conn.Commands!.Reply($"{type} spell hit: {amount} → {dealt} after {mitigation}; " +
            $"suppression {(suppressed ? "rolled" : "not rolled")} ({chance}% chance, effective {effective:0.#}%" +
            $"{(lucky ? ", lucky" : "")}{(unlucky ? ", unlucky" : "")}); life {life}/{conn.PlayerMaxLife}.");
    })
    .Add("crit", "/crit (attack and spell critical strike chance and multiplier)", async _ =>
    {
        var p = conn.Pools;
        await conn.Commands!.Reply(
            $"Attack crit {p.AttackCriticalStrikeChance / 100.0:0.##}% ({p.CriticalStrikeChancePct}% increased), " +
            $"multiplier {p.CriticalStrikeMultiplier}%. Spell crit {p.SpellCriticalStrikeChance / 100.0:0.##}% " +
            $"({p.SpellCriticalStrikeChancePct}% increased), multiplier {p.SpellCriticalStrikeMultiplier}%.");
    })
    .Add("block", "/block (attack and spell block chance and caps)", async _ =>
    {
        var p = conn.Pools;
        await conn.Commands!.Reply($"Block {p.BlockChance}% (max {p.BlockChanceMax}%, effective {DamageModel.EffectiveBlockChance(p.BlockChance, p.AttackBlockLucky):0.#}%), " +
            $"spell block {p.SpellBlockChance}% (max {p.SpellBlockChanceMax}%, effective {DamageModel.EffectiveBlockChance(p.SpellBlockChance, p.SpellBlockLucky):0.#}%)" +
            $"{(p.AttackBlockLucky ? "; attack lucky" : "")}{(p.SpellBlockLucky ? "; spell lucky" : "")}.");
    })
    .Add("block_luck", "/block_luck <attack|spell|both|off> (dev: force lucky block)", async args =>
    {
        string mode = args.Length == 0 ? "" : args[0].ToLowerInvariant();
        bool attack = mode is "attack" or "both", spell = mode is "spell" or "both";
        if (mode is not ("attack" or "spell" or "both" or "off" or "")) throw new ArgumentException("attack, spell, both or off");
        lock (conn.Monsters)
        {
            conn.ForceAttackBlockLucky = attack;
            conn.ForceSpellBlockLucky = spell;
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
        }
        await SendCharacterStats(remote, stream, ciphers, conn, $"lucky block {mode}");
        await conn.Commands!.Reply($"Lucky block: attack {(attack ? "on" : "off")}, spell {(spell ? "on" : "off")}.");
    })
    .Add("blockhit", "/blockhit <attack|spell> <fire|cold|lightning|chaos|physical> <damage> (dev: hit through block and resistance)", async args =>
    {
        if (args.Length < 3) throw new ArgumentException("attack|spell, a damage type and an amount");
        bool spell = args[0].Equals("spell", StringComparison.OrdinalIgnoreCase);
        if (!spell && !args[0].Equals("attack", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("attack or spell");
        if (ParseDamageType(args[1]) is not { } type) throw new ArgumentException("fire, cold, lightning, chaos or physical");
        if (!int.TryParse(args[2], out int amount) || amount <= 0) throw new ArgumentException("a positive amount");
        int resistance = Resistance(conn, type), maxResistance = MaxResistance(conn, type);
        int chance = spell ? conn.Pools.SpellBlockChance : conn.Pools.BlockChance;
        bool lucky = spell ? conn.Pools.SpellBlockLucky : conn.Pools.AttackBlockLucky;
        bool blocked = DamageModel.Blocks(chance, Random.Shared.NextDouble() * 100, lucky);
        int dealt = blocked ? 0 : type == DamageType.Physical
            ? DamageModel.MitigatePhysical(amount, conn.Pools.Armour)
            : DamageModel.Mitigate(amount, resistance, maxResistance);
        string mitigation = type == DamageType.Physical
            ? $"{DamageModel.ArmourReduction(conn.Pools.Armour, amount)}% armour"
            : $"{DamageModel.Capped(resistance, maxResistance)}% resistance";
        uint life, mana, manaBefore;
        bool wasDead;
        lock (conn.Monsters)
        {
            wasDead = conn.PlayerLife == 0;
            manaBefore = conn.PlayerMana;
            if (!wasDead)
            {
                int gainedLife = blocked ? conn.Pools.LifeGainedOnBlock : 0;
                int gainedMana = blocked ? conn.Pools.ManaGainedOnBlock : 0;
                conn.PlayerLife = (uint)Math.Min(conn.PlayerMaxLife,
                    Math.Max(0, (long)conn.PlayerLife - dealt + gainedLife));
                conn.PlayerMana = (uint)Math.Min((uint)UnreservedMana(conn), (long)conn.PlayerMana + gainedMana);
                life = conn.PlayerLife;
                mana = conn.PlayerMana;
            }
            else { life = 0; mana = conn.PlayerMana; }
        }
        if (wasDead)
        {
            await conn.Commands!.Reply("You are already dead.");
            return;
        }
        if (life == 0)
            await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, [(StatKeys.HideMiniLifeBar, 1)]),
                "0x01b3 player hide_mini_life_bar = 1 (dev block hit)");
        await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, life),
            $"0x01b1 player dev {(spell ? "spell" : "attack")} hit for {dealt} ({(blocked ? "blocked" : mitigation)}), life {life}/{conn.PlayerMaxLife}");
        if (mana != manaBefore)
            await SendPlayerMana(remote, stream, ciphers, conn,
                $"0x01b1 player mana {mana} ({(blocked ? $"+{conn.Pools.ManaGainedOnBlock} on block" : "no block gain")})");
        if (life == 0)
            await Send(remote, stream, ciphers, InstanceProtocol.DeathScreen(DeathScreenFlags(conn)), $"0x0055 death screen ({conn.League ?? "?"} league, dev block hit)");
        await conn.Commands!.Reply($"{(spell ? "Spell" : "Attack")} hit: {amount} → {dealt}; {(blocked ? "blocked" : "not blocked")} ({chance}% chance); life {life}/{conn.PlayerMaxLife}.");
    })
    .Add("god", "/god (toggles: 1000% increased attack and cast speed, 100% of life and 10 000 mana regenerated per second, +10 000 life, +1000 attributes)", async _ =>
    {
        bool on = !conn.God;
        await SetGod(remote, stream, ciphers, conn, on);
        await conn.Commands!.Reply(on ? "God mode on." : "God mode off.");
    })
    .Add("heal", "/heal (full life; also revives: 0x1b1 from 0 runs the client's revive)", async _ =>
    {
        lock (conn.Monsters) (conn.PlayerLife, conn.PlayerMana) = (conn.PlayerMaxLife, (uint)UnreservedMana(conn));
        await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerMaxLife),
            $"0x01b1 player healed to {conn.PlayerMaxLife}");
        await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana {conn.PlayerMana}");
        await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, [(StatKeys.HideMiniLifeBar, 0)]),
            "0x01b3 player hide_mini_life_bar = 0");
    })
    .Add("death_screen", "/death_screen [hex flags] (show the death screen; default: this league's; 2 hides it)", async args =>
    {
        byte flags = args.Length > 0 ? Convert.ToByte(args[0].Replace("0x", ""), 16) : DeathScreenFlags(conn);
        await Send(remote, stream, ciphers, InstanceProtocol.DeathScreen(flags), $"0x0055 death screen flags 0x{flags:x2}");
    })
    .Add("transition", "/transition [pos [here | x y | +dx +dy] | rot <degrees> | z <z | none> | model <path | none> | index <n>] " +
        "(dev: show or change the first area transition; it is re-created)", async args =>
    {
        var (px, py) = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        if (conn.Layout.AreaTransitions.Length == 0) { await conn.Commands!.Reply("This area has no area transition."); return; }
        var (id, at) = conn.Layout.AreaTransitions[0];
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string? what = args.Length > 0 ? args[0] : null;
        string[] rest = args.Skip(1).ToArray();
        AreaTransitionObject? changed = what switch
        {
            "pos" when rest.Length > 0 => rest[0] == "here" ? at with { GridX = px, GridY = py }
                : rest[0].StartsWith('+') || rest[0].StartsWith('-')
                    ? at with { GridX = at.GridX + int.Parse(rest[0]), GridY = at.GridY + int.Parse(rest[1]) }
                    : at with { GridX = int.Parse(rest[0]), GridY = int.Parse(rest[1]) },
            "rot" => at with { Rotation = float.Parse(rest[0], inv) * MathF.PI / 180f },
            "z" => at with { Height = rest[0] == "none" ? null : float.Parse(rest[0], inv) },
            "model" => at with { Model = rest[0] == "none" ? null : string.Join(' ', rest) },
            "index" => at with { Index = int.Parse(rest[0]) },
            _ => null,
        };
        if (changed is not null)
        {
            conn.ReplaceAreaTransition(id, changed);
            at = changed;
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove area transition 0x{id:x}");
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(at.Type), at.Blob()),
                $"0x02b9 create area transition {at}");
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), $"0x01a9 area transition 0x{id:x} enters wake zone");
        }
        await conn.Commands!.Reply($"Area transition 0x{id:x} at ({at.GridX}, {at.GridY}), {at.Rotation * 180 / MathF.PI:0.#} degrees, " +
            $"height {at.Height?.ToString(inv) ?? "terrain"}, model {at.Model ?? "none"}; you at ({px}, {py}).");
    })
    .Add("quest_flag", "/quest_flag [<QuestFlags Id> [off]] (dev: list, set or clear quest flags, e.g. /quest_flag a1q1-KilledHillock)", async args =>
    {
        if (args.Length < 1) { await conn.Commands!.Reply(string.Join(", ", conn.QuestFlags.DefaultIfEmpty("no quest flags"))); return; }
        bool off = args.Length > 1 && args[1].Equals("off", StringComparison.OrdinalIgnoreCase);
        await SetQuestFlags(remote, stream, ciphers, conn, [args[0]], on: !off);
        await conn.Commands!.Reply($"{args[0]} {(off ? "cleared" : "set")}");
    })
    .Add("goto", "/goto <area id or name> (dev: change area as if through an area transition, e.g. /goto 1_1_3)", async args =>
    {
        string target = string.Join(' ', args);
        if (AreaTransitionCatalog.HashOf(target) is not { } hash) { await conn.Commands!.Reply($"Unknown area \"{target}\" (needs data/area-transitions.json)."); return; }
        await ChangeArea(remote, stream, ciphers, conn, new PendingTransfer(hash, 0, false), $"/goto {target}");
    })
    .Add("league_flag", "/league_flag [<id> [off]] (dev: list, set or clear league quest flags by the client's id, e.g. /league_flag 350)", async args =>
    {
        if (args.Length < 1 || !int.TryParse(args[0], out int flag))
        {
            await Send(remote, stream, ciphers, InstanceChat.ChatLine($"League quest flags: {string.Join(", ", conn.LeagueQuestFlags.Order())}"), "0x0017 chat line: league quest flags");
            return;
        }
        bool off = args.Length > 1 && args[1].Equals("off", StringComparison.OrdinalIgnoreCase);
        if (off) conn.LeagueQuestFlags.Remove(flag); else conn.LeagueQuestFlags.Add(flag);
        await Send(remote, stream, ciphers, InstanceEntities.PlayerLeagueQuestFlagsUpdate(PlayerId, Quests.League.Bits(conn.LeagueQuestFlags)),
            $"0x02ba player league quest flags: {string.Join(", ", conn.LeagueQuestFlags.Order())}");
        await Send(remote, stream, ciphers, InstanceChat.ChatLine($"League quest flag {flag} {(off ? "cleared" : "set")}."), $"0x0017 chat line: league quest flag {flag}");
    })
    .Add("hideout_keys", "/hideout_keys <a> <b> (dev: the Player component's two Hideouts.dat keys, HASH16 hex or a hideout area id)", async args =>
    {
        ushort Key(string s) => HideoutCatalog.Hideouts.FirstOrDefault(h => h.AreaId.Equals(s, StringComparison.OrdinalIgnoreCase)) is { } h ? h.Hash16
            : ushort.TryParse(s.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out var k) ? k : (ushort)0;
        ushort a = args.Length > 0 ? Key(args[0]) : (ushort)0, b = args.Length > 1 ? Key(args[1]) : (ushort)0;
        await Send(remote, stream, ciphers, InstanceEntities.PlayerHideoutsUpdate(PlayerId, a, b), $"0x02ba player hideout keys 0x{a:x4} 0x{b:x4}");
        await Send(remote, stream, ciphers, InstanceChat.ChatLine($"Hideout keys 0x{a:x4} 0x{b:x4}."), "0x0017 chat line: hideout keys");
    })
    .Add("waypoints", "/waypoints [all | reset] (list the activated waypoints; dev: activate all and visit every pinned area, or keep only the towns')", async args =>
    {
        var entries = WaypointCatalog.Areas.Entries;
        if (entries.Count == 0) { await conn.Commands!.Reply("No waypoints (needs data/tables/WorldAreas.json)."); return; }
        string what = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (what is "all" or "reset")
        {
            string[] all;
            string[] visited;
            lock (conn.Waypoints)
            {
                conn.Waypoints.Clear();
                conn.Waypoints.UnionWith(entries.Where(e => what == "all" || e.IsTown).Select(e => e.Id));
                all = [.. conn.Waypoints];
                // "all" also visits every area with a World panel pin, so every pin and line shows.
                if (what == "all") conn.Visited.UnionWith(WaypointCatalog.Visits.PinnedAreas);
                else { conn.Visited.Clear(); conn.Visited.Add(conn.Layout.Id); }
                visited = [.. conn.Visited];
            }
            CharacterSaves.SetWaypoints(CharacterName(conn), all);
            CharacterSaves.SetVisited(CharacterName(conn), visited);
            await SendWaypointList(remote, stream, ciphers, conn, $"/waypoints {what}");
        }
        string list;
        lock (conn.Waypoints) list = string.Join(", ", entries.Where(e => conn.Waypoints.Contains(e.Id)).Select(e => e.Name));
        await conn.Commands!.Reply(list.Length == 0 ? "No activated waypoints." : $"Activated waypoints: {list}.");
    })
    .Add("npc", "/npc <NPCs row, Id or name> [rotation°] (spawns an NPC next to you)", async args =>
    {
        if (args.Length == 0) throw new ArgumentException("which NPC");
        if (Npcs.Value.Error is { } error) { await conn.Commands!.Reply(error); return; }
        float? deg = args.Length > 1 && float.TryParse(args[^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float d) ? d : null;
        string query = string.Join(' ', deg is null ? args : args[..^1]);
        if (Npcs.Value.Find(query) is not { } npc) { await conn.Commands!.Reply($"No NPC {query}."); return; }
        if (!npc.Supported) { await conn.Commands!.Reply($"{npc} has components the server cannot write: {string.Join(", ", npc.Components ?? [])}."); return; }
        var (x, y) = NextToPlayer(conn);
        await SpawnNpc(remote, stream, ciphers, conn, npc, x, y, (deg ?? 0) * MathF.PI / 180);
    })
    // Turns an NPC: it is created again with the new facing (0x2bb, then 0x2b9).
    .Add("npc_face", "/npc_face <name> <degrees> (turn an NPC here)", async args =>
    {
        if (args.Length < 2) throw new ArgumentException("name and degrees");
        float deg = float.Parse(args[^1], System.Globalization.CultureInfo.InvariantCulture);
        string name = string.Join(' ', args[..^1]);
        KeyValuePair<uint, SpawnedNpc> found;
        lock (conn.Npcs) found = conn.Npcs.FirstOrDefault(n => n.Value.Npc.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (found.Value is not { } npc) { await conn.Commands!.Reply($"No NPC {name} here."); return; }
        lock (conn.Npcs) conn.Npcs.Remove(found.Key);
        await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(found.Key), $"0x02bb remove npc 0x{found.Key:x} {npc.Npc.Name}");
        float rad = deg * MathF.PI / 180;
        await SpawnNpc(remote, stream, ciphers, conn, npc.Npc, npc.X, npc.Y, rad);
        await conn.Commands!.Reply($"{npc.Npc.Name} at ({npc.X}, {npc.Y}) faces {deg}° ({rad:0.###} rad).");
    })
    // Probe: S→C 0x7b (open a UI) with any kind and its four u32s (16-waypoints.md §3; kind 0 = World panel).
    .Add("ui", "/ui <kind hex> [a] [b] [c] [d] (S→C 0x7b open a UI)", async args =>
    {
        if (args.Length == 0) throw new ArgumentException("which kind");
        byte kind = Convert.ToByte(args[0].Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16);
        uint U(int i) => args.Length > i ? Convert.ToUInt32(args[i], args[i].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10) : 0;
        await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(kind, U(1), U(2), U(3), U(4)), $"0x007b open UI kind 0x{kind:x2} ({U(1)}, {U(2)}, {U(3)}, {U(4)})");
    })
    .Add("npcs", "/npcs (list NPCs here)", async _ =>
    {
        string list;
        lock (conn.Npcs) list = string.Join(", ", conn.Npcs.Select(n => $"0x{n.Key:x} {n.Value.Npc.Name} ({n.Value.X}, {n.Value.Y})"));
        await conn.Commands!.Reply(list.Length == 0 ? "No NPCs." : list);
    })
    .Add("pos", "/pos (your cell)", async _ =>
    {
        var (x, y) = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        Console.WriteLine($"[gateway] {remote} /pos ({x}, {y}) in {conn.Layout.Id}");
        await conn.Commands!.Reply($"({x}, {y}) in {conn.Layout.Id}");
    })
    .Add("mobs", "/mobs (list spawned monsters)", async _ =>
        await conn.Commands!.Reply(conn.Monsters.Count == 0 ? "No monsters." :
            string.Join(", ", conn.Monsters.Select(m =>
                $"0x{m.Key:x} {m.Value.Label} {m.Value.Walk.Position(conn.Clock.Elapsed.TotalSeconds)}{(m.Value.Aggro ? " (chasing)" : "")}"))))
    .Add("despawn", "/despawn [all | <id>] (default: the last one spawned)", async args =>
    {
        IEnumerable<uint> ids = args.Length == 0 ? conn.Monsters.Keys.TakeLast(1)
            : args[0].Equals("all", StringComparison.OrdinalIgnoreCase) ? [.. conn.Monsters.Keys]
            : [Convert.ToUInt32(args[0], args[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10)];
        foreach (uint id in ids.ToList())
        {
            bool removed;
            lock (conn.Monsters) removed = conn.Monsters.Remove(id);
            if (!removed) { await conn.Commands!.Reply($"No monster 0x{id:x}."); continue; }
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove monster 0x{id:x}");
        }
    })
    // Movement probes: walk (0x1ba, Positioned move with a speed) or teleport (0x1b7) a monster to a few cells from the player.
    .Add("mob_move", "/mob_move [id] [speed] (0x1ba toward you; default the last monster, speed 20)", async args =>
    {
        if (MonsterArg(conn, args, 0) is not { } id) { await conn.Commands!.Reply("No such monster."); return; }
        string? sp = args.FirstOrDefault(a => !a.StartsWith("0x", StringComparison.OrdinalIgnoreCase));
        float speed = sp is null ? 20f : float.Parse(sp, System.Globalization.CultureInfo.InvariantCulture);
        var (x, y) = NextToPlayer(conn);
        await Send(remote, stream, ciphers, InstanceProtocol.MoveTo(id, x, y, speed), $"0x01ba move monster 0x{id:x} to ({x}, {y}) speed {speed}");
        lock (conn.Monsters) conn.Monsters[id].Walk = conn.NewWalk(x, y, conn.Monsters[id].Walk.Speed);
    })
    // Action probe: start the monster's Move action (0x1ab) toward the player, like the echo of a player's click. The skill
    // id is the player's Move (0x2909) unless given (hex); sequence 0xffff (the monster is not client-predicted).
    .Add("mob_walk", "/mob_walk [id] [skill hex] (0x1ab Move toward you)", async args =>
    {
        if (MonsterArg(conn, args, 0) is not { } id) { await conn.Commands!.Reply("No such monster."); return; }
        string? sk = args.FirstOrDefault(a => !a.StartsWith("0x", StringComparison.OrdinalIgnoreCase));
        ushort skill = sk is null ? (ushort)0x2909 : Convert.ToUInt16(sk, 16);
        var (x, y) = NextToPlayer(conn);
        (int X, int Y) from;
        lock (conn.Monsters) from = conn.Monsters[id].Walk.Order(x, y, conn.Clock.Elapsed.TotalSeconds);
        await Send(remote, stream, ciphers, InstanceProtocol.ActionStarted(id, from.X, from.Y, x, y, skill, 0xffff, 0x0408),
            $"0x01ab monster 0x{id:x} action skill 0x{skill:x4} from {from} to ({x}, {y})");
    })
    .Add("mob_tp", "/mob_tp [id] (0x1b7 next to you; default the last monster)", async args =>
    {
        if (MonsterArg(conn, args, 0) is not { } id) { await conn.Commands!.Reply("No such monster."); return; }
        var (x, y) = NextToPlayer(conn);
        await Send(remote, stream, ciphers, InstanceProtocol.Teleport(id, x, y), $"0x01b7 teleport monster 0x{id:x} to ({x}, {y})");
        lock (conn.Monsters) conn.Monsters[id].Walk = conn.NewWalk(x, y, conn.Monsters[id].Walk.Speed);
    })
    .Add("ai", "/ai [on|off] (monsters chase you when you come near)", async args =>
    {
        if (args.Length > 0) conn.AiEnabled = !args[0].Equals("off", StringComparison.OrdinalIgnoreCase);
        await conn.Commands!.Reply($"Monster AI is {(conn.AiEnabled ? "on" : "off")}.");
    });
}

// Monster AI, one tick every 500 ms per connection. A monster within its aggro range (MonsterVarieties MaxAgroRange, in
// cells) of the player starts chasing: it walks (Move action 0x1ab, skill 0x2909; the client paths it) to a cell
// StopRange from the player, re-ordered when that cell has moved by 3+ cells. Beyond twice the range it gives up and stops.
// Positions are the server's estimates (WalkEstimator at MonsterVarieties MovementSpeed cells/s).
static async Task RunMonsterAi(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    const ushort Move = 0x2909;
    try
    {
        while (!conn.Closed)
        {
            await Task.Delay(500);
            if (!conn.AiEnabled || conn.Paused) continue;
            var orders = new List<(uint Id, (int X, int Y) From, (int X, int Y) To, string Why)>();
            var swings = new List<(uint Id, SpawnedMonster M, (int X, int Y) From)>();
            double now = conn.Clock.Elapsed.TotalSeconds;
            var (px, py) = conn.Walk.Position(now);
            lock (conn.Monsters)
            {
                foreach (var (id, m) in conn.Monsters)
                {
                    if (m.Dead) continue;
                    var (mx, my) = m.Walk.Position(now);
                    double d = Math.Sqrt((double)(mx - px) * (mx - px) + (double)(my - py) * (my - py));
                    if (!m.Aggro && d <= m.AggroRange) { m.Aggro = true; m.LastTarget = null; }
                    else if (m.Aggro && d > 2 * m.AggroRange && !m.Hunts)
                    {
                        m.Aggro = false;
                        orders.Add((id, m.Walk.Order(mx, my, now), (mx, my), "gives up"));
                        continue;
                    }
                    if (!m.Aggro || now < m.BusyUntil) continue;
                    // In reach: stop and swing at the player (hostile monsters with an attack, while the player lives).
                    if (m.Hostile && m.Attack is { } a && d <= a.Range && conn.PlayerLife > 0)
                    {
                        m.BusyUntil = now + a.Seconds;
                        m.LastTarget = null;
                        m.Walk.Order(mx, my, now);
                        swings.Add((id, m, (mx, my)));
                        continue;
                    }
                    if (d <= m.StopRange) continue;
                    // The cell StopRange - 1 from the player on the line toward the monster (the player's cell if blocked).
                    double f = (m.StopRange - 1) / d;
                    (int X, int Y) to = ((int)Math.Round(px + (mx - px) * f), (int)Math.Round(py + (my - py) * f));
                    if (conn.Layout.Walkable is { } w && !w(to.X, to.Y)) to = (px, py);
                    if (m.LastTarget is { } last && Math.Abs(last.X - to.X) + Math.Abs(last.Y - to.Y) < 3) continue;
                    m.LastTarget = to;
                    orders.Add((id, m.Walk.Order(to.X, to.Y, now), to, $"chases (distance {d:F0})"));
                }
            }
            foreach (var (id, from, to, why) in orders)
                await Send(remote, stream, ciphers, InstanceProtocol.ActionStarted(id, from.X, from.Y, to.X, to.Y, Move, 0xffff, 0x0408),
                    $"0x01ab monster 0x{id:x} {why}: move from {from} to {to}");
            foreach (var (id, m, from) in swings)
                _ = MonsterSwing(remote, stream, ciphers, conn, id, m, from, (px, py));
        }
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
    {
        // The connection closed while sending.
    }
}

// A monster swings at the player: its skill as an action aimed at the player (0x1ab, flags | 0x80 + target id, sequence
// 0xffff), then at the contact point (half the attack time) the hit lands if the monster still lives and the player is
// still in reach (walking away dodges it): the player's life goes down by the monster's damage (0x1b1 on the player).
// At 0 the client runs the player's death.
// PoE's evasion entropy (wiki: Evasion): shared by every attack against the player, reset after 3.33 s. Call under
// the Monsters lock.
static bool EvadesLocked(ClientState conn, int hitChance, double now)
{
    if (conn.EvasionEntropy < 0 || now - conn.LastEvasionAt > 3.33)
        conn.EvasionEntropy = Random.Shared.Next(100);
    conn.LastEvasionAt = now;
    conn.EvasionEntropy += hitChance;
    if (conn.EvasionEntropy < 100) return true;
    conn.EvasionEntropy -= 100;
    return false;
}

// The Escape menu (C→S 0x1d4, 22-pause.md) pauses the area outside towns and hideouts, where the client does not ask
// anyway: the Clock stops, the monster AI and the player tick skip their steps, and S→C 0x1d3 pauses the client's world.
static async Task SetPaused(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, bool pause)
{
    ushort here = conn.Layout.Hash;
    if (pause && (WaypointCatalog.Areas.Find(here) is { IsTown: true } || HideoutCatalog.Hideouts.Any(h => h.AreaHash16 == here)))
    {
        Console.WriteLine($"[gateway] {remote} <= 0x01d4 pause: in a town or hideout, not paused");
        return;
    }
    if (pause) conn.Clock.Stop(); else conn.Clock.Start();
    await Send(remote, stream, ciphers, InstancePause.State(pause), $"0x01d3 {(pause ? "paused" : "resumed")} (C→S 0x01d4 {(pause ? 1 : 0)})");
}

static async Task WhilePaused(ClientState conn)
{
    while (conn.Paused && !conn.Closed) await Task.Delay(100);
}

static async Task MonsterSwing(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
    SpawnedMonster m, (int X, int Y) from, (int X, int Y) player)
{
    var a = m.Attack!;
    await Send(remote, stream, ciphers,
        InstanceProtocol.ActionStarted(id, from.X, from.Y, player.X, player.Y, a.SkillId, 0xffff, 0x0408, targetObjectId: PlayerId),
        $"0x01ab monster 0x{id:x} ({m.Label}) attacks the player, attack time {a.Seconds:0.00} s");
    await Task.Delay(TimeSpan.FromSeconds(a.Seconds / 2));
    await WhilePaused(conn);   // a swing started just before the pause lands after it
    double now = conn.Clock.Elapsed.TotalSeconds;
    uint life, mana, manaBefore;
    int rawDamage = 0, damage = 0;
    bool blocked;
    lock (conn.Monsters)
    {
        if (m.Dead || conn.PlayerLife == 0 || conn.Closed) return;
        var here = conn.Walk.Position(now);
        if (Dist(here, m.Walk.Position(now)) > a.Range + 2)
        {
            Console.WriteLine($"[gateway] {remote} monster 0x{id:x} missed: the player is {Dist(here, m.Walk.Position(now)):0.0} away");
            return;
        }
        // The monster's accuracy comes from DefaultMonsterStats at its level. Evasion uses PoE's shared entropy
        // counter: every attack adds its hit chance, and reaching 100 consumes 100 and hits (evasion cannot streak
        // indefinitely). A new counter starts after 3.33 s without an attack.
        int monsterAccuracy = Monsters.BaseAccuracy(m.Level) ?? 0;
        int hitChance = DamageModel.AccuracyHitChance(conn.Pools.Evasion, monsterAccuracy);
        if (EvadesLocked(conn, hitChance, now))
        {
            Console.WriteLine($"[gateway] {remote} monster 0x{id:x} ({m.Label}) evaded: accuracy {monsterAccuracy} " +
                $"vs evasion {conn.Pools.Evasion}, {hitChance}% chance to hit, entropy {conn.EvasionEntropy:0}");
            return;
        }
        // Roll each damage type separately, apply its resistance, then armour to the physical part. The attack is
        // currently physical only (MonsterAttack), but the packet path keeps future monster skills correct.
        var rolled = a.Damage.Roll(Random.Shared);
        var mitigated = DamageModel.Mitigate(rolled, type =>
            new DamageModel.ResistanceValue(Resistance(conn, type), MaxResistance(conn, type)));
        rawDamage = (int)Math.Round(rolled[DamageType.Physical].Min);
        damage = 0;
        foreach (var (type, range) in mitigated.Components)
        {
            int value = (int)Math.Round(range.Min);
            damage += type == DamageType.Physical
                ? DamageModel.MitigatePhysical(value, conn.Pools.Armour)
                : value;
        }
        // Survivors: Toughness takes its share off the hit.
        if (conn.Run is { } run) damage = (int)Math.Round(damage * run.DamageTaken);
        blocked = DamageModel.Blocks(conn.Pools.BlockChance, Random.Shared.NextDouble() * 100, conn.Pools.AttackBlockLucky);
        if (blocked) damage = 0;
        manaBefore = conn.PlayerMana;
        int gainedLife = blocked ? conn.Pools.LifeGainedOnBlock : 0;
        int gainedMana = blocked ? conn.Pools.ManaGainedOnBlock : 0;
        conn.PlayerLife = (uint)Math.Min(conn.PlayerMaxLife, Math.Max(0, (long)conn.PlayerLife - damage + gainedLife));
        conn.PlayerMana = (uint)Math.Min((uint)UnreservedMana(conn), (long)conn.PlayerMana + gainedMana);
        life = conn.PlayerLife;
        mana = conn.PlayerMana;
    }
    // At 0 the Life tick stops refreshing the overhead life bar (see Attack): hide it.
    if (life == 0)
        await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, [(StatKeys.HideMiniLifeBar, 1)]),
            "0x01b3 player hide_mini_life_bar = 1");
    await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, life),
        $"0x01b1 player hit by monster 0x{id:x} for {damage}" +
        $"{(rawDamage != damage ? $" ({rawDamage} physical before {DamageModel.ArmourReduction(conn.Pools.Armour, rawDamage)}% armour)" : "")}" +
        $"{(blocked ? " (blocked)" : "")}, life {life}/{conn.PlayerMaxLife}{(life == 0 ? " (dies)" : "")}");
    if (mana != manaBefore)
        await SendPlayerMana(remote, stream, ciphers, conn,
            $"0x01b1 player mana {mana} ({(blocked ? $"+{conn.Pools.ManaGainedOnBlock} on block" : "no block gain")})");
    if (life == 0)
        await Send(remote, stream, ciphers, InstanceProtocol.DeathScreen(DeathScreenFlags(conn)), $"0x0055 death screen ({conn.League ?? "?"} league)");
}

// The death screen's buttons (C→S 0x54). Both hide the screen (0x55 with bit1) and give the player full life; "at
// checkpoint" revives next to the area's activated waypoint, else at its arrival point (teleport 0x1b7, AreaLayout.Checkpoint),
// "in town" in the character's last town (an area change;
// in that town it is the same as the checkpoint).
static async Task Resurrect(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, bool inTown)
{
    lock (conn.Monsters)
    {
        if (conn.PlayerLife > 0) return;
        conn.PlayerLife = conn.PlayerMaxLife;
        conn.PlayerMana = (uint)UnreservedMana(conn);
        foreach (var m in conn.Monsters.Values) m.Aggro = false;
    }
    Console.WriteLine($"[gateway] {remote} <= 0x0054 resurrect {(inTown ? "in town" : "at checkpoint")}");
    await Send(remote, stream, ciphers, InstanceProtocol.DeathScreen(0x02), "0x0055 hide the death screen");
    // A Survivors run ends with the death: either button takes the player home (the map's one life is spent).
    if (conn.Run is not null && HideoutCatalog.Default is { } home)
    {
        await GainFlaskCharges(remote, stream, ciphers, conn, int.MaxValue, "resurrected");
        await ChangeArea(remote, stream, ciphers, conn, new PendingTransfer(home.AreaHash16, 0, false, AtWaypoint: true), "Survivors run lost: to the hideout");
        return;
    }
    // The flasks are full again after a death.
    await GainFlaskCharges(remote, stream, ciphers, conn, int.MaxValue, "resurrected");
    if (inTown && conn.LastTown != 0 && conn.Layout.Hash != conn.LastTown)
    {
        // The new area's player object is created with full life.
        await ChangeArea(remote, stream, ciphers, conn, new PendingTransfer(conn.LastTown, 0, false, AtSpawn: true), "resurrect in town");
        return;
    }
    // Revive first: a teleport sent while the player is dead left them where they died (live 2026-10-17).
    await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerMaxLife),
        $"0x01b1 player revived with {conn.PlayerMaxLife} life");
    await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana {conn.PlayerMana}");
    // The checkpoint: the area's waypoint once activated, else where the player entered the area, as in the real game
    // before any checkpoint is activated. Checkpoint objects and boss arenas (revive at the arena's door) are not done.
    bool activated;
    lock (conn.Waypoints)
        activated = WaypointCatalog.Areas.Find(conn.Layout.Hash) is { } entry && conn.Waypoints.Contains(entry.Id);
    var (x, y) = conn.Layout.Checkpoint(activated);
    conn.Walk.Teleport(x, y, conn.Clock.Elapsed.TotalSeconds);
    await Send(remote, stream, ciphers, InstanceProtocol.Teleport(PlayerId, x, y),
        $"0x01b7 player to the checkpoint ({x}, {y}): {(activated && conn.Layout.Waypoint is not null ? "the activated waypoint" : "the arrival point")}");
    await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, [(StatKeys.HideMiniLifeBar, 0)]),
        "0x01b3 player hide_mini_life_bar = 0");
}

// A spawned monster's id from the argument if it is 0x hex, else the last spawned one.
static uint? MonsterArg(ClientState conn, string[] args, int i)
{
    if (args.Length > i && (args[i].StartsWith("0x", StringComparison.OrdinalIgnoreCase)))
    {
        uint id = Convert.ToUInt32(args[i], 16);
        return conn.Monsters.ContainsKey(id) ? id : null;
    }
    return conn.Monsters.Count == 0 ? null : conn.Monsters.Keys.Last();
}

// A cell 3 cells from the server's estimate of the player, walkable if the walk map says so.
static (int X, int Y) NextToPlayer(ClientState conn)
{
    var (px, py) = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
    foreach (var (dx, dy) in new[] { (3, 0), (-3, 0), (0, 3), (0, -3), (2, 2), (-2, -2) })
        if (conn.Layout.Walkable is not { } w || w(px + dx, py + dy)) return (px + dx, py + dy);
    return (px, py);
}

// One sender at a time per connection: the Salsa20 keystream must be applied and written in the same order (the read
// loop, the monster AI tick and the transfer timer all send).
static async Task Send(string remote, NetworkStream stream, ConnectionCiphers ciphers, byte[] bytes, string? log)
{
    var gate = SendGates.GetValue(ciphers, _ => new SemaphoreSlim(1, 1));
    await gate.WaitAsync();
    try
    {
        if (log is not null) Console.WriteLine($"[gateway] {remote} => {log}: {Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 96)))}");
        ciphers.Send.Process(bytes);
        await stream.WriteAsync(bytes);
    }
    finally { gate.Release(); }
}

// C→S 0x28: a click on an item. It moves to the cursor inventory (row 12) in one save (PlayerInventories.PickUp): one 0xbf
// removes it from its inventory, another adds it to the cursor at (0, 0). Refused (logged) when the cursor already holds an
// item or the item is not there.
static async Task PickUpItem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceItems.PickUpItem pick)
{
    if (conn.Sale is { } sale && pick.InventoryId == sale.Mine) { await TakeFromSale(remote, stream, ciphers, conn, sale, pick.ItemId); return; }
    if (StashOf(conn).TabOf(pick.InventoryId) is int tab) { await PickUpFromStash(remote, stream, ciphers, conn, tab, pick); return; }
    var items = ItemsOf(conn);
    if (PlayerInventories.Row(pick.InventoryId) is not byte row || items.PickUp(row, pick.ItemId) is not { } held)
    {
        Console.WriteLine($"[gateway] {remote} <= 0x0028 pick up {pick}: refused");
        return;
    }
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(pick.InventoryId, items.NextRevision(row), [held.Id], []),
        $"0x00bf inventory {pick.InventoryId}: remove item {held.Id} (picked up {pick})");
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
        items.NextRevision(PlayerInventories.Cursor), [], [held]), $"0x00bf cursor: add item {held.Id}");
    // Dual wielding: taking the main hand off moves the off hand's one-handed weapon into the main hand.
    if (items.PromoteOffhand(row, ShapeOf) is { } promoted)
    {
        byte off = (byte)(row + 1);
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(off), items.NextRevision(off), [promoted.Id], []),
            $"0x00bf inventory {PlayerInventories.InventoryId(off)}: remove item {promoted.Id} (to the main hand)");
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), [], [promoted]),
            $"0x00bf inventory {PlayerInventories.InventoryId(row)}: add item {promoted.Id} (from the off hand)");
    }
    if (PlayerInventories.ShowsOnModel(row)) await SendEquipment(remote, stream, ciphers, conn, items);
}

static PlayerInventories ItemsOf(ClientState conn) => PlayerInventories.For(string.IsNullOrEmpty(conn.Character) ? "Exile" : conn.Character, Items);

// The flat armour and evasion of the equipped body armour, helmet, gloves and boots. Only the base type's ArmourTypes
// row is used (its minimum roll, matching the item's default base percentile); local/global mods and quality are not
// modelled. Shields are excluded for now. Inactive items (requirements not met) give none.
static (int Armour, int Evasion) EquippedBaseDefences(ClientState conn)
{
    var items = ItemsOf(conn);
    int armour = 0, evasion = 0;
    foreach (byte row in new[] { PlayerInventories.BodyArmour, PlayerInventories.Helmet, PlayerInventories.Gloves, PlayerInventories.Boots })
        if (items.Items(row) is [var equipped, ..] && !IsInactive(conn, equipped.Id)
            && Items.ByHash(equipped.Item.TypeHash) is { } type && Items.Defences(type) is { } d)
        {
            armour += d.ArmourMin;
            evasion += d.EvasionMin;
        }
    return (armour, evasion);
}

// The equipped main/off hands in both weapon sets. Rows 2/3 are Weapon1 and rows 14/15 Weapon2.
// The local stats of the active main hand and off hand items (the first weapon set, else the second), for the panel's
// hand rows (PanelStats).
static (Dictionary<string, int> Main, Dictionary<string, int> Off) HandLocalStats(ClientState conn)
{
    var items = ItemsOf(conn);
    Dictionary<string, int> Local(byte row, byte swap) =>
        items.Items(row).Concat(items.Items(swap)).Where(i => !IsInactive(conn, i.Id)).Select(i => i.Item).FirstOrDefault() is { } item
            ? ItemModStats.Local(item, Items.Mods) : [];
    return (Local(2, 14), Local(3, 15));
}

static WeaponProfile WeaponProfileFor(ClientState conn)
{
    var items = ItemsOf(conn);
    ItemCatalog.BaseType? At(byte row) => items.Items(row).Select(i => Items.ByHash(i.Item.TypeHash)).FirstOrDefault(t => t is not null);
    static bool OneHandMelee(ItemCatalog.BaseType t) => t.Class is "Claw" or "Dagger" or "Rune Dagger"
        or "One Hand Sword" or "Thrusting One Hand Sword" or "One Hand Axe" or "One Hand Mace" or "Sceptre";
    static bool OneHand(ItemCatalog.BaseType t) => OneHandMelee(t) || t.Class == "Wand";
    static bool TwoHandMelee(ItemCatalog.BaseType t) => t.Class is "Two Hand Sword" or "Two Hand Axe"
        or "Two Hand Mace" or "Staff" or "Warstaff";
    static bool Pair(ItemCatalog.BaseType? main, ItemCatalog.BaseType? off) =>
        main is { } m && off is { } o && OneHand(m) && OneHand(o);
    var main1 = At(2);
    var off1 = At(3);
    var main2 = At(14);
    var off2 = At(15);
    var all = new[] { main1, off1, main2, off2 }.OfType<ItemCatalog.BaseType>().ToList();
    var dual = Pair(main1, off1) || Pair(main2, off2);
    var claws = (main1?.Class == "Claw" && off1?.Class == "Claw") || (main2?.Class == "Claw" && off2?.Class == "Claw");
    var unarmed = main1 is null && main2 is null;
    var mainItem = items.Items(2).Concat(items.Items(14)).Where(i => !IsInactive(conn, i.Id)).Select(i => i.Item).FirstOrDefault();
    var local = mainItem is null ? [] : ItemModStats.Local(mainItem, Items.Mods);
    return new WeaponProfile(
        Shield: HasShield(conn),
        DualWield: dual,
        Staff: all.Any(t => t.Class is "Staff" or "Warstaff"),
        Bow: all.Any(t => t.Class == "Bow"),
        DualClaws: claws,
        Unarmed: unarmed,
        OneHand: all.Any(OneHand),
        TwoHand: all.Any(t => TwoHandMelee(t) || t.Class == "Bow"),
        Melee: unarmed || all.Any(t => OneHandMelee(t) || TwoHandMelee(t)),
        OneHandMelee: all.Any(OneHandMelee),
        TwoHandMelee: all.Any(TwoHandMelee),
        Axe: all.Any(t => t.Class is "One Hand Axe" or "Two Hand Axe"),
        Sword: all.Any(t => t.Class is "One Hand Sword" or "Thrusting One Hand Sword" or "Two Hand Sword"),
        Mace: all.Any(t => t.Class is "One Hand Mace" or "Two Hand Mace"),
        Claw: all.Any(t => t.Class == "Claw"),
        Dagger: all.Any(t => t.Class is "Dagger" or "Rune Dagger"),
        Wand: all.Any(t => t.Class == "Wand"),
        // The main hand's local mods: increased critical strike chance scales its base, attack speed and accuracy go
        // into PassiveBonus.
        Critical: (main1 ?? main2) is { } main ? Items.WeaponCritical(main) * (100 + local.GetValueOrDefault("local_critical_strike_chance_+%")) / 100 : 0,
        LocalAttackSpeedPct: local.GetValueOrDefault("local_attack_speed_+%"),
        LocalAccuracy: local.GetValueOrDefault("local_accuracy_rating"));
}

// The equipped off hand(s): Weapon1's off hand is row 3, Weapon2's is row 15.
static bool HasShield(ClientState conn) => OffhandTypes(conn).Any(t => t.Class == "Shield");

static int ShieldBaseBlock(ClientState conn) => OffhandTypes(conn).Where(t => t.Class == "Shield")
    .Select(Items.ShieldBlock).DefaultIfEmpty(0).Max();

static IEnumerable<ItemCatalog.BaseType> OffhandTypes(ClientState conn)
{
    var items = ItemsOf(conn);
    foreach (byte row in new byte[] { PlayerInventories.Offhand1, 15 })
        foreach (var item in items.Items(row))
            if (Items.ByHash(item.Item.TypeHash) is { } type) yield return type;
}

static PlayerInventories.Shape? ShapeOf(Item i) => Items.ByHash(i.TypeHash) is { } t
    ? new PlayerInventories.Shape(t.Width, t.Height, t.Class, t.MaxStack,
        t.BecomesWhenFull && Items.Row(t.FullStackRow) is { Supported: true } full ? () => ItemCatalog.Create(full) : null)
    : null;

static VisibleItem? LookOf(Item i) => Items.ByHash(i.TypeHash) is { } t ? new VisibleItem(0, t.VisualIdentity, t.Stance) : null;

// What the player wears changed (an item went into or out of a visible equipment row): 0x2ba Inventories for the model,
// then the Actor skill list again, since the gems socketed in the equipment grant skills.
static async Task SendEquipment(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, PlayerInventories items)
{
    var equipped = items.Equipped(LookOf);
    await Send(remote, stream, ciphers, InstanceEntities.PlayerEquipmentUpdate(PlayerId, equipped),
        $"0x02ba player equipment: {string.Join(", ", equipped.Select(e => $"slot {e.Slot} visual 0x{e.VisualIdentity:x4} stance {e.Stance}"))}");
    conn.Pools = PoolsFor(conn, conn.Pools.Level);
    lock (conn.Monsters) conn.PlayerMana = Math.Min(conn.PlayerMana, (uint)UnreservedMana(conn));
    await SendSkills(remote, stream, ciphers, conn);
    await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, PlayerStatsWithCleared(conn)),
        "0x01b3 player stats (equipment)");
    await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana {conn.PlayerMana} (equipment)");
}

// Dev commands (/mod, /rarity): changes the held item and replaces it on the cursor (0xbf removing and adding its id).
static async Task ChangeHeldItem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, Func<Item, Item> change, string what)
{
    var items = ItemsOf(conn);
    if (items.Items(PlayerInventories.Cursor) is not [var held, ..]) { await conn.Commands!.Reply("Hold an item first"); return; }
    if (!held.Item.Components.Contains("Mods")) { await conn.Commands!.Reply($"{Items.ByHash(held.Item.TypeHash)?.Name} has no Mods component"); return; }
    if (items.Change(PlayerInventories.Cursor, held.Id, change) is not { } changed) return;
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
        items.NextRevision(PlayerInventories.Cursor), [held.Id], [changed]), $"0x00bf cursor: item {held.Id} {what}");
    await conn.Commands!.Reply(what);
}

static ModSection? ParseSection(string text) => text.ToLowerInvariant() switch
{
    "implicit" or "imp" => ModSection.Implicit,
    "explicit" or "exp" => ModSection.Explicit,
    "enchant" or "ench" => ModSection.Enchant,
    "scourge" or "s10" => ModSection.Scourge,
    "hidden" or "s11" => ModSection.Hidden,
    _ => null,
};

// Takes the held item off the cursor (0xbf); null when nothing is held.
static async Task<InventoryItem?> TakeHeld(string remote, NetworkStream stream, ConnectionCiphers ciphers, PlayerInventories items, string why)
{
    if (items.Items(PlayerInventories.Cursor) is not [var held, ..] || items.Remove(PlayerInventories.Cursor, held.Id) is null) return null;
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
        items.NextRevision(PlayerInventories.Cursor), [held.Id], []), $"0x00bf cursor: remove item {held.Id} ({why})");
    return held;
}

// C→S 0x30: the held gem goes into a socket. The cursor loses it (and holds the gem that was in the socket, if any); the
// item is replaced in its inventory (0xbf removing and adding the same id). Gems in equipment grant skills.
static async Task SocketGem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceItems.SocketGem s)
{
    var items = ItemsOf(conn);
    string why = "unknown inventory";
    var done = PlayerInventories.Row(s.InventoryId) is byte row ? items.Socket(row, s.ItemId, (int)s.Socket, ShapeOf, items.NewItemId, out why) : null;
    if (done is not var (item, before, now) || PlayerInventories.Row(s.InventoryId) is not byte r)
    {
        Console.WriteLine($"[gateway] {remote} <= 0x0030 socket {s}: refused ({why})");
        return;
    }
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
        items.NextRevision(PlayerInventories.Cursor), [before.Id], now is null ? [] : [now]),
        $"0x00bf cursor: remove gem {before.Id}" + (now is null ? "" : $", hold gem {now.Id} from the socket"));
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(s.InventoryId, items.NextRevision(r), [item.Id], [item]),
        $"0x00bf inventory {s.InventoryId}: item {item.Id} with a gem in socket {s.Socket}");
    if (PlayerInventories.ShowsOnModel(r)) await SendEquipment(remote, stream, ciphers, conn, items);
}

// C→S 0x2f: a socketed gem goes onto the cursor; the item is replaced in its inventory.
static async Task UnsocketGem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceItems.SocketGem s)
{
    var items = ItemsOf(conn);
    string why = "unknown inventory";
    var done = PlayerInventories.Row(s.InventoryId) is byte row ? items.Unsocket(row, s.ItemId, (int)s.Socket, items.NewItemId, out why) : null;
    if (done is not var (item, held) || PlayerInventories.Row(s.InventoryId) is not byte r)
    {
        Console.WriteLine($"[gateway] {remote} <= 0x002f unsocket {s}: refused ({why})");
        return;
    }
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(s.InventoryId, items.NextRevision(r), [item.Id], [item]),
        $"0x00bf inventory {s.InventoryId}: item {item.Id} without the gem in socket {s.Socket}");
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
        items.NextRevision(PlayerInventories.Cursor), [], [held]), $"0x00bf cursor: hold gem {held.Id}");
    if (PlayerInventories.ShowsOnModel(r)) await SendEquipment(remote, stream, ciphers, conn, items);
}

// C→S 0x16 /destroy (the town popup's Destroy): the held item is gone.
static async Task DestroyHeldItem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    if (await TakeHeld(remote, stream, ciphers, ItemsOf(conn), "destroyed") is null)
        Console.WriteLine($"[gateway] {remote} <= 0x0016 /destroy: nothing held");
}

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
}

// The instance's items on the ground, where they were left (InstanceStore): dropped or looted on an earlier connection.
static async Task SendGroundItems(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    foreach (var (id, ground) in conn.Instance.Ground.All())
    {
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), ground.Blob()),
            $"0x02b9 create world item 0x{id:x} left at ({ground.GridX}, {ground.GridY})");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
    }
}

// C→S 0x70: a flask key. The flask in that cell of the flask bar spends its charges (S→C 0xbf with the changed item) and
// its life or mana comes back over its recovery time in the player tick, under the flask_effect_life / _mana buff
// (13-items.md §8). Without enough charges, or while dead, nothing happens.
static async Task UseFlask(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint slot)
{
    var items = ItemsOf(conn);
    var flask = items.Items(PlayerInventories.Flask).FirstOrDefault(i => i.X == slot);
    if (flask is null || Items.ByHash(flask.Item.TypeHash) is not { } type || Items.FlaskOf(type) is not { } use)
    {
        Console.WriteLine($"[gateway] {remote} <= 0x0070 use flask {slot}: {(flask is null ? "no flask there" : "not a life or mana flask (not handled)")}");
        return;
    }
    if (conn.PlayerLife == 0 || Flasks.Spend(flask.Item, use) is null)
    {
        Console.WriteLine($"[gateway] {remote} <= 0x0070 use flask {slot} ({type.Name}): {(conn.PlayerLife == 0 ? "dead" : $"{flask.Item.Charges}/{use.MaxCharges} charges, a use needs {use.ChargesPerUse}")}");
        return;
    }
    if (items.Change(PlayerInventories.Flask, flask.Id, i => Flasks.Spend(i, use) ?? i) is not { } spent) return;
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Flask),
        items.NextRevision(PlayerInventories.Flask), [spent.Id], [spent]), $"0x00bf flask {slot}: {spent.Item.Charges}/{use.MaxCharges} charges");
    double now = conn.Clock.Elapsed.TotalSeconds;
    ushort buff;
    lock (conn.Monsters) buff = conn.NextBuff++;
    ushort row = use.LifePerUse > 0 ? FlaskEffectLifeBuffRow : FlaskEffectManaBuffRow;
    var effect = new Flasks.Effect((byte)slot, buff, row, use.LifePerUse / use.Seconds, use.ManaPerUse / use.Seconds, now, now + use.Seconds);
    if (conn.FlaskEffects.Start(effect) is { } replaced && FlaskBuffs)
        await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, replaced.Buff), null);
    Console.WriteLine($"[gateway] {remote} <= 0x0070 use flask {slot} ({type.Name}): {use.LifePerUse} life, {use.ManaPerUse} mana over {use.Seconds:0.##} s");
    // Both rows are IsRecovery: 0x1c1 carries one u32 per GrantedStats entry, here base_life (or mana)
    // _recovery_per_minute_from_flask. Without it the client misread the message and showed no life change after it
    // (live 2026-10-20).
    uint perMinute = (uint)Math.Round((use.LifePerUse > 0 ? use.LifePerUse : use.ManaPerUse) * 60 / use.Seconds);
    // Being tested: the duration, from which the orb could draw the life still to come. Game_FLASK_BUFF_DURATION picks
    // the field: "ms" (default) u32 +0x68 in milliseconds (flags bit1), "f6c" f32 +0x6c in seconds (bit7), "0" none.
    string durationField = Environment.GetEnvironmentVariable("Game_FLASK_BUFF_DURATION") ?? "ms";
    uint? u68 = durationField == "ms" ? (uint)Math.Round(use.Seconds * 1000) : null;
    float? f6c = durationField == "f6c" ? (float)use.Seconds : null;
    if (FlaskBuffs)
        await Send(remote, stream, ciphers, InstanceEntities.AddBuff(PlayerId, buff, row, PlayerId, values: [perMinute], u68: u68, f6c: f6c),
            $"0x01c1 player buff {buff}: {(row == FlaskEffectLifeBuffRow ? "flask_effect_life" : "flask_effect_mana")} (BuffDefinitions row {row}), {perMinute} per minute, duration field {durationField}");
}

// Gives each life and mana flask in the flask bar up to `charges` charges (int.MaxValue: full) and saves them (or, for
// the refills of kills and the like, lets them wait for the next save: PlayerInventories.ChangeUnsaved): the
// flasks that changed.
static List<InventoryItem> FillFlasks(PlayerInventories items, int charges, bool saveNow = true)
{
    List<InventoryItem> changed = [];
    foreach (var flask in items.Items(PlayerInventories.Flask))
        if (Items.ByHash(flask.Item.TypeHash) is { } type && Items.FlaskOf(type) is { } use && flask.Item.Charges < use.MaxCharges &&
            (saveNow ? items.Change(PlayerInventories.Flask, flask.Id, i => Flasks.Gain(i, use, charges)) : items.ChangeUnsaved(PlayerInventories.Flask, flask.Id, i => Flasks.Gain(i, use, charges))) is { } done)
            changed.Add(done);
    return changed;
}

// FillFlasks, and the changed flasks to the client (one S→C 0xbf).
static async Task GainFlaskCharges(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, int charges, string why)
{
    var items = ItemsOf(conn);
    if (FillFlasks(items, charges, saveNow: false) is not { Count: > 0 } changed) return;
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Flask),
        items.NextRevision(PlayerInventories.Flask), [.. changed.Select(c => c.Id)], changed),
        $"0x00bf flasks: {string.Join(", ", changed.Select(c => $"{c.X}: {c.Item.Charges}"))} charges ({why})");
}

// A new character's class weapon lies on the ground a few cells from where it appears, as on the real game's Twilight
// Strand, until it is picked up (PlayerInventories.StartWeapon): put once into each instance it enters outside towns.
static async Task PlaceStartWeapon(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    if (ItemsOf(conn).StartWeapon is not { } weapon || WaypointCatalog.Areas.Find(conn.Layout.Hash) is { IsTown: true }) return;
    var (x, y) = conn.Layout.Near((conn.Layout.SpawnX, conn.Layout.SpawnY));
    var ground = new WorldItemObject(x, y, weapon);
    // Put here on an earlier connection: SendGroundItems has sent it already, if it still lies here.
    if (conn.Instance.PlaceStartWeapon(CharacterName(conn), ground) is not { } id) return;
    await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), ground.Blob()),
        $"0x02b9 create world item 0x{id:x} start weapon {Items.ByHash(weapon.TypeHash)?.Name} at ({x}, {y}), spawn ({conn.Layout.SpawnX}, {conn.Layout.SpawnY})");
    await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
}

// C→S 0x29: drop the held item where the player stands, as a WorldItem object (0x2b9 + wake zone 0x1a9).
static async Task DropItem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    if (await TakeHeld(remote, stream, ciphers, ItemsOf(conn), "dropped") is not { } held)
    {
        Console.WriteLine($"[gateway] {remote} <= 0x0029 drop: nothing held");
        return;
    }
    var (x, y) = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
    var ground = new WorldItemObject(x, y, held.Item);
    uint id = conn.Instance.Ground.Add(ground);
    await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), ground.Blob()),
        $"0x02b9 create world item 0x{id:x} at ({x}, {y})");
    await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), $"0x01a9 world item 0x{id:x} enters wake zone");
}

// C→S 0x24 on a world item: walk there, then put it in the main inventory (first free cell) and remove the object (0x2bb).
static async Task PickUpGroundItem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, WorldItemObject ground,
    InstanceTransfer.UseSkillOnObject use)
{
    int action = Interlocked.Increment(ref conn.ActionCount);
    double now = conn.Clock.Elapsed.TotalSeconds;
    var (here, stop, arrive) = conn.Walk.Approach(ground.GridX, ground.GridY, PickupReach, now);
    Console.WriteLine($"[gateway] {remote} <= 0x0024 pick up world item 0x{id:x} at ({ground.GridX}, {ground.GridY}) {use} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
    // Echo the interaction at once, from where the player is now (as a move's echo does): the client then walks to its
    // own pickup range itself. An echo sent on arrival carried the server's estimated stop, and the client snapped to it.
    await Send(remote, stream, ciphers,
        InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, ground.GridX, ground.GridY, use.SkillId, use.Sequence, use.Flags, targetObjectId: id),
        $"0x01ab player picks up world item 0x{id:x}");
    if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
    if (conn.ActionCount != action) return;
    if (IsGold(ground))
    {
        await PickUpGold(remote, stream, ciphers, conn, id, ground, "picked up");
        return;
    }
    var items = ItemsOf(conn);
    var shape = ShapeOf(ground.Item) ?? new(1, 1, "");
    // With the inventory panel open the item goes to the cursor (when it is free), otherwise to the first free cell.
    bool toCursor = (use.Flags & InstanceTransfer.InventoryOpenFlag) != 0 && items.Items(PlayerInventories.Cursor).Count == 0;
    byte row = toCursor ? PlayerInventories.Cursor : PlayerInventories.MainInventory;
    if (!toCursor && shape.MaxStack > 1)
    {
        // A stack goes onto the inventory's stacks of its type first, then into free cells; all of it or nothing. Nothing
        // awaits between the check, the take and the add, and only this connection changes the character's items.
        if (!items.Fits(PlayerInventories.MainInventory, ground.Item, ShapeOf))
        {
            Console.WriteLine($"[gateway] {remote} world item 0x{id:x}: no room in the inventory for {ground.Item.StackSize}");
            return;
        }
        if (conn.Instance.Ground.Take(id) is null) return;   // already picked up
        var stacked = KeepOnGroundIfNotSaved(conn, ground, () => items.AddStacked(PlayerInventories.MainInventory, ground.Item, ShapeOf));
        if (stacked.Left > 0)
        {
            // Fits said yes, so this is not expected; the rest goes back on the ground rather than vanish.
            var rest = new WorldItemObject(ground.GridX, ground.GridY, ground.Item with { StackSize = (ushort)stacked.Left });
            uint restId = conn.Instance.Ground.Add(rest);
            Console.WriteLine($"[gateway] {remote} world item 0x{id:x}: {stacked.Left} didn't fit, back on the ground as 0x{restId:x}");
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(restId, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), rest.Blob()),
                $"0x02b9 create world item 0x{restId:x} (the rest of 0x{id:x})");
        }
        await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove world item 0x{id:x} (picked up from ({stop.X}, {stop.Y}))");
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), stacked.RemovedIds, stacked.AddedItems),
            $"0x00bf inventory 1: {ground.Item.StackSize} picked up ({stacked.Changed.Count} stack(s) grown, {stacked.Added.Count} new)");
        return;
    }
    (byte X, byte Y) cell = (0, 0);
    if (!toCursor)
    {
        if (items.FreeCell(PlayerInventories.MainInventory, shape.Width, shape.Height, ShapeOf) is not { } free)
        {
            Console.WriteLine($"[gateway] {remote} world item 0x{id:x}: no room in the inventory");
            return;
        }
        cell = free;
    }
    if (conn.Instance.Ground.Take(id) is null) return;   // already picked up
    // The start weapon stops waiting; the Put below saves both changes at once.
    if (conn.Instance.IsStartWeapon(CharacterName(conn), id) && items.ClaimStartWeapon())
        Console.WriteLine($"[gateway] {remote} start weapon picked up");

    var placed = KeepOnGroundIfNotSaved(conn, ground, () => items.Put(row, new InventoryItem(items.NewItemId(), cell.X, cell.Y, ground.Item)));
    await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove world item 0x{id:x} (picked up from ({stop.X}, {stop.Y}))");
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), [], [placed]),
        toCursor ? $"0x00bf cursor: hold item {placed.Id}" : $"0x00bf inventory 1: add item {placed.Id} at ({cell.X}, {cell.Y})");
}

static bool IsGold(WorldItemObject ground) => Items.ByHash(ground.Item.TypeHash)?.Class == ItemDrops.GoldClass;

// Gold goes to the stash's gold of the character's league (S→C 0x2a9), not into the inventory.
static async Task PickUpGold(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, WorldItemObject ground, string how)
{
    if (conn.Instance.Ground.Take(id) is null) return;   // already picked up
    uint total = PersonalStashOf(conn).AddGold(ground.Item.StackSize);
    await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove world item 0x{id:x} (gold {how})");
    await Send(remote, stream, ciphers, InstanceEntities.Gold(total), $"0x02a9 gold: {total} (+{ground.Item.StackSize} {how})");
}

// A pickup whose save fails puts the item back on the ground before the error closes the connection: it left the ground
// first, and the items are loaded again as saved (Saves).
static T KeepOnGroundIfNotSaved<T>(ClientState conn, WorldItemObject ground, Func<T> pickUp)
{
    try { return pickUp(); }
    catch
    {
        conn.Instance.Ground.Add(ground);
        throw;
    }
}

// C→S 0x7c: Shift + click on a stack, then the split window's amount. The stack shrinks in place (0xbf removing and adding
// its id), the split-off part is held as a new item (0xbf cursor).
static async Task SplitStack(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceItems.SplitStack split, string hex)
{
    var items = ItemsOf(conn);
    int amount = (int)Math.Min(split.Amount, ushort.MaxValue);
    if (StashOf(conn).TabOf(split.InventoryId) is int tab)
    {
        // From a stash tab: the tab's stack shrinks and the split-off part is held with a character id, in one transaction.
        var stash = StashOf(conn);
        if (ItemMoves.SplitFromStash(stash, items, tab, split.ItemId, amount, ShapeOf) is not var (left, part))
        {
            Console.WriteLine($"[gateway] {remote} <= 0x007c split {split} in stash tab {tab}: refused: {hex}");
            return;
        }
        Console.WriteLine($"[gateway] {remote} <= 0x007c split {split} in stash tab {tab}" + (left is null ? " (the whole stack: picked up)" : ""));
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(split.InventoryId, stash.NextRevision(tab), [left?.Id ?? part.Id], left is null ? [] : [left], stash.IsGuild ? InstanceStash.GuildManager : (byte)0),
            left is null ? $"0x00bf stash tab {tab}: remove item {part.Id}" : $"0x00bf stash tab {tab}: stack {left.Id} now {left.Item.StackSize}");
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
            items.NextRevision(PlayerInventories.Cursor), [], [part]), $"0x00bf cursor: hold {part.Item.StackSize} as item {part.Id}");
        return;
    }
    string why = "unknown inventory";
    var done = PlayerInventories.Row(split.InventoryId) is byte row ? items.Split(row, split.ItemId, amount, out why) : null;
    if (done is not var (stack, held) || PlayerInventories.Row(split.InventoryId) is not byte r)
    {
        Console.WriteLine($"[gateway] {remote} <= 0x007c split {split}: refused ({why}): {hex}");
        return;
    }
    Console.WriteLine($"[gateway] {remote} <= 0x007c split {split}");
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(split.InventoryId, items.NextRevision(r), [stack.Id], [stack]),
        $"0x00bf inventory {split.InventoryId}: stack {stack.Id} now {stack.Item.StackSize}");
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
        items.NextRevision(PlayerInventories.Cursor), [], [held]), $"0x00bf cursor: hold {held.Item.StackSize} as item {held.Id}");
}

// C→S 0x58: use an item that needs no target (a right-click), such as an Orb of Regret: one item becomes one refund point.
static async Task UseItem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
    InstanceItems.UseItemRequest use, string hex)
{
    const string Regret = "Metadata/Items/Currency/CurrencyPassiveRefund";
    var items = ItemsOf(conn);
    byte? row = null;
    foreach (uint inventory in new[] { use.InventoryId, use.InventoryId + 1 })
        if (PlayerInventories.Row(inventory) is byte candidate && items.Items(candidate).Any(i => i.Id == use.ItemId))
        {
            row = candidate;
            break;
        }
    if (row is not byte r || items.Items(r).FirstOrDefault(i => i.Id == use.ItemId) is not { } item)
    {
        Console.WriteLine($"[gateway] {remote} <= 0x0058 use item {use}: no such item: {hex}");
        return;
    }
    if (Items.ByHash(item.Item.TypeHash) is not { } type)
    {
        Console.WriteLine($"[gateway] {remote} <= 0x0058 use item {use}: unknown type 0x{item.Item.TypeHash:x8}: {hex}");
        return;
    }
    if (type.Id == PortalScroll)
    {
        await UsePortalScroll(remote, stream, ciphers, conn, r, item, type);
        return;
    }
    if (type.Name == SkillBook)
    {
        await UseSkillBook(remote, stream, ciphers, conn, items, r, item, type);
        return;
    }
    if (type.Id != Regret && type.Name != RegretBook)
    {
        Console.WriteLine($"[gateway] {remote} <= 0x0058 use item {use}: {type.Name} is not handled: {hex}");
        return;
    }
    ushort refunds;
    lock (conn.Monsters)
    {
        if (conn.RefundPoints == ushort.MaxValue)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0058 use {type.Name}: refund points are already at the maximum");
            return;
        }
        // An Orb of Regret gives one refund point; a Book of Regrets two (ClientStrings ItemFunction2RespecPoints).
        refunds = (ushort)Math.Min(ushort.MaxValue, conn.RefundPoints + (type.Name == RegretBook ? 2 : 1));
    }
    // The orb and the point are saved together.
    if (ItemMoves.UseForRefundPoint(items, CharacterName(conn), r, use.ItemId, refunds) is not { } taken)
    {
        Console.WriteLine($"[gateway] {remote} <= 0x0058 use {type.Name}: item {use.ItemId} disappeared: {hex}");
        return;
    }
    lock (conn.Monsters) conn.RefundPoints = refunds;
    uint inventoryId = PlayerInventories.InventoryId(r);
    // Every change removes the id first: the client keeps an item by id, so a stack that only shrinks needs the remove
    // and the add of the same id with its new Stack component (as SendMerged does).
    await Send(remote, stream, ciphers,
        InstanceItems.InventoryChange(inventoryId, items.NextRevision(r), [item.Id],
            taken.Left is { } left ? [left] : []),
        $"0x00bf inventory {inventoryId}: {type.Name} used" + (taken.Left is { } now ? $", {now.Item.StackSize} left" : ", stack empty"));
    await SendPassives(remote, stream, ciphers, conn);
    Console.WriteLine($"[gateway] {remote} <= 0x0058 use {type.Name} ({use.ItemId}): {refunds} refund point(s)");
}

// A Book of Skill used (C→S 0x58, right-click; any of the quest books named so, Metadata/Items/QuestItems/SkillBooks/…):
// the book is used up and the character gets one more passive skill point, as /add_passive_points 1 (S→C 0x33).
static async Task UseSkillBook(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, PlayerInventories items,
    byte row, InventoryItem item, ItemCatalog.BaseType type)
{
    ushort extra;
    lock (conn.Monsters)
    {
        if (conn.ExtraPassivePoints == ushort.MaxValue)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0058 use {type.Name}: extra passive points are already at the maximum");
            return;
        }
        extra = (ushort)(conn.ExtraPassivePoints + 1);
    }
    // The book and the point are saved together.
    if (ItemMoves.UseForPassivePoint(items, CharacterName(conn), row, item.Id, extra) is not { } taken) return;
    lock (conn.Monsters) conn.ExtraPassivePoints = extra;
    uint inventoryId = PlayerInventories.InventoryId(row);
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(inventoryId, items.NextRevision(row), [item.Id], taken.Left is { } left ? [left] : []),
        $"0x00bf inventory {inventoryId}: {type.Name} used");
    await SendPassives(remote, stream, ciphers, conn);
    Console.WriteLine($"[gateway] {remote} <= 0x0058 use {type.Name} ({item.Id}, {type.Id}): +1 passive point, {extra} extra in all");
}

// C→S 0x71: an item used on another one. A Scroll of Wisdom identifies an unidentified item (its explicit mods and rare
// name are sent from now on) and loses one from its stack. Socket currency uses the same update path, including Shift.
static async Task UseItemOn(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceItems.UseItemOn use, string hex)
{
    const string Wisdom = "Metadata/Items/Currency/CurrencyIdentification";
    var items = ItemsOf(conn);
    if (items.RowOf(use.ItemId) is not byte usedRow || items.Items(usedRow).FirstOrDefault(i => i.Id == use.ItemId) is not { } used ||
        items.RowOf(use.TargetId) is not byte targetRow || items.Items(targetRow).FirstOrDefault(i => i.Id == use.TargetId) is not { } target)
    {
        Console.WriteLine($"[gateway] {remote} <= 0x0071 use item on item {use}: no such item: {hex}");
        return;
    }
    var type = Items.ByHash(used.Item.TypeHash);
    // What the currency makes of the target, or null (nothing happens, nothing is spent).
    Item? result = type?.Id switch
    {
        Wisdom => target.Item.Identified || !target.Item.Components.Contains("Mods") ? null : target.Item with { Identified = true },
        CurrencyEffects.ChromaticOrb => Items.ByHash(target.Item.TypeHash) is { } targetType
            ? CurrencyEffects.Chromatic(target.Item, Items.Requirements(targetType), Random.Shared) : null,
        CurrencyEffects.JewellersOrb => Items.ByHash(target.Item.TypeHash) is { } socketType
            ? CurrencyEffects.Jewellers(target.Item, socketType, Items.Requirements(socketType), Random.Shared) : null,
        CurrencyEffects.OrbOfFusing => Items.ByHash(target.Item.TypeHash) is { } linkType
            ? CurrencyEffects.Fusing(target.Item, linkType, Random.Shared) : null,
        CurrencyEffects.OrbOfAlchemy => CurrencyEffects.Alchemy(target.Item, Rolls, Random.Shared),
        CurrencyEffects.ChaosOrb => CurrencyEffects.Chaos(target.Item, Rolls, Random.Shared),
        CurrencyEffects.DivineOrb => Rolls.Reforge(target.Item, Random.Shared),
        CurrencyEffects.OrbOfScouring => CurrencyEffects.Scouring(target.Item),
        CurrencyEffects.OrbOfTransmutation => CurrencyEffects.Transmutation(target.Item, Rolls, Random.Shared),
        CurrencyEffects.OrbOfAlteration => CurrencyEffects.Alteration(target.Item, Rolls, Random.Shared),
        CurrencyEffects.OrbOfAugmentation => CurrencyEffects.Augmentation(target.Item, Rolls, Random.Shared),
        CurrencyEffects.RegalOrb => CurrencyEffects.Regal(target.Item, Rolls, Random.Shared),
        CurrencyEffects.ExaltedOrb => CurrencyEffects.Exalted(target.Item, Rolls, Random.Shared),
        CurrencyEffects.OrbOfAnnulment => CurrencyEffects.Annulment(target.Item, Random.Shared),
        _ => null,
    };
    if (result is null)
    {
        if (type?.Id == CurrencyEffects.JewellersOrb && Items.ByHash(target.Item.TypeHash) is { } refusedType)
            Console.WriteLine($"[gateway] Jeweller target {target.Id}: {refusedType.Id}, class {refusedType.Class}, ilvl {target.Item.ItemLevel}, " +
                $"sockets {target.Item.Sockets.Count}, roll cap {CurrencyEffects.MaximumSockets(target.Item, refusedType)}, " +
                $"base cap {CurrencyEffects.SocketCapacity(target.Item, refusedType)}, gems {target.Item.Sockets.Count(s => s.Gem is not null)}");
        Console.WriteLine($"[gateway] {remote} <= 0x0071 use {type?.Name ?? $"0x{used.Item.TypeHash:x8}"} on item {use.TargetId}: " +
            (type?.Id is Wisdom or CurrencyEffects.ChromaticOrb or CurrencyEffects.JewellersOrb or CurrencyEffects.OrbOfFusing or CurrencyEffects.OrbOfAlchemy or CurrencyEffects.ChaosOrb or CurrencyEffects.DivineOrb or CurrencyEffects.OrbOfScouring or CurrencyEffects.OrbOfTransmutation or CurrencyEffects.OrbOfAlteration or CurrencyEffects.OrbOfAugmentation or CurrencyEffects.RegalOrb or CurrencyEffects.ExaltedOrb or CurrencyEffects.OrbOfAnnulment
                ? "target refused (identified / wrong rarity / no explicit mods / unsupported sockets / maximum sockets / socketed gems / protected links)" : $"not handled: {hex}"));
        return;
    }
    // The currency and the changed item are saved together.
    if (ItemMoves.UseOn(items, usedRow, used.Id, targetRow, target.Id, _ => result) is not var (taken, identified)) return;
    // Remove and add the same ids: the client replaces an item only that way (see UseItem).
    uint usedInventory = PlayerInventories.InventoryId(usedRow), targetInventory = PlayerInventories.InventoryId(targetRow);
    if (usedRow == targetRow)
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(targetInventory, items.NextRevision(targetRow), [used.Id, target.Id],
            taken.Left is { } left ? [left, identified] : [identified]), $"0x00bf inventory {targetInventory}: item {target.Id} changed, {type!.Name} used");
    else
    {
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(usedInventory, items.NextRevision(usedRow), [used.Id],
            taken.Left is { } left ? [left] : []), $"0x00bf inventory {usedInventory}: {type!.Name} used");
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(targetInventory, items.NextRevision(targetRow), [target.Id], [identified]),
            $"0x00bf inventory {targetInventory}: item {target.Id} changed");
        if (PlayerInventories.ShowsOnModel(targetRow)) await SendEquipment(remote, stream, ciphers, conn, items);
    }
    Console.WriteLine($"[gateway] {remote} <= 0x0071 {type!.Name} ({used.Id}) on item {target.Id}: " + (type.Id is Wisdom or CurrencyEffects.OrbOfAlchemy or CurrencyEffects.ChaosOrb or CurrencyEffects.DivineOrb or CurrencyEffects.OrbOfScouring or CurrencyEffects.OrbOfTransmutation or CurrencyEffects.OrbOfAlteration or CurrencyEffects.OrbOfAugmentation or CurrencyEffects.RegalOrb or CurrencyEffects.ExaltedOrb or CurrencyEffects.OrbOfAnnulment
        ? string.Join("; ", identified.Item.Mods.Select(Items.Mods.Describe))
        : $"sockets {string.Join(" ", identified.Item.Sockets.Select(k => k.Colour))}; link groups {string.Join(",", identified.Item.Links ?? [])}"));
}

// C→S 0x4a: the held item clicked on an item. A stack of the same type with room takes what fits (PlayerInventories.MergeOnto,
// AccountStash.Merge); anything else (currency on an item …) is not handled yet.
static async Task HeldOntoItem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceItems.ItemAction a, string hex)
{
    var items = ItemsOf(conn);
    if (StashOf(conn).TabOf(a.InventoryId) is int tab)
    {
        // On a stash tab: as 0x2a on the clicked item's cell (merge, one with Shift, or a swap).
        if (StashOf(conn).Inventory(tab).Items.FirstOrDefault(i => i.Id == a.ItemId) is not { } onto)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x004a held item onto {a} in stash tab {tab}: no such item: {hex}");
            return;
        }
        Console.WriteLine($"[gateway] {remote} <= 0x004a held item onto {a} in stash tab {tab}: {hex}");
        await PlaceInStash(remote, stream, ciphers, conn, tab, new InstanceItems.PlaceItem(a.InventoryId, onto.X, onto.Y, (byte)(OneOf(a) ? 1 : 0), a.B, 0, 0));
        return;
    }
    string why = "unknown inventory";
    var done = PlayerInventories.Row(a.InventoryId) is byte row
        ? LevelTooLow(conn, row, items.Items(PlayerInventories.Cursor).FirstOrDefault()?.Item) is { } low ? null : items.MergeOnto(row, a.ItemId, ShapeOf, out why, OneOf(a))
        : null;
    if (done is null && PlayerInventories.Row(a.InventoryId) is byte lr && LevelTooLow(conn, lr, items.Items(PlayerInventories.Cursor).FirstOrDefault()?.Item) is { } tooLow) why = tooLow;
    if (done is null)
    {
        Console.WriteLine($"[gateway] {remote} <= 0x004a held item onto {a}: refused ({why}): {hex}");
        return;
    }
    Console.WriteLine($"[gateway] {remote} <= 0x004a held item onto {a}: {hex}");
    if (done.Merged || done.Rest is not null) { await SendMerged(remote, stream, ciphers, items, done); return; }
    // Not a stack with room: swapped, as 0x2a on that cell.
    var d = done.Displaced;
    uint target = PlayerInventories.InventoryId(done.Row);
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(target, items.NextRevision(done.Row), d is null ? [] : [d.Id], [done.Placed]),
        $"0x00bf inventory {target}: add item {done.Placed.Id} at ({done.Placed.X}, {done.Placed.Y})" + (d is null ? "" : $", remove item {d.Id}"));
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
        items.NextRevision(PlayerInventories.Cursor), [done.Held.Id], d is null ? [] : [d]),
        $"0x00bf cursor: remove item {done.Held.Id}" + (d is null ? "" : $", hold item {d.Id}"));
    if (PlayerInventories.ShowsOnModel(done.Row)) await SendEquipment(remote, stream, ciphers, conn, items);
}

// Shift held on a 0x4a: flag bit 2 (the sender's param 7; live 2026-10-19, Flags = 4 with Shift, 0 without).
static bool OneOf(InstanceItems.ItemAction a) => (a.Flags & 0x04) != 0;

// A held stack went onto a stack of its type: that one grows in place (same id), the held one shrinks (same id) or is gone.
static async Task SendMerged(string remote, NetworkStream stream, ConnectionCiphers ciphers, PlayerInventories items, PlayerInventories.Placement done)
{
    uint target = PlayerInventories.InventoryId(done.Row);
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(target, items.NextRevision(done.Row), done.Merged ? [done.Placed.Id] : [], [done.Placed]),
        $"0x00bf inventory {target}: " + (done.Merged ? $"stack {done.Placed.Id} now {done.Placed.Item.StackSize}" : $"add one as item {done.Placed.Id} at ({done.Placed.X}, {done.Placed.Y})")
        + (Items.ByHash(done.Placed.Item.TypeHash) is { } now && now.Hash != done.Held.Item.TypeHash ? $", became {now.Name}" : ""));
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
        items.NextRevision(PlayerInventories.Cursor), [done.Held.Id], done.Rest is { } rest ? [rest] : []),
        $"0x00bf cursor: " + (done.Rest is { } r ? $"stack {r.Id} now {r.Item.StackSize}" : $"remove item {done.Held.Id} (merged)"));
}

// C→S 0x2a: put the held item into an inventory cell (PlayerInventories.Place). The target inventory gets one 0xbf (the
// item it displaced removed, the held item added), the cursor another (the held item removed, the displaced one added).
static async Task PlaceItem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceItems.PlaceItem place)
{
    if (conn.Sale is { } sale && place.InventoryId == sale.Mine)
    {
        uint cursor = PlayerInventories.InventoryId(PlayerInventories.Cursor);
        var held = ItemsOf(conn).Items(PlayerInventories.Cursor).FirstOrDefault();
        if (held is not null) await OfferForSale(remote, stream, ciphers, conn, sale, cursor, held.Id, (byte)place.X, (byte)place.Y);
        return;
    }
    if (StashOf(conn).TabOf(place.InventoryId) is int tab) { await PlaceInStash(remote, stream, ciphers, conn, tab, place); return; }
    var items = ItemsOf(conn);
    if (place.A != 0) Console.WriteLine($"[gateway] {remote} <= 0x002a place {place}: one of the held stack (Shift)");
    PlayerInventories.Placement? done = null;
    string why = "unknown inventory";
    if (PlayerInventories.Row(place.InventoryId) is byte row && LevelTooLow(conn, row, items.Items(PlayerInventories.Cursor).FirstOrDefault()?.Item) is { } low)
        why = low;
    else if (PlayerInventories.Row(place.InventoryId) is byte r)
        done = items.Place(r, (int)place.X, (int)place.Y, ShapeOf, out why, one: place.A != 0);
    if (done is null)
    {
        Console.WriteLine($"[gateway] {remote} <= 0x002a place {place}: refused ({why})");
        return;
    }
    var d = done.Displaced;
    uint target = PlayerInventories.InventoryId(done.Row);   // may differ from the clicked one (off hand → main hand)
    if (done.Merged || done.Rest is not null) { await SendMerged(remote, stream, ciphers, items, done); return; }
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(target, items.NextRevision(done.Row), d is null ? [] : [d.Id], [done.Placed]),
        $"0x00bf inventory {target}: add item {done.Placed.Id} at ({done.Placed.X}, {done.Placed.Y})" + (d is null ? "" : $", remove item {d.Id}"));
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
        items.NextRevision(PlayerInventories.Cursor), [done.Held.Id], d is null ? [] : [d]),
        $"0x00bf cursor: remove item {done.Held.Id}" + (d is null ? "" : $", hold item {d.Id}"));
    if (PlayerInventories.ShowsOnModel(done.Row)) await SendEquipment(remote, stream, ciphers, conn, items);
}

// The account's stash (AccountStash): every character of the account shares it. Without an account (an old Auth) the
// character's name stands in.
static AccountStash StashOf(ClientState conn) => conn.GuildStashOpen ? GuildStashOf(conn) : PersonalStashOf(conn);

// The personal stash (and the account's gold, kept with it), whichever stash panel is open: the account's in the
// character's league (AccountStash.Key).
static AccountStash PersonalStashOf(ClientState conn) => AccountStash.For(AccountStash.Key(conn.Account ?? CharacterName(conn), conn.League), Items);

// The guild stash: no guilds yet, so each account has its own (AccountStash.ForGuild).
static AccountStash GuildStashOf(ClientState conn) => AccountStash.ForGuild(conn.Account ?? CharacterName(conn), Items);

// The tabs of that stash whose contents this connection has.
static HashSet<int> TabsSentOf(ClientState conn, AccountStash stash) => stash.IsGuild ? conn.GuildStashTabsSent : conn.StashTabsSent;

// The town's Stash object (Agents/docs/protocol/18-stash.md §1) where the town has one (StashCatalog).
static async Task SpawnTownStash(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    if (StashCatalog.In(conn.Layout.Hash) is not { } at || Environment.GetEnvironmentVariable("Game_STASH") == "0") return;
    uint id = conn.NextObjectId++;
    var stash = new StashObject(at.X, at.Y, at.Rotation);
    await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(StashObject.MetadataPath), stash.Blob()),
        $"0x02b9 create stash 0x{id:x} at ({at.X}, {at.Y})");
    await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), $"0x01a9 stash 0x{id:x} enters wake zone");
    conn.StashObject = id;
    // A hideout also gets the guild stash, a few cells from the stash.
    if (HideoutCatalog.ByArea(conn.Layout.Hash) is null) return;
    var (gx, gy) = conn.Layout.Near((at.X, at.Y));
    uint guild = conn.NextObjectId++;
    await Send(remote, stream, ciphers, InstanceEntities.CreateObject(guild, InstanceEntities.ObjectTypeHash(StashObject.GuildMetadataPath),
        new StashObject(gx, gy, at.Rotation).Blob()), $"0x02b9 create guild stash 0x{guild:x} at ({gx}, {gy})");
    await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(guild), null);
    conn.GuildStashObject = guild;
    conn.GuildStashAt = (gx, gy);
}

// A click on the guild stash (C→S 0x24): walk there, then its tab list (S→C 0x136 kind 1) and its panel (0x7b kind 2).
// While it is open the stash messages (0xc7, 0x28, 0x2a, Ctrl + click …) act on it (StashOf); the personal stash's open
// switches back.
static async Task OpenGuildStash(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceTransfer.UseSkillOnObject use)
{
    int action = Interlocked.Increment(ref conn.ActionCount);
    double now = conn.Clock.Elapsed.TotalSeconds;
    var (here, stop, arrive) = conn.Walk.Approach(conn.GuildStashAt.X, conn.GuildStashAt.Y, StashReach, now);
    Console.WriteLine($"[gateway] {remote} <= 0x0024 guild stash ({use}) from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
    if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
    if (conn.ActionCount != action) return;
    conn.GuildStashOpen = true;
    var guild = GuildStashOf(conn);
    var tabs = guild.Records(null, conn.GuildStashTabsSent);
    await Send(remote, stream, ciphers, InstanceStash.TabList(tabs, InstanceStash.Guild), $"0x0136 guild stash tab list: {tabs.Count} tab(s)");
    await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(InstanceStash.OpenGuildStashUi, conn.GuildStashObject, InstanceStash.NoAffinity),
        "0x007b open the guild stash");
}

// A hideout's placed doodads (HideoutCatalog.IsPlaced: the map device) from its decoration, with their HideoutDoodad data.
static async Task SpawnHideoutDoodads(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    if (HideoutCatalog.ByArea(conn.Layout.Hash) is not { } hideout) return;
    foreach (var d in hideout.Doodads.Where(HideoutCatalog.IsPlaced))
    {
        if (HideoutCatalog.TypeOf(d.Type!) is not var (components, states) || !HideoutObject.CanSerialize(components))
        {
            Console.WriteLine($"[gateway] {remote} hideout doodad {d.Name} ({d.Type}): unknown type or unsupported components, not created");
            continue;
        }
        uint id = conn.NextObjectId++;
        // "active" 1: the map device stands open and idle (its .ot: per-player quest flag MapDeviceActive).
        var obj = new HideoutObject(d.X, d.Y, d.Rotation, components, new HideoutDoodad(d.Hash, (byte)d.Variation),
            [.. states.Select(s => s == "active" ? 1 : 0)]);
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(d.Type!), obj.Blob()),
            $"0x02b9 create hideout doodad 0x{id:x} {d.Name} ({d.Type}) at ({d.X}, {d.Y}), components {string.Join(", ", components)}");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), $"0x01a9 hideout doodad 0x{id:x} enters wake zone");
        lock (conn.HideoutDoodads) conn.HideoutDoodads[id] = d;
    }
    // The portals of the character's open map stand at the device again.
    if (MapDevice.OpenOf(CharacterName(conn)) is { PortalsLeft: > 0 } open) await SpawnMapPortals(remote, stream, ciphers, conn, open, firstAfterMs: MapPortalEntryMs);
}

// A click on the map device (C→S 0x24): walk there, then S→C 0x7b kind 5 opens the map device window (21-hideouts.md §4).
// Its slots and the activation are not served yet.
static async Task UseMapDevice(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
    InstanceTransfer.UseSkillOnObject use, HideoutCatalog.Doodad device)
{
    int action = Interlocked.Increment(ref conn.ActionCount);
    double now = conn.Clock.Elapsed.TotalSeconds;
    var (here, stop, arrive) = conn.Walk.Approach(device.X, device.Y, StashReach, now);
    Console.WriteLine($"[gateway] {remote} <= 0x0024 {device.Name} ({use}) from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
    if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
    if (conn.ActionCount != action) return;
    await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(InstanceWaypoints.MapDeviceUi, use.TargetId), "0x007b open the map device window");
}

// C→S 0x00d8, the map device window's Activate (21-hideouts.md §5): every slot must hold a map or a map fragment, and
// exactly one map. Then the slots are consumed, the map's area opens (the first portal taken makes its instance) and its
// portals appear around the device, replacing any open before. Otherwise nothing changes and the chat says why.
static async Task ActivateMapDevice(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, (ushort Target, byte Index) activate)
{
    HideoutCatalog.Doodad? device;
    lock (conn.HideoutDoodads) device = conn.HideoutDoodads.Values.FirstOrDefault();
    var items = ItemsOf(conn);
    var slots = items.Items(PlayerInventories.MapDevice);
    Console.WriteLine($"[gateway] {remote} <= 0x00d8 activate the map device (target 0x{activate.Target:x4}, index {activate.Index}): " +
        $"{string.Join(", ", slots.Select(i => Items.ByHash(i.Item.TypeHash)?.Name ?? $"0x{i.Item.TypeHash:x8}").DefaultIfEmpty("empty"))}");
    Task Tell(string text) => Send(remote, stream, ciphers, InstanceChat.ChatLine(text), $"0x0017 chat line \"{text}\"");
    if (device is null) { await Tell("There is no Map Device here."); return; }
    var area = MapDevices.Value.Check([.. slots.Select(i => Items.ByHash(i.Item.TypeHash) is { } t
        ? new MapDevice.Slot(i.Item.TypeHash, t.Class, t.Name) : new MapDevice.Slot(i.Item.TypeHash, "", $"Item 0x{i.Item.TypeHash:x8}"))], out string why);
    if (area is null)
    {
        Console.WriteLine($"[gateway] {remote} map device not activated: {why}");
        await Tell(why);
        return;
    }
    // Every slot's item is consumed in one transaction: all of them or none.
    var consumed = Saves.Atomically(() => slots.Where(i => items.Remove(PlayerInventories.MapDevice, i.Id) is not null).Select(i => i.Id).ToList());
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MapDevice),
        items.NextRevision(PlayerInventories.MapDevice), consumed, []),
        $"0x00bf inventory {PlayerInventories.InventoryId(PlayerInventories.MapDevice)}: the map device consumed {consumed.Count} item(s)");
    await RemoveMapPortals(remote, stream, ciphers, conn);
    // Survivors: one portal, one life (Agents/docs/survivors.md).
    var open = MapDevice.Activate(CharacterName(conn), area, Survivors.IsLeague(conn.League) ? 1 : MapDevice.PortalCount);
    Console.WriteLine($"[gateway] {remote} map device opened {area.Name} ({area.Id}, 0x{area.Hash16:x4}, tier {area.Tier}): {open.PortalsLeft} portals");
    await SpawnMapPortals(remote, stream, ciphers, conn, open);
}

// The open map's portals that are left, at their places on a ring around the hideout's map device, one after another
// (MapPortalGapMs apart). 0x8f only reaches portal objects the client has already created, and a create sent in the same
// burst is not done yet (live 2026-10-20), so each portal is listed MapPortalListMs after its create; on entering the
// area the objects also wait until the world is active (firstAfterMs). A newer Activate or the portals' removal stops
// the sequence.
static Task SpawnMapPortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, MapDevice.OpenMap open, int firstAfterMs = 0)
{
    HideoutCatalog.Doodad? device;
    uint deviceId;
    lock (conn.HideoutDoodads) (deviceId, device) = conn.HideoutDoodads.Select(kv => (kv.Key, (HideoutCatalog.Doodad?)kv.Value)).FirstOrDefault();
    if (device is null) return Task.CompletedTask;
    int generation = Interlocked.Increment(ref conn.MapPortalGeneration);
    bool Current() => !conn.Closed && conn.MapPortalGeneration == generation;
    uint type = InstanceEntities.ObjectTypeHash(MapPortalObject.MetadataPath);
    _ = Task.Run(async () =>
    {
        if (firstAfterMs > 0) await Task.Delay(firstAfterMs);
        int n = 0;
        foreach (int index in open.Left)
        {
            if (!Current()) return;
            if (n++ > 0) await Task.Delay(MapPortalGapMs);
            if (!Current()) return;
            uint id = conn.NextObjectId++;
            var (x, y) = MapDevice.PortalCell(device.X, device.Y, index);
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, type,
                    new MapPortalObject(x, y, index, MapPortalType, Open: 1, Rotation: conn.PortalRotationRadians).Blob()),
                $"0x02b9 create map portal 0x{id:x} (place {index}, {n}/{open.PortalsLeft}) to {open.Area.Name} at ({x}, {y})");
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
            lock (conn.MapPortals) conn.MapPortals[id] = (x, y, index);
            await Task.Delay(MapPortalListMs);
            if (!Current()) return;
            await SendMapPortals(remote, stream, ciphers, conn, deviceId, open.Area.Hash16);
        }
    });
    return Task.CompletedTask;
}

static void SendMapPortalsLater(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint deviceId, ushort area, int delayMs) =>
    _ = Task.Delay(delayMs).ContinueWith(async _ => { if (!conn.Closed) await SendMapPortals(remote, stream, ciphers, conn, deviceId, area); });

// Removes and recreates the map portals that stand, at the same places and with the current rotation, then lists them
// again: the client builds a portal's visual from its object, so a rotated portal effect can be tried without reopening
// the device. Returns how many were rebuilt.
static async Task<int> RebuildMapPortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    uint deviceId;
    lock (conn.HideoutDoodads) deviceId = conn.HideoutDoodads.Keys.FirstOrDefault();
    List<(uint Id, int X, int Y, int Index)> old;
    lock (conn.MapPortals)
    {
        old = [.. conn.MapPortals.Select(kv => (kv.Key, kv.Value.X, kv.Value.Y, kv.Value.Index))];
        conn.MapPortals.Clear();
    }
    if (old.Count == 0) return 0;
    uint type = InstanceEntities.ObjectTypeHash(MapPortalObject.MetadataPath);
    foreach (var (id, _, _, _) in old)
        await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove map portal 0x{id:x} (rebuild)");
    // Each portal is listed a moment after its create: 0x008f in the same burst as the 0x2b9 reaches none of them (the
    // client has not built the object yet), which is why they then stay invisible.
    ushort area = MapDevice.OpenOf(CharacterName(conn))?.Area.Hash16 ?? conn.Layout.Hash;
    foreach (var (_, x, y, index) in old)
    {
        uint id = conn.NextObjectId++;
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, type,
                new MapPortalObject(x, y, index, MapPortalType, Open: 1, Rotation: conn.PortalRotationRadians).Blob()),
            $"0x02b9 create map portal 0x{id:x} (place {index}) at ({x}, {y}) rotation {conn.PortalRotationDegrees:0.#}°");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
        lock (conn.MapPortals) conn.MapPortals[id] = (x, y, index);
        await Task.Delay(MapPortalListMs);
        await SendMapPortals(remote, stream, ciphers, conn, deviceId, area);
    }
    return old.Count;
}

// Lists the portals already standing again, so a portal cosmetic takes effect without reopening the map device: 0x008f
// carries the variation of every entry and the client re-applies the visual. False when nothing stands.
static async Task<bool> ResendMapPortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    uint deviceId;
    lock (conn.HideoutDoodads) deviceId = conn.HideoutDoodads.Keys.FirstOrDefault();
    lock (conn.MapPortals)
        if (conn.MapPortals.Count == 0) return false;
    await SendMapPortals(remote, stream, ciphers, conn, deviceId,
        MapDevice.OpenOf(CharacterName(conn))?.Area.Hash16 ?? conn.Layout.Hash);
    return true;
}

// S→C 0x8f: the device's portals and where they lead (21-hideouts.md §5). Without it the client never draws them; a
// portal listed with area 0 (one just taken) closes.
static async Task SendMapPortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint deviceId, ushort area, uint closed = 0)
{
    byte style = conn.MapPortalStyle;
    List<uint> ids;
    lock (conn.MapPortals) ids = [.. conn.MapPortals.Keys.Order()];
    string owner = CharacterName(conn);
    var list = (closed == 0 ? [] : new List<InstanceMapDevice.Portal> { new(closed, 0) })
        .Concat(ids.Select(id => new InstanceMapDevice.Portal(id, area, owner, style, conn.CosmeticPortalVariation))).ToList();
    await Send(remote, stream, ciphers, InstanceMapDevice.Portals(deviceId, list),
        $"0x008f map device 0x{deviceId:x} portals: {ids.Count} open to 0x{area:x4}{(closed == 0 ? "" : $", 0x{closed:x} closed")}");
}

// Created closed (hidden), a map device portal is drawn and targetable once it is opened (Transitionable 0 → 1). Sent in
// the same burst as its creation, the update hung the client (live 2026-10-20), so it goes once the portal is built.
static void OpenMapPortalLater(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, int delayMs) =>
    _ = Task.Delay(delayMs).ContinueWith(async _ =>
    {
        bool still;
        lock (conn.MapPortals) still = conn.MapPortals.ContainsKey(id);
        if (still && !conn.Closed) await Send(remote, stream, ciphers, MapPortalObject.OpenUpdate(id), $"0x02ba map portal 0x{id:x} opens");
    });

static async Task RemoveMapPortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    List<uint> ids;
    Interlocked.Increment(ref conn.MapPortalGeneration);   // stops a sequence still placing them
    lock (conn.MapPortals) { ids = [.. conn.MapPortals.Keys]; conn.MapPortals.Clear(); }
    uint deviceId;
    lock (conn.HideoutDoodads) deviceId = conn.HideoutDoodads.Keys.FirstOrDefault();
    if (ids.Count > 0 && deviceId != 0)
        await Send(remote, stream, ciphers, InstanceMapDevice.Portals(deviceId, [.. ids.Select(id => new InstanceMapDevice.Portal(id, 0))]),
            $"0x008f map device 0x{deviceId:x}: {ids.Count} portal(s) closed");
    foreach (uint id in ids) await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x{InstanceProtocol.RemoveObjectId:x4} remove map portal 0x{id:x}");
}

// A click on a map portal (C→S 0x24): walk there, then the portal is used up and the player goes into the open map, a new
// instance for the first portal and that same instance for the others.
static async Task UseMapPortal(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
    InstanceTransfer.UseSkillOnObject use, (int X, int Y, int Index) at)
{
    int action = Interlocked.Increment(ref conn.ActionCount);
    double now = conn.Clock.Elapsed.TotalSeconds;
    var (here, stop, arrive) = conn.Walk.Approach(at.X, at.Y, AreaTransitionReach, now);
    Console.WriteLine($"[gateway] {remote} <= 0x0024 map portal 0x{use.TargetId:x} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
    if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
    if (conn.ActionCount != action || conn.PendingTransfer is not null) return;
    lock (conn.MapPortals) if (!conn.MapPortals.Remove(use.TargetId)) return;
    string character = CharacterName(conn);
    uint deviceId;
    lock (conn.HideoutDoodads) deviceId = conn.HideoutDoodads.Keys.FirstOrDefault();
    // The portal at this place is used up: it is not there when the player comes back (the others are).
    if (MapDevice.TakePortal(character, at.Index) is not var (map, left))
    {
        Console.WriteLine($"[gateway] {remote} map portal 0x{use.TargetId:x}: no open map");
        await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(use.TargetId), $"0x{InstanceProtocol.RemoveObjectId:x4} remove map portal 0x{use.TargetId:x}");
        return;
    }
    if (deviceId != 0) await SendMapPortals(remote, stream, ciphers, conn, deviceId, map.Area.Hash16, closed: use.TargetId);
    await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(use.TargetId), $"0x{InstanceProtocol.RemoveObjectId:x4} remove map portal 0x{use.TargetId:x} (used)");
    Console.WriteLine($"[gateway] {remote} map portal at place {at.Index} taken: {left} left, into {(map.Instance == 0 ? "a new instance" : $"instance {map.Instance}")} of {map.Area.Name}");
    await ChangeArea(remote, stream, ciphers, conn, new PendingTransfer(map.Area.Hash16, map.Instance, map.Instance == 0)
    {
        Done = r =>
        {
            if (r.Ok) MapDevice.Entered(character, map.Area, r.Instance);
            else MapDevice.Close(character);
        },
    }, $"map portal to {map.Area.Name}");
}

// In a map's area (one a map item opens): a portal back to the hideout next to the arrival point, listed in 0x8f without
// a device once the world is active (21-hideouts.md §5). It is not used up.
static async Task SpawnMapReturnPortal(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, (int X, int Y)? near = null)
{
    if (!MapDevices.Value.IsMapArea(conn.Layout.Hash) || HideoutCatalog.Default is not { } hideout) return;
    var (x, y) = conn.Layout.Near(near ?? (conn.Layout.SpawnX, conn.Layout.SpawnY));
    uint id = conn.NextObjectId++;
    await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(MapPortalObject.MetadataPath),
        new MapPortalObject(x, y, -1, MapPortalType, Open: 1, Rotation: conn.PortalRotationRadians).Blob()),
        $"0x02b9 create the portal back to the {hideout.Name} 0x{id:x} at ({x}, {y})");
    await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
    conn.ReturnPortal = (id, x, y);
    conn.ReturnPortalTo = 0;
    string owner = CharacterName(conn);
    _ = Task.Delay(MapPortalEntryMs).ContinueWith(async _ =>
    {
        if (!conn.Closed)
            await Send(remote, stream, ciphers, InstanceMapDevice.Portals(0,
                [new(id, hideout.AreaHash16, owner, Variation: conn.CosmeticPortalVariation)]),
                $"0x008f the portal 0x{id:x} leads to the {hideout.Name} (0x{hideout.AreaHash16:x4})");
    });
}

// A click on the portal back (C→S 0x24, the map's own or a Portal Scroll's): walk there, then to the hideout next to its
// waypoint, or to the town the scroll's portal leads to (ReturnPortalTo), at its spawn.
static async Task UseMapReturnPortal(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
    InstanceTransfer.UseSkillOnObject use, (int X, int Y) at)
{
    int action = Interlocked.Increment(ref conn.ActionCount);
    double now = conn.Clock.Elapsed.TotalSeconds;
    var (here, stop, arrive) = conn.Walk.Approach(at.X, at.Y, AreaTransitionReach, now);
    Console.WriteLine($"[gateway] {remote} <= 0x0024 portal back 0x{use.TargetId:x} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
    if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
    if (conn.ActionCount != action) return;
    if (conn.ReturnPortalTo is ushort town and not 0)
        await ChangeArea(remote, stream, ciphers, conn, new PendingTransfer(town, 0, false, AtSpawn: true), "portal to town");
    else
        await WarpToHideout(remote, stream, ciphers, conn, "map portal back");
}

// A Portal Scroll used (C→S 0x58) outside towns and hideouts: one scroll is spent and a portal opens where the player
// stands, replacing the one there was (the scroll's earlier one, or a map's own portal back). From a map it leads to the
// hideout (the player came through its map device), anywhere else to the character's last town. In a town or a hideout
// nothing happens and nothing is spent.
static async Task UsePortalScroll(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, byte row, InventoryItem item,
    ItemCatalog.BaseType type)
{
    ushort here = conn.Layout.Hash;
    if (WaypointCatalog.Areas.Find(here) is { IsTown: true } || HideoutCatalog.Hideouts.Any(h => h.AreaHash16 == here))
    {
        Console.WriteLine($"[gateway] {remote} <= 0x0058 use {type.Name}: in a town or hideout, nothing happens");
        return;
    }
    if (conn.Run is not null)
    {
        Console.WriteLine($"[gateway] {remote} <= 0x0058 use {type.Name}: a Survivors run has no portals, nothing happens");
        await conn.Commands!.Reply("Survivors: no portals during a run. Survive!");
        return;
    }
    bool toHideout = MapDevices.Value.IsMapArea(here) && HideoutCatalog.Default is not null;
    ushort town = conn.LastTown != 0 ? conn.LastTown : AreaLayouts.LioneyesWatch;
    var items = ItemsOf(conn);
    if (items.ConsumeOne(row, item.Id) is not { } taken) return;
    uint inventoryId = PlayerInventories.InventoryId(row);
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(inventoryId, items.NextRevision(row), [item.Id], taken.Left is { } left ? [left] : []),
        $"0x00bf inventory {inventoryId}: {type.Name} used" + (taken.Left is { } now ? $", {now.Item.StackSize} left" : ", stack empty"));
    // One portal per player: the old one closes (0x8f with area 0) and goes.
    if (conn.ReturnPortal is { } old)
    {
        await Send(remote, stream, ciphers, InstanceMapDevice.Portals(0, [new(old.Id, 0)]), null);
        await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(old.Id), $"0x{InstanceProtocol.RemoveObjectId:x4} remove the old portal 0x{old.Id:x}");
    }
    var (px, py) = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
    // Beside the player, so the click that opens it is not on the player.
    var (x, y) = conn.Layout.Walkable is { } walkable && walkable(px + 3, py) ? (px + 3, py) : (px, py);
    uint id = conn.NextObjectId++;
    await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(MapPortalObject.MetadataPath),
        new MapPortalObject(x, y, -1, MapPortalType, Open: 1).Blob()), $"0x02b9 create the Portal Scroll's portal 0x{id:x} at ({x}, {y})");
    await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
    conn.ReturnPortal = (id, x, y);
    conn.ReturnPortalTo = toHideout ? (ushort)0 : town;
    ushort leadsTo = toHideout ? HideoutCatalog.Default!.AreaHash16 : town;
    string owner = CharacterName(conn);
    // 0x8f only reaches a portal the client has finished creating (MapPortalListMs, as the device's portals).
    _ = Task.Delay(MapPortalListMs).ContinueWith(async _ =>
    {
        if (!conn.Closed && conn.ReturnPortal?.Id == id)
            await Send(remote, stream, ciphers, InstanceMapDevice.Portals(0, [new(id, leadsTo, owner)]),
                $"0x008f the Portal Scroll's portal 0x{id:x} leads to 0x{leadsTo:x4} ({(toHideout ? "the hideout" : "the last town")})");
    });
}

// A click on the Stash (C→S 0x24): walk there, then S→C 0x7b kind 1 opens the stash panel. Its affinity argument selects
// the tab carrying that affinity bit in the tab list, which only the account's last tab does (AccountStash.Records).
static async Task OpenStash(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceTransfer.UseSkillOnObject use)
{
    if (StashCatalog.In(conn.Layout.Hash) is not { } at) return;
    int action = Interlocked.Increment(ref conn.ActionCount);
    double now = conn.Clock.Elapsed.TotalSeconds;
    var (here, stop, arrive) = conn.Walk.Approach(at.X, at.Y, StashReach, now);
    Console.WriteLine($"[gateway] {remote} <= 0x0024 stash ({use}) from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
    if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
    if (conn.ActionCount != action) return;
    conn.GuildStashOpen = false;
    var stash = StashOf(conn);
    // The panel keeps its tab while this connection lasts; only its first open here is moved to the account's last tab.
    // A second 0x7b while it is open would close it (0x140dc4db0 toggles), so the choice is made now.
    if (!conn.StashOpened)
    {
        conn.StashOpened = true;
        conn.StashForcedTab = stash.LastTab;
        conn.StashForcedUntil = conn.Clock.Elapsed.TotalSeconds + StashForcedWindow;
        await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(InstanceStash.OpenStashUi, conn.StashObject, StashSelectAffinity),
            $"0x007b open the stash on tab {stash.LastTab} \"{stash.NameOf(stash.LastTab)}\" (affinity {StashSelectAffinity})");
    }
    else
        await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(InstanceStash.OpenStashUi, conn.StashObject, InstanceStash.NoAffinity),
            "0x007b open the stash on the panel's own tab");
}

// C→S 0xc7: the panel shows a tab whose contents it does not have (or it just opened on it). The first time per
// connection the tab's inventory goes out (S→C 0xc0, manager 0, row 26), then S→C 0xc8 marks the tab loaded and the panel
// builds its page. The tab asked for is the one on screen: the stash opens on it next time.
static async Task SendStashTab(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, ushort tab, byte kind)
{
    var stash = StashOf(conn);
    if (kind != (stash.IsGuild ? InstanceStash.Guild : InstanceStash.Personal) || tab >= stash.Count)
    {
        Console.WriteLine($"[gateway] {remote} <= 0x00c7 stash {kind} tab {tab}: no such tab");
        return;
    }
    // The panel asks for the tab it shows: as it opens (its tab before the selection) and when a tab without contents
    // is picked. The client sends its queue newest first, so during the first open the forced tab's request comes before
    // the panel's old one; that one is answered but not taken as the player's choice.
    bool forcedOpen = !stash.IsGuild && conn.StashForcedTab >= 0 && conn.Clock.Elapsed.TotalSeconds < conn.StashForcedUntil;
    bool chosen = !forcedOpen || tab == conn.StashForcedTab;
    Console.WriteLine($"[gateway] {remote} <= 0x00c7 stash tab {tab} \"{stash.NameOf(tab)}\" ({stash.TypeOf(tab)})" +
        (chosen ? "" : $" (the panel's tab before it opened on {conn.StashForcedTab}: not kept as the last tab)"));
    if (chosen) stash.SetLastTab(tab);
    uint inventory = stash.InventoryOf(tab);
    if (TabsSentOf(conn, stash).Add(tab))
    {
        var (slot, items) = stash.Inventory(tab);
        await Send(remote, stream, ciphers, InstanceInventories.AddInventory(inventory, slot, stash.IsGuild ? InstanceStash.GuildManager : (byte)0, items),
            $"0x00c0 stash tab {tab} inventory {inventory} ({slot.Width} x {slot.Height}): {items.Count} item(s)");
    }
    await Send(remote, stream, ciphers, InstanceStash.TabLoaded(tab, inventory, stash.IsGuild ? InstanceStash.Guild : InstanceStash.Personal), $"0x00c8 stash tab {tab} loaded");
}

// C→S 0x28 on a stash tab: the item leaves the tab for the (empty) cursor, keeping its id; both saves in one transaction
// (ItemMoves).
static async Task PickUpFromStash(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, int tab, InstanceItems.PickUpItem pick)
{
    var stash = StashOf(conn);
    var items = ItemsOf(conn);
    if (ItemMoves.StashToCursor(stash, items, tab, pick.ItemId) is not { } held)
    {
        Console.WriteLine($"[gateway] {remote} <= 0x0028 pick up {pick} from stash tab {tab}: refused");
        return;
    }
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(pick.InventoryId, stash.NextRevision(tab), [held.Id], [], stash.IsGuild ? InstanceStash.GuildManager : (byte)0),
        $"0x00bf stash tab {tab}: remove item {held.Id}");
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
        items.NextRevision(PlayerInventories.Cursor), [], [held]), $"0x00bf cursor: add item {held.Id} (from the stash)");
}

// C→S 0x2a on a stash tab: the held item goes into the tab (with a stash id, AccountStash.Place) or onto a stack of its
// type there; the one item it was dropped on comes onto the cursor. One transaction (ItemMoves.HeldToStash).
static async Task PlaceInStash(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, int tab, InstanceItems.PlaceItem place)
{
    var stash = StashOf(conn);
    var items = ItemsOf(conn);
    if (ItemMoves.HeldToStash(stash, items, tab, (int)place.X, (int)place.Y, place.A != 0, ShapeOf, out string why) is not { } done)
    {
        Console.WriteLine($"[gateway] {remote} <= 0x002a place {place} in stash tab {tab}: refused ({why})");
        return;
    }
    var (inTab, before) = (done.InTab, done.HeldBefore);
    if (done.Merged)
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(place.InventoryId, stash.NextRevision(tab), [inTab.Id], [inTab], stash.IsGuild ? InstanceStash.GuildManager : (byte)0),
            $"0x00bf stash tab {tab}: stack {inTab.Id} now {inTab.Item.StackSize}");
    else
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(place.InventoryId, stash.NextRevision(tab), done.Displaced is null ? [] : [done.Displaced.Id], [inTab], stash.IsGuild ? InstanceStash.GuildManager : (byte)0),
            $"0x00bf stash tab {tab}: add item {inTab.Id} at ({inTab.X}, {inTab.Y})" + (done.Displaced is null ? "" : $", remove item {done.Displaced.Id}"));
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
        items.NextRevision(PlayerInventories.Cursor), [before.Id], done.Held is null ? [] : [done.Held]),
        "0x00bf cursor: " + (done.Held is null ? $"remove item {before.Id}" : done.Displaced is null ? $"stack {before.Id} now {done.Held.Item.StackSize}" : $"remove item {before.Id}, hold item {done.Held.Id}"));
}

// C→S 0xe0: Ctrl + click on an inventory item while a stash tab is shown: into that tab (ItemMoves.InventoryToStash: a
// stack onto the tab's stacks, a special tab only its own items, a slotted tab into the item's slot), one transaction.
static async Task MoveToStash(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceStash.MoveToTab move)
{
    if (move.Kind != InstanceStash.Personal)
    {
        Console.WriteLine($"[gateway] {remote} <= 0x00e0 move item {move.ItemId} to stash tab {move.Tab} ({move}): refused (not the personal stash)");
        return;
    }
    await CtrlClickToStash(remote, stream, ciphers, conn, move.Tab, move.ItemId, $"0x00e0 ({move})");
}

// C→S 0xe1: Ctrl + click on an inventory item that has an affinity tab (currency …): into the tab the client picked for it
// (StashTabAffinities), whichever tab is shown.
static async Task MoveToAffinityTab(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceStash.MoveToAffinityTab move)
{
    if (move.Tabs is not [var (tab, affinity), ..])
    {
        Console.WriteLine($"[gateway] {remote} <= 0x00e1 move item {move.ItemId} to its affinity tab: refused (no tab named) ({move})");
        return;
    }
    await CtrlClickToStash(remote, stream, ciphers, conn, tab, move.ItemId, $"0x00e1 (affinity {affinity})");
}

static async Task CtrlClickToStash(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, int tab, uint itemId, string why)
{
    var stash = StashOf(conn);
    var items = ItemsOf(conn);
    var moved = ItemMoves.InventoryToStash(stash, items, tab, itemId, ShapeOf, out string refused);
    Console.WriteLine($"[gateway] {remote} <= {why}: move item {itemId} to stash tab {tab}" + (moved is null ? $": refused ({refused})" : ""));
    if (moved is not null) await SendTransfer(remote, stream, ciphers, conn, items, stash, moved, tabFirst: false);
}

// An item out of a stash tab into the main inventory (C→S 0xe3), a stack onto the inventory's stacks first
// (ItemMoves.StashToInventory), one transaction.
static async Task TakeFromStash(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, int tab, uint itemId)
{
    var stash = StashOf(conn);
    var items = ItemsOf(conn);
    if (ItemMoves.StashToInventory(stash, items, tab, itemId, ShapeOf) is not { } moved)
    {
        Console.WriteLine($"[gateway] {remote} stash tab {tab} item {itemId}: refused (not there, unknown type or no room in the inventory)");
        return;
    }
    await SendTransfer(remote, stream, ciphers, conn, items, stash, moved, tabFirst: true);
}

// What a Ctrl + click moved (ItemMoves.Transfer), to the client: one 0xbf for the inventory row, one for the tab (only if
// this connection has the tab's contents: else it gets them whole when it asks, 0xc7).
static async Task SendTransfer(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, PlayerInventories items,
    AccountStash stash, ItemMoves.Transfer t, bool tabFirst)
{
    List<uint> invGone = [.. t.InventoryChanged.Select(i => i.Id)], tabGone = [.. t.TabChanged.Select(i => i.Id)];
    List<InventoryItem> invNow = [.. t.InventoryChanged, .. t.InventoryAdded], tabNow = [.. t.TabChanged, .. t.TabAdded];
    if (t.InventoryItemId != 0) { invGone.Add(t.InventoryItemId); if (t.LeftInInventory is { } l) invNow.Add(l); }
    if (t.TabItemId != 0) { tabGone.Add(t.TabItemId); if (t.LeftInTab is { } l) tabNow.Add(l); }
    async Task Inventory()
    {
        if (invGone.Count + invNow.Count == 0) return;
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(t.Row), items.NextRevision(t.Row), invGone, invNow),
            $"0x00bf inventory {PlayerInventories.InventoryId(t.Row)}: remove [{string.Join(", ", invGone)}], add [{string.Join(", ", invNow.Select(i => $"{i.Id} at ({i.X}, {i.Y}) x{i.Item.StackSize}"))}]");
    }
    async Task Tab()
    {
        if (tabGone.Count + tabNow.Count == 0 || !TabsSentOf(conn, stash).Contains(t.Tab)) return;
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(stash.InventoryOf(t.Tab), stash.NextRevision(t.Tab), tabGone, tabNow, stash.IsGuild ? InstanceStash.GuildManager : (byte)0),
            $"0x00bf stash tab {t.Tab}: remove [{string.Join(", ", tabGone)}], add [{string.Join(", ", tabNow.Select(i => $"{i.Id} at ({i.X}, {i.Y}) x{i.Item.StackSize}"))}]");
    }
    if (tabFirst) { await Tab(); await Inventory(); }
    else { await Inventory(); await Tab(); }
    if (t.InventoryItemId != 0 && PlayerInventories.ShowsOnModel(t.Row)) await SendEquipment(remote, stream, ciphers, conn, items);
}

// C→S 0xd3: the tab settings' affinities, confirmed: saved with the tab, and the tab list again (S→C 0x136), which the
// settings window waits for.
static async Task SetTabAffinities(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceStash.SetAffinities set)
{
    var stash = StashOf(conn);
    uint? now = set.Kind == InstanceStash.Personal ? stash.SetAffinities(set.Tab, set.Changes) : null;
    Console.WriteLine($"[gateway] {remote} <= 0x00d3 stash tab {set.Tab} affinities [{string.Join(", ", set.Changes.Select(c => $"{c.Affinity}={(c.On ? "on" : "off")}"))}]" +
        (now is uint bits ? $": now 0x{bits:x}" : ": refused (no such personal tab)"));
    var tabs = stash.Records(null, TabsSentOf(conn, stash));
    await Send(remote, stream, ciphers, InstanceStash.TabList(tabs), $"0x0136 stash tab list again ({tabs.Count} tabs, after the affinities)");
}

// The player in its class: the Characters.dat row's object type (Metadata/Characters/StrInt/StrInt for the Templar), model
// (AOFile), class byte (IntegerId), attributes and unarmed attack; the name is the character's.
static async Task SendPlayer(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    var (layout, name, classRow, pools) = (conn.Layout, conn.Character, conn.ClassRow, conn.Pools);
    var weapon = Skills.UnarmedFor(classRow);
    var cls = Skills.ClassFor(classRow);
    var stats = CharacterPlayerStats(conn);
    var statList = stats.Select(kv => (kv.Key, kv.Value)).ToList();
    var weaponStats = weapon.Stats(stats).ToList();
    // The new player object starts with exactly these (PlayerStatsWithCleared clears from here).
    lock (conn.Monsters) conn.SentStatKeys = [.. statList.Select(s => s.Item1), .. weaponStats.Select(s => s.Item1)];
    int reservedMana = ReservedMana(conn);
    var player = new PlayerObject(string.IsNullOrEmpty(name) ? "Exile" : name, ClassIndex: (byte)cls.IntegerId, GridX: layout.SpawnX, GridY: layout.SpawnY, Life: pools.Life,
        Mana: (uint)Math.Min(conn.PlayerMana, (uint)Math.Max(0, (int)pools.Mana - reservedMana)), ManaReserved: (uint)reservedMana,
        Experience: conn.Experience,
        AnimatedObject: Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? cls.AnimatedObject,
        Skills: [.. Skills.PlayerDefaults(weaponStats), .. GemSkills(name, [.. weaponStats, .. statList])], Stats: [.. weaponStats, .. statList],
        Equipped: PlayerInventories.For(string.IsNullOrEmpty(name) ? "Exile" : name, Items).Equipped(LookOf),
        QuestFlags: PlayerQuestFlags(conn), Hideout: HideoutCatalog.Default?.Hash16 ?? 0, LeagueQuestFlags: [.. Quests.League.Bits(conn.LeagueQuestFlags)]);
    conn.ClientSkillIds = [.. player.Skills!.Select(s => s.Id)];
    uint type = InstanceEntities.ObjectTypeHash(cls.ObjectType);
    var messages = new List<(string Name, byte[] Bytes)>
    {
        ("0x0022 local player 0x1000", InstanceEntities.SetLocalPlayer(PlayerId)),
        ($"0x02b9 create {player} ({cls.ObjectType}) type=0x{type:x8}", InstanceEntities.CreateObject(PlayerId, type, player.Blob())),
    };
    // The player's inventories (id = row + 1), needed by UI built on entering (flask bar → row 11), with the character's items.
    var items = PlayerInventories.For(string.IsNullOrEmpty(name) ? "Exile" : name, Items);
    if (Items.Error is not null) Console.WriteLine($"[gateway] items: {Items.Error}");
    foreach (var slot in InstanceInventories.PlayerDefaults)
    {
        var inside = items.Items(slot.Row);
        messages.Add(($"0x00c0 inventory {PlayerInventories.InventoryId(slot.Row)} row {slot.Row}" + (inside.Count > 0 ? $" items {inside.Count}" : ""),
            InstanceInventories.AddInventory(PlayerInventories.InventoryId(slot.Row), slot, items: inside)));
    }
    messages.Add(("0x003d atlas passive tree presets 0-2", InstanceEntities.PassiveSlots()));
    var (passives, passivesLabel) = PassivesMessage(conn);
    messages.Add((passivesLabel, passives));
    // Area transitions. Like the player they are queued by the world and created when 0x21 activates it.
    foreach (var (id, transition) in layout.AreaTransitions)
        messages.Add(($"0x02b9 create area transition 0x{id:x} {transition}",
            InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(transition.Type), transition.Blob())));
    if (layout.Waypoint is { } waypoint)
        messages.Add(($"0x02b9 create waypoint 0x{WaypointCatalog.ObjectId:x} {waypoint}",
            InstanceEntities.CreateObject(WaypointCatalog.ObjectId, InstanceEntities.ObjectTypeHash(WaypointObject.MetadataPath), waypoint.Blob())));
    if (Environment.GetEnvironmentVariable("Game_GATEWAY_ENTER") == "1")
    {
        var tabs = PersonalStashOf(conn).Records((int)StashSelectAffinity);
        messages.Add(($"0x0021 enter area, {tabs.Count} stash tab(s), last {PersonalStashOf(conn).LastTab}", InstanceProtocol.EnterArea(tabs)));
        // Put the player in the wake zone so it is drawn and its actions/movement run.
        messages.Add(("0x01a9 player enters wake zone", InstanceProtocol.EnterWakeZone(PlayerId)));
        foreach (var (id, _) in layout.AreaTransitions) messages.Add(($"0x01a9 area transition 0x{id:x} enters wake zone", InstanceProtocol.EnterWakeZone(id)));
        if (layout.Waypoint is not null) messages.Add(($"0x01a9 waypoint 0x{WaypointCatalog.ObjectId:x} enters wake zone", InstanceProtocol.EnterWakeZone(WaypointCatalog.ObjectId)));
        // The attributes once more as a stats update. UI built on entering caches them (0x141e802d0 reads the Stats
        // component's getter); live 2026-10-18 the cache held 0 while the getter returned 14/14/32.
        messages.Add(("0x01b3 class attributes", InstanceEntities.StatsUpdate(PlayerId, [.. statList])));
    }
    foreach (var (label, bytes) in messages) await Send(remote, stream, ciphers, bytes, label);
    // An area change creates a new player object, so the running auras' buffs must be put on it again.
    List<AuraCatalog.Aura> auras;
    lock (conn.Monsters) auras = [.. conn.Auras.Values];
    foreach (var aura in auras)
        await Send(remote, stream, ciphers,
            InstanceEntities.AddBuff(PlayerId, aura.Buff, aura.BuffRow, PlayerId, aura.SkillId),
            $"0x01c1 player buff {aura.Buff}: {aura.Name} restored after the area change");
}

sealed partial class Program
{
    internal const uint PlayerId = 0x1000;
    // The server's tick: 30 per second. Nothing the server times (an attack) is shorter than one tick.
    internal const int TickRate = 30;
    // Game_LEAGUE=hardcore treats every character as hardcore; otherwise the character's league decides (Auth's resolve).
    internal static readonly bool ForceHardcore = Environment.GetEnvironmentVariable("Game_LEAGUE")?.ToLowerInvariant() == "hardcore";
    // S→C 0x55 flags (11-skills.md §8): standard = bit2 (Resurrect at Checkpoint) + bit3 (Resurrect in Town); hardcore =
    // bit0 (a single Exit button, "Exit to the character selection screen", which sends C→S 0x153). Before the character
    // has a town (The Twilight Strand) only bit2, as in the real game.
    internal static byte DeathScreenFlags(ClientState conn) => ForceHardcore || conn.Hardcore ? (byte)0x01 : conn.LastTown == 0 ? (byte)0x04 : (byte)0x0c;
    internal const double TickSeconds = 1.0 / TickRate;
    // PoB's "recently": the last 4 seconds.
    internal const double RecentlySeconds = 4;
    // Shown once per connection when the client moves in lockstep (InstanceProtocol.IsLockstepMovement).
    internal const string LockstepWarning = "Your Network Mode is lockstep, which this server does not support yet: your character stays " +
        "hidden and cannot move. Set Options > Game > Network Mode to Predictive, then log out to the character screen and Play again.";
    // How close (cells, centre to centre) the player must be to use an area transition.
    internal const double AreaTransitionReach = 6;
    // How close the player must be to use a waypoint (Waypoint.ot object_size 6, as an area transition), and how close
    // it activates on its own.
    internal const double WaypointReach = 6, WaypointActivationRange = 25;
    // How close the player walks to an NPC before talking (not measured yet).
    internal const double NpcReach = 8;
    // How close the player walks to the Stash before it opens (Stash.ot object_size 3; not measured).
    internal const double StashReach = 6;
    // The affinity (StashTabAffinityId row 0, "UNUSED0": no item has it) that marks the account's last tab in the tab
    // list, so that S->C 0x7b kind 1 with it opens the stash on that tab.
    internal const uint StashSelectAffinity = 0;
    // How long (s) after the first open's 0x7b the panel's requests belong to that open (the client sends one 0xc7 per
    // 200 ms, at most two then).
    internal const double StashForcedWindow = 1.0;
    // Destination tickets of waypoint travel: that connection's player arrives next to the area's waypoint.
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, byte> WaypointArrivals = new();
    // The height u32 of S→C 0x1ba (+0x28, passed with the move to Positioned vtable +0xd8) and 0x1b7 (→ +0x290): the
    // fixed height, 0x7fffffff = none, follow the terrain. 0 pinned a moving projectile under the ground (live 2026-10-18).
    internal const uint TerrainHeight = 0x7fffffff;
    // How close (cells, centre to centre) the player must be to pick an item up from the ground: WorldItem.ot's
    // InteractionAction interaction_distance_override = 20 grid cells, plus the radii of the item and the player
    // (object_size 2 each). Measured live 2026-10-18 (Positioned +0x294/+0x298): the client walked from (1301, 1250)
    // toward an item at (1278, 1304) and stopped at (1284, 1284), 20.9 cells away, on the first step inside the range.
    internal const double PickupReach = 20 + 2;
    // How close (cells, centre to centre) gold on the ground is picked up by itself (RunPlayerTick): our own number, a
    // little inside a click's PickupReach.
    internal const double GoldAutoLootRange = 15;

    // NPCs.dat with the NPC types' components; loaded with the first town.
    internal static readonly Lazy<NpcCatalog> Npcs = new(() => NpcCatalog.Load());
    // Shared by every connection; loaded on the first /spawn_mob.
    internal static readonly MonsterCatalog Monsters = new();
    // MonsterPacks per area (MonsterPacks.cs); loaded with the first area that has monsters.
    internal static readonly PackCatalog Packs = new();
    // The skills granted to players (the default attack); loaded with the first player.
    internal static readonly SkillCatalog Skills = new();
    // Stat names ↔ client keys (Stats.dat row + 1).
    internal static readonly StatCatalog Stats = new();
    // The shop's portal effects the server can apply itself (/cosmetic): the visual of the player's own portals.
    internal static readonly CosmeticCatalog Cosmetics = new();
    // Per character: the ids of worn items whose requirements are not met (CharacterStatSources).
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HashSet<uint>> InactiveItems = new(StringComparer.OrdinalIgnoreCase);
    // The character panel's Spell Suppression chance stats (the client reads the final value).
    internal static readonly uint[] SuppressionChanceKeys = [.. new[] { "spell_suppression_chance_%",
        "base_spell_suppression_chance_%", "combined_spell_suppression_chance_%" }.Select(n => Stats.Key(n)).OfType<uint>()];
    // Item types (BaseItemTypes + data/item-types.json); loaded with the first player.
    internal static readonly ItemCatalog Items = new();
    // Per character: the skill ids of the socketed gems (GemSkillIds), and the ids gem skills may use (after the default
    // attack, the ids below FirstSkillId + 64 that no basic action uses: SkillCatalog.IsGemSkill; 55 in 3.29.3b).
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, GemSkillIds> GemSkillIdsOf = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly Lazy<ushort[]> GemSkillIdPool = new(() => [.. Skills.SkillIds().Skip(1).Where(Skills.IsGemSkill)]);
    // Maps.dat: which area each map opens (MapDevice.cs); loaded with the first Activate.
    internal static readonly Lazy<MapDevice> MapDevices = new(() => MapDevice.Load("data", row => Items.Row(row)?.Hash));
    // The map device's portals are MultiplexPortals of the player type (1): the map device type (3) is only drawn after an
    // open transition, which hung or crashed the client (21-hideouts.md §5). Game_MAP_PORTAL_TYPE overrides it.
    internal static readonly byte MapPortalType = byte.TryParse(Environment.GetEnvironmentVariable("Game_MAP_PORTAL_TYPE"), out byte t) ? t : MapPortalObject.PlayerPortal;
    // Map portal timing (ms): each portal's 0x8f after its create, the gap between portals, the first one after entering
    // the hideout (Game_MAP_PORTAL_LIST_MS, _GAP_MS, _ENTRY_MS).
    internal static readonly int MapPortalListMs = EnvMs("Game_MAP_PORTAL_LIST_MS", 100), MapPortalGapMs = EnvMs("Game_MAP_PORTAL_GAP_MS", 150),
        MapPortalEntryMs = EnvMs("Game_MAP_PORTAL_ENTRY_MS", 1500);
    static int EnvMs(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out int ms) && ms >= 0 ? ms : fallback;
    // What dying monsters drop (ItemDrops.cs).
    // Magic and rare items (ItemRolls.cs).
    internal static readonly ItemRolls Rolls = new(Items);
    internal static readonly ItemDrops Drops = new(Items, Rolls);
    // Each live area instance's ground items, across connections (InstanceStore.cs). The lifetime is Auth's
    // InstanceRegistry.Lifetime: the Gateway wipes an instance when Auth would.
    internal static readonly InstanceStore Instances = new(TimeProvider.System, new GameServer.Core.World.InstanceRegistry().Lifetime);
    // The passive tree's nodes (PassiveSkills.dat by PassiveSkillGraphId).
    internal static readonly PassiveTree Passives = new();
    // How close (cells) the player must be to a dying monster to gain its experience. Game_XP_RANGE overrides it.
    internal const int GodRegenPerSecond = 10_000;
    // /god regenerates 100% of maximum life per second (more than Righteous Fire's 90% burn).
    internal const int GodLifeRegenPctPerSecond = 100;
    // Character.ot mana_regeneration_rate_per_minute_%: every character regenerates 1.75% of its mana per second.
    internal const int BaseManaRegenPctPerMinute = 105;
    // S→C 0x1b1's pool byte for mana (0 life, 1 mana, 2 energy shield).
    internal const byte ManaPool = 1;
    // When a spell's projectile leaves (the class's spell animation's contact keyframe).
    internal static readonly AnimationContacts Animations = new();
    // BuffDefinitions rows: righteous_fire (on the caster) and righteous_fire_aura (on the enemies it burns).
    internal const ushort RighteousFireBuffRow = 12, RighteousFireAuraBuffRow = 13;
    // The flask buffs' BuffDefinitions rows (flask_effect_life "Healing", flask_effect_mana "Regenerating Mana": invisible,
    // ended by death or a full pool). Game_FLASK_BUFFS=0 recovers without them.
    internal const ushort FlaskEffectLifeBuffRow = 5, FlaskEffectManaBuffRow = 9;
    /// <summary>The quest books that give a passive skill point (BaseItemTypes Name; several Ids, one per quest).</summary>
    internal const string SkillBook = "Book of Skill";
    /// <summary>The quest books that give two passive refund points (BaseItemTypes Name; Acts 1, 6, 9 and 10).</summary>
    internal const string RegretBook = "Book of Regrets";
    internal const string PortalScroll = "Metadata/Items/Currency/CurrencyPortal";
    // Survivors: an open pick pauses the area (Game_SURVIVORS_PAUSE=0 keeps it running).
    internal static readonly bool SurvivorsPauses = Environment.GetEnvironmentVariable("Game_SURVIVORS_PAUSE") != "0";
    /// <summary>The flask recovery's step (RunFlaskRecovery): short enough for one update per point of life.</summary>
    internal const int FlaskStepMs = 20;
    internal static bool FlaskBuffs => Environment.GetEnvironmentVariable("Game_FLASK_BUFFS") != "0";
    // MiscAnimated RighteousFireRing: the big burning circle around a Righteous Fire caster.
    internal const string RighteousFireRingAo = "Metadata/Effects/Spells/ri_fire/rifire.ao";
    // The player's own tick (RunPlayerTick): regeneration and damage over time.
    internal const int PlayerTickMs = 250;
    internal static readonly (string Stat, int Value)[] GodStats =
    [
        ("attack_speed_+%", 1000), ("base_cast_speed_+%", 1000), ("cast_speed_+%", 1000),
        ("life_regeneration_rate_per_minute_%", GodLifeRegenPctPerSecond * 60),
        ("base_mana_regeneration_rate_per_minute", GodRegenPerSecond * 60), ("mana_regeneration_rate_per_minute", GodRegenPerSecond * 60),
    ];

    internal static readonly double ExperienceRange =
        double.TryParse(Environment.GetEnvironmentVariable("Game_XP_RANGE"), System.Globalization.CultureInfo.InvariantCulture, out var xr) ? xr : 150;

    internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ConnectionCiphers, SemaphoreSlim> SendGates = new();

    // The client's walking speed in grid cells per second at 100% (measured live: ~37 = Character.ot Pathfinding
    // base_speed). Game_WALK_SPEED overrides it.
    internal static readonly double WalkSpeed =
        double.TryParse(Environment.GetEnvironmentVariable("Game_WALK_SPEED"), System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 37;
}

/// <summary>
/// Client stat keys (the int passed to the Stats getter 0x1424cfa40), read from client code. They are NOT the rows of
/// our extracted data/tables/Stats.json: the order drifts (key 245 is row 244 there, other keys are further off).
/// </summary>
static class StatKeys
{
    /// <summary>movement_velocity_+%: walk speed = base × (1 + value / 100), at least −100 (0x141e7c0b0).</summary>
    public const uint MovementVelocityPct = 0xf5;
    /// <summary>movement_velocity_cap (0x141e7c0b0 caps the speed with it when positive).</summary>
    public const uint MovementVelocityCap = 0x44f;
    // Main-hand attack stats (client key = Stats.dat row + 1, checked against the keys the client reads in an attack).
    public const uint MainHandMinPhysical = 0x117, MainHandMaxPhysical = 0x118;
    public const uint MainHandMinTotal = 0x29e, MainHandMaxTotal = 0x29f;
    public const uint MainHandAttackDurationMs = 0x142;          // main_hand_base_weapon_attack_duration_ms
    public const uint MainHandTotalAttackDurationMs = 0x221a;    // main_hand_total_base_weapon_attack_duration_ms
    public const uint HundredTimesAttacksPerSecond = 0x2a6;
    public const uint MainHandBaseMaxAttackDistance = 0x146, MainHandMaxAttackDistance = 0x4d3;
    /// <summary>hide_mini_life_bar: non-zero hides the object's overhead life bar (listener 0x140220c30).</summary>
    public const uint HideMiniLifeBar = 0x2a8b;
    /// <summary>skill_is_non_pathing: the action starts where the actor stands instead of walking into range first.</summary>
    public const uint SkillIsNonPathing = 0x4f32;
    // "Increased attack speed" stats that apply to an unarmed melee attack (attack time = base / (1 + sum / 100)).
    public const uint AttackSpeedPct = 0x47, MeleeAttackSpeedPct = 0x49, MainHandAttackSpeedPct = 0x126,
        UnarmedMeleeAttackSpeedPct = 0xb19;
    /// <summary>The raw attack-speed inputs /set_stat can add; the passives' conditional ones are summed into
    /// <see cref="CharacterStats.Pools.AttackSpeedPct"/> by PassiveBonus.</summary>
    public static readonly uint[] AttackSpeedInputs = [AttackSpeedPct, MeleeAttackSpeedPct, MainHandAttackSpeedPct, UnarmedMeleeAttackSpeedPct];
    /// <summary>The character panel's Damage per Second and Damage per Use rows (values are hundredths).</summary>
    public const uint HundredTimesDamagePerSecond = 0x2a7, DisplayHundredTimesDamagePerSkillUse = 0x7fc;
    /// <summary>The main-hand flag the character panel checks before drawing the MH_* damage rows.</summary>
    public const uint SkillUsesMainHand = 0x1e70;
    // "Increased damage" stats that apply to an unarmed melee attack's physical damage (damage × (1 + sum / 100)):
    // damage_+%, physical_damage_+%, melee_damage_+%, melee_physical_damage_+% (strength's 1% per 5).
    public const uint DamagePct = 0x19, PhysicalDamagePct = 0x1b, MeleeDamagePct = 0x558, MeleePhysicalDamagePct = 0x1ed;
}

/// <summary>What the Gateway knows about an area's layout: the terrain grid (for movement estimates), where the player
/// appears and the area transitions. Towns are fixed; other areas are generated from the seed, so their walkability only exists as
/// a dump of the client's walkability grid per (area, seed).</summary>
sealed record AreaLayout(ushort Hash, string Id, int GridW, int GridH, int SpawnX, int SpawnY,
    Func<int, int, bool>? Walkable, (uint Id, AreaTransitionObject Transition)[] AreaTransitions)
{
    /// <summary>The area's waypoint (WaypointCatalog), if it has one and its place is known.</summary>
    public WaypointObject? Waypoint { get; init; }

    public string Describe() =>
        $"area {Id} (0x{Hash:x4}) grid {GridW}x{GridH} spawn ({SpawnX}, {SpawnY}), {(Walkable is null ? "no walkability" : "walkability")}, {AreaTransitions.Length} area transition(s)" +
        (Waypoint is { } w ? $", waypoint at ({w.GridX}, {w.GridY})" : "");

    // One-way entries have no exit back to stand next to: the arrival is the layout's named entrance point
    // (08-area-transitions.md §3). Lioneye's Watch from The Twilight Strand: "entrance1" (disabled in beachtown.dgr).
    static readonly Dictionary<(ushort Area, ushort From), (int X, int Y)> OneWayArrivals = new()
    {
        [(AreaLayouts.LioneyesWatch, 0xf00a /* 1_1_1 The Twilight Strand */)] = (123, 308),
    };

    /// <summary>Where a player entering this area appears: next to the exit leading back to <paramref name="cameFrom"/>
    /// (the area they left through an exit), else the spawn (logins, Resurrect in Town, no exit back).</summary>
    public (int X, int Y) Arrival(ushort? cameFrom)
    {
        if (cameFrom is not { } from) return (SpawnX, SpawnY);
        if (OneWayArrivals.TryGetValue((Hash, from), out var point)) return point;
        foreach (var (_, t) in AreaTransitions)
            if (t.DestinationHash16 == from) return Near((t.GridX, t.GridY));
        return (SpawnX, SpawnY);
    }

    /// <summary>Where "Resurrect at Checkpoint" revives: next to the area's waypoint once the character has activated it
    /// (waypoints are checkpoints), else where the player entered the area (the spawn, set to the arrival on entry).</summary>
    public (int X, int Y) Checkpoint(bool waypointActivated) =>
        waypointActivated && Waypoint is { } w ? Near((w.GridX, w.GridY)) : (SpawnX, SpawnY);

    /// <summary>A walkable cell a few cells from (x, y), nearest first; (x + 8, y) without a walk map.</summary>
    public (int X, int Y) Near((int X, int Y) at)
    {
        if (Walkable is not { } walkable) return (at.X + 8, at.Y);
        for (int r = 6; r <= 20; r++)
            for (int dy = -r; dy <= r; dy++)
                foreach (int dx in new[] { -r, r })
                {
                    if (walkable(at.X + dx, at.Y + dy)) return (at.X + dx, at.Y + dy);
                    if (walkable(at.X + dy, at.Y + dx)) return (at.X + dy, at.Y + dx);
                }
        return at;
    }
}

static class AreaLayouts
{
    public const ushort LioneyesWatch = 0x235c, TheCoast = 0x7ad2;

    /// <summary>The area each transfer's destination ticket left through an exit; the new connection arrives next to the
    /// exit back (<see cref="AreaLayout.Arrival"/>). In memory: a Gateway restart drops the client to the login anyway.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, ushort> CameFrom = new();

    // Lioneye's Watch is one room, Metadata/Terrain/Act1/Town/Rooms/beach_town_unique_normal.arm (18 x 30 tiles), placed
    // R270 by Graphs/beachtown.dgr ("default" "entrance2" "entrance1disabled"). Spawn (197, 261): the walkable cell farthest
    // from any edge (Agents/docs/protocol/03 §7).
    // Area transitions come from AreaTransitionCatalog (the atlas, or the client's generated layout captured by
    // scripts/walkmaps). Without the atlas the town still gets its gate to The Coast: the town tile's .tmd record
    // beachencampment_v01_01_area_transition2 as the client places it (grid (388, 231), 3π/2, z −351.04) with its
    // virtual .ao (the arch highlighted on hover). Agents/docs/protocol/08-area-transitions.md §5.
    static readonly (uint Id, AreaTransitionObject Transition)[] TownFallback =
        [(AreaTransitionCatalog.FirstId, new AreaTransitionObject(GridX: 388, GridY: 231, DestinationHash16: TheCoast,
            Model: "Metadata/Terrain/Beach/Town_v2/beachencampment_v01_01_area_transition2.ao",
            Rotation: 3 * MathF.PI / 2, Height: -351.04f))];

    static readonly Dictionary<ushort, string> Ids = new() { [LioneyesWatch] = "1_1_town", [TheCoast] = "1_1_2" };

    const string Folder = "data/walkability";

    public static AreaLayout For(ushort hash, uint seed)
    {
        var hideout = HideoutCatalog.ByArea(hash);
        string id = Ids.GetValueOrDefault(hash) ?? AreaTransitionCatalog.IdOf(hash) ?? hideout?.AreaId ?? $"0x{hash:x4}";
        // A hideout's waypoint is one of its decoration's doodads (21-hideouts.md §2), not a tile object.
        return WithoutWaypoint(hash, id, seed) with { Waypoint = hideout is null ? WaypointCatalog.For(hash, id, seed) : HideoutCatalog.Waypoint(hideout) };
    }

    static AreaLayout WithoutWaypoint(ushort hash, string id, uint seed)
    {
        byte[]? cells = LoadCells(id, seed, out int w, out int h);
        var notes = new List<string>();
        var transitions = AreaTransitionCatalog.For(hash, id, seed, notes);
        foreach (var n in notes) Console.WriteLine($"[gateway] area {id} seed {seed}: {n}");
        if (hash == LioneyesWatch)
        {
            if (transitions.Length == 0) transitions = TownFallback;
            // Older dumps were saved as data/walkability/1_1_town.bin without a .json.
            if (cells is null && File.Exists($"{Folder}/1_1_town.bin")) { cells = File.ReadAllBytes($"{Folder}/1_1_town.bin"); (w, h) = (690, 414); }
            bool fits = cells is not null && cells.Length == 690 * 414 && (w, h) == (690, 414);
            return new AreaLayout(hash, id, 690, 414, 197, 261, fits ? Walkable(cells!, w, h) : null, transitions);
        }
        if (cells is not null)
        {
            // The area's start point (a room's "default", e.g. The Twilight Strand's beach), else the deepest cell.
            var start = AreaPopulation.LoadPoints(id, seed, []).FirstOrDefault(p => p.Name == "default");
            var (sx, sy) = start is null ? DeepestCell(cells, w, h) : (start.X, start.Y);
            return new AreaLayout(hash, id, w, h, sx, sy, Walkable(cells, w, h), transitions);
        }
        // No walk map yet: a position inside any grid; the client may place the player on unwalkable ground.
        return new AreaLayout(hash, id, 0, 0, 197, 261, null, transitions);
    }

    /// <summary>Tells scripts/walkmaps which area the next generated grid belongs to (data/walkability/current.json).</summary>
    public static void Announce(AreaLayout layout, uint seed)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string json = JsonSerializer.Serialize(new
            {
                id = layout.Id, seed, hash = layout.Hash, written = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                nonce = Guid.NewGuid().ToString("N"),
            });
            SaveFiles.Write($"{Folder}/current.json", json);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>When the client reports the area generated (0x7d): the walk map scripts/walkmaps saved meanwhile, if
    /// any. Waits up to 2 s for the file.</summary>
    public static async Task<AreaLayout?> WaitFor(AreaLayout layout, uint seed)
    {
        string bin = $"{Folder}/{layout.Id}_seed{seed}.bin";
        for (int i = 0; i < 20; i++)
        {
            if (File.Exists(bin) && File.Exists(Path.ChangeExtension(bin, ".json")))
            {
                var better = For(layout.Hash, seed);
                return better.Walkable is null ? null : better;
            }
            await Task.Delay(100);
        }
        return null;
    }

    /// <summary>When the client reports the area generated (0x7d) and the layout has no area transitions yet: the ones
    /// scripts/walkmaps captured meanwhile, if any. Waits up to 1 s for the file.</summary>
    public static async Task<AreaLayout?> WaitForTransitions(AreaLayout layout, uint seed)
    {
        for (int i = 0; i < 10; i++)
        {
            if (AreaTransitionCatalog.Captured(layout.Id, seed))
            {
                await Task.Delay(200);   // the capture is rewritten per object; let the last ones land
                var better = For(layout.Hash, seed);
                return better.AreaTransitions.Length > 0 || better.Waypoint is not null ? better : null;
            }
            await Task.Delay(100);
        }
        return null;
    }

    // data/walkability/<id>_seed<seed>.bin (1 byte per cell) + .json {"w":..,"h":..}, written by scripts/walkmaps.
    static byte[]? LoadCells(string id, uint seed, out int w, out int h)
    {
        (w, h) = (0, 0);
        string bin = $"{Folder}/{id}_seed{seed}.bin", meta = Path.ChangeExtension(bin, ".json");
        if (!File.Exists(bin) || !File.Exists(meta)) return null;
        try
        {
            var m = JsonDocument.Parse(File.ReadAllText(meta)).RootElement;
            (w, h) = (m.GetProperty("w").GetInt32(), m.GetProperty("h").GetInt32());
            byte[] cells = File.ReadAllBytes(bin);
            return cells.Length == w * h ? cells : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    // 1 byte per cell; the client walks on values >= 2 (Agents/docs/protocol/07 §5).
    static Func<int, int, bool> Walkable(byte[] cells, int w, int h) =>
        (x, y) => x >= 0 && y >= 0 && x < w && y < h && cells[y * w + x] >= 2;

    /// <summary>The walkable cell farthest from any blocked cell (multi-source BFS from blocked cells).</summary>
    static (int X, int Y) DeepestCell(byte[] cells, int w, int h)
    {
        var dist = new int[w * h];
        var q = new Queue<int>();
        for (int i = 0; i < cells.Length; i++)
            if (cells[i] < 2) { dist[i] = 0; q.Enqueue(i); } else dist[i] = int.MaxValue;
        while (q.Count > 0)
        {
            int i = q.Dequeue(), x = i % w, y = i / w;
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                int j = ny * w + nx;
                if (dist[j] > dist[i] + 1) { dist[j] = dist[i] + 1; q.Enqueue(j); }
            }
        }
        int best = Array.IndexOf(dist, dist.Where(d => d != int.MaxValue).Max());
        return (best % w, best / w);
    }

}

/// <param name="AtSpawn">Arrive at the destination's spawn (Resurrect in Town) instead of next to the exit back.</param>
/// <param name="AtWaypoint">Waypoint travel: arrive next to the destination's waypoint.</param>
sealed record PendingTransfer(ushort Area, ulong Instance, bool New, bool AtSpawn = false, bool AtWaypoint = false)
{
    /// <summary>Called with Auth's answer to the transfer (a map portal records the map's new instance).</summary>
    public Action<ControlResponse>? Done { get; init; }
}

// A monster this connection spawned: label for /mobs, the server's estimate of its walk, and its AI state.
// A monster's attack: its skill id, attack time, reach (cells, centre to centre) and damage per hit.
sealed record MonsterAttack(ushort SkillId, double Seconds, double Range, DamagePacket Damage)
{
    /// <summary>The attack's final stats (the client derives none, 11-skills.md §4): the swing time, reach and damage.
    /// These also go on the monster's Stats component.</summary>
    public IReadOnlyList<(uint, int)> Stats()
    {
        int ms = (int)Math.Round(Seconds * 1000);
        var physical = Damage[DamageType.Physical];
        int min = (int)Math.Floor(physical.Min), max = (int)Math.Floor(physical.Max);
        return
        [
            (StatKeys.MainHandMinPhysical, min), (StatKeys.MainHandMaxPhysical, max),
            (StatKeys.MainHandMinTotal, min), (StatKeys.MainHandMaxTotal, max),
            (StatKeys.MainHandAttackDurationMs, ms), (StatKeys.MainHandTotalAttackDurationMs, ms),
            (StatKeys.HundredTimesAttacksPerSecond, (int)Math.Round(100 / Seconds)),
            (StatKeys.MainHandBaseMaxAttackDistance, (int)Range), (StatKeys.MainHandMaxAttackDistance, (int)Range),
        ];
    }

    /// <summary>The skill's stats: <see cref="Stats"/> plus skill_is_non_pathing. The server walks the monster into range
    /// itself; without it the Melee action's begin (0x141938830) sends the attack through the client's own pathing step,
    /// which drops a server-driven monster's attack (no swing). Only on the skill: on the monster's Stats component it
    /// also stops its Move action from pathing (live 2026-10-17: the monsters stood still).</summary>
    public IReadOnlyList<(uint, int)> SkillStats() => [.. Stats(), (StatKeys.SkillIsNonPathing, 1)];
}

sealed record SpawnedNpc(NpcCatalog.Npc Npc, int X, int Y);

// An open sell window: the NPC, the two offer inventories (manager 1), the offered items and the NPC's offer.
sealed class NpcSale(SpawnedNpc npc, uint mine, uint theirs)
{
    public SpawnedNpc Npc { get; } = npc;
    public uint Mine { get; } = mine;
    public uint Theirs { get; } = theirs;
    // The offered items stay in the character's inventory cells until the sale ends (PlayerInventories.Offer).
    public List<InventoryItem> Reward = [];
    public uint MineRevision, TheirsRevision;
}

// An open shop tab: the NPC, its inventory id (manager 1), the items left by id and the tab's 0xbf revision.
sealed class OpenShop(SpawnedNpc npc, uint tab, Dictionary<uint, InventoryItem> items)
{
    public SpawnedNpc Npc { get; } = npc;
    public uint Tab { get; } = tab;
    public Dictionary<uint, InventoryItem> Items { get; } = items;
    public uint Revision;
}

sealed class SpawnedMonster(string label, WalkEstimator walk, int aggroRange, int stopRange, uint maxLife)
{
    public MonsterAttack? Attack { get; init; }
    public bool Hostile { get; init; } = true;
    // What it is, for the experience and items its death gives (null for monsters made without a variety).
    public MonsterCatalog.Variety? Variety { get; init; }
    public byte Level { get; init; } = 1;
    public MonsterRarity Rarity { get; init; }
    // A Survivors wave monster: it hunts the player wherever it is and never gives up (RunMonsterAi).
    public bool Hunts { get; init; }
    // Damage over time not yet applied (fractions of a point between ticks).
    public double Burn;
    // The impales the player's attacks have left on it (Impale.cs); cleared when it dies.
    public readonly ImpaleState Impale = new();
    // While a swing runs (Clock seconds) the monster neither chases nor swings again.
    public double BusyUntil;
    public string Label => label;
    public WalkEstimator Walk { get; set; } = walk;
    public int AggroRange => aggroRange;
    public int StopRange => stopRange;
    public uint MaxLife { get; } = maxLife;
    public uint Life = maxLife;
    public bool Aggro, Dead;
    public (int X, int Y)? LastTarget;
}

// A running Righteous Fire: the player's buff instance, the skill, its damage (percent of maximum life per minute, to
// enemies and to the player), its radius in cells, and the aura buff instance on each monster inside it.
sealed record RighteousFireState(ushort Buff, ushort SelfAura, uint Ring, ushort Skill, int DamagePctPerMinute, int BurnPctPerMinute, int Radius)
{
    public readonly Dictionary<uint, ushort> Auras = [];
}

// The last instance list shown (0xae); 0xaf picks a row of it.
sealed record ChooserList(ushort Area, IReadOnlyList<ulong> Ids, bool AtWaypoint = false);

sealed class ClientState(AreaLayout layout, uint ticket, uint seed)
{
    public AreaLayout Layout { get; private set; } = layout;
    public uint Ticket => ticket;
    public uint Seed => seed;
    public string? Character { get; init; }
    /// <summary>The account the character belongs to (Auth's resolve): its stash (AccountStash) is shared by all of them.</summary>
    public string? Account { get; init; }
    public string? League { get; init; }
    public bool Hardcore { get; init; }
    /// <summary>The character's last town (HASH16, from Auth's resolve): Resurrect in Town goes there. 0 before the
    /// character has entered a town (the death screen then has no Resurrect in Town).</summary>
    public ushort LastTown { get; init; }
    /// <summary>The area the player left through an exit to come here, or null (login, Resurrect in Town).</summary>
    public ushort? CameFrom { get; init; }
    // The character's class: its Characters.dat row (the Witch: 1).
    public int ClassRow { get; init; } = SkillCatalog.WitchRow;
    public readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    // The Escape menu's pause (C→S 0x1d4) stops the Clock: walks, cooldowns and flask effects stand still meanwhile.
    public bool Paused => !Clock.IsRunning;
    // Server-side estimate of the client-predicted walk (reported in each 0x1ab), kept on walkable cells.
    public WalkEstimator Walk { get; private set; } = NewWalk(layout);

    /// <summary>Switches to a layout with a walk map (before the player exists).</summary>
    public void Use(AreaLayout better)
    {
        Layout = better;
        Walk = NewWalk(better);
        ApplyMovementSpeed();
    }

    /// <summary>Applies the passive movement speed to the server's walk estimate; /set_speed is a dev override.</summary>
    public void ApplyMovementSpeed() =>
        Walk.SetSpeed(Math.Max(1, Program.WalkSpeed * (100 + Pools.MovementVelocityPct) / 100), Clock.Elapsed.TotalSeconds);

    /// <summary>Replaces an area transition of this connection's layout (dev changes; the walk estimate keeps going).</summary>
    public void ReplaceAreaTransition(uint id, AreaTransitionObject transition) =>
        Layout = Layout with { AreaTransitions = [.. Layout.AreaTransitions.Select(e => e.Id == id ? (e.Id, transition) : e)] };

    private static WalkEstimator NewWalk(AreaLayout l) =>
        new(l.SpawnX, l.SpawnY, Program.WalkSpeed, l.Walkable, l.GridW > 0 ? l.GridW : 4096, l.GridH > 0 ? l.GridH : 4096);
    public bool PlayerSent;
    public byte[] Carry = [];
    /// <summary>The portal cosmetic: the MicrotransactionPortalVariations row every portal is listed with (0x008f +0x26),
    /// 0 for the base effect. Applied to portals already standing as well as to new ones.</summary>
    public ushort CosmeticPortalVariation;
    /// <summary>How map portals are turned around the vertical axis (degrees; Positioned rotation). Dev: /portal_rotation,
    /// which rebuilds the portals that already stand.</summary>
    public float PortalRotationDegrees;
    public float PortalRotationRadians => PortalRotationDegrees * MathF.PI / 180f;
    public long Ticks;
    public volatile PendingTransfer? PendingTransfer;
    public ChooserList? Chooser;
    public ChatCommands? Commands;
    // 0x1c's u32 is an HNC carrier and must decrypt to idle zero bytes, or the client throws "HNC Protocol Violated".
    public HncSender? Hnc;
    public InstanceKeyring Keyring = null!;
    // Server objects created after entering (monsters); the player is 0x1000 and area transitions start at 0x2000.
    public uint NextObjectId = 0x3000;
    // The player's attacks: the next time one may start (Clock seconds). The client repeats 0x24 while the button is held.
    public double NextAttackAt;
    // Counts the player's actions; an attack's hit lands only if no other action started since (moving cancels it).
    public int ActionCount;
    // The stats the server has set on the player after creating it (/set_stat, /set_speed): what the client computes
    // with, so the server can follow it (attack time).
    public readonly Dictionary<uint, int> PlayerStats = [];
    // Running auras by skill id, with their buff instances and reservation. Guarded by the Monsters lock.
    public readonly Dictionary<ushort, AuraCatalog.Aura> Auras = [];
    // When each skill with a cooldown (GrantedEffects row) may be used again, in Clock seconds. Guarded by the Monsters lock.
    public readonly Dictionary<int, double> SkillReadyAt = [];
    // The skill ids the client has on this connection's player (SendPlayer, SendSkills): the 0x40 skill list only adds,
    // so the ones that leave are removed by id.
    public HashSet<ushort> ClientSkillIds = [];
    // The character's level and the pools that follow from it and the class (CharacterStats).
    public CharacterStats.Pools Pools { get; set; } = new(1, 50, 40, 2, 0);
    // The player stat keys the client holds (PlayerStatsWithCleared). Guarded by the Monsters lock.
    public HashSet<uint> SentStatKeys = [];
    // Dev-only forced lucky block, on top of the stats (Svalinn's "Chance to Block is Lucky").
    public bool ForceAttackBlockLucky, ForceSpellBlockLucky;
    // When the player last hit an enemy (attack or spell hit; Clock seconds), for "if you've hit an enemy recently".
    // Guarded by the Monsters lock.
    public double LastEnemyHitAt = double.NegativeInfinity;
    // PoE evasion entropy (0..99): every attack adds the attacker's hit chance, and 100+ means a hit. Shared by all
    // attackers and reset after 3.33 s without an attack. Guarded by the Monsters lock.
    public double EvasionEntropy = -1;
    public double LastEvasionAt = double.NegativeInfinity;
    // Dev-only forced lucky/unlucky Spell Suppression rolls, on top of the passive stat.
    public bool ForceSpellSuppressionLucky, ForceSpellSuppressionUnlucky;
    // The character's experience (the Player component's; the level follows from it) and its allocated passives
    // (PassiveSkillGraphId), both kept in its save (CharacterSaves). Changed under the Monsters lock.
    public uint Experience;
    public List<ushort> Passives = [];
    // Selected mastery effects: mastery node (PassiveSkillGraphId) -> effect HASH16. Saved with the character.
    public Dictionary<ushort, ushort> MasteryEffects = [];
    // Extra passive points from /add_passive_points (the 0x33 extra_skill_points field), saved with the character.
    public ushort ExtraPassivePoints;
    // Refund points (0x33's first u16; Orbs of Regret), saved with the character; /add_refund_points grants them for now.
    public ushort RefundPoints;
    // /god (GodStats) and the running Righteous Fire, if any; buff instance ids (0x1c1) given out on this connection.
    public bool God;
    public RighteousFireState? RighteousFire;
    /// <summary>The flasks recovering life and mana (C→S 0x70), applied by the player tick.</summary>
    public Flasks.Effects FlaskEffects { get; } = new();
    public ushort NextBuff = 1;
    public uint PlayerMaxLife => Pools.Life;
    // The player's life as the server keeps it (PlayerObject's Life, full on creation); 0 = dead.
    public uint PlayerLife;
    // The player's mana as the server keeps it: spent by gem skills (mana_cost), regenerated by RunPlayerTick.
    public uint PlayerMana;
    // (MonsterVarieties row, level) pairs already registered with 0x20 on this connection.
    public readonly HashSet<(int Row, byte Level)> MonsterTypes = [];
    // The open sell window (Sell Items): what the player offered and what the NPC offers back.
    public NpcSale? Sale;
    // The shop tab opened last (Purchase Items), with what is left in it.
    public OpenShop? Shop;
    // The town's Stash object (0 = none here), and the stash tabs whose inventory this connection has sent (0xc0 + 0xc8).
    public uint StashObject;
    public readonly HashSet<int> StashTabsSent = [];
    /// <summary>The guild stash: its object in the hideout, its tabs this connection has the contents of, and whether its
    /// panel is the one open (then the stash messages act on it; StashOf).</summary>
    public uint GuildStashObject;
    public (int X, int Y) GuildStashAt;
    public readonly HashSet<int> GuildStashTabsSent = [];
    public bool GuildStashOpen;
    // Whether this connection opened the stash already (only the first open selects the account's last tab), and while
    // that first open runs (until StashForcedUntil, Clock seconds) the tab it selects: the panel's own request for the
    // tab it was on is then not the player's choice.
    public bool StashOpened;
    public int StashForcedTab = -1;
    public double StashForcedUntil;
    // The NPC whose dialogue window is open (0x84 sent, 0x85 not yet), 0 = none.
    public volatile uint TalkingTo;
    // Whether the open dialogue shows an NPCTalk row's talk (0x84 with its index) rather than the option list.
    public volatile bool InNpcTalk;
    // NPCs created on this connection by object id (town NPCs, /npc).
    public readonly Dictionary<uint, SpawnedNpc> Npcs = [];
    /// <summary>The character's quest flags (QuestFlags.dat Ids), saved in data/saves/.</summary>
    public HashSet<string> QuestFlags = [];

    /// <summary>The character's league quest flags (client ids, Quests.League); not saved: every character has the same.</summary>
    public HashSet<int> LeagueQuestFlags = [.. Quests.League.EveryCharacter];

    /// <summary>The hideout doodads created in this area (the map device), by object id.</summary>
    public readonly Dictionary<uint, HideoutCatalog.Doodad> HideoutDoodads = [];

    /// <summary>The open map's portals created in this area, by object id: where each stands.</summary>
    public readonly Dictionary<uint, (int X, int Y, int Index)> MapPortals = [];

    /// <summary>Counts the map portal sequences started (SpawnMapPortals); a newer one stops the older.</summary>
    public int MapPortalGeneration;

    /// <summary>In a map's area: the portal back to the hideout (object id and cell), or null.</summary>
    public (uint Id, int X, int Y)? ReturnPortal;
    /// <summary>Where ReturnPortal leads: 0 the hideout (a map's own portal, or a Portal Scroll's in a map), else a town's
    /// WorldAreas HASH16 (a Portal Scroll's anywhere else).</summary>
    public ushort ReturnPortalTo;

    /// <summary>The 0x8f entries' model byte (/map_portal sets it for experiments).</summary>
    public byte MapPortalStyle;
    /// <summary>The Survivors run in this map (Survivors.cs), or null.</summary>
    public Survivors.Run? Run;
    // Spawned monsters by object id, in spawn order (for /mobs and /despawn).
    public readonly SortedDictionary<uint, SpawnedMonster> Monsters = [];
    // Items on the ground by object id (dropped with C→S 0x29).
    /// <summary>The area instance's state shared across connections: its ground items.</summary>
    public InstanceState Instance { get; init; } = null!;
    /// <summary>This connection placed or restored the instance's monsters, so it saves them when it closes.</summary>
    public bool MonstersPlaced;
    public volatile bool AiEnabled = true, Closed;
    // Whether the lockstep warning was shown on this connection.
    public bool WarnedLockstep;
    // The character's activated waypoints (WorldAreas Ids, kept in its save) and whether this connection was reached
    // by waypoint travel (the player then appears next to the area's waypoint).
    public readonly HashSet<string> Waypoints = [];
    // The areas the character has entered (WorldAreas Ids, kept in its save): their visit quest flags.
    public readonly HashSet<string> Visited = [];
    public bool ArriveAtWaypoint;

    /// <summary>A walk estimate for a server object on this area's walk map.</summary>
    public WalkEstimator NewWalk(int x, int y, double speed) =>
        new(x, y, Math.Max(1, speed), Layout.Walkable, Layout.GridW > 0 ? Layout.GridW : 4096, Layout.GridH > 0 ? Layout.GridH : 4096);

    public (int X, int Y) SpawnNear((int X, int Y) at) => Layout.Near(at);
}
