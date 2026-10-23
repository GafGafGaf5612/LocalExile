# 19. Gems

State of 2026-10-20: gem items serialize, socket and save ([13](13-items.md)). Every socketed skill gem grants its
variant's skill at the gem's level and quality (§3), and skill changes after login go out as the Actor's 0x20 update
([11](11-skills.md) §3). Gem experience, requirements and supports are planned in the Roadmap's "Gems" items (#62,
#63, #64). This file collects what is known for them.

## 1. The gem in the item data (SkillGem component)

The SkillGem component's reader `0x14246eb00` takes, in order:

| Data | Stored at | Use |
|---|---|---|
| i32 experience | +0x28 | -1 is special (below) |
| u32 | +0x3c | the variant: an index into the SkillGems row's `GemVariants` (0 the normal gem) |
| u8 | +0x10 | flags (read through `0x140e28cf0`); bit 5 is set when the variant is not 0 |
| u16 | +0x40 | an IndexableSupportGems row's `Index` (0: none); not used for gems yet |

The reader zeroes the level (+0x2c) and two experience thresholds (+0x30, +0x34) first. Then:

- **Experience -1:** experience 0 and level = the gem type's +0x28 + 1. The gem type's +0x28 is the maximum level
  (below), so this is one level over the cap. A guess to check: the corrupted level 21 gems.
- **Any other experience:** `0x14246f900` derives the level. From level 0, while the level is under the maximum (gem
  type +0x28) and the next threshold (+0x34) is not above the experience, the level goes up by one, +0x30 takes the old
  threshold and +0x34 the next level's from the gem's experience table (ItemExperiencePerLevel, the row's +0x14).

So the client derives the level from the experience alone; the server sends experience, not a level. How a gem that
can level up but hasn't been levelled by the player is represented is open (Roadmap "Gems 2"), so the server lets a
gem's experience run levels freely and its skill uses that level (`GemLevel`). The one stall is the character's level
requirement: the game keeps the gem at the last level the character meets, and the server stops the experience just
below the next level's threshold (`SkillCatalog.MaxLevelAt`, `GainGemExperience`; §"Requirements" below) — the client
does not stall it, it derives whatever the experience says (checked live 2026-10-23).

The server gives the kills' experience to the gems (`GainGemExperience`, from `MonsterKilled` only: a quest's level and
`/add_xp` give the gems nothing): each gem socketed in an equipped item, in either weapon set (rows 1–10, 14, 15),
gains 10% of the player's monster experience, up to the threshold of the first level whose requirement the character
does not meet and the last level's threshold (a support gem too; a gem in an
inactive item, at its last level, or at experience -1, a corrupted gem over its cap, nothing). All the gems change in
one step under the inventory's lock (`PlayerInventories.ChangeSocketedGems`), written into the gem inside the socket
list, and are sent as the socket's equipment row 0x00bf item change, each item once with all its gems — the same item
id removed and added again, the replace idiom: an addition of a known id alone is dropped as a duplicate and the client
keeps the stale gem (live 2026-10-23). One per row per kill; the client's XP bar and tooltip
follow, it levels the gem itself at the threshold, and the level's change is logged. A level's costs, damage and
reservation ride the skills, so a level-up also re-sends the skills (the 0x20 Actor update, `SendSkills`), the same
thing an equipment change does.

The variant: the copy `0x14246fa50` checks +0x3c against the `GemVariants` count (the SkillGems row's +0x77, the list's
count in the 3.29 layout) and keeps 0 when it is outside the list. The u16: `0x14246fae3` and `0x1424732c3` look it up
in `Data/IndexableSupportGems.dat` when it is not 0.

The server writes the component as `i32 experience, u32 variant, u8 0, u16 0` (`Item.Payload`, `GemVariant`); saves keep
the experience and the variant (`GemExperience`, `GemVariant`).

## 2. The game data

- **SkillGems:** 847 rows, 534 skill gems and 313 support gems (`IsSupport`). Each row has the gem's base item type,
  its Str/Dex/Int requirement percentages, its experience type (`ItemExperienceType`), its variants (`GemVariants`, rows
  of GemEffects), and links to its Vaal and awakened versions (`VaalVariant_BaseItemTypesKey`, `IsVaalVariant`,
  `AwakenedVariant`, `RegularVariant`).
- **Variants (GemEffects):** the first variant is the normal gem. The 445 further variants are 297 named ones
  (transfigured "of X", the 12 "of Trarthus" Trarthan gems, and the Vaal gems' variants) and 148 placeholders named
  "[DNT] …AltX/AltY" that don't exist in the game. A transfigured variant has its own GrantedEffect (Ice Nova of
  Frostbolts is IceNovaAltX) and its own gem tags.
- **Vaal gems** are their own 59 SkillGems rows. Each variant's GrantedEffect is the regular Vaal skill and its
  GrantedEffect2 the base skill: Vaal Fireball's three variants are Vaal Fireball with Fireball, FireballAltX or
  FireballAltY. Vaal Sweep has no base skill.
