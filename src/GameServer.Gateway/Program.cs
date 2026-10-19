using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using WalkEstimator = GameServer.Core.World.WalkEstimator;
using Progression = GameServer.Core.World.Progression;

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

// Local play only by default; Game_BIND=0.0.0.0 to accept other machines (then also set Game_GATEWAY_HOST).
var bind = IPAddress.Parse(Environment.GetEnvironmentVariable("Game_BIND") ?? "127.0.0.1");
var listener = new TcpListener(bind, port);
listener.Start();
Console.WriteLine($"[gateway] listening on {bind}:{port}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

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
Console.WriteLine("[gateway] stopped");
return 0;

static string? FindUp(string relative)
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
    return null;
}

static async Task HandleAsync(TcpClient client, InstanceKeyring keyring)
{
    string remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
    Console.WriteLine($"[gateway] CONNECT {remote} at {DateTime.Now:HH:mm:ss.fff}");
    long total = 0;
    uint ticket = 0;
    InstanceState? place = null;
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
            if (!resolved.Ok) Console.WriteLine($"[gateway] {remote} resolve failed ({resolved.Error}); using Lioneye's Watch");
            ushort areaHash = resolved.Ok ? resolved.Area : AreaLayouts.LioneyesWatch;
            uint seed = resolved.Ok ? resolved.Seed : 1;
            ulong instanceId = resolved.Ok ? resolved.Instance : 1;
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
            var conn = new ClientState(layout, ticket, seed) { Character = resolved.Character, League = resolved.League, Hardcore = resolved.Hardcore, CameFrom = AreaLayouts.CameFrom.TryRemove(ticket, out var from) ? from : null, Instance = Instances.Enter(instanceId, resolved.Created), LastTown = resolved.Ok ? resolved.LastTown : AreaLayouts.LioneyesWatch, ClassRow = Skills.RowOfClass(resolved.Class), Pools = CharacterStats.For(Skills.ClassFor(Skills.RowOfClass(resolved.Class)), resolved.Level), Hnc = new HncSender(keyring.Key(opening.Ticket)), Keyring = keyring };
            place = conn.Instance;
            conn.Commands = Commands(remote, stream, ciphers, conn);
            PlayerInventories.SetClass(CharacterName(conn), conn.ClassRow);   // before its items are first used (start kit)
            LoadProgress(conn, resolved.Level);
            conn.PlayerLife = conn.PlayerMaxLife;
            conn.PlayerMana = conn.Pools.Mana;
            Console.WriteLine($"[gateway] {remote} {CharacterName(conn)}: {Skills.ClassFor(conn.ClassRow).ObjectType} (Characters row {conn.ClassRow}), level {conn.Pools.Level} (experience {conn.Experience}, {conn.Passives.Count} passive(s), life {conn.Pools.Life}, mana {conn.Pools.Mana}), league {conn.League ?? "?"}");
            byte[] rest = [.. pending.Skip(InstanceProtocol.OpeningLength)];
            if (rest.Length > 0) await OnClientData(remote, stream, ciphers, rest, conn);
            // Closed early by C→S 0x153 (exit to character selection): leaving the loop closes the connection.
            while (!conn.Closed)
            {
                int n = await stream.ReadAsync(buffer);
                if (n == 0) break;
                total += n;
                await OnClientData(remote, stream, ciphers, buffer.AsSpan(0, n).ToArray(), conn);
            }
            conn.Closed = true;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[gateway] {remote} error: {ex.GetType().Name}: {ex.Message}");
    }
    if (place is not null) Instances.Leave(place);
    if (ticket != 0) await InstanceControl.CallAsync(new ControlRequest { Op = "leave", Ticket = ticket });
    Console.WriteLine($"[gateway] DISCONNECT {remote} total={total}");
}

// Decrypts client bytes and handles the messages we can frame (InstanceProtocol.SplitClientMessages):
// 0x7d (area generated) → the local player id (0x22), the player object (0x2b9), inventories, passive slots, the area transitions,
// and with Game_GATEWAY_ENTER=1 enter area (0x21) + wake zone; 0x1b (tick) → 0x1c (ack); 0x25 (move) → 0x1ab echo;
// 0x24 on an area transition → change area (0x2b2, then 0x27 with the new instance) or, with Ctrl, the instance chooser (0xae).
// Anything else is logged in hex: those are the next messages to map.
static async Task OnClientData(string remote, NetworkStream stream, ConnectionCiphers ciphers, byte[] data, ClientState conn)
{
    ciphers.Receive.Process(data);
    byte[] all = [.. conn.Carry, .. data];
    var messages = InstanceProtocol.SplitClientMessages(all, out conn.Carry, out byte[] unknown);
    foreach (var (id, bytes) in messages)
    {
        if (id == InstanceProtocol.TickId)
        {
            if (conn.Ticks++ % 50 == 0) Console.WriteLine($"[gateway] {remote} <= 0x001b tick #{conn.Ticks}: {Convert.ToHexString(bytes)} (answered with 0x001c)");
            await Send(remote, stream, ciphers, InstanceProtocol.Pong(sideChannel: conn.Hnc!.NextIdleU32()), null);
        }
        else if (id == InstanceProtocol.UseSkillAtLocationId)
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
            Console.WriteLine($"[gateway] {remote} <= 0x0034 allocate passive {node} ({Passives.Find(node)?.Name ?? "unknown"}), u16 {u2}, u8 {u6}");
            await ChangePassives(remote, stream, ciphers, conn, [node], allocate: true, "Passive Skill Tree");
        }
        else if (id == InstanceEntities.PanelToggleId)
            Console.WriteLine($"[gateway] {remote} <= 0x0123 panel {(bytes[2] != 0 ? "opened" : "closed")}");
        else if (id == InstanceItems.DropItemId)
            await DropItem(remote, stream, ciphers, conn);
        else if (id == InstanceItems.CommandId)
        {
            var command = InstanceItems.ReadCommand(bytes);
            if (command.Id == InstanceItems.DestroyCursorItemCommand) await DestroyHeldItem(remote, stream, ciphers, conn);
            else Console.WriteLine($"[gateway] {remote} <= 0x0016 command {command} (not handled)");
        }
        else if (id == InstanceItems.UnsocketGemId)
            await UnsocketGem(remote, stream, ciphers, conn, InstanceItems.ReadUnsocketGem(bytes));
        else if (id == InstanceItems.SocketGemId)
            await SocketGem(remote, stream, ciphers, conn, InstanceItems.ReadSocketGem(bytes));
        else if (id == InstanceItems.PlaceItemId)
            await PlaceItem(remote, stream, ciphers, conn, InstanceItems.ReadPlaceItem(bytes));
        else if (id is InstanceItems.InventoryRequestId or InstanceItems.ItemActionId)
            Console.WriteLine($"[gateway] {remote} <= 0x{id:x4} (item message, not handled yet): {Convert.ToHexString(bytes)}");
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
            await ChangeArea(remote, stream, ciphers, conn, new PendingTransfer(chooser.Area, instance, isNew), $"chooser row {row}");
        }
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
                // Next to the exit back; Resurrect at Checkpoint and the monster packs' clearance use it too.
                if (conn.Layout.Arrival(conn.CameFrom) is var (ax, ay) && (ax, ay) != (conn.Layout.SpawnX, conn.Layout.SpawnY))
                {
                    conn.Use(conn.Layout with { SpawnX = ax, SpawnY = ay });
                    Console.WriteLine($"[gateway] {remote} arrival from 0x{conn.CameFrom:x4}: ({ax}, {ay})");
                }
                await SendPlayer(remote, stream, ciphers, conn);
                conn.PlayerSent = true;
                // The saved skill bar (S→C 0x53), right after the player and the area.
                await SendSkillBar(remote, stream, ciphers, conn);
                await PopulateArea(remote, stream, ciphers, conn);
                await SendGroundItems(remote, stream, ciphers, conn);
                _ = RunMonsterAi(remote, stream, ciphers, conn);
                _ = RunPlayerTick(remote, stream, ciphers, conn);
            }
        }
    }
    if (unknown.Length > 0)
    {
        ushort uid = unknown.Length > 1 ? (ushort)(unknown[0] << 8 | unknown[1]) : unknown[0];
        Console.WriteLine($"[gateway] {remote} <= unframed 0x{uid:x4} ({unknown.Length} bytes): {Convert.ToHexString(unknown.AsSpan(0, Math.Min(unknown.Length, 256)))}");
        // The client's answer to 0x2b2 (0x142148690): it is ready to leave, so move it now.
        if (uid == InstanceTransfer.LeaveAreaAckId && conn.PendingTransfer is { } t) await Transfer(remote, stream, ciphers, conn, t);
    }
}

