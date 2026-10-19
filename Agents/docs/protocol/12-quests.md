# 12 — Quests (research notes, nothing implemented)

State of 2026-10-18: no quest is implemented. These notes collect what the game files say about quests, worked out on
the first one players hit that changes the world: **a1q4 "Breaking Some Eggs"** in The Mud Flats, which opens the
Submerged Passage. Seen live: the area transition to The Submerged Passage spawns
([08](08-area-transitions.md) §7), but an invisible wall in front of the cave stops the player.

## 1. The data model

| Table (`data/tables/`) | What it holds | a1q4 |
|---|---|---|
| `Quest.json` | one row per quest: `Id`, `Act`, `Name`, `QuestId`, icon | row 2: `a1q4`, act 1, "Breaking Some Eggs", `QuestId` 4, `Art/2DArt/QuestIcons/mudflats.dds` |
| `QuestFlags.json` | named boolean flags, `Id` + `HASH32` | rows 23–25, 29, 34–37, 50–52 below |
| `QuestStates.json` | the quest log text per state: `Quest` (Quest row), `Order`, `FlagsPresent` / `FlagsMissing` (QuestFlags rows), `Text`, `Message`, `MapPinsKeys1`, `MapPinsTexts` | rows 20–28 |
| `QuestItems.json` | quest item rules (`TriggeredQuestFlag`, `Script` …) | 53 rows; the glyph rows did not decode with our schema (open) |
| `BaseItemTypes.json` | the items | rows 1531–1533: the glyphs |
| `Chests.json` | chest types: `Id`, `Name`, `AOFiles`, `BaseItemTypesKey` | rows 183–185: the Rhoa Nests |

A quest's progress is the set of its flags; the quest log shows the first `QuestStates` row (by `Order`) whose
`FlagsPresent` are all set and whose `FlagsMissing` are all clear.

### a1q4's flags

| QuestFlags row | Id | Set when |
|---|---|---|
| 23 | `a1q4-KnowsAboutGlyphs` | a glyph is picked up (first one) |
| 25 | `a1q4-SeenGlyphWall` | the player sees/clicks the glyph wall |
| 29 | `a1q4-TarkleighClue` | Tarkleigh's hint (town) |
| 50, 51, 52 | `a1q4-HaveGlyph1`, `-HaveGlyph2`, `-HaveGlyph3` | holding each glyph |
| **24** | **`a1q4-PlacedGlyphs`** | the glyphs are placed in the wall: **opens the passage** |
| 34–37, 2147, 2148 | `NessaCongratulated`, `QuestRewardSeen`, `QuestRewardTaken`, `Congratulated`, `QuestReward2Seen/Taken` | reward dialogue |

### a1q4's states (QuestStates, highest Order first = earliest)

