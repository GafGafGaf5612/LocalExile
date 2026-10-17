# 11 — Skills: how the client gets them, the default attack

Status (live, 2026-10-17): the player has the default attack. It shows in the skill picker, can be put on the left mouse
button, walks into range, swings at the Witch's attack speed (757 ms, or faster with `/set_stat attack_speed_+%`), and
the server lowers the monster's life 2–5 per hit at the swing's contact point until it dies. Code: `GrantedSkill`, `PlayerObject` (`InstanceEntities.cs`), `SkillCatalog.cs` and `Attack` in the
Gateway. Test: `InstanceEntitiesTests.Player_blob_with_stats_and_a_granted_skill`.

## 1. ActiveSkillManager

Every Actor has an `ActiveSkillManager` (RTTI `Skills::ActiveSkillManager`, vtable `0x1435db980`) at **Actor +0x598**,
returned by the Actor's vtable +0x140. Its skills are a vector at **manager +0x158/+0x160** of
`shared_ptr<ActiveSkillInstance>` sorted by the instance's u16 at **+0x40**. Every add ends in the manager's slot 7
(+0x38), `SkillInstanceAdded` `0x141eea6a0` (found through its lambda's RTTI name). Lookups by id: `0x141ee97f0`
(creates the action for a skill id, used by S→C 0x1ab and by the client's own skill use).

ActiveSkillInstance (base vtable `0x1435b9728`, 400-byte `make_shared` block, vtable `0x1435db960`):

| Offset | Meaning |
|---|---|
| +0x40 | u16 **skill id**: what C→S 0x24/0x25 and S→C 0x1ab carry |
| +0x42 | u16 **action id**: an ActionTypes `HASH16` |
| +0x48 | the GrantedEffectsPerLevel reference (row handle, then the effect, its `Id` string) |

## 2. Basic actions: from the object type

The `ClientActor` constructor (`0x142192860`) walks a u16 list on the Actor type (`type+0xf8..+0x100`) and calls
**AddSkill** `0x142198d40(manager, u16)` for each: id = value & 0x3fff, action = value. The list comes from the .ot's
`Actor { basic_action = "…" }` lines (Character.ot: Move, Interaction, InteractionChannelled, WashedUp, Dance,
AreaTransitionAnimation, SinPickup, BloodVortexGetUp, EpilogueKnockdown, AncestralTrialDestroyTotem, …; Monster.ot:
Move, Flee, StrafeLeft, StrafeRight, Advance, DoNothing, MonsterPickup), and the value is the action's
**ActionTypes.dat `HASH16`**: Move = 10505 = **0x2909**, Interaction = 49766 = 0xc266 (id 0x0266). Traced live with
`scripts/re/frida/trace-skills.js`: the player gets 12, a Hungry Corpse 9. Nothing on the wire is needed for them.

## 3. Granted skills: Actor slot 20, flag 0x40

Real skills (gems, the default attack) come from the server, inside the **Actor component's vtable +0xa0 reader
`0x1421932b0`** (the part after the main body, [04](04-player-entity.md) §4.9; also what S→C 0x2ba runs). Its first flag
byte f0:

- **f0 0x40 → `0x142197b10`**: the skill list (replaces the manager's server skills).
- f0 0x20 → `0x142198800`: also reads skill records through `0x1421981d0` (an update; not used yet).

`0x142197b10`:
```
u8 n,  n × skill record (0x142196ce0, through 0x1421981d0: insert sorted by id, replacing an equal id, then slot 7)
u8 m,  m × (u32 row of a 0x101-byte table, u32, u32)          → manager +0x188 (not used)
u32 k, k × (u16 skill id, u32 c, c × (u32, u32, u32, u32))     (not used)
```

Skill record (`0x142196ce0`):
```
u16  skill id                                      → +0x40
GEPL reference (0x1424a0dc0):
  u8                                               → ref +0x10 (0 for us; a stat-set reference with its u8 set uses it + 1)
  u32  GrantedEffectsPerLevel row (0x78-byte rows)
  stat-set reference (0x1424a0990): u8 a, [u8 if a], u32 GrantedEffectStatSetsPerLevel row (0xb8-byte rows)
  one more stat-set reference per entry of the effect's additional stat sets (GrantedEffects row → its list count)
u8   flags: bit0 → u32 parent object id ("Skill parent object did not exist" if unknown) + u16; bit1, bit2 → instance flags
u8   n supports, n × (GEPL reference, u32)
u32, u32, u32, u8
compact n × (compact stat key, compact value)      the skill's own stats
per additional stat set: the same stats list
```
The action id (+0x42) is not sent: `0x141ee73d0` takes it from the data (GEPL row → GrantedEffect → ActiveSkill →
ActionType → `HASH16`); the default attack's is `Melee` 45448 = 0xb188.

**Skill ids** are the server's choice, but they share the manager with the basic actions, whose ids are `HASH16 &
0x3fff`. 0x0101 collided with a Monster.ot basic action (monsters showed a skill 0x101 / 0x4101). The Gateway uses
**0x0102**, which no ActionType masks to in 3.29.3b (`SkillCatalog.FirstSkillId`).

## 4. The default attack

Characters.dat `StartingMeleeSkill` is the GEPL row of each class's default attack: **4474** = `PlayerMelee` level 1
for the Witch. Its stat set is GrantedEffects `StatSet1` = 476 (`PlayerMelee`), level 1 = GESSPL row **4430**.
`SkillCatalog` looks both up by GrantedEffects `Id` and level (`Game_PLAYER_ATTACK` picks another effect).

Without stats the swing is instant: no animation, and the client repeats C→S 0x24 every ~40 ms while the button is
held (live, first try). The Gateway sends the class's unarmed weapon (Characters.dat `WeaponSpeed`, `MinDamage`,
`MaxDamage`, `MaxAttackDistance`: Witch 833 ms, 2–5, 8) both in the player's Stats component and in the skill's stats:

| Key | Stat |
|---|---|
| 0x117, 0x118 | main_hand_minimum/maximum_physical_damage |
| 0x29e, 0x29f | main_hand_minimum/maximum_total_damage |
| 0x142 | main_hand_base_weapon_attack_duration_ms |
| 0x221a | main_hand_total_base_weapon_attack_duration_ms |
| 0x2a6 | hundred_times_attacks_per_second |
| 0x126 | main_hand_attack_speed_+% |
| 0x146, 0x4d3 | main_hand_base_maximum_attack_distance, main_hand_maximum_attack_distance |

**The server sends final values; the client derives none of them.** Live 2026-10-17 (`dump-stats-object.js`): a skill
holding `attack_speed_+%` 2000 still swung every ~757 ms and the character panel kept showing 1.2. What the client
uses:

- The **swing time** follows the skill's `main_hand_attack_speed_+%` (with the base 833 ms). When it is not sent the
  getter answers **10**: unarmed attacks alternate hands, i.e. count as dual wielding, worth 10% attack speed. So the
  Witch's real unarmed swing is 833 / 1.1 ≈ 757 ms.
- The panel's **Attacks per Second** is `hundred_times_attacks_per_second / 100`. The panel caches it: it shows a new
  value only after the skill is selected again in the panel.
- The client walks into range by itself using `main_hand_maximum_attack_distance` (8 cells for the Witch).

`SkillCatalog.Unarmed.Stats(player)` computes them: increased = 10 + `attack_speed_+%` + `melee_attack_speed_+%` +
`unarmed_melee_attack_speed_+%` (+ a hand-set `main_hand_attack_speed_+%`), attack time = base / (1 + increased / 100),
at least one server tick (`Program.TickRate` = 30/s, so at most 30 attacks per second), and
`hundred_times_attacks_per_second` = 100 / attack time. "More" multipliers are not modelled. `/set_stat` (which takes
stat names now) records the stat for the player and sends the player's stats (0x1b3) and the skill again.

**Changing a skill after entering:** S→C **0x2ba** on the player with component index 9 (Actor) and the slot-20 data
`f0 0x40, f1 0, skill list` (`InstanceEntities.PlayerSkillsUpdate`): the list replaces the skill of the same id, new
stats included. Live: the swing sped up at once.

## 5. Combat timing (Gateway `Attack`)

1. C→S 0x24 arrives when the player clicks, before the client walks into range. The server walks its position estimate
   toward the monster and stops at the first cell within `MaxAttackDistance` (`WalkEstimator.Approach`).
2. On arrival it starts the swing: the 0x1ab echo (from the stop cell, aimed at the monster). Echoing at the click made
   the client swing on the spot, out of range.
3. Half an attack time later (the contact point, roughly) it lowers the monster's life with 0x1b1.
4. Any later player action (0x25 move, another 0x24) cancels steps 2 and 3 (`ClientState.ActionCount`).
5. Pacing: one swing per attack time. While the button is held the client sends one 0x24 per swing; a swing may
   arrive up to a quarter of an attack early (jitter) and the schedule stays on the client's cadence. When the server
   thought the swing took 833 ms instead of 757 ms it fell behind and dropped every third or fourth swing, which then
   animated without damage.

Exits use the same approach (`ExitReach` = 6 cells) before the area change or the instance list.

**Life bar on death:** when life reaches 0 the client's Life tick (`0x141e6a190`) stops notifying its listeners, so the
mini life bar over the monster kept its last value. The bar entry (`0x140220ab0` computes its flags; `0x140221fd0`
decides visibility) also listens to stats (`0x140220c30`): `hide_mini_life_bar` (0x2a8b) or `virtual_is_hidden_monster`
(0x59d0) hide it. The Gateway sends 0x1b3 `hide_mini_life_bar = 1` on the monster with the killing 0x1b1. Live: the bar
disappears.

## 6. Monsters attack

Live 2026-10-17: Hungry Corpses walk up, swing (animated) and lower the player's life; walking away dodges a swing.

- **Skill.** A monster's Actor gets the same skill list as the player (slot 20, f0 0x40) with its MonsterVarieties
  `GrantedEffectsKeys[0]` (Hungry Corpse: 406 `Melee`). Monster effects have per-level rows only for some levels, so
  `SkillCatalog.FindByRow` takes the highest GEPL / GESSPL level at or below the monster's (Melee: level 1, GEPL 4394).
  Id 0x0102, like the player's (no ActionType masks to it).
- **Stats.** The final attack stats (`MonsterAttack.Stats`): attack time = MonsterVarieties `AttackSpeed` (2400 ms),
  reach = `MaximumAttackDistance` (8), damage = DefaultMonsterStats `Damage` at the level × `DamageMultiplier` / 100
  with a ±20% spread (the spread is assumed). They go in the monster's Stats component and in the skill.
- **`skill_is_non_pathing` (0x4f32) on the skill.** The Melee action's begin (vtable +0x38 `0x141938830`) is just that
  stat: when 0, the action runner (`0x141e8e060`) sends the action through the client's own walk-into-range step,
  which drops a server-driven monster's queued attack in the same frame (traced: created, queued, both checks passed,
  then neither current nor queued). The server walks the monster itself, so the skill is non-pathing. Only on the
  skill: on the monster's Stats component it also stops its Move action from pathing (the monsters stood still).
- **AI** (`RunMonsterAi`, 500 ms): a hostile, chasing monster within reach stops and swings: 0x1ab with its skill, flags
  0x0408 | 0x80, target = the player, sequence 0xffff. At half the attack time the hit lands if the monster lives and
  the player is still within reach + 2: 0x1b1 on the player.
- **Player life** (`ClientState.PlayerLife`, 50 for the level-1 Witch). At 0 the client plays the death animation and
  blocks moving. `/heal` sends full life: 0x1b1 from 0 runs the client's revive. The overhead life bar is hidden at 0
  (`hide_mini_life_bar`, as for monsters) and shown again on revive.

## 7. The death screen

The client's `death_screen` panel (constructor `0x14028fc80`, setup `0x140290820`) does not open by itself when the
local player dies: **S→C 0x55** opens it (instance group, class `0x1435dce40`, read `0x14219f820`, dispatcher case 0x14 →
listeners at instance +0xf78 → `0x140d5d6a0` → a task → HUD vtable +0x4d0 `0x140d86180`).

```
u16 0x0055, u8 flags: bit0, bit1 = hide, bit2, bit3, bit4
unless bit1: u32 BE (+0x18), u8 (+0x15) when that u32 is non-zero, u16 BE (+0x1c)     (meanings unknown, sent 0)
```

Title "You have died!" (ClientStrings ids are row + 1). Live 2026-10-17:

| Flags | Buttons |
|---|---|
| 0x0c (standard league) | "Resurrect in Town", "Resurrect at Checkpoint" |
| 0x00 | "Resurrect" |
| 0x01 (hardcore) | "Exit" ("Exit to the character selection screen": sends C→S 0x153, not implemented yet) |
| 0x02 | hides the screen |

bit3 turns the left button into "Resurrect in Town", bit2 adds the checkpoint button (not with bit0); a third
"Spectate" button belongs to PvP. bit4 and the trailing fields are not mapped (Sanctum and other modes probably use them).

The buttons (`0x140290bf0`) send **C→S 0x54**: `u16 0x0054, u8 flags`, bit0 = "Resurrect in Town" (1) or the
checkpoint / plain "Resurrect" (0). The Gateway (`Resurrect`): hides the screen (0x55 bit1); in town → area change to
Lioneye's Watch (the new player object has full life); at the checkpoint → 0x1b1 full life first (a teleport sent while
dead was ignored), then 0x1b7 to the area's arrival point (no waypoints yet). `Game_LEAGUE=hardcore` sends 0x01;
`/death_screen [flags]` previews any variant.

## 8. Stat keys

**Client stat key = Stats.dat row + 1** with the vendored schema: every key read during an attack matches
(0xf5 → row 244 `movement_velocity_+%`, 0x7d → 124 `base_maximum_life`, 0x144 → 323
`main_hand_minimum_attack_distance`, 0x35a → 857 `is_totem`, 0xce6 → 3301 `virtual_action_speed_+%`, …).
`scripts/re/frida/trace-skill-stats.js` logs the keys, values and callers of the stat getter `0x1424cfa40` while an
action is created (`0x141941c90`) or started (`0x141e8cf10`); `dump-stats-object.js` dumps the stored (key, value)
vector (stats +0xf0) of whichever stats object answers a key; `scripts/re/statkeys.py <key>…` lists the client code
that reads a key (`mov edx, key` + call). Stats like `main_hand_total_base_weapon_attack_duration_ms` are read by many
small calculators in `0x141b..`–`0x141d..`.

## Open

- The animation's real contact point (the server uses half the attack time); the player's other stats (life, mana,
  attributes) are still not sent; "more" multipliers.
- A 30 Hz server loop: `TickRate` only bounds the attack time; hits use timers and the monster AI runs every 500 ms.
- Monsters use only their first granted effect; no ranged or spell skills, no monster skill choice.
- A damage model: armour, evasion, resistances, crits; the player's real life from level and stats.
- The death screen's bit4 and trailing fields (Sanctum and other modes); exit to character selection (0x153).
- f0 0x20 (skill update), the second and third lists of f0 0x40, supports, skills from gems in items.