// Creates one monster: registers its type at this level (0x20, once per connection), creates it (0x2b9) and puts it in
// the wake zone (0x1a9), and keeps it for the AI. /spawn_mob and the area's packs (PopulateArea) use it.
static async Task SpawnMonster(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, MonsterCatalog.Variety variety,
    byte level, MonsterRarity rarity, int x, int y, string? name = null, bool friendly = false, bool log = true)
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
    var attack = new MonsterAttack(SkillCatalog.FirstSkillId, variety.AttackMs / 1000.0, variety.AttackRange, damage.Min, damage.Max);
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
        { Attack = skill is null ? null : attack, Hostile = !friendly, Variety = variety, Level = level, Rarity = rarity };
}

// The area's monsters, once the player is in (Agents/docs/protocol/14-monster-packs.md): MonsterPacks scattered over the
// walk map, the room points' packs and the unique boss at "mapboss" (AreaPopulation). Towns have none; neither does an
// area without a walk map. Game_MONSTERS=0 turns it off.
static async Task PopulateArea(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    var layout = conn.Layout;
    if (Environment.GetEnvironmentVariable("Game_MONSTERS") == "0" || layout.Walkable is not { } walkable) return;
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
    var (_, transition) = conn.Layout.AreaTransitions.FirstOrDefault(e => e.Id == use.TargetId);
    if (transition is null)
    {
        Console.WriteLine($"[gateway] {remote} <= 0x0024 use object {use} (not an area transition)");
        return;
    }
    ushort dest = transition.DestinationHash16;
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
    if (use.Ctrl)
    {
        var list = await InstanceControl.CallAsync(new ControlRequest { Op = "list", Ticket = conn.Ticket, Area = dest });
        var instances = list.Instances ?? [];
        var entries = instances
            .Select(i => new InstanceTransfer.InstanceListEntry((ulong)i.CreatedUnix, (uint)i.SecondsLeft, 0, i.Players)).ToList();
        conn.Chooser = new ChooserList(dest, [.. instances.Select(i => i.Id)]);
        await Send(remote, stream, ciphers, InstanceTransfer.InstanceList(dest, entries),
            $"0x00ae instance list for 0x{dest:x4}: {entries.Count} instance(s) [{string.Join(", ", instances.Select(i => $"{i.Id} ({i.SecondsLeft} s left): {string.Join("/", i.Players)}"))}]");
        return;
    }
    await ChangeArea(remote, stream, ciphers, conn, new PendingTransfer(dest, 0, false), use.ToString());
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
    lock (conn.Monsters) duration = weapon.AttackSeconds(conn.PlayerStats);
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
    var (minDamage, maxDamage) = weapon.Damage(CharacterPlayerStats(conn));
    int damage = Random.Shared.Next(minDamage, maxDamage + 1);
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
        if (life == 0) { m.Aggro = false; m.Dead = true; }
    }
    // At 0 the client's Life tick (0x141e6a190) stops notifying its listeners, so the mini life bar would keep the last
    // value: hide it with the stat its listener watches (hide_mini_life_bar).
    if (life == 0)
        await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(use.TargetId, [(StatKeys.HideMiniLifeBar, 1)]),
            $"0x01b3 monster 0x{use.TargetId:x} hide_mini_life_bar = 1");
    await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(use.TargetId, life),
        $"0x01b1 monster 0x{use.TargetId:x} hit for {damage}, life {life}/{m.MaxLife}{(life == 0 ? " (dies)" : "")}");
    if (life == 0) await MonsterKilled(remote, stream, ciphers, conn, use.TargetId, m);
}

// A gem skill used on a monster (C→S 0x24 with a skill id other than the default attack's): echo the action from where the
// player stands, aimed at the monster, so the client plays the cast. No damage yet: what the client expects after it
// (projectiles, hits) is still being found, so this logs what it sends.
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

// A gem skill's cast: Righteous Fire puts its buff on, any other skill launches its projectile (if it has one).
static async Task CastGemSkill(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
    ushort skillId, (int X, int Y) from, (int X, int Y) to, uint targetId = 0)
{
    var gem = GemSkillEffects(conn.Character).FirstOrDefault(g => g.Id == skillId);
    if (gem.Name is not null)
    {
        if (!PayMana(conn, gem.Effect, out uint cost))
        {
            Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: not enough mana ({conn.PlayerMana} < {cost})");
            return;
        }
        if (cost > 0)
            await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerMana, pool: ManaPool),
                $"0x01b1 player mana {conn.PlayerMana} (-{cost} for {gem.Name})");
    }
    if (gem.Name is not null && IsRighteousFire(gem.Effect))
        await ActivateRighteousFire(remote, stream, ciphers, conn, skillId, gem.Effect);
    else
        await LaunchProjectile(remote, stream, ciphers, conn, skillId, from, to, targetId);
}

// Experimental: a projectile gem skill's projectile. After the cast time the server creates the Projectiles.dat object
// named after the effect (Fireball → Metadata/Projectiles/Fireball) just ahead of the caster, wakes it and moves it to the
// aimed point and on to its range with 0x1ba at the row's ProjectileSpeed (world units per second, 250/23 per grid
// cell), stopping at the first wall or monster on its path; a monster takes the skill's fire damage
// (spell_minimum/maximum_base_fire_damage; no damage model yet). It explodes there; then the object is removed.
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
    await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, type, obj.Blob()),
        $"0x02b9 create projectile 0x{id:x} {projectile.Id} type=0x{type:x8} at ({from.X}, {from.Y})");
    await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), $"0x01a9 projectile 0x{id:x} enters wake zone");
    // 0x1ba's float is the move's duration in seconds, not a speed (InstanceProtocol.MoveTo).
    await Send(remote, stream, ciphers, InstanceProtocol.MoveTo(id, to.X, to.Y, (float)(cells / cellsPerSecond), u28: endHeight is int e ? (uint)e : TerrainHeight),
        $"0x01ba projectile 0x{id:x} to ({to.X}, {to.Y}), {cells:0.0} cells at {cellsPerSecond:0.0} cells/s, height {startHeight?.ToString() ?? "terrain"} → {endHeight?.ToString() ?? "terrain"}" +
        (hitId != 0 ? $", will hit monster 0x{hitId:x}" : hitWall ? ", will hit a wall" : ""));
    await Task.Delay(TimeSpan.FromSeconds(cells / cellsPerSecond));
    if (conn.Closed) return;
    SpawnedMonster? m = null;
    if (hitId != 0) lock (conn.Monsters) m = conn.Monsters.GetValueOrDefault(hitId);
    if (m is not null && m.Life > 0)
    {
        var stats = Skills.EffectStats(gem.Effect, 1).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        int min = Stats.Key("spell_minimum_base_fire_damage") is uint kmin ? stats.GetValueOrDefault(kmin) : 0;
        int max = Stats.Key("spell_maximum_base_fire_damage") is uint kmax ? stats.GetValueOrDefault(kmax) : 0;
        int damage = Random.Shared.Next(min, Math.Max(min, max) + 1);
        uint life;
        lock (conn.Monsters)
        {
            m.Life = (uint)Math.Max(0, (long)m.Life - damage);
            life = m.Life;
            if (life == 0) { m.Aggro = false; m.Dead = true; }
        }
        if (life == 0)
            await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(hitId, [(StatKeys.HideMiniLifeBar, 1)]),
                $"0x01b3 monster 0x{hitId:x} hide_mini_life_bar = 1");
        await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(hitId, life),
            $"0x01b1 monster 0x{hitId:x} hit by {projectile.Id} for {damage}, life {life}/{m.MaxLife}{(life == 0 ? " (dies)" : "")}");
        if (life == 0) await MonsterKilled(remote, stream, ciphers, conn, hitId, m);
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

