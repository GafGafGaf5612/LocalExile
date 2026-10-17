using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using WalkEstimator = GameServer.Core.World.WalkEstimator;

// GameServer.Gateway — the instance (game) server the client connects to after login 0x14, or after 0x27 on an area
// change (Agents/docs/protocol/02, 09).
// Reads the plaintext opening (0x0F ticket + 0x0E hello), asks GameServer.Auth which area/instance the ticket belongs to
// (InstanceControl "resolve"), answers 0x11 so both directions switch to Salsa20 keyed by the ticket's instance key,
// sends the area (0x1d), answers the client's 0x7d with the player and the area's exits, and handles ticks, movement and
// exit clicks (0x24: change area, or with Ctrl the instance chooser; Agents/docs/protocol/09-area-change.md).
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

            var area = new AreaInstance(InstanceId: (uint)instanceId, AreaHash: areaHash, Seed: seed, Level: 0);
            await Send(remote, stream, ciphers, area.Encode(), $"0x001d {area}");

            // Anything after the opening was already encrypted by the client.
            var conn = new ClientState(layout, ticket, seed) { Character = resolved.Character, Hnc = new HncSender(keyring.Key(opening.Ticket)), Keyring = keyring };
            conn.Commands = Commands(remote, stream, ciphers, conn);
            byte[] rest = [.. pending.Skip(InstanceProtocol.OpeningLength)];
            if (rest.Length > 0) await OnClientData(remote, stream, ciphers, rest, conn);
            while (true)
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
    if (ticket != 0) await InstanceControl.CallAsync(new ControlRequest { Op = "leave", Ticket = ticket });
    Console.WriteLine($"[gateway] DISCONNECT {remote} total={total}");
}

// Decrypts client bytes and handles the messages we can frame (InstanceProtocol.SplitClientMessages):
// 0x7d (area generated) → the local player id (0x22), the player object (0x2b9), inventories, passive slots, the exits,
// and with Game_GATEWAY_ENTER=1 enter area (0x21) + wake zone; 0x1b (tick) → 0x1c (ack); 0x25 (move) → 0x1ab echo;
// 0x24 on an exit → change area (0x2b2, then 0x27 with the new instance) or, with Ctrl, the instance chooser (0xae).
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
            var (px, py) = conn.Walk.Order(use.X, use.Y, conn.Clock.Elapsed.TotalSeconds);
            await Send(remote, stream, ciphers,
                InstanceProtocol.ActionStarted(PlayerId, px, py, use.X, use.Y, use.SkillId, use.Slot, use.Flags),
                $"0x01ab action started {use} at ({px}, {py})");
            string mode = Environment.GetEnvironmentVariable("Game_MOVE_MODE") ?? "echo";
            if (mode == "teleport")
                await Send(remote, stream, ciphers, InstanceProtocol.Teleport(PlayerId, use.X, use.Y), $"0x01b7 teleport to ({use.X}, {use.Y})");
            else if (mode == "move")
            {
                float speed = float.TryParse(Environment.GetEnvironmentVariable("Game_MOVE_SPEED"), System.Globalization.CultureInfo.InvariantCulture, out var sp) ? sp : 37f;
                await Send(remote, stream, ciphers, InstanceProtocol.MoveTo(PlayerId, use.X, use.Y, speed), $"0x01ba move to ({use.X}, {use.Y}) speed {speed}");
            }
        }
        else if (id == InstanceTransfer.UseSkillOnObjectId)
        {
            var use = InstanceTransfer.ReadUseSkillOnObject(bytes);
            // In the background: the player may first walk to the target, and the read loop must keep answering ticks.
            _ = OnUseObject(remote, stream, ciphers, conn, use);
        }
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
                await SendPlayer(remote, stream, ciphers, conn.Layout, conn.Character);
                conn.PlayerSent = true;
                _ = RunMonsterAi(remote, stream, ciphers, conn);
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