- **Level caps** come from the experience type (ItemExperienceTypes, ItemExperiencePerLevel):

  | Experience types | Cap | Gems |
  |---|---|---|
  | GemProgression1 … GemProgression38 | 20 | most gems |
  | GemProgressionExceptional | 3 | the 52 exceptional supports (Empower, Enhance, Enlighten, Greater Spell Cascade, Eclipse, …) |
  | GemProgressionExceptionalPlus | 4 | Awakened Empower, Enhance and Enlighten |
  | GemProgressionPlus1 … Plus7 | 5 | the 35 awakened supports |
  | GemProgressionSingleLevel | 1 | 21 rows: Portal, Detonate Mines, Convocation, Capture Monster, …, and some named "[UNUSED] …" or "WIP …" (never created, like the "[DNT]" variants) |
  | GemProgressionBrandRecall, BloodAndSand, PlayTest | 6 | three special cases |
  | GemProgressionRoyale1, Royale4 | 10 | Royale gems |
  | GemProgressionGraftSkill | 40 | graft skills |

- **Levels:** GrantedEffectsPerLevel (per GrantedEffect and level: the player level requirement, costs, the support
  cost multiplier `CostMultiplier`, cooldown, mana and life reservation) and GrantedEffectStatSetsPerLevel (the stats)
  go up to level 40 for most skills, for the levels gained over the cap.
- **Quality:** GrantedEffectQualityStats, per GrantedEffect: stats and their values per 1% quality, in thousandths
  (`StatsValuesPermille`). The wiki: non-integer results usually round down.
- **Requirements:** the level requirement is GrantedEffectStatSetsPerLevel `PlayerLevelReq` of the gem's level (the
  same values as GrantedEffectsPerLevel's). The client checks it itself (0x14246fff0): a gem the character is too low
  for is drawn red, "Requires level 70", and the item it is socketed in with it (checked live 2026-10-23: a level 20
  Fireball in an equipped wand, then `/set_level 10`). It still sends uses of the skill, though — it plays the cast
  animation and takes the mana off its bar before the server hears of it — so the server is the one that refuses. The
  server treats the item as inactive, as it does a worn item the character is too low for (`CharacterStatSources`,
  `InactiveItems`): its mods and its gems do nothing and its skills leave the Actor skill list, so the client has
  nothing to cast (`GemSkillEffects`); a use that arrives anyway is refused before the action is started — no 0x1ab so
  the cast does not run, and the mana pool goes out again so the cost comes back (`GemDisabled`, logged "(disabled,
  action not started)"). Socketing a gem the character is too low for is refused (0x30, `GemRequirementsUnmet`), and a
  gem's level stalls at the first one the character cannot meet by stopping its experience (`SkillCatalog.MaxLevelAt`, §1).
  Anchors from the client's
  tooltips (poedb, 3.26 data, matching GrantedEffectStatSetsPerLevel): Haste level 1 needs character level 24 and
  58 Dex, level 20 needs 70 and 155; Clarity level 1 needs 10 and 22 Int, level 20 needs 70 and 119; Fireball level 20
  needs 70 and 155 Int.
- **Attribute requirements** are the client's computation in `0x14246fc40` (per attribute; `0x14246fff0` is the level
  requirement and clamps the level the same way): for one attribute at the gem's level, with `g` the level requirement
  of that level (`PlayerLevelReq`) and `p` the gem's SkillGems share in percent
  (Strength/Dexterity/IntelligenceRequirementPercent),

  ```
  value = (3 g + 11) * (p / 100) ^ 0.9 * k + 0.5     (truncated; k = 0.5 for a support, 0.7 for a skill)
  value < 14 ? 0 : value
  ```

  The client then scales by (100 + the requirement modifiers)/100 (none on gems today). The level is the gem's, clamped
  to its cap (a corrupted level 21 gem keeps level 20's requirement). Checked against poedb over 570 tooltip cells
  (Haste, Fireball, Arc, Wrath, Grace, Discipline, Determination, Clarity, Anger, Hatred, Enfeeble, Flammability, the
  transforms' 60/40 and 33/33/34 splits, and five supports) with no mismatch: 100% at 70 is 155, 75% 119, 60%/40%
  98/68, a support at 70 is 111 and at 8 is 18, and low-level gems fall under 14 and show none. The server checks the
  same values at socketing and for worn items (`GemAttributeRequirements`, `GemRequirementsUnmet`), against
  `virtual_strength/dexterity/intelligence_for_requirements` as the client does. Checked live 2026-10-23: a level 20
  Fireball socketed at `/set_stat intelligence 200`, then 130 — at 130 the client reds the gem, the wand's mods stop
  counting, and a use is refused.
- **Supports:** GrantedEffects holds what a support applies to (`AllowedActiveSkillTypes`, `ExcludedActiveSkillTypes`,
  `AddedActiveSkillTypes`, `SupportsGemsOnly`) and its skill-bar letter (`SupportGemLetter`); a skill's types come from
  its ActiveSkills row.

## 3. Skills from gems today

- Each socketed skill gem's skill at the gem's level, sent in the Actor skill list (`GemSkills`, `GemSkillEffects`):
  - **Level** from the experience as the client derives it (§1; `ItemCatalog.GemType.Level`), capped by the experience
    type (§2), with its experience stalled by the character's level requirement (§2 "Requirements"). Experience -1 is
    the cap + 1.
  - **Skill:** the variant's GemEffects `GrantedEffect`; a transfigured or Trarthan gem is its own skill (Ice Nova of
    Frostbolts grants IceNovaAltX). A Vaal gem grants two skills, its Vaal skill and its base skill (`GrantedEffect2`),
    each with its own skill id. A support gem grants none.
  - **Numbers** from the level's rows: the GrantedEffectsPerLevel row (cost, reservation, cooldown, Vaal souls) and the
    GrantedEffectStatSetsPerLevel row (damage and the other stats), the highest level at or below the gem's when a
    skill lacks that level. Fireball deals 9–14 for 5 mana at level 1 and 1883–2825 for 20 mana at level 20; Clarity
    reserves 34 mana at level 1 and 279 at level 20.
  - **Quality:** GrantedEffectQualityStats per 1% in thousandths × the quality, rounded toward zero (Fireball at 20%:
    150 × 20 / 1000 = +3 radius). Whether the 16 negative quality values round toward zero or down is unverified.
  - **Cooldown:** the cooldown of the gem's level (GrantedEffectsPerLevel `Cooldown`, the auras' 1.2 s) is the one
    the server enforces and sends with the skill ([11](11-skills.md)); the server allows one use per cooldown even when
    `StoredUses` is higher.
  - **Vaal skills** need their `VaalSouls`; souls are not gathered yet (Roadmap "Gems later"), so a Vaal skill is refused.