// The player's granted skills: the default attack with the unarmed weapon's stats and every stat the server set on the
// player since (a skill's stats include the character's; the client reads the skill's).
static IReadOnlyList<GrantedSkill> PlayerSkills(ClientState conn)
{
    var player = FinalPlayerStats(conn).ToList();
    return [.. Skills.PlayerDefaults(player), .. GemSkills(conn.Character, player)];
}

// The player's stats as the server sets them: what /set_stat set, then the attack's final values computed from them
// (the client derives nothing itself: 11-skills.md §4).
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
    return stats;
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
    var extra = Passives.Stats(nodes);
    if (conn.God)
    {
        foreach (var (stat, value) in GodStats)
            if (Stats.Key(stat) is uint key) extra[key] = extra.GetValueOrDefault(key) + value;
        // the final value the client shows: 100% of maximum life per second, per minute
        if (Stats.Key("life_regeneration_rate_per_minute") is uint lr) extra[lr] = (int)Math.Min(int.MaxValue, (long)conn.Pools.Life * GodLifeRegenPctPerSecond / 100 * 60);
    }
    foreach (var (k, v) in extra) if (seen.Add(k)) yield return (k, v);
}

// The class's attributes (Characters.dat BaseStrength / BaseDexterity / BaseIntelligence) as final stats: strength,
// dexterity and intelligence for the character panel, and virtual_*_for_requirements, which item requirements are
// checked against (0x1402ce5f0 via the Player cache 0x141e80340, keys 0x3d1b-0x3d1d; global_no_attribute_requirements
// skips the check, global_item_attribute_requirements_+% scales it).
// With the level-based pools (CharacterStats): level, maximum_life / maximum_mana (and their base_ forms),
// accuracy_rating and evasion_rating (base_evasion_rating 15), as final values, and the attributes' increases:
// evasion_rating_+% (dexterity), maximum_energy_shield_+% (intelligence) and melee_physical_damage_+% (strength).
static IEnumerable<(uint, int)> ClassAttributes(int classRow, CharacterStats.Pools pools)
{
    foreach (var (stat, value) in new[] { ("level", pools.Level), ("base_maximum_life", (int)pools.Life), ("maximum_life", (int)pools.Life),
        ("base_maximum_mana", (int)pools.Mana), ("maximum_mana", (int)pools.Mana), ("accuracy_rating", pools.Accuracy),
        ("base_evasion_rating", CharacterStats.BaseEvasion), ("evasion_rating", pools.Evasion), ("evasion_rating_+%", pools.EvasionPct),
        ("maximum_energy_shield_+%", pools.EnergyShieldPct), ("melee_physical_damage_+%", pools.MeleePhysicalDamagePct) })
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

// The skills of the gems socketed in the equipped items: each gem's GrantedEffects row at level 1 with its own stats
// (SkillCatalog.EffectStats: damage, cast time, mana cost) after the player's, with ids after the default attack's that
// no basic action uses (SkillCatalog.SkillIds).
static IEnumerable<GrantedSkill> GemSkills(string? character, IEnumerable<(uint, int)>? player = null)
{
    var playerStats = player?.ToList() ?? [];
    foreach (var (id, effect, name) in GemSkillEffects(character))
    {
        var stats = new Dictionary<uint, int>();
        foreach (var (k, v) in playerStats) stats[k] = v;
        foreach (var (k, v) in Skills.EffectStats(effect, 1)) stats[k] = v;
        // Increased cast speed shortens the cast the client plays (the final values: 11-skills.md §4).
        if (CastSpeedPct(stats) is int castSpeed and not 0 && Skills.CastMs(effect) is int castMs and > 0)
        {
            int ms = (int)Math.Round(castMs / (1 + castSpeed / 100.0));
            if (Stats.Key("virtual_base_spell_cast_time_ms") is uint vk) stats[vk] = ms;
            if (Stats.Key("hundred_times_casts_per_second") is uint hk) stats[hk] = (int)Math.Round(100_000.0 / Math.Max(1, ms));
        }
        if (Skills.FindByRow(id, effect, 1, [.. stats.Select(kv => (kv.Key, kv.Value))]) is { } skill)
        {
            Console.WriteLine($"[gateway] gem skill 0x{skill.Id:x4} {name} (effect {effect}): GEPL {skill.EffectsPerLevelRow}, GESSPL {skill.StatSetsPerLevelRow}, " +
                string.Join(", ", Skills.EffectStats(effect, 1).Select(kv => $"{Stats.Name(kv.Item1)} {kv.Item2}")));
            yield return skill;
        }
    }
}

// What a skill bar slot holds, as saved (CharacterSaves): a gem or the default attack by GrantedEffects Id (skill ids
// change with the socketed gems), a basic action as "Action/<ActionTypes Id>" ("Action/Move": "Move only" on a mouse
// button), "" for a cleared slot. Null for an id the server does not know.
static string? SkillBarEntry(ClientState conn, ushort skillId) =>
    skillId == 0 ? "" : SkillEffectId(conn, skillId) ?? (Skills.ActionName(skillId) is { } a ? "Action/" + a : null);

// The saved skill bar as the player's current skill ids, sent with S→C 0x53. The default attack goes on slot 0 (left
// mouse button) only when nothing at all was saved for that slot; nothing is sent without a saved bar, so the client
// keeps its own default.
static async Task SendSkillBar(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
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
    if (!bar.ContainsKey(0)) ids[0] = SkillCatalog.FirstSkillId;
    await Send(remote, stream, ciphers, InstanceProtocol.SkillBar(ids),
        $"0x0053 skill bar: {string.Join(", ", ids.Select((id, i) => id == 0 ? null : $"slot {i} 0x{id:x4}").OfType<string>())}");
}

// The GrantedEffects Id behind a skill id of the player (the default attack or a gem skill), or null.
static string? SkillEffectId(ClientState conn, ushort skillId) =>
    skillId == SkillCatalog.FirstSkillId ? Environment.GetEnvironmentVariable("Game_PLAYER_ATTACK") ?? "PlayerMelee"
    : GemSkillEffects(conn.Character).FirstOrDefault(g => g.Id == skillId) is { Name: not null } g ? Skills.EffectId(g.Effect) : null;

static string CharacterName(ClientState conn) => string.IsNullOrEmpty(conn.Character) ? "Exile" : conn.Character;

// The active gems socketed in the equipped items in skill-list order: skill id, GrantedEffects row, gem name.
static IEnumerable<(ushort Id, int Effect, string Name)> GemSkillEffects(string? character)
{
    using var ids = Skills.SkillIds().Skip(1).GetEnumerator();
    foreach (var gem in PlayerInventories.For(string.IsNullOrEmpty(character) ? "Exile" : character, Items).SocketedGems())
        if (Items.ByHash(gem.TypeHash) is { } t && Items.GemEffect(t) is int effect and >= 0 && ids.MoveNext())
            yield return (ids.Current, effect, t.Name);
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
    lock (conn.Monsters)
    {
        TrimPassives(conn, level);
        conn.Pools = PoolsFor(conn, level);
    }
    CharacterSaves.SetPassives(CharacterName(conn), PassiveState(conn).Nodes);
    Console.WriteLine($"[gateway] {remote} {CharacterName(conn)}: level {before} → {level} (life {conn.Pools.Life}, mana {conn.Pools.Mana}, {Progression.PassivePoints(level)} passive point(s))");
    await SendCharacterStats(remote, stream, ciphers, conn, $"level {level}");
    lock (conn.Monsters) (conn.PlayerLife, conn.PlayerMana) = (conn.PlayerMaxLife, conn.Pools.Mana);
    await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerMaxLife), $"0x01b1 player life {conn.PlayerMaxLife}");
    await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerMana, pool: ManaPool), $"0x01b1 player mana {conn.PlayerMana}");
    await SendPassives(remote, stream, ciphers, conn);
}

