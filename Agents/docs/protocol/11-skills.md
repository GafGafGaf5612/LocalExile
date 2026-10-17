# 11 — Skills: how the client gets them, the default attack

Status (live, 2026-10-17): the player has the default attack. It shows in the skill picker, can be put on the left mouse
button, plays the unarmed swing at the Witch's attack speed (833 ms), and the server lowers the monster's life 2–5 per
hit until it dies. Code: `GrantedSkill`, `PlayerObject` (`InstanceEntities.cs`), `SkillCatalog.cs` and `Attack` in the
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
| 0x146, 0x4d3 | main_hand_base_maximum_attack_distance, main_hand_maximum_attack_distance |

Which of them sets the swing time is not isolated; with all of them the animation plays at the expected pace.

The server accepts one attack per `WeaponSpeed` (repeats of 0x24 meanwhile are ignored), sends the 0x1ab echo, and
half an attack later lowers the monster's life by a random MinDamage..MaxDamage with 0x1b1 ([10](10-chat-and-commands.md)
§5).

## 5. Stat keys

**Client stat key = Stats.dat row + 1** with the vendored schema: every key read during an attack matches
(0xf5 → row 244 `movement_velocity_+%`, 0x7d → 124 `base_maximum_life`, 0x144 → 323
`main_hand_minimum_attack_distance`, 0x35a → 857 `is_totem`, 0xce6 → 3301 `virtual_action_speed_+%`, …).
`scripts/re/frida/trace-skill-stats.js` logs the keys, values and callers of the stat getter `0x1424cfa40` while an
action is created (`0x141941c90`) or started (`0x141e8cf10`).

## Open

- Which stats really drive the swing time and the client's damage display; the player's other stats (life, mana,
  attributes) are still not sent.
- Monsters' skills: they would get their MonsterVarieties GrantedEffects the same way, then fight back.
- f0 0x20 (skill update), the second and third lists of f0 0x40, supports, skills from gems in items.