// An exit click. Plain: change area (0x2b2 now, the move itself once the client acknowledges with 0x2cb).
// With Ctrl: list the destination's instances (0xae) for the client's instance chooser.
static async Task OnUseObject(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceTransfer.UseSkillOnObject use)
{
    SpawnedMonster? target;
    lock (conn.Monsters) target = conn.Monsters.GetValueOrDefault(use.TargetId);
    if (target is not null)
    {
        await Attack(remote, stream, ciphers, conn, use, target);
        return;
    }
    var exit = conn.Layout.Exits.FirstOrDefault(e => e.Id == use.TargetId);
    if (exit.Exit is null)
    {
        Console.WriteLine($"[gateway] {remote} <= 0x0024 use object {use} (not an exit)");
        return;
    }
    ushort dest = exit.Exit.DestinationHash16;
    // The client walks to the exit first; follow it and act once the player is there (a move meanwhile cancels).
    int action = Interlocked.Increment(ref conn.ActionCount);
    double now = conn.Clock.Elapsed.TotalSeconds;
    var (here, stop, arrive) = conn.Walk.Approach(exit.Exit.GridX, exit.Exit.GridY, ExitReach, now);
    Console.WriteLine($"[gateway] {remote} <= 0x0024 exit 0x{use.TargetId:x} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
    if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
    if (conn.ActionCount != action) return;
    if (Dist(stop, (exit.Exit.GridX, exit.Exit.GridY)) > ExitReach + 2)
    {
        Console.WriteLine($"[gateway] {remote} exit 0x{use.TargetId:x} out of reach from ({stop.X}, {stop.Y})");
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
// (Characters.dat MinDamage..MaxDamage; no damage model yet). Any later action (a move, another click) cancels both.
// One attack per attack time (WeaponSpeed ms scaled by the player's attack speed stats, a quarter early allowed for
// jitter); earlier repeats of 0x24 are ignored. At 0 the client's Life component runs its death; the corpse is
// removed 10 s later.
static async Task Attack(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
    InstanceTransfer.UseSkillOnObject use, SpawnedMonster m)
{
    var weapon = Skills.UnarmedFor(1);
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
    int damage = Random.Shared.Next(weapon.MinDamage, weapon.MaxDamage + 1);
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
    if (life > 0) return;
    _ = Task.Delay(10_000).ContinueWith(async _ =>
    {
        bool removed;
        lock (conn.Monsters) removed = conn.Monsters.Remove(use.TargetId);
        if (removed && !conn.Closed)
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(use.TargetId), $"0x02bb remove dead monster 0x{use.TargetId:x}");
    });
}

// The player's granted skills: the default attack with the unarmed weapon's stats and every stat the server set on the
// player since (a skill's stats include the character's; the client reads the skill's).
static IReadOnlyList<GrantedSkill> PlayerSkills(ClientState conn)
{
    return Skills.PlayerDefaults([.. FinalPlayerStats(conn)]);
}

// The player's stats as the server sets them: what /set_stat set, then the attack's final values computed from them
// (the client derives nothing itself: 11-skills.md §4).
static IEnumerable<(uint, int)> FinalPlayerStats(ClientState conn)
{
    Dictionary<uint, int> stats;
    lock (conn.Monsters) stats = new(conn.PlayerStats);
    foreach (var (k, v) in Skills.UnarmedFor(1).Stats(stats)) stats[k] = v;
    return stats.Select(kv => (kv.Key, kv.Value));
}

static double Dist((int X, int Y) a, (int X, int Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

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
        uint id = conn.NextObjectId++;
        // Full life: the client derives the maximum from the level (MonsterCatalog.BaseLife); mods are not sent yet.
        uint life = (uint)(Monsters.BaseLife(variety, level) ?? 100);
        // The type supplies the animated object; Game_MONSTER_AO=1 also sends the variety's first AOFiles path (Animated
        // bit0), which is not needed.
        string? ao = Environment.GetEnvironmentVariable("Game_MONSTER_AO") == "1" ? variety.AnimatedObject : null;
        ushort team = friendly ? (ushort)0 : (ushort)1;
        var monster = new MonsterObject(x, y, life, variety.ComponentsOrBase, ao, rarity, name, variety.StateMachine, variety.TransitionStates, team);
        // A monster type needs a level: 0x20 registers "<Id>@<level>" (once per connection), the 0x2b9 type is its hash.
        if (conn.MonsterTypes.Add((variety.Row, level)))
            await Send(remote, stream, ciphers, InstanceEntities.MonsterTypes([((ushort)variety.Row, level)]),
                $"0x0020 monster type {variety.Id}@{level} (row {variety.Row})");
        uint type = InstanceEntities.ObjectTypeHash(MonsterObject.TypePath(variety.Id, level));
        // The world is active by now, so 0x2b9 is created at once; 0x1a9 makes it drawn and updated.
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, type, monster.Blob()),
            $"0x02b9 create {rarity}{(friendly ? " friendly" : "")} monster 0x{id:x} {variety}{(name is null ? "" : $" \"{name}\"")} type=0x{type:x8} at ({x}, {y}), life {life}, components {string.Join(", ", variety.ComponentsOrBase)}");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), $"0x01a9 monster 0x{id:x} enters wake zone");
        lock (conn.Monsters)
            conn.Monsters[id] = new SpawnedMonster($"{variety.Name}@{level}", conn.NewWalk(x, y, variety.MovementSpeed),
                variety.AggroRange, Math.Max(2, variety.ObjectSize + 1), life);
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
                    if (!m.Aggro || d <= m.StopRange) continue;
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
        }
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
    {
        // The connection closed while sending.
    }
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