// The player's stats (0x1b3) and the skills computed with them (0x2ba Actor), after the level or the passives changed.
static async Task SendCharacterStats(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string why)
{
    await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, [.. FinalPlayerStats(conn)]), $"0x01b3 player stats ({why})");
    var skills = PlayerSkills(conn);
    await Send(remote, stream, ciphers, InstanceEntities.PlayerSkillsUpdate(PlayerId, skills),
        $"0x02ba player Actor: {skills.Count} skill(s) with {skills.Sum(k => k.Stats?.Count ?? 0)} stats");
}

// The character's experience and passives from its save, with the account's level: experience saved at another level
// (/set_level, an older save) gives way to the level's start, and passives beyond the level's points are dropped.
static void LoadProgress(ClientState conn, int level)
{
    var save = CharacterSaves.Load(CharacterName(conn));
    level = Math.Clamp(level, 1, Progression.MaxLevel);
    lock (conn.Monsters)
    {
        conn.Passives = [.. (save?.Passives ?? []).Where(n => Passives.Find(n) is { IsAscendancy: false })];
        conn.Experience = save?.Experience is uint xp && Progression.LevelFor(xp, CharacterStats.LevelStarts) == level
            ? xp : CharacterStats.ExperienceFor(level);
        TrimPassives(conn, level);
        conn.Pools = PoolsFor(conn, level);
    }
}

// The pools of the character's class at this level with its passives (call under the Monsters lock).
static CharacterStats.Pools PoolsFor(ClientState conn, int level)
{
    var bonus = PassiveBonus(Passives.Stats(conn.Passives));
    if (conn.God)
        bonus = bonus with { Strength = bonus.Strength + 1000, Dexterity = bonus.Dexterity + 1000, Intelligence = bonus.Intelligence + 1000, Life = bonus.Life + 10_000 };
    return CharacterStats.For(Skills.ClassFor(conn.ClassRow), level, bonus);
}

// What the allocated passives add to the pools: attributes (base_* and additional_*, all attributes and the pairs),
// flat and increased life and mana, and increased evasion, energy shield and melee physical damage.
static CharacterStats.Bonus PassiveBonus(Dictionary<uint, int> stats)
{
    int Get(string name) => Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0;
    int all = Get("additional_all_attributes") + Get("base_all_attributes");
    return new CharacterStats.Bonus(
        Strength: Get("base_strength") + Get("additional_strength") + all + Get("additional_strength_and_dexterity") + Get("additional_strength_and_intelligence"),
        Dexterity: Get("base_dexterity") + Get("additional_dexterity") + all + Get("additional_strength_and_dexterity") + Get("additional_dexterity_and_intelligence"),
        Intelligence: Get("base_intelligence") + Get("additional_intelligence") + all + Get("additional_strength_and_intelligence") + Get("additional_dexterity_and_intelligence"),
        Life: Get("base_maximum_life"), LifePct: Get("maximum_life_+%"), Mana: Get("base_maximum_mana"), ManaPct: Get("maximum_mana_+%"),
        EvasionPct: Get("evasion_rating_+%"), EnergyShieldPct: Get("maximum_energy_shield_+%"), MeleePhysicalDamagePct: Get("melee_physical_damage_+%"));
}

// The character's passive skill points (one per level after the first; the client computes the same from the level in
// 0x33) and the allocated nodes. The client allows that many nodes plus the points the allocated nodes grant themselves.
static (ushort Points, List<ushort> Nodes) PassiveState(ClientState conn)
{
    lock (conn.Monsters) return ((ushort)Progression.PassivePoints(conn.Pools.Level), [.. conn.Passives]);
}

// Drops the last allocated passives beyond the level's points (call under the Monsters lock).
static void TrimPassives(ClientState conn, int level)
{
    while (conn.Passives.Count > 0 && conn.Passives.Count > Progression.PassivePoints(level) + Passives.PointsGranted(conn.Passives))
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
    var (points, nodes) = PassiveState(conn);
    int unspent = points + Passives.PointsGranted(nodes) - nodes.Count;
    return (InstanceEntities.CharacterPassives((byte)conn.Pools.Level, (byte)Skills.ClassFor(conn.ClassRow).IntegerId, nodes),
        $"0x0033 passive skill tree: level {conn.Pools.Level}, {points} skill point(s), {nodes.Count} allocated, {unspent} unspent");
}

// Allocates (or, with allocate false, refunds) passive nodes: checked against the tree's nodes and the character's
// points, then saved, applied to the stats and sent back (0x3d). The client asks only for nodes connected to its tree.
static async Task<string?> ChangePassives(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
    IReadOnlyList<ushort> nodes, bool allocate, string why)
{
    string? error = null;
    List<ushort> now;
    lock (conn.Monsters)
    {
        var next = new List<ushort>(conn.Passives);
        foreach (var n in nodes)
        {
            if (Passives.Find(n) is not { } node) { error = $"unknown passive {n}"; break; }
            if (node.IsAscendancy) { error = $"{node.Name} ({n}) is an ascendancy passive"; break; }
            if (allocate) { if (!next.Contains(n)) next.Add(n); }
            else next.Remove(n);
        }
        if (error is null && next.Count > Progression.PassivePoints(conn.Pools.Level) + Passives.PointsGranted(next))
            error = $"not enough passive skill points ({Progression.PassivePoints(conn.Pools.Level)} at level {conn.Pools.Level}, {next.Count} wanted)";
        if (error is null)
        {
            conn.Passives = next;
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.PlayerLife = Math.Min(conn.PlayerLife, conn.PlayerMaxLife);
            conn.PlayerMana = Math.Min(conn.PlayerMana, conn.Pools.Mana);
        }
        now = [.. conn.Passives];
    }
    Console.WriteLine($"[gateway] {remote} passives {(allocate ? "allocate" : "refund")} [{string.Join(", ", nodes.Select(n => $"{n} {Passives.Find(n)?.Name}"))}] ({why}): " +
        (error ?? $"{now.Count} allocated, life {conn.Pools.Life}, mana {conn.Pools.Mana}"));
    if (error is null)
    {
        CharacterSaves.SetPassives(CharacterName(conn), now);
        await SendCharacterStats(remote, stream, ciphers, conn, "passives");
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
    uint before, after;
    int oldLevel;
    lock (conn.Monsters)
    {
        before = conn.Experience;
        oldLevel = conn.Pools.Level;
        conn.Experience = after = Progression.AddExperience(before, gain, CharacterStats.LevelStarts);
    }
    if (after == before) return;
    int level = Progression.LevelFor(after, CharacterStats.LevelStarts);
    await Send(remote, stream, ciphers, InstanceEntities.PlayerExperienceUpdate(PlayerId, after),
        $"0x02ba player Player: experience {before} + {after - before} = {after} (level {level}; {why})");
    CharacterSaves.SetExperience(CharacterName(conn), after);
    if (level <= oldLevel) return;
    var r = await InstanceControl.CallAsync(new ControlRequest { Op = "set_level", Ticket = conn.Ticket, Level = level });
    if (!r.Ok) Console.WriteLine($"[gateway] {remote} level {level} not saved to the account: {r.Error}");
    await ApplyLevel(remote, stream, ciphers, conn, level);
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
    if (alive && Dist(at, player) <= ExperienceRange && Monsters.BaseExperience(variety, m.Level) is int baseXp)
    {
        uint xp = Progression.MonsterExperience(baseXp, variety.ExperienceMultiplier, m.Rarity, conn.Pools.Level, m.Level);
        await GainExperience(remote, stream, ciphers, conn, xp, $"{m.Rarity} {m.Label} 0x{id:x}");
    }
    await DropLoot(remote, stream, ciphers, conn, m, at);
}

// The monster's items (ItemDrops.Roll) as WorldItem objects scattered around where it died, on walkable cells when the
// area's walk map is known. Like dropped items they lie in the instance (InstanceStore).
static async Task DropLoot(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, SpawnedMonster m, (int X, int Y) at)
{
    if (Environment.GetEnvironmentVariable("Game_DROPS") == "0") return;
    var drops = Drops.Roll(m.Level, m.Rarity, Random.Shared);
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

// /god: what it adds on top of the character's stats (GodStats) and pools (PoolsFor): 1000% increased attack and cast
// speed, 100% of maximum life and 10 000 mana regenerated per second, +10 000 maximum life and +1000 to each attribute.

static async Task SetGod(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, bool on)
{
    lock (conn.Monsters)
    {
        conn.God = on;
        conn.Pools = PoolsFor(conn, conn.Pools.Level);
        conn.PlayerLife = on ? conn.PlayerMaxLife : Math.Min(conn.PlayerLife, conn.PlayerMaxLife);
        conn.PlayerMana = on ? conn.Pools.Mana : Math.Min(conn.PlayerMana, conn.Pools.Mana);
    }
    Console.WriteLine($"[gateway] {remote} god mode {(on ? "on" : "off")}: life {conn.Pools.Life}, mana {conn.Pools.Mana}");
    await SendCharacterStats(remote, stream, ciphers, conn, on ? "god mode on" : "god mode off");
    await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerLife), $"0x01b1 player life {conn.PlayerLife}");
    await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerMana, pool: ManaPool), $"0x01b1 player mana {conn.PlayerMana}");
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

