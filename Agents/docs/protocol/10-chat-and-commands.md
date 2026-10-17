# 10 — Chat input, server replies and dev commands

Status (live, 2026-10-16): everything typed in the chat box reaches the Gateway. Slash commands run on the server and
answer with a message box. `/set_speed` works since 2026-10-17: the client reads the new `movement_velocity_+%` and
walks faster. The first version sent the wrong key (see §3). `/spawn_mob` works since 2026-10-17: it puts a visible,
idle monster next to the player (§5).

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
| `/spawn_mob <row, Id or name>[@level] [normal\|magic\|rare\|unique] [name]` | 0x20 + 0x2b9 + 0x1a9 for a MonsterVarieties row at a level (default 1), with a rarity and an optional name, a few cells from the player (§5) |

Every player can use every command; there are no permissions yet.

## 5. Monsters (`/spawn_mob`)

**Live 2026-10-17:** `/spawn_mob ZombieBite@13` three times put three Hungry Corpses next to the player, standing
idle (first with an "Unidentified" tag, gone once the identified bit was set). `check-components.js` traced all three: 11 components read in the order below,
198 bytes consumed, 0 left. Code: `MonsterObject`, `InstanceEntities.MonsterTypes` (`InstanceEntities.cs`),
`MonsterCatalog.cs` and the `spawn_mob` command in the Gateway. Tests: `InstanceEntitiesTests.Minimal_monster_blob_layout`,
`Monster_types_message_and_type_path`.

Sent, in order: **0x20** (once per variety and level on a connection), **0x2b9**, **0x1a9**.

- **Type.** The object-type register ([04](04-player-entity.md) §1) files every row of the 0x31a-byte table, which is
  **MonsterVarieties.dat** (10 691 rows × 0x31a in 3.29.3b), under the row's `Id` with `BaseMonsterTypeIndex` (row
  +0x48) as its .ot, e.g. `Metadata/Monsters/Zombies/ZombieBite` (Hungry Corpse) on
  `Metadata/Monsters/Zombies/Drowned/BiteZombie`. The variety Ids have no .ot of their own.
- **Level: S→C 0x20.** A monster type needs a level. The Monster component's factory (`0x141e6fd00`) throws
  **"Creating monster with no level"** when its type's `+0x20` is 0. The Monster type (vtable `0x1435a5a28`, parser
  slot 9 `0x141e701d0`) parses the number after the last **`@`** of the type path into `+0x20` (1..100; otherwise
  "Unable to parse monster level of …" / "Unable to create a monster type at level …"). The register files the plain Id
  only; the server registers the levelled path with **0x20** (instance group, dispatcher case 9, class `0x1435dc320`,
  read `0x1421a4f30`):
  ```
  u16 0x0020, u16 n (BE), u16 preload (BE, +0x10), n × u16 MonsterVarieties row (raw, little-endian), n × u8 level
  ```
  Per pair the handler looks the row up (`0x142288c90`, a row index into Data/MonsterVarieties.dat) and registers
  `"<Id>@<level>"` extending the plain Id (`0x141ec2540`; level 0 → the plain Id through `0x141ec28e0`), appending the
  type to instance `+0xc828`; then it loads the first `preload` new types (`0x140685030`). The 0x2b9 type is then
  `ObjectTypeHash("<Id>@<level>")`.
  - Without 0x20 (the first live try) the exception is thrown while the components are built (`0x142427510`, before
    the blob is read), and the cleanup then frees a Targetable component that was never created: an access violation
    in its deleter `0x14161d160`. So the client hangs and crashes instead of showing a message.
