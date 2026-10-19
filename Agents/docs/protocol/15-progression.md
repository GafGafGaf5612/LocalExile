# 15. Progression: experience, levels, the Passive Skill Tree, loot

Code: `GameServer.Core/World/Progression.cs` (rules, tested in `ProgressionTests`), `InstanceEntities.CharacterPassives`
/ `ReadAllocatePassive` (messages), Gateway `GainExperience`, `ApplyLevel`, `ChangePassives`, `MonsterKilled`,
`DropLoot`, `PassiveTree.cs`, `ItemDrops.cs`, `CharacterSaves.cs`. Live 2026-10-18 with a Witch: kills on The Coast gave
experience (Gateway log), nodes allocated in the Passive Skill Tree, and dying monsters created their drops (log; not
yet confirmed on screen).

## 1. Experience and levels

- **The client derives the level from the experience** in the Player component ([04](04-player-entity.md) §4.6):
  S→C 0x2ba to component 6 with flag bit0 + u32 experience (`InstanceEntities.PlayerExperienceUpdate`). Crossing a
  level start (ExperienceLevels.dat) changes the level on the client.
- **A kill** (`MonsterKilled`): the player gains experience when alive and within `Game_XP_RANGE` cells (default 150)
  of the monster when it dies (melee, Fireball or Righteous Fire). Amount (`Progression.MonsterExperience`):
  DefaultMonsterStats `Experience` at the monster's level × MonsterVarieties `ExperienceMultiplier` / 100 × rarity
  (magic 2, rare and unique 5: our own numbers) × the level penalty, rounded.
- **Level penalty** (`Progression.LevelPenalty`): safe zone 3 + level / 16; beyond it, with d the levels past it,
  ((L + 5) / (L + 5 + d^2.5))^1.5, at least 0.01 (L = the player's level). The extra penalty above level 95 is not
  modelled.
- **A new level** (`ApplyLevel`): the level's stats (0x1b3) and skills (0x2ba Actor), full life and mana (0x1b1), the
  Passive Skill Tree with the new level (0x33, §2), and the account's level through Auth's `set_level` control op (the
  character list and the next tickets carry it). Experience stops at the start of level 100.
- **Saved** in the character's save (`data/saves/<name>.json`, `Experience`) after every gain; a save whose
  experience is from another level than the account's (after `/set_level`) gives way to that level's start.

## 2. The Passive Skill Tree

- **S→C 0x33** (dispatcher case 0xf, class `0x1435dca60`, read `0x14219e3f0`, handler `0x141ef2710` on the tree at
  instance `+0xc2d8`):

  | Field | Goes to | Meaning |
  |---|---|---|
  | u16 | tree +0x270 | probably refund points |
  | u16 | +0x274 | `extra_skill_points` (quest points; we send 0) |
  | u16 | +0x27c | ascendancy points |
  | u8 | +0x26c | **the level**: the client's `points_from_level` = (level − 1) × points per level |
  | u8 | +0x268 | the class byte (Characters.dat IntegerId, ascendancy in bits 5–7): the tree's start |
  | u8 | +0x269 | |
  | u8 flags | | bit0 → +0x91, bit1 → no lists (then `0x141ef73e0` sets only the numbers) |
  | vec16 u16 | +0x180 | **allocated nodes**: PassiveSkills.dat `PassiveSkillGraphId`, raw (little-endian) |
  | vec16 u16, vec16 u16 | | (sent empty) |
  | vec16 u32 | | mastery effects: node \| effect << 16, raw little-endian; effect is the `PassiveSkillMasteryEffects.dat` HASH16 |
  | vec16 u32 | | (sent empty) |

  The client's check `0x141ef3e60` logs "character passive skills allocated but only allowed {}.
  points_from_level={}, skill_points_from_passives={}, extra_skill_points={}" and drops unconnected nodes itself.
  The Gateway sends it after the player on entry and after every level or passive change.
- **C→S 0x34** (7 bytes): `u16 0, u16 node (big-endian), u8 0`. A normal allocation has effect 0. Captured normal: `00340000DFB000` = 57264,
  Spell Damage and Mana, next to the Witch's start. The client waits for the tree back; without 0x33 the click is
  undone. The Gateway
  checks the node exists, is not an ascendancy node and fits the points (level − 1 plus the nodes' own
  `SkillPointsGranted`), saves the list (`Passives`) and answers with 0x33; a refused click gets the old tree back.
- **C→S 0x123** (3 bytes, u8 1 / 0): a panel opened or closed (sent when the tree opens and closes).
- **C->S 0x35** (6 bytes): u16 node (big-endian, PassiveSkillGraphId), u16 effect (big-endian,
  PassiveSkillMasteryEffects.dat HASH16). Captured `003598091194` = node 38921 (Block Mastery), effect 4500
  (+1% spell block per 5% attack block). The Gateway allocates the mastery node and records the effect, then answers
  with 0x33 and the updated stats.
- **S→C 0x3d is the Atlas tree**, not the character's: its three presets ([05](05-inventories-and-ui.md) §4; the check
  `0x141eed000` logs "atlas passive skills allocated but only allowed").
- **Masteries**: `PassiveSkillMasteryGroups.dat` maps a mastery node's `MasteryGroup` to its effects and
  `PassiveSkillMasteryEffects.dat` holds their stats. The selected effect is stored per node and its stats are summed
  with the node's (`Passives.Stats`). Block Mastery's four effects are max attack block +2, max spell block +2, 20
  life and 20 mana gained on block, and +1% spell block per 5% attack block (the last is applied after the shield's
  base block).
- **Stats** (`PassiveBonus`, `BaseStats`): the nodes' stats summed; attributes (`base_*`, `additional_*`, all and
  pairs), flat and increased life and mana, increased evasion, energy shield and melee physical damage go into the
  pools ([11](11-skills.md) §4); the other stats are sent as they are (the character panel shows them; nothing uses
  them yet).
- `/passives` lists them, `/reset_passives` refunds all. Refunding single nodes from the tree is not mapped.

## 3. Loot

`ItemDrops.Roll` (our own table): normal monsters drop 1 item at 35%, magic 1–2, rare 2–4, unique 4–6. Each item is
currency (45%: Scroll of Wisdom and Portal Scroll most, then the basic orbs, Chaos rarest), a life or mana flask (10%)
or equipment (45%: any weapon, armour or jewellery base with DropLevel ≤ the monster's level, minus event and league
bases), item level = the monster's level, normal, with the type's default sockets. `DropLoot` puts each as a WorldItem
([13](13-items.md) §5) 3–6 cells around the corpse, on a walkable cell when the walk map is known. `Game_DROPS=0` turns
it off.

## Open

- The real rarity experience bonuses, the level 95+ penalty, party sharing.
- Quest passive points, refund points and the refund click (probably 0x34 with another trailing byte, or its own id),
  ascendancy; the tree's connections (PassiveSkillGraph.psg) are not checked by the server.
- Real drop pools (MonsterVarieties / area drop tables), item rarity and mods, quantity and rarity bonuses; ground
  items only exist on the connection that saw them drop.