// A gem skill's mana cost (its mana_cost stat), paid when the cast starts. False when the player lacks the mana.
static bool PayMana(ClientState conn, int effect, out uint cost)
{
    cost = Stats.Key("mana_cost") is uint k ? (uint)Skills.EffectStats(effect, 1).Where(kv => kv.Item1 == k).Select(kv => kv.Item2).FirstOrDefault() : 0;
    lock (conn.Monsters)
    {
        if (conn.PlayerMana < cost) return false;
        conn.PlayerMana -= cost;
        return true;
    }
}

// A cast's time in ms: the effect's CastTime scaled by the player's increased cast speed.
static int CastMs(ClientState conn, int effect) => (int)Math.Round(Skills.CastMs(effect) / (1 + CastSpeedPct(CharacterPlayerStats(conn)) / 100.0));

static int CastSpeedPct(IReadOnlyDictionary<uint, int> stats) =>
    (Stats.Key("base_cast_speed_+%") is uint a ? stats.GetValueOrDefault(a) : 0) + (Stats.Key("cast_speed_+%") is uint b ? stats.GetValueOrDefault(b) : 0);

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
    if (conn.RighteousFire is not { } rf) return;
    double now = conn.Clock.Elapsed.TotalSeconds, arrives = conn.Walk.ArrivesAt;
    var to = conn.Walk.Position(Math.Max(now, arrives));
    await Send(remote, stream, ciphers, InstanceProtocol.MoveTo(rf.Ring, to.X, to.Y, (float)Math.Max(0.05, arrives - now), u28: TerrainHeight), null);
}

static async Task ActivateRighteousFire(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, ushort skillId, int effect)
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
        var stats = Skills.EffectStats(effect, 1).ToDictionary(kv => kv.Item1, kv => kv.Item2);
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
    var ringObject = new EffectObject(at.X, at.Y, RighteousFireRingAo);
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
    while (!conn.Closed)
    {
        await Task.Delay(PlayerTickMs);
        if (conn.Closed) break;
        double seconds = PlayerTickMs / 1000.0;
        double regen = LifeRegenPerSecond(conn) * seconds;
        var rf = conn.RighteousFire;
        uint before, after;
        bool dead;
        lock (conn.Monsters)
        {
            before = conn.PlayerLife;
            dead = before == 0;
            double burn = rf is null ? 0 : conn.PlayerMaxLife * rf.BurnPctPerMinute / 6000.0 * seconds;
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
        uint manaBefore, manaAfter;
        double manaRegen = ManaRegenPerSecond(conn) * seconds;
        lock (conn.Monsters)
        {
            manaBefore = conn.PlayerMana;
            double change = manaCarry + manaRegen;
            int whole = (int)Math.Truncate(change);
            manaCarry = change - whole;
            conn.PlayerMana = manaAfter = (uint)Math.Clamp((long)manaBefore + whole, 0, conn.Pools.Mana);
            if (manaAfter == conn.Pools.Mana) manaCarry = 0;
        }
        if (manaAfter != manaBefore) await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, manaAfter, pool: ManaPool), null);
        if (rf is not null) await BurnAround(remote, stream, ciphers, conn, rf, seconds);
    }
}

// Righteous Fire's damage to the hostile monsters within its radius for one tick, with their aura buff on while they
// are inside.
static async Task BurnAround(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, RighteousFireState rf, double seconds)
{
    double now = conn.Clock.Elapsed.TotalSeconds;
    double perTick = conn.PlayerMaxLife * rf.DamagePctPerMinute / 6000.0 * seconds;
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
            m.Burn += perTick;
            int damage = (int)Math.Truncate(m.Burn);
            if (damage == 0) continue;
            m.Burn -= damage;
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
    if (!r.Ok)
    {
        Console.WriteLine($"[gateway] {remote} transfer failed: {r.Error}");
        return;
    }
    if (!t.AtSpawn) AreaLayouts.CameFrom[r.Ticket] = conn.Layout.Hash;
    string host = Environment.GetEnvironmentVariable("Game_GATEWAY_HOST") ?? "127.0.0.1";
    ushort port = ushort.TryParse(Environment.GetEnvironmentVariable("Game_GATEWAY_PORT"), out var gp) ? gp : (ushort)6112;
    await Send(remote, stream, ciphers, InstanceTransfer.ConnectInstance(host, port, r.Ticket, r.Area, conn.Keyring.Key(r.Ticket)),
        $"0x0027 connect to instance {r.Instance} of area 0x{r.Area:x4} (seed {r.Seed}) at {host}:{port}, ticket 0x{r.Ticket:x8}");
}

