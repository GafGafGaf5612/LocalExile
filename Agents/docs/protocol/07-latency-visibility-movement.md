# 07 — Latency (0x1b/0x1c and HNC), visibility (wake zone) and movement

State of 2026-10-16: the client's "network" graph stays flat (0–14 ms on localhost since 2026-10-19, §1), the Witch is drawn in Lioneye's
Watch, and clicking the ground walks her there at normal speed. Evidence:
[../re/evidence/2026-10-16-witch-visible.jpg](../re/evidence/2026-10-16-witch-visible.jpg).

## 1. Tick and ack: C→S 0x1b, S→C 0x1c

The client sends **0x1b** every 100–500 ms in state 3 (u8 counter + 4 bytes, 7 bytes in all; `0x142148880`) and pushes
the send time onto a queue at instance `+0xc458`. Each frame it sets `latency = now − queue.front` (or 0 when empty);
that is the perf overlay's "network: N ms". Without an answer the queue only grows, so the value climbs forever.

**0x1c** (instance group, dispatcher case 5, class `0x1435dc8c0`, read `0x141fa0720`): `u16 server delay ms`,
`u16 index`, `u32 HNC carrier`, all big-endian. `0x142155930` drops queue entries 0..index and returns entry `index`'s
send time; an index ≥ the queue's size clears the queue and returns `+0xc470`, the latest tick's send time (live Frida
2026-10-19). `+0xc488` is not a round trip: every client send stamps it with the time. One 0x1c with index 0 per
received 0x1b keeps the queue at the one tick in flight (`InstanceProtocol.Pong`).

Ticks that no server answers stay in the queue for good: those sent while the client leaves an area, or lost in
unframed bytes. Index 0 then always pops a stale entry. Live in The Coast the queue held 3 at every 0x1c (ticks every
167 ms), and the meter read ~400 ms. The first 0x1c of each connection, and the first after an unframed read, therefore
sends index 0xFFFF (`ClientInbox.NextAckIndex`). After that the queue held 1 at every 0x1c.

**What the latency graph shows.** The network update `0x142148880` runs once per client frame on the network thread:
it reads the socket, handles each 0x1c, sets the sample `+0xc480` = now − queue front (0 when empty), and sends a 0x1b
every 100 ms (500 ms after more than 100 ms without a client send, `+0xc488`). The graph and "latency N ms" show
`+0xc490` (`0x140d57cd0` feeds it to the perf ring). Two places move it:

- the network update raises it to the sample when the sample exceeds it by a margin (a tick overdue);
- the 0x1c handler (`0x14214b8dc`, after the pop) computes rtt = now − the popped send time, subtracts the 0x1c's
  u16 server delay from rtt and from the sample, and clamps the value into [sample − delay, rtt − delay]: it only
  drops when an rtt comes in below it.

So the value is the lowest round trip measured since the last overdue tick, and it cannot go below one client
update: the client reads the 0x1c at the start of the update after the one that sent the 0x1b (live: socket read →
pop → send the next tick, all in one update). Live Frida 2026-10-19 in town: sample 0, value 12, rtt 166–169 ms with
the client in the background (~6 updates/s), so the value stayed at the 12 measured while it ran at full frame rate.
The server answers within a millisecond; the 12–14 ms is the client's own frame interval. The u16 server delay could
hide it, but it is meant for time the server held the tick, so the Gateway sends 0.

## 2. HNC, the hidden side channel

The 0x1c u32 is not free data. `0x141fa0720` feeds its bits 9..31 then 0..8 (LSB first) into a byte accumulator on the
stream (`+0xd0`). Every completed byte is decrypted and run through `0x14299de50`:

- idle: a byte must decrypt to 0; otherwise it must be a packet type `0xDA`–`0xDD`
- then a length byte (≠ 0), then that many payload bytes, queued as a packet
- anything else throws **"HNC Protocol Violated"** (an exception dialog; a 0x1c with u32 = 0 did exactly that)

