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

## 9. Gem skills: stats and casting

Live 2026-10-18 with the Witch's Fireball. Code: `SkillCatalog.EffectStats`, `GemSkills`, `CastOn`, `LaunchProjectile`
(Gateway).

- **Skill ids.** Gem skills take the ids after the default attack's that no ActionType `HASH16 & 0x3fff` uses
  (`SkillCatalog.SkillIds`): 0x0106, 0x010a and 0x0114 are basic actions in 3.29.3b, so plain counting from 0x0103 would
  have replaced one. Fireball is 0x0103.
- **Stats** (`SkillCatalog.EffectStats`): the effect's GrantedEffectStatSets row (ImplicitStats = 1, ConstantStats with
  ConstantStatsValues), its GESSPL row at the gem level (FloatStats with BaseResolvedValues, AdditionalStats with
  AdditionalStatsValues), the cast and the cost. Fireball level 1: `spell_minimum/maximum_base_fire_damage` 9–14,
  `active_skill_base_area_of_effect_radius` 10, `base_chance_to_ignite_%` 25, `base_is_projectile` 1, mana cost 5. The
  skill's list also carries the player's stats.
- **Cast time.** The client reads **`virtual_base_spell_cast_time_ms`** (0x24b9) when it starts the cast; without it
  the cast was instant (`trace-skill-stats.js`). The Gateway sends GrantedEffects `CastTime` (750 ms) there and in
  `base_spell_cast_time_ms`, and `hundred_times_casts_per_second`. `mana_cost` (0x2e72) is what the client spends.
- **Casting.** At the ground: C→S 0x25 with the gem skill's id; the server echoes 0x1ab from where the player stands (no
  walk). On a monster: C→S 0x24; the server echoes 0x1ab aimed at it (no walk into range, unlike the default attack).

## 10. Projectiles (experimental)

The server creates and moves the projectile. The client's Projectile component (vtable `0x1435a0d10`, 0x20 bytes) does
nothing by itself: its slot 17 reads nothing and almost every other slot is a bare `ret`.

- **Object.** A 0x2b9 of the Projectiles.dat type named after the effect (`Metadata/Projectiles/Fireball`, hash of the
  path; the type register knows it from the table). `Metadata/Projectiles/Projectile` deserializes Positioned,
  BaseEvents, Animated and Projectile, so the blob is Positioned, one Animated byte and the tasks byte
  (`ProjectileObject`), then 0x1a9 (wake zone). The real link between a skill and its projectile is unknown; the
  Gateway matches the Id.
- **Flight.** S→C **0x1ba** to the target cell at the row's `ProjectileSpeed` (world units per second, 1040 → ~96
  cells per second at 250/23 world units per cell). The u32 at +0x28 is a **height**, not padding: the handler
  (`0x141e30c90`) passes it to `0x141e34400`, which keeps the terrain height for 0x7fffffff and otherwise sets
  z = −height × 7.8125. With 0 the fireball sat under the ground and did not seem to move; with 0x7fffffff it flies
  (slowed to 10 cells per second with `Game_PROJECTILE_SPEED` to watch it). Positioned's vtable +0xd8, which the move
  calls when the flag byte has bit 0, is a `ret`; the interpolation is `0x141e4d180`.
- **Rotation.** Positioned rotation (+0x29c) = **atan2(dx, −dy)** in grid cells (the player's own moves traced with
  `trace-move.js`: facing (+15, −18) read 0.668).
