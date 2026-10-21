# 19. Gems

State of 2026-10-20: gem items serialize, socket and save ([13](13-items.md)). Every socketed skill gem grants its skill
at level 1 ([13](13-items.md) §6), and skill changes after login go out as the Actor's 0x20 update
([11](11-skills.md) §3). Gem levels, experience, requirements, quality and supports are planned in the Roadmap's "Gems"
items (#62, #63, #64). This file collects what is known for them.

## 1. The gem in the item data (SkillGem component)

The SkillGem component's reader `0x14246eb00` takes, in order:

| Data | Stored at | Use |
|---|---|---|
| i32 experience | +0x28 | -1 is special (below) |
| u32 | +0x3c | unknown |
| u8 | +0x10 | unknown (read through `0x140e28cf0`) |
| u16 | +0x40 | unknown; probably the variant (transfigured or Trarthan, §2): Roadmap "Gems 1" (b) |

The reader zeroes the level (+0x2c) and two experience thresholds (+0x30, +0x34) first. Then:

- **Experience -1:** experience 0 and level = the gem type's +0x28 + 1. The gem type's +0x28 is the maximum level
  (below), so this is one level over the cap. A guess to check: the corrupted level 21 gems.
- **Any other experience:** `0x14246f900` derives the level. From level 0, while the level is under the maximum (gem
  type +0x28) and the next threshold (+0x34) is not above the experience, the level goes up by one, +0x30 takes the old
  threshold and +0x34 the next level's from the gem's experience table (ItemExperiencePerLevel, the row's +0x14).

So the client derives the level from the experience alone; the server sends experience, not a level. How a gem that
can level up but hasn't been levelled by the player is represented is open (Roadmap "Gems 2").

The server writes the component as `i32 experience, u32 0, u8 0, u16 0` (`Item.Payload`); saves keep the experience
(`GemExperience`).

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
- **Requirements:** the level requirement is GrantedEffectsPerLevel `PlayerLevelReq`. The attribute requirements follow
  from it and the SkillGems percentages by a formula still to fit. Anchors from the client's tooltips: Haste level 1
  needs level 24 and 58 Dex; Clarity level 1 needs level 10 and 22 Int.
- **Supports:** GrantedEffects holds what a support applies to (`AllowedActiveSkillTypes`, `ExcludedActiveSkillTypes`,
  `AddedActiveSkillTypes`, `SupportsGemsOnly`) and its skill-bar letter (`SupportGemLetter`); a skill's types come from
  its ActiveSkills row.

## 3. Skills from gems today

- Each socketed skill gem's GrantedEffect at level 1, sent in the Actor skill list (`GemSkills`, `GemSkillEffects`).
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

- The SkillGem component's u32 (+0x3c), u8 (+0x10) and u16 (+0x40).
- "Ready to level": how the client shows a gem that can level up when it derives the level from experience; the
  level-up panel's messages; where a gem's lock is kept.
- The attribute requirement formula.
- The support list's layout in the skill record, and supports in the 0x20 update.
