# 08 — Area transitions (exits between areas)

State of 2026-10-16: Lioneye's Watch has its exit to **The Coast** at the town's east edge. The client creates it,
shows its icon on the map and puts it in the wake zone. Using it (changing area) is not implemented yet.

![The Coast exit on the town map](../re/evidence/2026-10-16-coast-exit-on-map.jpg)

Code: `AreaTransitionObject` (`src/GameServer.Core/Protocol/Instance/InstanceEntities.cs`), `Program.Exits`
(`src/GameServer.Gateway/Program.cs`). Test: `InstanceEntitiesTests.Area_transition_blob_layout_and_type`.

## 1. What an exit is

A server object of type **`Metadata/MiscellaneousObjects/AreaTransition`** (MurmurHash2 key, see
[04](04-player-entity.md#1-object-types-need-no-registration-message); the type is in the client's register, and live it
was created without "Unknown object type"). Its `.ot` file:

| Component | Notes |
|---|---|
| Positioned | `object_size = 6`, `blocking = false` |
| BaseEvents | `server_only = true` (no client data) |
| Animated | |
| AreaTransition | the destination |
| Stats | |
| ObjectMagicProperties | stat descriptions `map_stat_descriptions.txt` |
| MinimapIcon | `icon = 18` |
| InteractionAction | |
| client: Render, Targetable | `display_text = "ObjectDisplayTextAreaTransition"` (the label is the destination's name) |

Sent like the player: **0x2b9** (object id 0x2000 for the first exit) before 0x21, which queues it until the world
activates, then **0x1a9** (wake zone) after 0x21.

## 2. The component blob (traced with Frida, 41 bytes, consumed exactly)

The live object's components, in deserialize order: Positioned `0x1435a0f18`, BaseEvents `0x1435a55e8` (no data),
Animated `0x1435a5030`, AreaTransition `0x14346de00` (a client subclass, slot 17 `0x14166bb80` → base `0x141e5b870`),
Stats `0x1435dd4e8`, ObjectMagicProperties `0x1435a6ae0` (slot 17 → slot 20 `0x141e77990`), MinimapIcon
`0x1435a7018` (slot 20 `0x141e6f5d0`), InteractionAction `0x14359f7d0` (`0x141e1e240`), then two the loop does not reach
(`0x143468920`, `0x14346c238`).

| Part | Bytes | Fields |
|---|---|---|
| object flags, base data | `00 00` | as the player |
| Positioned | 16 | i32 x, i32 y (grid), u32, u16, 2 flag bytes |
| Animated | `00` | flags |
| **AreaTransition** | 5 | u8 flags (low nibble = state `+0xb2`, bit4/5/6 → `+0xb5/+0xb6/+0xb7`, bit7 → extra record `0x141e62df0`); **destination variant** (`0x141e62490`): u8 index + value, index 0 → u16, 1 → u32, **2 → u16 WorldAreas.HASH16** (resolved by `0x141e5be60` through `0x1422d9920`, the same lookup as 0x14/0x1d); then a compact int `+0xa4`, read when the type's flag (`type+0x78 → +0xb0 → +0x14`) is set and the state is 0 (true for this type) |
| Stats | 10 | as the player's comp1: compact-int n pairs, u32, u32, u8 |
| ObjectMagicProperties | 4 | **base magic properties first** (`0x142201d70`, called with the reader still in `rdx`: u16 flags, rarity = low 3 bits, u8 `+0x7a`, sections behind flag bits 0x10/0x40/0x200/0x400/0x800 and, with bit3, 0x80/0x100/0x20), then the component's own u8 flags (bit1 → u16 flavour text key, bit2 → string) |
| MinimapIcon | `00` | u8 flags (bit1 hidden/shown, bit0 → u16 icon override, bit3 → variant) |
| InteractionAction | `00` | u8 flags (bit2 → u8 [+ u16 action]) |
| tasks | `00` | |

The Coast exit: `00 00 | 80010000 D9000000 00000000 0000 0000 | 00 | 00 02 D27A 00 | 00 00000000 00000000 00 | 0000 00 00 | 00 | 00 | 00`.

How the deserializers were found: name string (`strexact.py`) → static initializer (`0x14006xxxx`: AreaTransition
`0x140067c60`, MinimapIcon `0x140068870`, InteractionAction `0x1400667c0`, ObjectMagicProperties `0x140068d00`,
BaseEvents `0x140067cd0`) → pool (`0x1445bb800`, `0x1445bc1b0`, `0x1445ba6c0`, `0x1445bbea0`, `0x1445bb7b0`) → factory
(`0x141e5b1d0`, `0x141e6e5e0`, `0x141e1c9e0`, `0x141e775c0`, `0x141e5e220`) → vtable → slot 17 (+0x88). Then three Frida
rounds fixed the byte counts: AreaTransition needed the compact int, and ObjectMagicProperties needed the 3-byte base
part that the decompiler hid (a call that reuses `rdx`).

## 3. Where exits go: the area's layout files

The server has to place each exit where the client's own layout expects it. For Lioneye's Watch:

1. `WorldAreas` row `1_1_town` (HASH16 `0x235c`): `Connections` = `1_1_1` The Twilight Strand, `1_1_2` **The Coast**
   (HASH16 `0x7ad2`); `TopologiesKeys` → `Topologies` row `ShoreEncampTownNormal` → DGR file
   `Metadata/Terrain/Act1/Town/Graphs/beachtown.dgr`.
2. The DGR has a single node: `19 11 0 "beachtown" R270 3 "default" "entrance2" "entrance1disabled" …`, i.e. room
   "beachtown", rotated 270°, with three named points enabled or disabled.
3. `Metadata/Terrain/Act1/Town/master.tsi` → `generate.rs` → room `Rooms/beach_town_unique_normal.arm` (version 36,
   18 × 30 tiles of 23 cells = 414 × 690). Its named points, in room cells:
   ```
   105 123 3.14162 "entrance1"
   196 384 0.261828 "entrance2"
   178 346 12.5664 "default"
   ```
4. The R270 placement maps room (x, y) to the 690 × 414 area grid as **(y, 413 − x)**. That is the only rotation that
   puts all three points on walkable cells of the client's walkability dump:

| Point | Room | Grid | Role |
|---|---|---|---|
| entrance1 | (105, 123) | (123, 308) | disabled in the DGR: the one-way Twilight Strand side |
| **entrance2** | (196, 384) | **(384, 217)** | **exit to The Coast**, east edge (matches the map icon) |
| default | (178, 346) | (346, 235) | the default arrival point (where a player coming into town appears) |

`scripts/re/worldareas.py` reads WorldAreas rows (Id, Name, Act, IsTown, HasWaypoint, Connections, AreaLevel,
HASH16) straight from `Data/WorldAreas.datc64`. The JSON extraction of that table is still broken past the early columns.

## 4. All zones: what it takes

- **Fixed layouts (towns, hideouts, many boss arenas):** the method above works per area: WorldAreas →
  Topologies → DGR → room `.arm` named points → rotation, then which point leads where. Named entrance points line up
  with `Connections` by name and DGR tags (enabled or "…disabled"). Matching names to destinations still needs care for
  areas with several exits. Doing this for every town and fixed area is mechanical work.
- **Generated areas (most outdoor and dungeon zones):** the layout comes from the area seed through the client's level
  generator (the DGR is a graph of rooms joined by randomly picked tiles). The exits sit on the generated layout, so the
  server must either reproduce the generator or learn the positions from the client (for example a Frida dump after
  generation, as was done for walkability). This is the large part.
- **Using an exit (captured live):** clicking the exit sends **C→S 0x24** "use skill on object", 12 bytes:
  `0024 00002000 0266 1066 0408` = u16 id, **u32 target object id (0x2000, the exit)**, u16 skill id `0x0266` (the
  interaction skill), u16 sequence (`0x1066`, `0x1067`, … like 0x25), u16 flags `0x0408`. It is 0x25's sibling aimed at
  an object instead of a cell. A `0016 08 0000` (C→S 0x16, 5 bytes) was also seen. The client keeps re-sending 0x24 while
  nothing answers. Next: echo the action (as 0x1ab does for 0x25) and then move the player to the destination area,
  most likely with a new 0x14-style instance assignment or an area change on the same connection.

## Open

- The server's answer to 0x24 and the area-change sequence (C→S 0x24 itself is captured, see §4).
- Exit label and icon: confirm the label shows "The Coast" (Render `display_text`) once you can hover it.
- Arrival points: when coming back from The Coast, the player should appear at entrance2. A fresh login should
  probably use "default" (346, 235) instead of today's (197, 261).