The HNC cipher is the login-style Salsa20 pair keyed with **km'** = the 64-byte 0x14 key XOR the table at
`0x142fe0da0`, then byte swaps 0↔0x15, 4↔2, 0xd↔0x2a, 0x30↔1, 0x1d↔0x1e, 0x32↔0x13 (`0x14299db60`); the client receives
with IV `km'[48:56]`. So an idle carrier is the keystream itself: `v = rotl(LE32(keystream[4]), 9)`
(`HncSender.NextIdleU32`, tested by replaying the client's unpacking in `HncTests`). The client also sends HNC packets
(type `0xDD` from the tick); the server ignores them for now.

## 3. Visibility: the wake zone (S→C 0x1a9)

An object created with 0x2b9 exists (position, components, camera follow, teleports) but is **not drawn and does not
update** until it is in the wake zone. **0x1a9** (world group, class `0x1435dd838`, object header only: `u16 id, u32
object id, u32 0, u16 0`) sets object `+0x8d |= 2` and calls `0x141ec0aa0` ("ObjectInWakeZone"); 0x1aa removes it. The
Gateway sends 0x1a9 for the player right after 0x21. That alone made the model appear (in the washed-up pose) and let
actions run.

Also sent, not proven necessary: Positioned `flags1 0x02` with (hidden 0, shown 1) (`0x141e2f0b0` → vtable +0xb0), and
Animated bit0 with the path `Metadata/Characters/Int/Int.ao` (the type already supplies it; the Animated deserializer
only reloads when the path differs or none is set). Neither made the model appear before 0x1a9 was added.

## 4. World-group object messages

The world handler `0x142187480` looks up `u32 object id` (message `+0x10`) and routes by id:

| Ids | Goes to | Notes |
|---|---|---|
| 0x51, 0x1ab–0x1b0, 0x1b4–0x1b6, 0x1ce–0x1d2, 0x1f0/0x1f1, 0x261, 0x2c6 | Actor `0x1421940a0` | actions |
| 0x1b7–0x1c0, 0x2b0 | Positioned `0x141e30c90` | position / movement |
| 0x1a9 / 0x1aa | wake zone in / out | |
| 0x2ba | component update by component index | |
| 0x2bb | remove object | |

Object header (all these messages): `u16 id, u32 object id (+0x10), u32 (+0x14), u16 (+0x18)`, written by `0x141f91f60`.

## 5. Movement

**C→S 0x25** "use skill at location" (sender `0x142166300`, class `0x1435bfbd8`, 16 bytes): `u32 x, u32 y` (target grid
cell), `u16 skill id` (Move = 0x2909 for the Witch), `u16` action `+0x11a` (an action sequence number: 0x1001, 0x1002, …;
only sent when ≥ 0), `u16` action `+0x122` (flags, 0x0408). 0x24 is the same aimed at an object id. The client starts the
action itself (prediction) but will not send another until the server echoes it.

**S→C 0x1ab** "action started" (class `0x1435c2340`, read `0x141fdda70`): object header, `u16 flags` (+0x38 → action
+0x122; bit 0x80 adds a `u32 target object id`), compact ints **actor position** (+0x2c/+0x30 → action +0x158) and
**target** (+0x24/+0x28 → action +0x130), `u16 skill id`, `u16 sequence` (+0x36 → action +0x11a). For the local,
predicted player (sequence ≥ 0) the handler compares the position pair with where the client thinks the player is and
**teleports** the player there when they are ≥ ~92 units apart (`|Δ|² ≥ 0x2110`); otherwise the echo is just a
confirmation (`InstanceProtocol.ActionStarted`). With the echo the client walks at its own speed and accepts the next
click. 0x1ac ends an action, but is ignored for predicted local actions.

The first version sent the target in the position pair: long clicks teleported the player, sometimes onto unwalkable
cells past the town edge (toward The Coast), where the client could no longer path anywhere. The server therefore needs
its own estimate of the player's position: `WalkEstimator` follows each order with A* over the walkability grid
(walkable = mask ≥ 2, 8 neighbours, no corner cutting, paths ≤ 1.2 × straight distance + 5, else the closest reachable
cell) at **37 cells/s** (measured with Frida: ~7.3 cells per 200 ms, = `base_speed` in `Character.ot`). Fitted to four
recorded client walks, its end points are within ~4.5 cells of the client's; live, the reported positions matched the
client's within ~4 cells. Grid: `data/walkability/1_1_town.bin` (the Frida dump, untracked).

**Passive movement speed.** The passives' `base_movement_velocity_+%` are summed into `CharacterStats.Pools` and the
walk estimate uses `37 × (1 + pct / 100)` cells/s; the same total is sent as the client's `movement_velocity_+%`, so the
client and server walk at the same speed. `ApplyMovementSpeed` runs on login, level, passive changes and area changes;
`/set_speed` adds its percentage on top of the passive total.
Only unconditional `increased Movement Speed` passives are modelled: action speed, more multipliers, gear/buff
`movement_velocity_+%`, conditional nodes and the `movement_velocity_cap` constant are not applied yet.

**Compact int** (writer `0x141fa3c50`): signed, 1 byte for −0x40..0x3f (value & 0x7f), else 2/3/4 bytes with first-byte
prefixes 0x80/0xc0/0xe0 carrying the top 6/5/4 bits, else 0xf0 + 4 bytes; big-endian (`InstanceProtocol.CompactInt`).

**S→C 0x1ba** move (class `0x1435dd7b8`, read `0x141fe0ff0`): header, `i32 x, i32 y, u32 +0x28, f32 speed, u8`; the
Positioned handler calls `0x141e34120` with duration = distance / speed. With speed 37 it overrode the client's own walk
and crawled (~1 cell per 1.5 s), so it is not sent for the local player (`Game_MOVE_MODE=move` to test).
**S→C 0x1b7** teleport: header, `i32 x, i32 y, u32 → +0x290, u32 → +0x2b4`; works (`Game_MOVE_MODE=teleport`).

**Lockstep.** Everything above is the client's **Predictive** network mode (Options → Game → Network Mode;
`[NETWORKING] networking_type=predictive` in `Documents\My Games\Path of Exile\production_Config.ini`). With
`networking_type=auto` the client chose lockstep on localhost (live 2026-10-18): the player object was created and
in the wake zone but never drawn, and clicks sent no 0x25. Each click sent instead:

| Id | Bytes | Notes |
|---|---|---|
| 0x018e | 15 | `018E 00000029 FFFFFFEF 2909 0402 00`: i32, i32 (small signed values: offsets from the player?), u16 skill id (0x2909 Move), u16 flags (0x0400 / 0x0402), u8 |
| 0x0192 | 10 | `0192 00000010 FFFFFFF2`: two i32, as in 0x018e |
| 0x0195 | 2 | id only, ends the click |

Lengths are from 26 captures of each, every one alone in its chunk; the field layout is inferred, not read from the
client's serializers. The server sends nothing back that lockstep accepts, so the Gateway frames the three
(`InstanceProtocol.IsLockstepMovement`), ignores them, and once per connection logs a warning and shows a message box
asking for Predictive. Supporting lockstep would mean finding the senders and the S→C answer the client waits for.

## 6. More client messages seen

| Id | Bytes | Notes |
|---|---|---|
| 0x015a | 2 | id only, after entering |
| 0x01ef | 2 | id only |
| 0x0099 | 20 | `0099 00000134 0334 0001 0001 3400 0101 0001 29` both times; meaning unknown |
| 0x004e | 5 | `004E 00 2909`: u8 + Move skill id, after a click (skill released?) |

## Open

- Area transitions: The Coast exit (and other exits) are world objects the server has to create; none are sent yet, so
  walking to the town edge just stops there.

- What the server should do with the client's HNC packets (type 0xDD).
- The meaning of 0x1ba's +0x28 and u8. Moving other (non-predicted) objects works with 0x1ab and the Move skill
  0x2909 (monsters, [10](10-chat-and-commands.md) §5); 0x1ba's float is distance per second (duration = distance / value).
- 0x0099, 0x004e and validation of moves against the walkability grid ([03](03-area-and-world.md) §7).
