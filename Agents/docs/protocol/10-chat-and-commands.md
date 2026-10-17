# 10 — Chat input, server replies and dev commands

Status (live, 2026-10-16): everything typed in the chat box reaches the Gateway. Slash commands run on the server and
answer with a message box. `/set_speed` works since 2026-10-17: the client reads the new `movement_velocity_+%` and
walks faster. The first version sent the wrong key (see §3). `/spawn_mob` is a stub.

## 1. C→S 0x15: chat input

| Field | Encoding |
|---|---|
| text | compact wide string (`LoginWriter.WStr`: u16 count, per code point 1 byte < 0x80, 0x80 + UTF-16LE, 0xC0 + u32) |
| u8 | 0 in every capture |

What arrives is the text as typed: slash commands as is (`/spawn_mob 42`), plain chat with the channel prefix the client
added (`#hello server` for global). The client handles none of our commands locally. `InstanceChat.Length` frames it
(the length depends on the text), and `SplitClientMessages` uses it for id 0x15.

## 2. Server → client text

Candidates that read a wide string were found by mapping every S→C factory case to its class and read function (slot
2) and keeping those that call the wstr reader `0x140e0cbe0`. Each was then sent live with `/raw`:

| Id | Layout | What the client did |
|---|---|---|
| **0x0d** | wstr | **Message box with the text and an OK button.** Class `0x1435bf650`, dispatcher case 0 → listeners at +0xa1f0. Used for replies. |
| 0x12d | u16 code, wstr | Shows `BackendErrors.dat` row `code - 1` (1 → "An account with this name already exists."). The UI listener `0x140be6ef0` prints the row text as is and uses the wstr only to compare a name, so `{0}` is not filled in. |
| 0x26 | u16, wstr | Disconnect with a reason (handler `0x142149ca0` closes the socket). Do not send it. |
| 0x157 | wstr, u8 | "System challenges you to PvP" popup (duel request). |
| 0x10 | u32, wstr | Nothing visible (no dispatcher case). |
| 0x188 | wstr, u16 | Nothing visible. |
| 0xa9 | party info | Not chat. |
| 0xc9 | u8, wstr, wstr | Friend presence map. Not chat. |

The chat line message (sender + text in the chat panel) is not found yet. It does not go over the login connection
(Auth received nothing after Play). The client also prints "Timeout while joining global chat channel 820", so the
channel join is a request we don't answer yet.

## 3. S→C 0x1b3: stats update

World group; class `0x1435dda98`, read `0x141fe1350`. The world handler (`0x142187480`, case 0x1b3) looks up the
object's Stats component and calls `0x1424cfaf0(stats, key, value)` for each pair.

```
u16 0x1b3, u32 object id, u32 0, u16 0,
compact uint n, n × (compact uint stat key, compact int value)
```

- Unsigned compact (`0x14089cad0`): < 0x80 is 1 byte; then 0x80 + 14 bits, 0xc0 + 21 bits, 0xe0 + 28 bits; else 0xf0 +
  u32. Signed compact is `0x141fa3d60` (`InstanceProtocol.CompactInt`).
- The stat key is the client's own stat id: the int its getter `0x1424cfa40(stats, key)` binary-searches in the sorted
  (key, value) vector at stats+0xf0, which `0x1424cfaf0` fills. It is **not** the row of our extracted
  `data/tables/Stats.json`. That order drifts: client key 245 is `movement_velocity_+%`, which is row 244 there, and
  the keys the Life component reads match unrelated rows. Take keys from client code (Frida on the getter shows which
  keys a feature reads, with callers). `StatKeys` in the Gateway lists the known ones.
- Walk speed (`0x141e7c0b0`): base (`+0x548`) × (1 + key **0xf5** / 100, at least −100). With a flag it is also
  multiplied by (1 + key 0xce6 / 100). It is capped by key **0x44f** (`movement_velocity_cap`) when that is positive.
  Measured live: sending 0xf5 = 200 makes the getter return 200 and the walk visibly faster.
- The Stats component has no 0x2ba (component update) handler: its vtable slot +0xa0 is a no-op. So 0x1b3 is the way to
  change stats.

## 4. Commands (Gateway, `ChatCommands.cs`)

| Command | Effect |
|---|---|
| `/help` | lists the commands (message box) |
| `/raw <hex>` | sends the bytes as one S→C message (dev probe; spaces allowed) |
| `/set_speed <percent>` | 0x1b3 `movement_velocity_+%` = percent − 100 on the player; the server's walk estimate uses 37 × percent / 100 cells/s |
| `/set_stat <client key> <value>` | 0x1b3 for any stat, by client key (not the Stats.json row) |
| `/spawn_mob <id>` | not implemented: monster objects need their own component data (see below) |

Every player can use every command; there are no permissions yet.

## 5. Open

- The chat line message, and the channel join request and its answer.
- A correct stat id ↔ name table: re-extract Stats.dat from this client's GGPK, or find why our table's row order
  differs.
- `/spawn_mob`: a monster is a 0x2b9 object of a `Metadata/Monsters/...` type. Its component list (Monster, Life,
  Actor, Positioned, Animated, Stats, …) has to be serialized like the player's ([04](04-player-entity.md)). The
  Monster component's deserializer and the MonsterVarieties row id are not mapped yet.