// The player is always a Witch for now (the only class whose object is mapped); the name is the character's.
static async Task SendPlayer(string remote, NetworkStream stream, ConnectionCiphers ciphers, AreaLayout layout, string? name)
{
    var weapon = Skills.UnarmedFor(1);
    var witch = new PlayerObject(string.IsNullOrEmpty(name) ? "Exile" : name, ClassIndex: 1, GridX: layout.SpawnX, GridY: layout.SpawnY, Life: 50, Mana: 40,
        AnimatedObject: Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? "Metadata/Characters/Int/Int.ao",
        Skills: Skills.PlayerDefaults(weapon), Stats: weapon.Stats());
    uint type = InstanceEntities.ObjectTypeHash("Metadata/Characters/Int/Int");
    var messages = new List<(string Name, byte[] Bytes)>
    {
        ("0x0022 local player 0x1000", InstanceEntities.SetLocalPlayer(PlayerId)),
        ($"0x02b9 create {witch} type=0x{type:x8}", InstanceEntities.CreateObject(PlayerId, type, witch.Blob())),
    };
    // The player's empty inventories (ids 1..n), needed by UI built on entering (flask bar → row 11).
    uint inventoryId = 1;
    foreach (var slot in InstanceInventories.PlayerDefaults)
        messages.Add(($"0x00c0 inventory {inventoryId} row {slot.Row}", InstanceInventories.AddInventory(inventoryId++, slot)));
    messages.Add(("0x003d passive slots 0-2", InstanceEntities.PassiveSlots()));
    // Area exits. Like the player they are queued by the world and created when 0x21 activates it.
    uint transitionType = InstanceEntities.ObjectTypeHash(AreaTransitionObject.MetadataPath);
    foreach (var (id, exit) in layout.Exits)
        messages.Add(($"0x02b9 create exit {exit} type=0x{transitionType:x8}", InstanceEntities.CreateObject(id, transitionType, exit.Blob())));
    if (Environment.GetEnvironmentVariable("Game_GATEWAY_ENTER") == "1")
    {
        messages.Add(("0x0021 enter area", InstanceProtocol.EnterArea()));
        // Put the player in the wake zone so it is drawn and its actions/movement run.
        messages.Add(("0x01a9 player enters wake zone", InstanceProtocol.EnterWakeZone(PlayerId)));
        foreach (var (id, _) in layout.Exits) messages.Add(($"0x01a9 exit 0x{id:x} enters wake zone", InstanceProtocol.EnterWakeZone(id)));
    }
    foreach (var (label, bytes) in messages) await Send(remote, stream, ciphers, bytes, label);
}

sealed partial class Program
{
    internal const uint PlayerId = 0x1000;
    // The server's tick: 30 per second. Nothing the server times (an attack) is shorter than one tick.
    internal const int TickRate = 30;
    internal const double TickSeconds = 1.0 / TickRate;
    // How close (cells, centre to centre) the player must be to use an exit.
    internal const double ExitReach = 6;

    // Shared by every connection; loaded on the first /spawn_mob.
    internal static readonly MonsterCatalog Monsters = new();
    // The skills granted to players (the default attack); loaded with the first player.
    internal static readonly SkillCatalog Skills = new();
    // Stat names ↔ client keys (Stats.dat row + 1).
    internal static readonly StatCatalog Stats = new();

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
    // "Increased attack speed" stats that apply to an unarmed melee attack (attack time = base / (1 + sum / 100)).
    public const uint AttackSpeedPct = 0x47, MeleeAttackSpeedPct = 0x49, MainHandAttackSpeedPct = 0x126,
        UnarmedMeleeAttackSpeedPct = 0xb19;
}

/// <summary>What the Gateway knows about an area's layout: the terrain grid (for movement estimates), where the player
/// appears and the exits. Towns are fixed; other areas are generated from the seed, so their walkability only exists as
/// a dump of the client's walkability grid per (area, seed).</summary>
sealed record AreaLayout(ushort Hash, string Id, int GridW, int GridH, int SpawnX, int SpawnY,
    Func<int, int, bool>? Walkable, (uint Id, AreaTransitionObject Exit)[] Exits)
{
    public string Describe() =>
        $"area {Id} (0x{Hash:x4}) grid {GridW}x{GridH} spawn ({SpawnX}, {SpawnY}), {(Walkable is null ? "no walkability" : "walkability")}, {Exits.Length} exit(s)";
}

static class AreaLayouts
{
    public const ushort LioneyesWatch = 0x235c, TheCoast = 0x7ad2;

