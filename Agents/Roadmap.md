# Roadmap

What to build next, for anyone (person or agent) who wants to help. What already works is in [Status.md](Status.md).

## How to pick up an item

1. Open a GitHub issue named after the item (or comment on an existing one) so two people don't do the same work.
2. Read the docs the item links to. Most items start with reverse engineering: find how the client reads a message,
   then send it from the server. [docs/re/tooling.md](docs/re/tooling.md) has the workflow and recipes.
3. Verify with the real client through `.\scripts\dev\live.ps1`, and check the logs in `logs\`. Add tests for any
   message layout you implement (see `tests/GameServer.Core.Tests`).
4. In the same pull request: document what you found in `docs/protocol/` (new message ids, layouts, client addresses
   in [address-index.md](docs/protocol/address-index.md)), move the item to [Status.md](Status.md), and remove or
   update it here.

Items are in rough priority order within each section. "Known" says what is already found; "Start" says where to look.

## 1. Quality of life

### Exit to character selection (Esc)
- **Known:** Esc → "Exit to character selection" sends C→S 0x153 (2 bytes). Nothing answers, so the client waits forever.
- **Start:** find what the client expects after 0x153 (probably a reply on the instance connection, then the login
  connection's character list again). [09](docs/protocol/09-area-change.md) §5.
- **Done when:** Esc returns to character select and Play enters the game again.

### Chat lines and the global channel
- **Known:** C→S 0x15 (chat text) is parsed; replies use S→C 0x0d (message box). 0x12d shows BackendErrors rows, 0x157 is
  a PvP challenge, 0x26 disconnects. The client logs "Timeout while joining global chat channel 820".
- **Start:** [10](docs/protocol/10-chat-and-commands.md) §2 and §6. Find the S→C message that adds a line (sender + text) to the
  chat panel, and the channel join request and its answer.
- **Done when:** command replies and server messages appear as chat lines, and the channel join stops timing out.

### A server tick loop
- **Known:** `Program.TickRate` (30/s) only bounds attack times; hits use timers, the AI a 500 ms loop.
- **Done when:** one 30 Hz loop per instance drives movement estimates, AI and combat.

### Arrival points next to exits
- **Known:** exit positions come from the area's DGR/.arm named points ([08](docs/protocol/08-area-transitions.md) §3).
  The Coast spawns the player on its deepest walkable cell.
- **Start:** [08](docs/protocol/08-area-transitions.md) §4, [09](docs/protocol/09-area-change.md) §5.
- **Done when:** coming back from The Coast puts the player next to The Coast exit in town, and entering The Coast
  puts them at its entrance.

## 2. The world

### Area transitions without a walk-map run
- **Known:** every area gets its area transitions and destinations, but a generated area only has them once
  `live.ps1 -Walkmaps` captured its layout for that seed ([08](docs/protocol/08-area-transitions.md) §7). 11 atlas
  connections are unresolved, 41 are point-only, and four object types are sent as plain AreaTransition.
- **Start:** reproduce the client's level generator (the atlas already parses graphs, rooms and tiles), or ship
  captures for fixed seeds.
- **Done when:** a new seed gets its area transitions without Frida.

### Quests: Breaking Some Eggs (a1q4), the Submerged Passage
- **Known:** the whole chain is in [12](docs/protocol/12-quests.md): three Rhoa Nest chests drop three glyphs, the Glyph
  Wall takes them, quest flag `a1q4-PlacedGlyphs` removes the client-side `SunkenPassageBlocker` wall (the invisible
  wall in front of the cave today) and the wall and waterfall play their Transitionable animations.
- **Needs:** items (Roadmap "Items and inventories"), the Chest component's blob, the S→C quest flag message (unknown;
  starting points in 12 §4), a Transitionable state change.
- **Start:** find the quest flag message first: it alone opens the passage (e.g. a dev `/quest_flag a1q4-PlacedGlyphs`).
- **Done when:** the glyph quest can be played through and the Submerged Passage opens.

### Monsters: beyond `/spawn_mob`
- **Known:** spawning, levels, rarity, names, team, life, StateMachine / Transitionable types, walking, chasing and
  melee attacks all work ([10](docs/protocol/10-chat-and-commands.md) §5, [11](docs/protocol/11-skills.md) §6).
- **Start:** the "emerge" action for emerge monsters, mods, the boss life bar (StateMachine `boss_life_bar`), the 346
  varieties with NPC / ControlZone / BlightTower … components (each needs its deserializer, schema.md), spawning packs
  in areas. Check every new component with `scripts/re/frida/check-components.js`.
- **Done when:** areas are populated with monsters that fight back.

### NPCs in town
- Same object mechanism as monsters, with NPC types and their dialogue components. Depends on the monster work.

## 3. Gameplay

### The player's real stats
- **Known:** client stat key = Stats.dat row + 1 ([11](docs/protocol/11-skills.md) §8); `/set_stat` takes names. The
  server must send final values (the client derives none): the attack's are computed in `SkillCatalog.Unarmed.Stats`.
- **Done when:** life, mana, attributes, resistances and their derived values come from the server's own calculation
  (Characters.dat base values, level, items, passives) and are sent in the Stats component.

### Character progression
- Use `data/tables/ExperienceLevels.json` and `Characters.json` (class base life, mana, attributes) through an adapter
  in Host or Gateway, keeping `GameServer.Core` free of game-data files.
- The player blob's stats component ([04](docs/protocol/04-player-entity.md), "Open") needs the pairs the client
  expects for a real character.

### Classes other than the Witch
- Characters of any class can be created, but every one appears as a Witch. The player object type and blob are built
  for `Metadata/Characters/Int/Int`.

### Items: mods, rarity, using flasks and currency
- **Known:** items serialize, move, socket and drop ([13](docs/protocol/13-items.md)); every item is sent normal and
  identified with no mods. The Mud Flats glyphs are quest items (`Metadata/Items/QuestItems/Glyphs/Glyph1..3`,
  [12](docs/protocol/12-quests.md)).
- **Start:** the Mods component's mod lists (`0x142201d70`, sections behind flag bits 0x10/0x40/0x400/0x800, entries
  `0x142430380`), C→S 0x4a and 0x2d (13 §4, probably item use), flask charges after use.
- **Done when:** items have their implicit mods and a rarity, and flasks and currency can be used.

### A damage model (next)
- **Known:** both sides hit with flat damage ranges ([11](docs/protocol/11-skills.md) §5, §6); the player's life is a
  fixed 50.
- **Start:** the player's real stats (below), then armour / evasion / resistances / crits from the stats both sides
  already carry; monster skills beyond the first granted effect (ranged, spells).
- **Done when:** damage numbers match the character panel and the monsters' levels.

### Death screen variants
- **Known:** S→C 0x55 flags bit0/2/3 are mapped ([11](docs/protocol/11-skills.md) §7); bit4 and the trailing u32 / u8 /
  u16 are not.
- **Done when:** Sanctum and other modes show their own options; hardcore's "Exit" works (with Esc → Exit, C→S 0x153).

### Gem skills
- **Known:** active gems in equipment are granted with their level-1 stats; Fireball casts, flies as a server-made
  projectile object and hits the targeted monster ([11](docs/protocol/11-skills.md) §9–10). Support gems crash the client
  when granted as skills.
- **Start:** height grids for more areas (or in the walk-map recorder), the cast point at the hand, collision
  and the explosion; the skill record's support list (linked supports); gem experience and levels.
- **Done when:** Fireball with Arcane Surge linked can be cast and does its damage, and looks like the real one.

### Saving progress
- **Known:** items, equipment and the skill bar are saved per character in `data/saves/` ([13](docs/protocol/13-items.md) §7,
  [11](docs/protocol/11-skills.md) §11).
- **Done when:** the character's area, position and level are saved too (`GameServer.Storage` or the same saves).

## 4. Server design

- **Server-side movement:** validate moves against the walk map instead of only estimating
  ([07](docs/protocol/07-latency-visibility-movement.md) "Open"; messages 0x0099 and 0x004e).
- **HNC packets from the client** (type 0xDD): what the server should do with them
  ([07](docs/protocol/07-latency-visibility-movement.md)).
- **`GameServer.Sim`** is an empty placeholder: a home for a server-side world simulation (monsters, combat) when it is
  needed.
- **Several players** in one instance: the instance registry already supports it; the Gateway sends only the local player.

## 5. Project

- Rebuild the reverse-engineering setup from a fresh clone (Ghidra import, REA, Frida) and fix whatever the guide misses.
- Unnamed fields: [04](docs/protocol/04-player-entity.md), [05](docs/protocol/05-inventories-and-ui.md) and
  [09](docs/protocol/09-area-change.md) list fields whose meaning is unknown. Naming them is a good first task.
