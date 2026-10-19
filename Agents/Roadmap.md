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

### Chat lines and the global channel
- **Known:** C→S 0x15 (chat text) is parsed; replies use S→C 0x0d (message box). 0x12d shows BackendErrors rows, 0x157 is
  a PvP challenge, 0x26 disconnects. The client logs "Timeout while joining global chat channel 820".
- **Start:** [10](docs/protocol/10-chat-and-commands.md) §2 and §6. Find the S→C message that adds a line (sender + text) to the
  chat panel, and the channel join request and its answer.
- **Done when:** command replies and server messages appear as chat lines, and the channel join stops timing out.

### A server tick loop
- **Known:** `Program.TickRate` (30/s) only bounds attack times; hits use timers, the AI a 500 ms loop.
- **Done when:** one 30 Hz loop per instance drives movement estimates, AI and combat.

## 2. The world

### Every area's layout at seed 1
- **Known:** generated areas only exist in the client; `live.ps1 -Walkmaps` captures an area's walk map, area
  transitions and waypoint when the client generates it ([08](docs/protocol/08-area-transitions.md) §7). About 30
  areas are captured; waypoint travel to an uncaptured one (The Submerged Passage) puts the player outside the map
  ([16](docs/protocol/16-waypoints.md) "Open").
- **Start:** a dev command that walks the WorldAreas list with `/goto` and waits for each capture.
- **Done when:** every act area has its seed-1 capture, and every waypoint area its waypoint in it.

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
- **Needs:** items (Roadmap "Items and inventories"), the Chest component's blob, a Transitionable state change. The
  S→C quest flag message is known: 0x2ba to the Player component, update flag 0x02 ([16](docs/protocol/16-waypoints.md) §3).
- **Start:** a dev `/quest_flag a1q4-PlacedGlyphs` with `InstanceEntities.PlayerQuestFlagsUpdate`: check that it alone
  opens the passage. Then real quest progress: set and save flags instead of deriving them from visits (16 §4).
- **Done when:** the glyph quest can be played through and the Submerged Passage opens.

### Monsters: beyond `/spawn_mob`
- **Known:** spawning, levels, rarity, names, team, life, StateMachine / Transitionable types, walking, chasing and
  melee attacks all work ([10](docs/protocol/10-chat-and-commands.md) §5, [11](docs/protocol/11-skills.md) §6); areas
  are populated with their MonsterPacks and unique boss ([14](docs/protocol/14-monster-packs.md)).
- **Start:** the "emerge" action for emerge monsters (then add the skipped emerge packs), mods for magic/rare monsters
  (and their life bonus), the boss life bar (StateMachine `boss_life_bar`), pack `Grounds`, keeping an instance's
  monsters (dead stay dead), the 346 varieties with NPC / ControlZone / BlightTower … components (each needs its
  deserializer, schema.md). Check every new component with `scripts/re/frida/check-components.js`.
- **Done when:** monsters in areas match the official game's (mods, rarity bonuses, emerge, boss bar).

### NPCs: dialogue options, shops and the other towns
- **Known:** NPC objects, Lioneye's Watch placements and the dialogue window (S→C 0x84, C→S 0x85) work
  ([17](docs/protocol/17-npcs.md)). The client picks the NPCTalk row itself (`0x141e76d40`).
- **Start:** pick a dialogue option live and read the C→S message; NPCDialogueWindow's option handler (`0x140723d60`,
  `0x14071f6d0`). Shops from NPCShop / ShopSets. Placements for the other towns (`NpcCatalog.Towns`, with `/pos`).
- **Done when:** talk options play their text, Nessa and Tarkleigh trade, and every town has its NPCs.

## 3. Gameplay

### The player's real stats
- **Known:** client stat key = Stats.dat row + 1 ([11](docs/protocol/11-skills.md) §8); `/set_stat` takes names. The
  server must send final values (the client derives none): the attack's are computed in `SkillCatalog.Unarmed.Stats`.
- **Done when:** life, mana, attributes, resistances and their derived values come from the server's own calculation
  (Characters.dat base values, level, items, passives) and are sent in the Stats component.

### Character progression: the rest of the Passive Skill Tree
- **Known:** experience, levels, S→C 0x33 and C→S 0x34 (allocate) work ([15](docs/protocol/15-progression.md)).
- **Start:** the refund click (with refund points, 0x33's first u16), masteries (0x33's u32 list), ascendancy points,
  quest points (0x33 +0x8a); the tree's connections (PassiveSkillGraph.psg) so the server checks them too.
- **Done when:** passives can be refunded from the tree and masteries chosen.

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
- **Start:** height grids for more areas (or in the walk-map recorder), collision with doodads and objects that have their own colliders;
  the skill record's support list (linked supports); gem experience and levels.
- **Done when:** Fireball with Arcane Surge linked can be cast and does its damage, and looks like the real one.

### The game's start and resurrection
- **Known:** items, equipment, the skill bar, experience and passives are saved per character in `data/saves/`
  ([13](docs/protocol/13-items.md) §7, [11](docs/protocol/11-skills.md) §11, [15](docs/protocol/15-progression.md)), and
  every login starts at the last town's spawn point ([09](docs/protocol/09-area-change.md) §4). This follows the real
  game on purpose: the area and position a character was in are not saved, because the real client never logs anyone
  into them. New characters start in Lioneye's Watch (`AccountService.StartingArea`). The death screen's "Resurrect in
  Town" goes to the last town, with a loading screen; "Resurrect at Checkpoint" goes to the area's arrival point
  ([09](docs/protocol/09-area-change.md) §7).
- **Done when:** as in the real game, a new character starts on The Twilight Strand (`StartingArea`), reaches Lioneye's
  Watch only after defeating Brutus, and logs in on the Twilight Strand until then; "Resurrect at Checkpoint" goes to
  the last activated checkpoint of the area, with no loading screen (checkpoint objects are not implemented yet).
  Without an activated checkpoint it revives where the player entered the area, which the Gateway already does. Dying
  in a boss arena revives just outside the arena's door, even with a checkpoint activated; dying in a map revives in
  the hideout (no hideouts yet).
- **One-way entries:** the arrival next to the exit back needs an exit back. Lioneye's Watch from The Twilight Strand
  uses the town's named point `entrance1` (`AreaLayout.OneWayArrivals`, [08](docs/protocol/08-area-transitions.md) §3):
  in the real game the player comes out on the town side of the gate. Other one-way links fall back to the area's spawn
  until their named entrance points are added.

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