// The chat commands of one connection (ChatCommands.cs).
static ChatCommands Commands(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    return new ChatCommands
    {
        Send = (bytes, log) => Send(remote, stream, ciphers, bytes, log),
        // S→C 0x0d shows the text in a message box with OK (no chat line message is mapped yet).
        Reply = text => Send(remote, stream, ciphers, InstanceChat.MessageBox(text), $"0x000d message box \"{text}\""),
    }
    .Add("raw", "/raw <hex> (send one S→C message)", async args =>
    {
        byte[] m = ChatCommands.ParseHex(args);
        await Send(remote, stream, ciphers, m, $"0x{m[0] << 8 | m[1]:x4} raw ({m.Length} bytes)");
    })
    .Add("set_speed", "/set_speed <percent> (100 = normal)", async args =>
    {
        int percent = int.Parse(args[0]);
        if (percent is < 10 or > 1000) throw new ArgumentException("10 to 1000");
        conn.Walk.SetSpeed(WalkSpeed * percent / 100, conn.Clock.Elapsed.TotalSeconds);
        lock (conn.Monsters) conn.PlayerStats[StatKeys.MovementVelocityPct] = percent - 100;
        await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, [(StatKeys.MovementVelocityPct, percent - 100)]),
            $"0x01b3 stats: movement_velocity_+% (key {StatKeys.MovementVelocityPct}) = {percent - 100}");
    })
    .Add("set_stat", "/set_stat <stat name or client key> <value> (e.g. attack_speed_+% 100)", async args =>
    {
        uint stat = Stats.Key(args[0]) ?? throw new ArgumentException($"unknown stat {args[0]}");
        int value = int.Parse(args[1]);
        lock (conn.Monsters) conn.PlayerStats[stat] = value;
        await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, [.. FinalPlayerStats(conn)]),
            $"0x01b3 stats: {Stats.Name(stat)} (key 0x{stat:x}) = {value}, and the attack's final stats");
        // Skills compute with their own stats (the attack and the character panel read those), so send them again.
        var skills = PlayerSkills(conn);
        await Send(remote, stream, ciphers, InstanceEntities.PlayerSkillsUpdate(PlayerId, skills),
            $"0x02ba player Actor: {skills.Count} skill(s) with {skills.Sum(k => k.Stats?.Count ?? 0)} stats");
    })
    .Add("spawn_mob", "/spawn_mob <MonsterVarieties row, Id or name>[@level] [friendly] [normal|magic|rare|unique] [name]", async args =>
    {
        // "friendly": on the player's team (Positioned team 0); monsters are hostile (team 1) otherwise.
        bool friendly = args.Any(a => a.Equals("friendly", StringComparison.OrdinalIgnoreCase));
        args = [.. args.Where(a => !a.Equals("friendly", StringComparison.OrdinalIgnoreCase))];
        if (args.Length == 0) throw new ArgumentException("which monster");
        if (Monsters.Error is { } error) { await conn.Commands!.Reply(error); return; }
        // Words up to a rarity keyword name the monster; words after it are a custom name.
        int r = Array.FindIndex(args, a => Enum.TryParse<MonsterRarity>(a, ignoreCase: true, out _));
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
    .Add("item", "/item <BaseItemTypes row, Id or name> [count] (into the inventory, e.g. /item Small Life Flask 2, /item 384)", async args =>
    {
        if (args.Length == 0) throw new ArgumentException("which item?");
        int count = 1;
        if (args.Length > 1 && int.TryParse(args[^1], out int n)) { count = Math.Clamp(n, 1, 60); args = args[..^1]; }
        if (Items.Error is { } error) { await conn.Commands!.Reply(error); return; }
        if (Items.Find(string.Join(' ', args)) is not { } type) { await conn.Commands!.Reply($"No item type \"{string.Join(' ', args)}\""); return; }
        if (!type.Supported)
        {
            await conn.Commands!.Reply($"{type} has components the server cannot write yet: {string.Join(", ", type.Components.Where(c => !Item.Supported.Contains(c)))}");
            return;
        }
        var items = ItemsOf(conn);
        var (sockets, links) = Items.DefaultSockets(type);
        var added = new List<InventoryItem>();
        for (int i = 0; i < count; i++)
        {
            if (items.FreeCell(PlayerInventories.MainInventory, type.Width, type.Height, ShapeOf) is not { } cell) break;
            added.Add(items.Put(PlayerInventories.MainInventory, new InventoryItem(items.NewItemId(), cell.X, cell.Y, ItemCatalog.Create(type, sockets, links))));
        }
        if (added.Count == 0) { await conn.Commands!.Reply("No room in the inventory"); return; }
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
            items.NextRevision(PlayerInventories.MainInventory), [], added), $"0x00bf inventory 1: /item {type} × {added.Count}");
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
        var (points, nodes) = PassiveState(conn);
        int unspent = points + Passives.PointsGranted(nodes) - nodes.Count;
        await conn.Commands!.Reply($"Level {conn.Pools.Level}, {conn.Experience} experience: {points} passive skill point(s), {unspent} unspent.\n" +
            string.Join("\n", nodes.Select(n => $"{n} {Passives.Find(n)?.Name}").DefaultIfEmpty("No passives allocated.")));
    })
    .Add("reset_passives", "/reset_passives (refunds every passive, saved)", async _ =>
    {
        var (_, nodes) = PassiveState(conn);
        await ChangePassives(remote, stream, ciphers, conn, nodes, allocate: false, "/reset_passives");
        await conn.Commands!.Reply($"{nodes.Count} passive(s) refunded.");
    })
    .Add("god", "/god (toggles: 1000% increased attack and cast speed, 100% of life and 10 000 mana regenerated per second, +10 000 life, +1000 attributes)", async _ =>
    {
        bool on = !conn.God;
        await SetGod(remote, stream, ciphers, conn, on);
        await conn.Commands!.Reply(on ? "God mode on." : "God mode off.");
    })
    .Add("heal", "/heal (full life; also revives: 0x1b1 from 0 runs the client's revive)", async _ =>
    {
        lock (conn.Monsters) (conn.PlayerLife, conn.PlayerMana) = (conn.PlayerMaxLife, conn.Pools.Mana);
        await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerMaxLife),
            $"0x01b1 player healed to {conn.PlayerMaxLife}");
        await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerMana, pool: ManaPool), $"0x01b1 player mana {conn.PlayerMana}");
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
    .Add("goto", "/goto <area id or name> (dev: change area as if through an area transition, e.g. /goto 1_1_3)", async args =>
    {
        string target = string.Join(' ', args);
        if (AreaTransitionCatalog.HashOf(target) is not { } hash) { await conn.Commands!.Reply($"Unknown area \"{target}\" (needs data/area-transitions.json)."); return; }
        await ChangeArea(remote, stream, ciphers, conn, new PendingTransfer(hash, 0, false), $"/goto {target}");
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
            if (!conn.AiEnabled) continue;
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
                    else if (m.Aggro && d > 2 * m.AggroRange)
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
static async Task MonsterSwing(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
    SpawnedMonster m, (int X, int Y) from, (int X, int Y) player)
{
    var a = m.Attack!;
    await Send(remote, stream, ciphers,
        InstanceProtocol.ActionStarted(id, from.X, from.Y, player.X, player.Y, a.SkillId, 0xffff, 0x0408, targetObjectId: PlayerId),
        $"0x01ab monster 0x{id:x} ({m.Label}) attacks the player, attack time {a.Seconds:0.00} s");
    await Task.Delay(TimeSpan.FromSeconds(a.Seconds / 2));
    double now = conn.Clock.Elapsed.TotalSeconds;
    uint life;
    int damage = Random.Shared.Next(a.MinDamage, a.MaxDamage + 1);
    lock (conn.Monsters)
    {
        if (m.Dead || conn.PlayerLife == 0 || conn.Closed) return;
        var here = conn.Walk.Position(now);
        if (Dist(here, m.Walk.Position(now)) > a.Range + 2)
        {
            Console.WriteLine($"[gateway] {remote} monster 0x{id:x} missed: the player is {Dist(here, m.Walk.Position(now)):0.0} away");
            return;
        }
        conn.PlayerLife = (uint)Math.Max(0, (long)conn.PlayerLife - damage);
        life = conn.PlayerLife;
    }
    // At 0 the Life tick stops refreshing the overhead life bar (see Attack): hide it.
    if (life == 0)
        await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, [(StatKeys.HideMiniLifeBar, 1)]),
            "0x01b3 player hide_mini_life_bar = 1");
    await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, life),
        $"0x01b1 player hit by monster 0x{id:x} for {damage}, life {life}/{conn.PlayerMaxLife}{(life == 0 ? " (dies)" : "")}");
    if (life == 0)
        await Send(remote, stream, ciphers, InstanceProtocol.DeathScreen(DeathScreenFlags(conn)), $"0x0055 death screen ({conn.League ?? "?"} league)");
}