| Order | Needs | Log text (short) |
|---|---|---|
| 8 | 29 | Investigate the Rhoa Nests (Tarkleigh's clue) |
| 7 | 23 | Find a use for the strange glyph |
| 6 | 25 | Investigate the Mud Flats for a way to lower the water |
| 5 | 25, 29 | Investigate the Rhoa Nests |
| 4 | 23, 25 | Find the Glyphs in the Rhoa Nests |
| 3 | 25, 50, 51, 52 | Take the Glyphs to the Glyph Wall |
| 2 | 24 | Talk to Tarkleigh for your reward |
| 1 | 24, missing 27, 128, 3, 129, 130 | The Submerged Passage is now open |
| 0 | 36 | Quest Complete |

## 2. The objects in The Mud Flats (1_1_3)

| Object | Type (`.ot`) | Where it comes from | Components the 0x2b9 blob carries |
|---|---|---|---|
| Rhoa Nest ×3 | `Metadata/Chests/QuestChests/RhoaChest`, `RhoaChest2`, `RhoaChest3` (Chests rows 183–185; model `Metadata/Chests/QuestChests/beach_rhoanest_v01_01_chest.ao`) | tile `Metadata/Terrain/Beach/Features/rhoanest_v01_01.tdt` (tag `rhoanest`): its `.tmd` has one record `beach_rhoanest_v01_01_chest`, type `RhoaChest`, at tile cell (34.5, 34.5). Only `RhoaChest` appears in tile data, so the server picks which nests become `RhoaChest2`/`3` (one per glyph) | Positioned, BaseEvents, Animated, **Chest**, Stats, ObjectMagicProperties, InteractionAction, MinimapIcon (extends `Chests/QuestChests/QuestChest` → `Chests/Chest`) |
| the glyphs | `Metadata/Items/QuestItems/Glyphs/Glyph1` **Haliotis Glyph** (1×1), `Glyph2` **Ammonite Glyph** (1×1), `Glyph3` **Roseus Glyph** (1×2); BaseItemTypes 1531–1533, item class 31 | each nest chest's `BaseItemTypesKey` | items: not implemented |
| Glyph Wall (the thing on the wall the player clicks) | `Metadata/QuestObjects/WaterCave/GlyphWall` (`object_size 8`, MinimapIcon icon 5 `hide_on_transition`, Render "ObjectDisplayTextGlyphWall", display type Quest) | cave tile `Metadata/Terrain/Beach/Transitions/BeachToWatercave_v2/beach_to_watercave_v2.tdt`: record `…_stone_switch` at tile cell (38.5, 70.5), z −165.6, model `…/beach_to_watercave_v2_stone_switch.ao` | Positioned, BaseEvents, Animated, **Transitionable**, MinimapIcon, InteractionAction (`extends "nothing"`, so Transitionable is read) |
| the waterfall | `Metadata/QuestObjects/WaterCave/WaterTransition` (`waterdrop.ao`) | room `Metadata/Terrain/Act1/Area3/Rooms/to_cave.arm`, doodad at room (71, 147) | Positioned, BaseEvents, Animated, **Transitionable** |
| the cave entrance | `Metadata/QuestObjects/WaterCave/WaterCaveAreaTransition` (extends AreaTransition + empty Transitionable) | cave tile record `…_area_transition` at (55.5, 174.5), z −61.3 | live: the client reads only AreaTransition's components (§4) |
| **the invisible wall** | `Metadata/QuestObjects/WaterCave/SunkenPassageBlocker`: `object_size 4`, **`blocking = true`**, **`unblock_quest_flag = "a1q4minusPlacedGlyphs"`**, BaseEvents `no_serialise` | room `to_cave.arm`: about 20 of them in a diagonal line across the path (room (55..109, 60..114)), model `dead_seagull_v01_03.ao` (not drawn) | **client-side**: the client spawns them from the room and removes the blocking when the flag is set. `minus` stands for `-` (the `.ot` parser `0x141de7b20` replaces it): the flag is `a1q4-PlacedGlyphs` |
| other cave doodad | `…_waterfall` (Doodad, client) | cave tile record | — |

The nest rooms (`to_cave` excluded) carry the tag `rhoanest` and named points `"%area%-nestpack"` (monster packs
around each nest).

## 3. The sequence (as the player sees it)

1. Open three Rhoa Nests (chests). Each drops one glyph; picking it up sets `KnowsAboutGlyphs` and `HaveGlyphN`.
2. At the Glyph Wall (`SeenGlyphWall`), with all three glyphs: interact. The glyphs are taken, `PlacedGlyphs` is set,
   the wall transitions (client script: `DisableTargetable(); PlayAnimationByIndex(0); FadeOutMusic(500);
   PlayGlobalAudio('Audio/Music/Act1_SandBeach/Mudflats_Resolve.ogg')`), the waterfall transitions (its animation),
   and the blockers stop blocking (quest flag).
3. The Submerged Passage (1_1_4_1) is reachable. Tarkleigh rewards the player in town (`QuestRewardTaken`).

## 4. What the server needs

| Piece | Status |
|---|---|
| Spawn the Rhoa Nests (Chest objects) at the captured tile records | the walk-map capture sees chest records only if `walkmaps.js` keeps `Chests/QuestChests` types (today it keeps AreaTransition/Waypoint); the Chest component's blob is unknown |
| Open a chest (0x24 on the object), drop the glyph on the ground, pick it up | **needs items**: item objects, ground items, inventory add/remove (Roadmap "Items and inventories") |
| Quest flags S→C: tell the client a flag is set (unblocks the blockers, updates the quest log) | **found 2026-10-19** ([16](16-waypoints.md) §3): the flags live in the Player component (+0x1b8); 0x2ba to the Player component with update flag 0x02 replaces the set (`InstanceEntities.PlayerQuestFlagsUpdate`), and the 0x2b9 blob carries it. The Gateway sends the World panel's visit/access flags with it; whether `a1q4-PlacedGlyphs` alone opens the blocker is untested. Earlier starting points in the decompile: Positioned's `.ot` keys `unblock_quest_flag` / `block_quest_flag` (`0x141e2dbb0`), flag name lookup (`0x141de7b20`, `Data/QuestFlags.dat`), interaction `required_quest_flag` / `disable_quest_flag` (`0x141e1cab0`, `0x141e5c790`). Find who reads the blocker's flag at runtime and which message writes the player's flag set |
| Glyph Wall: interaction, then Transitionable from untransitioned to transitioned | Transitionable's blob byte is known (u8 state, [10](10-chat-and-commands.md) §5); the state *change* message (and whether untransitioned is state 0 or 1 for `num_states` 1) is open |
| The cave entrance's Transitionable | live 2026-10-18: `WaterCaveAreaTransition` inherits AreaTransition and adds Transitionable, but the client reads only the parent's components (a Transitionable byte became the task count: Buffer Underflow) and the component keeps state 0, whose post-create event crashes (`0x141ded7b9`). The Gateway creates it as a plain AreaTransition for now ([08](08-area-transitions.md) §7). How the real server sets that state is open |
| Quest log, rewards (Tarkleigh), NPC dialogue | not started; Lioneye's Watch NPCs stand and open their dialogue window ([17](17-npcs.md)); options are not handled |

## Open

- The S→C message that sets quest flags, and whether the client keeps per-player flags (the blockers read them).
- The QuestItems row layout (which row is which glyph; `TriggeredQuestFlag`).
- Transitionable on inherited types (`WaterCaveAreaTransition`): how its state gets set.
- How the server chooses which nests hold which glyph (RhoaChest / 2 / 3).