- **Not created** (`ItemCatalog.FindGem`, `/gem`): the "[DNT]" placeholder variants, the "[UNUSED] …", "[DNT] …" and
  "WIP …" gem rows, the awakened supports other than Awakened Empower, Enhance and Enlighten, and the Vaal variants
  whose base skill is only a placeholder's (Vaal Fireball has only its normal variant). A Vaal variant's name is "Vaal "
  and its base variant's ("Vaal Ice Nova of Frostbolts"); the table names them all "Vaal Ice Nova". Whether the game
  names them so is unverified.
- `/gem <name> [level] [quality]` puts a gem into the inventory (any variant by its name, levels 1 to the cap,
  quality 0 to 20).
- **Skill ids stay with their gem** (`GemSkillIds`): a gem keeps its skill id while it stays socketed. A gem moved to
  another socket keeps it too (matched by its skill): a move goes through the cursor, where the gem is out of the
  equipment for an update or more, so the id of a gem that left waits for a gem of its skill to come back. A new gem
  takes the lowest gem skill id no gem had, and only when none is left the id of the gem that left longest ago. The gem
  skill ids are the ids below `FirstSkillId + 64` that no basic action uses (`SkillCatalog.IsGemSkill`): 55 in 3.29.3b.
  The client binds skill bar keys by skill id: with ids given in socket order, taking Haste out of a wand gave Righteous
  Fire Haste's id, and Haste's keys then cast Righteous Fire (live 2026-10-20).
- An id a gem gives up never goes to another skill's gem in the same update. A gem swapped into a socket (the old gem
  goes to the cursor) or an item equipped over another changes the gems in one update; the old skill must go out as
  removed, so its aura or Righteous Fire ends and its skill bar key does not cast the new gem.
- A skill that goes away (its gem left the equipment) ends its aura or Righteous Fire, and the reserved mana comes back.
  One aura of a kind runs at a time: a second gem of the same aura can't start it.
- Support gems are not granted: one as a skill crashed the client ([13](13-items.md) §6). The skill record has a
  support list ([11](11-skills.md) §3), not used yet.
- Skill changes after login (equipment, levels, passives, auras) go out as the 0x20 update, which removes skills and
  tells the character panel ([11](11-skills.md) §3).

## Open

- The SkillGem component's u8 (+0x10) and what IndexableSupportGems (+0x40) is for.
- Not checked with the real client yet: a gem of a higher level or with quality, a transfigured gem's tooltip and skill,
  a Vaal gem's two skills, and how the client shows a Vaal skill without souls and a skill on cooldown. Checked
  2026-10-23: the level requirement (a level 20 gem red at character level 10; the client still sends its use, the
  server refusing the action) and the socketing refusal; the attribute requirements were fitted and checked live on
  2026-10-23 (`/set_stat intelligence 200`, then 130, under a level 20 Fireball).
- "Ready to level": how the client shows a gem that can level up when it derives the level from experience; the
  level-up panel's messages; where a gem's lock is kept. The server's kill experience leaves this visible immediately:
  a gem that crossed a threshold may show or apply the level without a click (checked live 2026-10-23: a monster kill's
  share levels the gem itself).
- A disabled gem's supports still apply, and its aura still runs: supports are not modelled yet (Roadmap "Gems 3").
- The support list's layout in the skill record, and supports in the 0x20 update.