- **Components.** `Metadata/Monsters/Monster.ot`: Positioned, Stats, ObjectMagicProperties, BaseEvents, Pathfinding,
  Buffs, Life, Animated, Inventories, Actor, Monster ([../re/object-types.md](../re/object-types.md)). Every one that
  reads is already known from the player or the exit; Monster (`0x1435a6778`), BaseEvents and Pathfinding read nothing.
  - Positioned: 16 bytes. Its 8-byte prefix (`param_1[0x2b] + 0x21`) is written into the object's **Sector** component,
    so it is for types with one; the .ot keys (`0x141e2d860`) set +0x14..+0x20, +0x22, +0x23 but not +0x21.
    `serialise_collidable` (Monster.ot) sets +0x1b.
  - Actor: the two conditional u8 are read when `components[type+0xbc] + 0x1a8 < 1`. +0x1a8 is the Life component's
    first pool `+0x178 + 0x30`, the **current life** just read from the blob, so a living monster has 17 bytes.
  - Animated: bit0 + the variety's first `AOFiles` path, as for the player.
  - ObjectMagicProperties (base part `0x142201d70`): u16 flags, u8 `+0x7a`, then the component's own u8 flags.
    Flags bits 0–2 = **rarity** (`+0x7c`: 0 normal, 1 magic, 2 rare, 3 unique); bit 3 = **identified** (`+0x78`;
    without it the client tags the monster "Unidentified"). Only when identified: bit 7 (0x80) → u32 UTF-16 unit count +
    units, a **custom name** (`+0x18`, shown instead of the variety name); else bit 8 → u8 n + n × u32 keys (name parts,
    looked up by `0x14225a4d0`, not mapped); then bit 5 → mod list. Before them: bit 9 → 2 × u8, bits 0x10/0x40/0x400/0x800 →
    u8 n + n mod records (`0x142430380`). Live 2026-10-17: flags 0x08/0x09/0x0a/0x0b showed normal, magic, rare and
    unique monsters; 0x8b with "Headhunt" showed a unique named Headhunt (ObjectMagicProperties 24 bytes, 0 left).
  - Life: (current, 0, …) for life; mana and energy shield 0. The **maximum is the client's own**: its Monster type
    (`0x141e701d0`) sets stat 0x7d (and other default stats: 0x142–0x147, 0x75, 0x78, 0x87, …) from DefaultMonsterStats
    row `level − 1` (0x48-byte rows) and the variety: life = max(1, column × LifeMultiplier / 100), the column being
    AltLife1 (`+0x2c`) if the MonsterTypes row has AltLife1, else AltLife2 (`+0x38`), else MinionLife (`+0x1c`) for
    IsPlayerMinion, else MonsterLife (`+0x14`). The Gateway computes the same (`MonsterCatalog.BaseLife`) and sends it as
    the current life. Live 2026-10-17: Hungry Corpse at levels 13 (77 life) and 70 showed full life bars; the level-70 one
    also showed a cold icon (probably the resistance profile of its MonsterTypes row growing with level; not checked).
  - Blob: 112 bytes with the base list, plus the Animated path (198 bytes for Hungry Corpse).
- **Other component lists.** Of the 10 691 varieties, 4 513 have exactly the base list; most others append
  DiesAfterTime, StateMachine or Transitionable. The blob also writes Transitionable (`0x1421c24b0`, u8 flags),
  MinimapIcon (u8), and Preload, Functions, PlayerClass (no data). **StateMachine** (`0x141e3ee40`: 2 bytes, then
  entries per state of the type's .ot) and **DiesAfterTime** (live vtable unknown: `ret` or `0x141e40a80`) are refused
  with a message box. The Gateway knows each type's list from `data/object-types.json`
  (`python -I scripts/re/ot_components.py --all --json data/object-types.json`, about 2 minutes); without it, it
  assumes the base list.
- **Placement.** The nearest walkable cell 6–20 cells from the server's estimate of the player; ids from 0x3000. The
  world is active, so 0x2b9 is created at once; 0x1a9 puts it in the wake zone.

## 6. Open

- The chat line message, and the channel join request and its answer.
- A correct stat id ↔ name table: re-extract Stats.dat from this client's GGPK, or find why our table's row order
  differs.
- Monsters: mods (the ObjectMagicProperties mod lists; rarity life bonuses come from mods), the StateMachine and DiesAfterTime types (verify the Transitionable / MinimapIcon writers live
  too), AI and combat.