// The death screen's buttons (C→S 0x54). Both hide the screen (0x55 with bit1) and give the player full life; "at
// checkpoint" revives at the area's arrival point (teleport 0x1b7), "in town" in the character's last town (an area change;
// in that town it is the same as the checkpoint).
static async Task Resurrect(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, bool inTown)
{
    lock (conn.Monsters)
    {
        if (conn.PlayerLife > 0) return;
        conn.PlayerLife = conn.PlayerMaxLife;
        conn.PlayerMana = conn.Pools.Mana;
        foreach (var m in conn.Monsters.Values) m.Aggro = false;
    }
    Console.WriteLine($"[gateway] {remote} <= 0x0054 resurrect {(inTown ? "in town" : "at checkpoint")}");
    await Send(remote, stream, ciphers, InstanceProtocol.DeathScreen(0x02), "0x0055 hide the death screen");
    if (inTown && conn.Layout.Hash != conn.LastTown)
    {
        // The new area's player object is created with full life.
        await ChangeArea(remote, stream, ciphers, conn, new PendingTransfer(conn.LastTown, 0, false, AtSpawn: true), "resurrect in town");
        return;
    }
    // Revive first: a teleport sent while the player is dead left them where they died (live 2026-10-17).
    await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerMaxLife),
        $"0x01b1 player revived with {conn.PlayerMaxLife} life");
    // The checkpoint: where the player entered the area, as in the real game before any checkpoint is activated
    // (checkpoints are not implemented yet).
    var (x, y) = (conn.Layout.SpawnX, conn.Layout.SpawnY);
    conn.Walk.Teleport(x, y, conn.Clock.Elapsed.TotalSeconds);
    await Send(remote, stream, ciphers, InstanceProtocol.Teleport(PlayerId, x, y), $"0x01b7 player to the arrival point ({x}, {y})");
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

static PlayerInventories.Shape? ShapeOf(Item i) => Items.ByHash(i.TypeHash) is { } t ? new PlayerInventories.Shape(t.Width, t.Height, t.Class) : null;

static VisibleItem? LookOf(Item i) => Items.ByHash(i.TypeHash) is { } t ? new VisibleItem(0, t.VisualIdentity, t.Stance) : null;

// What the player wears changed (an item went into or out of a visible equipment row): 0x2ba Inventories for the model,
// then the Actor skill list again, since the gems socketed in the equipment grant skills.
static async Task SendEquipment(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, PlayerInventories items)
{
    var equipped = items.Equipped(LookOf);
    await Send(remote, stream, ciphers, InstanceEntities.PlayerEquipmentUpdate(PlayerId, equipped),
        $"0x02ba player equipment: {string.Join(", ", equipped.Select(e => $"slot {e.Slot} visual 0x{e.VisualIdentity:x4} stance {e.Stance}"))}");
    var skills = PlayerSkills(conn);
    await Send(remote, stream, ciphers, InstanceEntities.PlayerSkillsUpdate(PlayerId, skills), $"0x02ba player Actor: {skills.Count} skill(s)");
}

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
    var items = ItemsOf(conn);
    var shape = ShapeOf(ground.Item) ?? new(1, 1, "");
    // With the inventory panel open the item goes to the cursor (when it is free), otherwise to the first free cell.
    bool toCursor = (use.Flags & InstanceTransfer.InventoryOpenFlag) != 0 && items.Items(PlayerInventories.Cursor).Count == 0;
    byte row = toCursor ? PlayerInventories.Cursor : PlayerInventories.MainInventory;
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

    var placed = items.Put(row, new InventoryItem(items.NewItemId(), cell.X, cell.Y, ground.Item));
    await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove world item 0x{id:x} (picked up from ({stop.X}, {stop.Y}))");
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), [], [placed]),
        toCursor ? $"0x00bf cursor: hold item {placed.Id}" : $"0x00bf inventory 1: add item {placed.Id} at ({cell.X}, {cell.Y})");
}

// C→S 0x2a: put the held item into an inventory cell (PlayerInventories.Place). The target inventory gets one 0xbf (the
// item it displaced removed, the held item added), the cursor another (the held item removed, the displaced one added).
static async Task PlaceItem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceItems.PlaceItem place)
{
    var items = ItemsOf(conn);
    PlayerInventories.Placement? done = null;
    string why = "unknown inventory";
    if (PlayerInventories.Row(place.InventoryId) is byte row)
        done = items.Place(row, (int)place.X, (int)place.Y,
            ShapeOf, out why);
    if (done is null)
    {
        Console.WriteLine($"[gateway] {remote} <= 0x002a place {place}: refused ({why})");
        return;
    }
    var d = done.Displaced;
    uint target = PlayerInventories.InventoryId(done.Row);   // may differ from the clicked one (off hand → main hand)
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(target, items.NextRevision(done.Row), d is null ? [] : [d.Id], [done.Placed]),
        $"0x00bf inventory {target}: add item {done.Placed.Id} at ({done.Placed.X}, {done.Placed.Y})" + (d is null ? "" : $", remove item {d.Id}"));
    await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
        items.NextRevision(PlayerInventories.Cursor), [done.Held.Id], d is null ? [] : [d]),
        $"0x00bf cursor: remove item {done.Held.Id}" + (d is null ? "" : $", hold item {d.Id}"));
    if (PlayerInventories.ShowsOnModel(done.Row)) await SendEquipment(remote, stream, ciphers, conn, items);
}