    // Lioneye's Watch is one room, Metadata/Terrain/Act1/Town/Rooms/beach_town_unique_normal.arm (18 x 30 tiles), placed
    // R270 by Graphs/beachtown.dgr ("default" "entrance2" "entrance1disabled"). Room (x, y) → grid (y, 413 - x):
    // entrance2 (196, 384) → (384, 217) on the east edge is the exit to The Coast (1_1_2, 0x7ad2); entrance1 is the
    // disabled one-way Twilight Strand side; default (178, 346) → (346, 235). Agents/docs/protocol/08-area-transitions.md.
    // Spawn (197, 261): the walkable cell farthest from any edge (Agents/docs/protocol/03 §7).
    static readonly (uint Id, AreaTransitionObject Exit)[] TownExits =
        [(0x2000, new AreaTransitionObject(GridX: 384, GridY: 217, DestinationHash16: TheCoast))];

    static readonly Dictionary<ushort, string> Ids = new() { [LioneyesWatch] = "1_1_town", [TheCoast] = "1_1_2" };

    const string Folder = "data/walkability";

    public static AreaLayout For(ushort hash, uint seed)
    {
        string id = Ids.GetValueOrDefault(hash, $"0x{hash:x4}");
        byte[]? cells = LoadCells(id, seed, out int w, out int h);
        if (hash == LioneyesWatch)
        {
            // Older dumps were saved as data/walkability/1_1_town.bin without a .json.
            if (cells is null && File.Exists($"{Folder}/1_1_town.bin")) { cells = File.ReadAllBytes($"{Folder}/1_1_town.bin"); (w, h) = (690, 414); }
            bool fits = cells is not null && cells.Length == 690 * 414 && (w, h) == (690, 414);
            return new AreaLayout(hash, id, 690, 414, 197, 261, fits ? Walkable(cells!, w, h) : null, TownExits);
        }
        if (cells is not null)
        {
            var (sx, sy) = DeepestCell(cells, w, h);
            return new AreaLayout(hash, id, w, h, sx, sy, Walkable(cells, w, h), []);
        }
        // No walk map yet: a position inside any grid; the client may place the player on unwalkable ground.
        return new AreaLayout(hash, id, 0, 0, 197, 261, null, []);
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

sealed record PendingTransfer(ushort Area, ulong Instance, bool New);

// A monster this connection spawned: label for /mobs, the server's estimate of its walk, and its AI state.
sealed class SpawnedMonster(string label, WalkEstimator walk, int aggroRange, int stopRange, uint maxLife)
{
    public string Label => label;
    public WalkEstimator Walk { get; set; } = walk;
    public int AggroRange => aggroRange;
    public int StopRange => stopRange;
    public uint MaxLife { get; } = maxLife;
    public uint Life = maxLife;
    public bool Aggro, Dead;
    public (int X, int Y)? LastTarget;
}

// The last instance list shown (0xae); 0xaf picks a row of it.
sealed record ChooserList(ushort Area, IReadOnlyList<ulong> Ids);

sealed class ClientState(AreaLayout layout, uint ticket, uint seed)
{
    public AreaLayout Layout { get; private set; } = layout;
    public uint Ticket => ticket;
    public uint Seed => seed;
    public string? Character { get; init; }
    public readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    // Server-side estimate of the client-predicted walk (reported in each 0x1ab), kept on walkable cells.
    public WalkEstimator Walk { get; private set; } = NewWalk(layout);

    /// <summary>Switches to a layout with a walk map (before the player exists).</summary>
    public void Use(AreaLayout better)
    {
        Layout = better;
        Walk = NewWalk(better);
    }

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
    // Server objects created after entering (monsters); the player is 0x1000 and exits start at 0x2000.
    public uint NextObjectId = 0x3000;
    // The player's attacks: the next time one may start (Clock seconds). The client repeats 0x24 while the button is held.
    public double NextAttackAt;
    // Counts the player's actions; an attack's hit lands only if no other action started since (moving cancels it).
    public int ActionCount;
    // The stats the server has set on the player after creating it (/set_stat, /set_speed): what the client computes
    // with, so the server can follow it (attack time).
    public readonly Dictionary<uint, int> PlayerStats = [];
    // (MonsterVarieties row, level) pairs already registered with 0x20 on this connection.
    public readonly HashSet<(int Row, byte Level)> MonsterTypes = [];
    // Spawned monsters by object id, in spawn order (for /mobs and /despawn).
    public readonly SortedDictionary<uint, SpawnedMonster> Monsters = [];
    public volatile bool AiEnabled = true, Closed;

    /// <summary>A walk estimate for a server object on this area's walk map.</summary>
    public WalkEstimator NewWalk(int x, int y, double speed) =>
        new(x, y, Math.Max(1, speed), Layout.Walkable, Layout.GridW > 0 ? Layout.GridW : 4096, Layout.GridH > 0 ? Layout.GridH : 4096);

    /// <summary>A walkable cell a few cells from (x, y), nearest first; (x + 8, y) without a walk map.</summary>
    public (int X, int Y) SpawnNear((int X, int Y) at)
    {
        if (Layout.Walkable is not { } walkable) return (at.X + 8, at.Y);
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