- **Hit (live 2026-10-18).** At launch the server walks the path cell by cell and stops it at the first living
  monster within 4 cells (`Game_PROJECTILE_HIT_RADIUS`; the row's CollisionRadius is 2) of where the projectile will
  be, using where that monster will be by then. Ground and monster casts alike: the projectile flies to the hit (else
  to its range), the monster loses the skill's fire damage (0x1b1), a kill is removed 10 s later. A monster that walks
  into the path after launch is not hit.
- **Walls (live 2026-10-18).** The client's walkability provider (walk-map hook `0x1410ddcfd`) returns a second grid
  of the same size at +0x20/+0x28: what stops projectiles. 0 blocks, 1..5 (a distance gradient like the walk map's) lets
  them through. On The Coast seed 1 every walkable cell is open in it, cliffs and rocks are 0 in both, and 1.44 M cells
  the walk map blocks (the sea) are open. `scripts/walkmaps` saves it as `<area>_seed<seed>.projectile.bin`
  (`ProjectileGrid`); the path stops half a cell before the first blocking cell and explodes there. Live: the fireball
  explodes on cliffs and flies over the sea. Doodads and objects with their own colliders (not in the grid) do not stop
  it yet. Grids exist for Lioneye's Watch and The Coast seed 1.
- **Explosion (live 2026-10-18).** Projectiles.dat names the impact as an animation of the projectile's own .ao
  (Fireball: ImpactAnimationIds `fireball_explode` → `point_impact.pet`). No server message found yet plays an
  animation by name, so on a hit the server swaps the projectile's animated object instead: S→C 0x2ba to component 2
  (Animated, in the order Positioned, BaseEvents, Animated, Projectile, Render) with flags bit0 and the path
  `Metadata/Effects/Spells/fireball/explode_new2.ao`, whose default animation `animate` plays the `splode` particles
  and the End sound (reader `0x141e57490`: bit0 path, bit1 a 0x2c-table index, bit2 `0x141e5adb0` attachments; no
  bit3 there). The object is removed 1.5 s later. `Game_PROJECTILE_IMPACT_AO` overrides the path (`none` turns it
  off). `Metadata/Effects/Effect` (Positioned, BaseEvents, Animated, LimitedLifespan) would be the cleaner carrier but
  is not known to be in the client's type register; an unknown type throws in `0x142187480`.
- **Tilt.** `Metadata/Projectiles/Projectile.ot` gives the client's Render `tilt_to_target` and
  `maintain_tilt_after_movement` (type data +0x76, +0x77; parser `0x1415ef5e0`). Render (`0x1415f53c0`) pitches the
  model by −`0x141e35c00`(Positioned, maintain): atan(dz / distance) of the current move, or, when not moving and
  maintained, the angle at Positioned +0x2a0 (f1 0x10 in the blob; the client set it to π/2 on our fireball). Traced live:
  the tilt is 0 during the flight, and the yaw (1.497 to the east, 4.617 to the west) follows the flight on screen.
- **Height.** Heights are absolute, in steps of 7.8125 world units. The client's terrain height of a cell is
  `0x141d84b00`(terrain, int cell[2]): terrain +0x18 / +0x20 = size in tiles of 23 cells, +0x28 = 0x38-byte tile
  entries (tile data pointer, +0x36 rotation code, +0x30 i16 height offset), result = the tile's height at the
  tile-local cell (`0x141d7d100`) + terrain +0x104 × the tile's offset; `0x141e35800` interpolates four cells. With a
  height grid (`TerrainHeights`, data/walkability/<area>_seed<seed>.heights.bin, int16 per cell, −32768 = unknown) the
  Gateway sends the projectile at terrain + `Game_PROJECTILE_HEIGHT` steps (default 8) in Positioned's f0 0x04 fixed
  height and in 0x1ba. Only Lioneye's Watch has a grid (−48..72), where skills cannot be used.
- **Capturing heights from the client failed** (2026-10-18, four client crashes or hangs, so the walk-map recorder does
  not do it): dumping every cell from Frida's thread at the first lookup worked in town but crashed on The Coast (the
  terrain was still being built); a native listener on `0x141d84b00` that called the hooked function crashed at login;
  delayed dumps hit unreadable tiles (access violations; the lookup is called with several terrain pointers) and hung
  the client. Next: compute the heights offline from the tile files the atlas already reads (the .tdt heights and each
  placed tile's height offset).
- **Heights in the .tdt (decoded offline, 2026-10-18).** The binary parser is `0x1429f31a0`. After the header (§6 of
  08-area-transitions.md) come a u16 flags word, a hull-plane count byte (0 here), then a u16 subtile count (w × h)
  and one record per subtile, row by row: u16 flags, u8, [3 bytes if the high flag byte has bit 1, version ≥ 13],
  then, unless flags bit 2 is set, a **byte map** (`0x1429f59a0`): header byte b, kind = b & 7, n = (b >> 3) & 3 extra
  bytes, size = (b >> 5) | extra << (3 + 8k). Kind 0 = a constant (size is the value; 0 = all zero); kinds 1..4 = an
  LZ4 block of `size` bytes that expands to the 69 / 137 / 281 / 529-byte palette form `0x141d8fc40` reads (2 / 4 / 16
  int8 palette entries + 1 / 2 / 4-bit indices, LSB first, or 529 raw int8). Unless flags bits 0 and 1 are both set,
  a u16 length and that many bytes follow (a second per-cell map). Check: `beachencampment_v01_01.tdt` (15 × 18
  subtiles) placed at cell (69, 0) with the transposing code reproduces the Lioneye's Watch capture exactly (142 830
  cells, 0 differences, height offset 0).
- **Placed tiles of generated areas (live 2026-10-18).** `0x142041200` loops over the finished terrain grid
  (arg0 +0x28: the 0x38-byte entries `0x141d84b00` reads; entry +0 = subtile record whose heights vector is at +0/+8,
  +0x30 i16 height offset, +0x36 code; terrain +0x104 = multiplier, 1 in both areas). The read-only Frida hook
  `scripts/re/frida/capture-terrain-tiles.js` dumps them on entry (no calls into the client; the client stayed up);
  `scripts/walkmaps/heights.py` applies the client's lookup (code tables `0x142fa04b0`, `0x142fb7f98`). The town
  capture reproduces the known grid exactly; The Coast seed 1 is 1725 × 1725, −37..90.
- **Sign.** World z points down: the client's terrain z is −height × 7.8125 (`0x141e35800`), and Positioned's fixed
  height is i32 z / 7.8125 with f32 z, as the area transitions send it. S→C 0x1ba takes +height. Sending +height in the
  fixed height put the fireball under raised ground; with −height it flies above the ground at every level (belly
  height with the default 8 steps).
- **Flight (live 2026-10-18).** S→C 0x1ba's float is the move's **duration in seconds**, not a speed: the handler
  passes distance / it as the speed, and the step walker sets Positioned +0x23c = that speed and +0x240 = the time per
  cell step (duration / the path's longest axis), advancing +0x244 by the frame time (`0x141e32710`). Sending the
  speed (95.7) made each step take ~1 s, so the fireball crawled (~10 world units/s, sampled with Frida); sending
  distance / speed it flies at ~1000 units/s. The fireball now starts `Game_PROJECTILE_AHEAD` cells (4) in front of the
  caster and, cast at the ground, flies on to `Game_PROJECTILE_RANGE` cells (120, clamped to the area); cast at a
  monster it stops there and hits. `/mob_move` and `Game_MOVE_MODE=move` still pass a speed in that field.
- **Still wrong.** Without a height grid the fireball flies on the terrain. The
  four floats of Positioned's f1 0x04 block (+0x2d8..+0x2e4, zero by default) are not the direction: (dx, dy, 0, 0) put
  it under the ground (`Game_PROJECTILE_DIRECTION=1` sends them).

## 11. The skill bar

- **C→S 0x4e** (5 bytes, class `0x1435bfc58`): `u8 slot, u16 skill id` (big-endian). Slot 2 is the right mouse button.
  The skill bar UI (`0x140aecab0`) sends it when the player puts a skill in a slot. The client also does it by itself
  when a skill is **added** to an existing player (0x2ba): it took the first free slot after socketing Fireball. Skills in
  the creation blob, or added before the HUD exists, stay unbound.
- **S→C 0x53** (instance group, class `0x1435dc4c0`, read `0x14219f600`): `13 × u16` skill ids by slot (raw bytes, so
  little-endian; basic actions by their own id, Move = 0x2909), then u16 +0x2a and u16 +0x2c (sent 0). The bar (vtable `0x14319d870`) rebuilds from such an array in
  vtable +0x390 `0x140ae0fb0`: it looks each id up in the ActiveSkillManager and fills the slot; an unknown id (0)
  clears it. Without 0x53 the bar is empty after every area change and login.
- **The Gateway** saves each 0x4e (`CharacterSaves`, data/saves/) as slot → a GrantedEffects Id for gems and the default
  attack (skill ids change with the socketed gems), `Action/<ActionTypes Id>` for a basic action (Move 0x2909 =
  "Move only"), or "" for a cleared slot. It sends 0x53 right after the player and area messages, no delay needed,
  with the default attack on slot 0 only when nothing was saved there. Live 2026-10-18: Fireball on the right mouse
  button and "Move only" on the left one came back after login and area changes, with the bar there at once.

## Open

- The animation's real contact point (the server uses half the attack time); the player's other stats (life, mana,
  attributes) are still not sent; "more" multipliers.
- A 30 Hz server loop: `TickRate` only bounds the attack time; hits use timers and the monster AI runs every 500 ms.
- Monsters use only their first granted effect; no ranged or spell skills, no monster skill choice.
- A damage model: armour, evasion, resistances, crits; the player's real life from level and stats.
- The death screen's bit4 and trailing fields (Sanctum and other modes); exit to character selection (0x153).
- f0 0x20 (skill update), the second and third lists of f0 0x40, supports (linked support gems), gem levels.
- Projectiles: their height above the ground and model orientation (§10), collision, the explosion, the real
  skill → projectile link; damage for ground casts.
- The meaning of 0x53's two trailing u16.