// The player in its class: the Characters.dat row's object type (Metadata/Characters/StrInt/StrInt for the Templar), model
// (AOFile), class byte (IntegerId), attributes and unarmed attack; the name is the character's.
static async Task SendPlayer(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
{
    var (layout, name, classRow, pools) = (conn.Layout, conn.Character, conn.ClassRow, conn.Pools);
    var weapon = Skills.UnarmedFor(classRow);
    var cls = Skills.ClassFor(classRow);
    var baseStats = BaseStats(conn).ToList();
    var player = new PlayerObject(string.IsNullOrEmpty(name) ? "Exile" : name, ClassIndex: (byte)cls.IntegerId, GridX: layout.SpawnX, GridY: layout.SpawnY, Life: pools.Life, Mana: pools.Mana, Experience: conn.Experience,
        AnimatedObject: Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? cls.AnimatedObject,
        Skills: [.. Skills.PlayerDefaults(weapon), .. GemSkills(name, [.. weapon.Stats(), .. baseStats])], Stats: [.. weapon.Stats(), .. baseStats],
        Equipped: PlayerInventories.For(string.IsNullOrEmpty(name) ? "Exile" : name, Items).Equipped(LookOf));
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
    if (Environment.GetEnvironmentVariable("Game_GATEWAY_ENTER") == "1")
    {
        messages.Add(("0x0021 enter area", InstanceProtocol.EnterArea()));
        // Put the player in the wake zone so it is drawn and its actions/movement run.
        messages.Add(("0x01a9 player enters wake zone", InstanceProtocol.EnterWakeZone(PlayerId)));
        foreach (var (id, _) in layout.AreaTransitions) messages.Add(($"0x01a9 area transition 0x{id:x} enters wake zone", InstanceProtocol.EnterWakeZone(id)));
        // The attributes once more as a stats update. UI built on entering caches them (0x141e802d0 reads the Stats
        // component's getter); live 2026-10-18 the cache held 0 while the getter returned 14/14/32.
        messages.Add(("0x01b3 class attributes", InstanceEntities.StatsUpdate(PlayerId, [.. baseStats])));
    }
    foreach (var (label, bytes) in messages) await Send(remote, stream, ciphers, bytes, label);
}

sealed partial class Program
{
    internal const uint PlayerId = 0x1000;
    // The server's tick: 30 per second. Nothing the server times (an attack) is shorter than one tick.
    internal const int TickRate = 30;
    // Game_LEAGUE=hardcore treats every character as hardcore; otherwise the character's league decides (Auth's resolve).
    internal static readonly bool ForceHardcore = Environment.GetEnvironmentVariable("Game_LEAGUE")?.ToLowerInvariant() == "hardcore";
    // S→C 0x55 flags (11-skills.md §8): standard = bit2 (Resurrect at Checkpoint) + bit3 (Resurrect in Town); hardcore =
    // bit0 (a single Exit button, "Exit to the character selection screen", which sends C→S 0x153).
    internal static byte DeathScreenFlags(ClientState conn) => ForceHardcore || conn.Hardcore ? (byte)0x01 : (byte)0x0c;
    internal const double TickSeconds = 1.0 / TickRate;
    // Shown once per connection when the client moves in lockstep (InstanceProtocol.IsLockstepMovement).
    internal const string LockstepWarning = "Your Network Mode is lockstep, which this server does not support yet: your character stays " +
        "hidden and cannot move. Set Options > Game > Network Mode to Predictive, then log out to the character screen and Play again.";
    // How close (cells, centre to centre) the player must be to use an area transition.
    internal const double AreaTransitionReach = 6;
    // The height u32 of S→C 0x1ba (+0x28, passed with the move to Positioned vtable +0xd8) and 0x1b7 (→ +0x290): the
    // fixed height, 0x7fffffff = none, follow the terrain. 0 pinned a moving projectile under the ground (live 2026-10-18).
    internal const uint TerrainHeight = 0x7fffffff;
    // How close (cells, centre to centre) the player must be to pick an item up from the ground: WorldItem.ot's
    // InteractionAction interaction_distance_override = 20 grid cells, plus the radii of the item and the player
    // (object_size 2 each). Measured live 2026-10-18 (Positioned +0x294/+0x298): the client walked from (1301, 1250)
    // toward an item at (1278, 1304) and stopped at (1284, 1284), 20.9 cells away, on the first step inside the range.
    internal const double PickupReach = 20 + 2;

    // Shared by every connection; loaded on the first /spawn_mob.
    internal static readonly MonsterCatalog Monsters = new();
    // MonsterPacks per area (MonsterPacks.cs); loaded with the first area that has monsters.
    internal static readonly PackCatalog Packs = new();
    // The skills granted to players (the default attack); loaded with the first player.
    internal static readonly SkillCatalog Skills = new();
    // Stat names ↔ client keys (Stats.dat row + 1).
    internal static readonly StatCatalog Stats = new();
    // Item types (BaseItemTypes + data/item-types.json); loaded with the first player.
    internal static readonly ItemCatalog Items = new();
    // What dying monsters drop (ItemDrops.cs).
    internal static readonly ItemDrops Drops = new(Items);
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
    public string Describe() =>
        $"area {Id} (0x{Hash:x4}) grid {GridW}x{GridH} spawn ({SpawnX}, {SpawnY}), {(Walkable is null ? "no walkability" : "walkability")}, {AreaTransitions.Length} area transition(s)";

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
        string id = Ids.GetValueOrDefault(hash) ?? AreaTransitionCatalog.IdOf(hash) ?? $"0x{hash:x4}";
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
            var (sx, sy) = DeepestCell(cells, w, h);
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
            File.WriteAllText($"{Folder}/current.json.tmp", json);
            File.Move($"{Folder}/current.json.tmp", $"{Folder}/current.json", overwrite: true);
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
                return better.AreaTransitions.Length > 0 ? better : null;
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
sealed record PendingTransfer(ushort Area, ulong Instance, bool New, bool AtSpawn = false);

// A monster this connection spawned: label for /mobs, the server's estimate of its walk, and its AI state.
// A monster's attack: its skill id, attack time, reach (cells, centre to centre) and damage per hit.
sealed record MonsterAttack(ushort SkillId, double Seconds, double Range, int MinDamage, int MaxDamage)
{
    /// <summary>The attack's final stats (the client derives none, 11-skills.md §4): the swing time, reach and damage.
    /// These also go on the monster's Stats component.</summary>
    public IReadOnlyList<(uint, int)> Stats()
    {
        int ms = (int)Math.Round(Seconds * 1000);
        return
        [
            (StatKeys.MainHandMinPhysical, MinDamage), (StatKeys.MainHandMaxPhysical, MaxDamage),
            (StatKeys.MainHandMinTotal, MinDamage), (StatKeys.MainHandMaxTotal, MaxDamage),
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

sealed class SpawnedMonster(string label, WalkEstimator walk, int aggroRange, int stopRange, uint maxLife)
{
    public MonsterAttack? Attack { get; init; }
    public bool Hostile { get; init; } = true;
    // What it is, for the experience and items its death gives (null for monsters made without a variety).
    public MonsterCatalog.Variety? Variety { get; init; }
    public byte Level { get; init; } = 1;
    public MonsterRarity Rarity { get; init; }
    // Damage over time not yet applied (fractions of a point between ticks).
    public double Burn;
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
sealed record ChooserList(ushort Area, IReadOnlyList<ulong> Ids);

sealed class ClientState(AreaLayout layout, uint ticket, uint seed)
{
    public AreaLayout Layout { get; private set; } = layout;
    public uint Ticket => ticket;
    public uint Seed => seed;
    public string? Character { get; init; }
    public string? League { get; init; }
    public bool Hardcore { get; init; }
    /// <summary>The character's last town (HASH16, from Auth's resolve): Resurrect in Town goes there.</summary>
    public ushort LastTown { get; init; }
    /// <summary>The area the player left through an exit to come here, or null (login, Resurrect in Town).</summary>
    public ushort? CameFrom { get; init; }
    // The character's class: its Characters.dat row (the Witch: 1).
    public int ClassRow { get; init; } = SkillCatalog.WitchRow;
    public readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    // Server-side estimate of the client-predicted walk (reported in each 0x1ab), kept on walkable cells.
    public WalkEstimator Walk { get; private set; } = NewWalk(layout);

    /// <summary>Switches to a layout with a walk map (before the player exists).</summary>
    public void Use(AreaLayout better)
    {
        Layout = better;
        Walk = NewWalk(better);
    }

    /// <summary>Replaces an area transition of this connection's layout (dev changes; the walk estimate keeps going).</summary>
    public void ReplaceAreaTransition(uint id, AreaTransitionObject transition) =>
        Layout = Layout with { AreaTransitions = [.. Layout.AreaTransitions.Select(e => e.Id == id ? (e.Id, transition) : e)] };

    private static WalkEstimator NewWalk(AreaLayout l) =>
        new(l.SpawnX, l.SpawnY, Program.WalkSpeed, l.Walkable, l.GridW > 0 ? l.GridW : 4096, l.GridH > 0 ? l.GridH : 4096);
    public bool PlayerSent;
    public byte[] Carry = [];
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
    // The character's level and the pools that follow from it and the class (CharacterStats).
    public CharacterStats.Pools Pools { get; set; } = new(1, 50, 40, 2, 0);
    // The character's experience (the Player component's; the level follows from it) and its allocated passives
    // (PassiveSkillGraphId), both kept in its save (CharacterSaves). Changed under the Monsters lock.
    public uint Experience;
    public List<ushort> Passives = [];
    // /god (GodStats) and the running Righteous Fire, if any; buff instance ids (0x1c1) given out on this connection.
    public bool God;
    public RighteousFireState? RighteousFire;
    public ushort NextBuff = 1;
    public uint PlayerMaxLife => Pools.Life;
    // The player's life as the server keeps it (PlayerObject's Life, full on creation); 0 = dead.
    public uint PlayerLife;
    // The player's mana as the server keeps it: spent by gem skills (mana_cost), regenerated by RunPlayerTick.
    public uint PlayerMana;
    // (MonsterVarieties row, level) pairs already registered with 0x20 on this connection.
    public readonly HashSet<(int Row, byte Level)> MonsterTypes = [];
    // Spawned monsters by object id, in spawn order (for /mobs and /despawn).
    public readonly SortedDictionary<uint, SpawnedMonster> Monsters = [];
    // Items on the ground by object id (dropped with C→S 0x29).
    /// <summary>The area instance's state shared across connections: its ground items.</summary>
    public InstanceState Instance { get; init; } = null!;
    public volatile bool AiEnabled = true, Closed;
    // Whether the lockstep warning was shown on this connection.
    public bool WarnedLockstep;

    /// <summary>A walk estimate for a server object on this area's walk map.</summary>
    public WalkEstimator NewWalk(int x, int y, double speed) =>
        new(x, y, Math.Max(1, speed), Layout.Walkable, Layout.GridW > 0 ? Layout.GridW : 4096, Layout.GridH > 0 ? Layout.GridH : 4096);

    public (int X, int Y) SpawnNear((int X, int Y) at) => Layout.Near(at);
}
