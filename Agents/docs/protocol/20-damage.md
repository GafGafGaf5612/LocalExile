# 20. Damage: hits, conversion and damage over time

This is the model the Gateway uses for combat. It is deliberately separate from the protocol messages: 0x1b1 carries a
life total, so everything here happens before that total is sent. The reference is Path of Exile's
[Damage](https://www.poewiki.net/wiki/Damage), [Damage conversion](https://www.poewiki.net/wiki/Damage_conversion) and
[Damage over time](https://www.poewiki.net/wiki/Damage_over_time) pages, with the exact conversion bookkeeping checked
against the local Path of Building (`Tools/PathOfBuilding/src/Modules/CalcOffence.lua`: `dmgTypeList`,
`buildConversionTable` and `calcDamage`).

## 1. Types and order

A hit is a **packet** with a separate minimum/maximum range for each type:

| Type | Notes |
|---|---|
| Physical | reduced by the defender's armour and physical damage reduction |
| Lightning | elemental |
| Cold | elemental |
| Fire | elemental; its damage over time is called burning |
| Chaos | not elemental; ignores energy shield in the real game |

Conversion only goes **forward** in this order:

```
physical → lightning → cold → fire → chaos
```

So physical can become fire, lightning or cold; cold can become fire or chaos; fire can become chaos. Fire cannot
become cold, and chaos does not convert. The `DamageType` enum is numbered in this same order (the old enum's numeric
values were not used on the wire).

## 2. Hit pipeline

```
base skill/weapon damage by type
  → added damage (DamageStats.Added; the first added-damage stat is read, see §2)
  → gain X% as extra (does not consume the source)
  → conversion (source is consumed; converted damage can convert again)
  → increased / more multipliers per type
  → critical strike
  → per-type resistance / mitigation (the critical-strike elemental rules resolve first, §4)
  → roll a whole number per type and send the life change
```

`DamagePacket` is immutable and holds one `DamageRange` per type. `DamageConversion` holds global conversion,
skill conversion and gain-as-extra percentages. `DamageConversion.Apply` processes the types in the table's order, so a
converted portion reaches the next type and can be converted again. Skill conversion is applied first; global
conversion fills whatever remains of the 100% cap, and the final conversion for a pair is stored as a whole percent,
as in Path of Building. Gains are added to the destination without reducing the source, matching the wiki's
"gain as extra" rule.

A player hit on a monster rolls each type once and then takes the monster's own base resistance on each type
(MonsterResistanceCatalog, §4), before the critical multiplier and the life update. A monster's attack on the player
does the mirror pass through the player's resistances. Damage over time is outside this pass: it has no hit, and
Righteous Fire's monster burn keeps its per-tick percentage-of-life model with no resistance or penetration.

Penetration lowers the target's resistance before the damage is reduced. The server caps the monster's base
resistance first (the 75 default, the 90 absolute maximum, the −200 floor) and subtracts the hit's penetration from
that capped value with no second clamp, as Path of Building's `calcResistForType` and hit branch do (16de4b8, lines
494..509 and 3490..3500): the effective resistance can go below the floor, so penetration against a negative
resistance deals more than the raw damage. Penetration is read per hit from the player's and the skill's own stats
and never touches the rolled input; the critical multiplier still applies once, to the mitigated total.
Player and skill contributions to the same raw penetration stat add in `Penetration.Of`.
All three hit paths collect those contributions before the general damage-stat dictionary overlays skill values.
Nine real-client checks read the target's native Life component after each authoritative update.
A level-68 fire skeleton took 60 from fixed 100 fire, then 80, 90 and 100 as penetration sources stacked.
The prohibition restored 60; its zero cold resistance took 100, then 120 with 20 cold penetration.
Map Hillock's 25% chaos resistance took 75, then 105 with 30 attack chaos penetration.

The unconditional stats the server reads (whole percents, this client's Stats.dat names):

| Stats | Scope |
|---|---|
| `base_reduce_enemy_{fire,cold,lightning}_resistance_%` | every hit on that element; the historical penetration stats despite the name (the per-element Penetration support gems grant them, as do the Heart of Flame/Ice/Thunder passives and the jewel mods) |
| `base_penetrate_elemental_resistances_%` | every hit on fire, cold and lightning (the Inquisitor's start node, Martyr's Legacy) |
| `base_attack_damage_penetrates_{fire,cold,lightning,chaos}_resist_%` | attacks only |
| `base_attack_damage_penetrates_elemental_resist_%` | attacks only, on fire, cold and lightning (Forces of Nature, Warden) |
| `cannot_penetrate_or_ignore_elemental_resistances` | a flag: every elemental penetration is off for the player's hits; chaos is not elemental |

The `main_hand_…`/`off_hand_…` and `local_…` weapon-hand aliases are not read (the server attacks with the main
hand's global stats, and a weapon-local scope guess would double-count), and neither are the non-base
`reduce_enemy_elemental/chaos_resistance_%` variants (Penetration Support's own granted stat in this client) or the
conditional ones tied to auras or enemy state.
The non-critical companion is handled by the critical resistance rules below; other conditions remain separate work.
Spells take the supported general stats only; this consumer supports chaos through the attack-only stat in this
client, only the attack-only one; physical damage never penetrates.

The same packet is used for attacks, spells and monster hits. The caller decides whether the attack-only or spell-only
stats apply; `DamageStats` reads the Stats.dat names:

| Kind | Example stat |
|---|---|
| base conversion | `base_physical_damage_%_to_convert_to_fire` |
| skill conversion | `skill_physical_damage_%_to_convert_to_cold` |
| global conversion | `physical_damage_%_to_convert_to_lightning` |
| attack conversion | `attack_physical_damage_%_to_convert_to_fire` |
| spell conversion | `spell_physical_damage_%_to_convert_to_fire` |
| gain as extra | `physical_damage_%_to_add_as_fire` |
| non-chaos as chaos | `non_chaos_damage_to_add_as_chaos_damage_%` |

`skill_…` stats use the skill-conversion table; `base_…`, `attack_…`, `spell_…` and the plain
`…_damage_%_to_convert_to_…` names use the global table. Conditional variants ("while affected by Anger", "against
frozen enemies", ...) are not read yet. Skill conversion takes precedence when skill and global conversion together
exceed 100%, matching PoB's `buildConversionTable`.

## 2b. Added damage from stats (the first stat)

Added damage is not fully modelled yet, but one stat is read.
`DamageStats.Added` adds `attack_skills_have_added_lightning_damage_equal_to_%_of_maximum_mana` (Stats.dat row
11881, client key 11882) to an attack's added-lightning range.
The value is the percentage: both the added lightning minimum and maximum become that percent of `maximum_mana`
from the same final stat dictionary, so spending mana and reserved mana never change the damage, while a pool
change (level, equipment, passives) does.
Path of Building pinned 16de4b82 (ModParser.lua lines 5636–5639) maps the node's value to both LightningMin and
LightningMax as BASE with PercentStat Mana and ModFlag Attack; the packet agrees.
At 6% of a 1000 mana pool (Whisperer "we know all", PassiveSkills.dat graph id 22720) the attack gets 60-60
added lightning, not 1-60.
The pool bonus joins the added range before the increases, so the increased attack-damage stats and later
conversion treat it like any other added lightning, and a fractional result is only rounded at the existing roll
and panel stages.
Ordinary attacks use `AttackHit` or the bow release snapshot; attack gems retain main's `SupportedHit` pipeline.
Both constructors read the same raw `DamageStats.ManaAddedLightning` grant before increases and effectiveness.
Support restrictions, support multipliers, conversion, target blocking and post-hit stages remain in that pipeline.
This stack does not introduce the separate derived-damage engine from archival PR #206.
Automated production-path cases cover ranged and melee gems, effectiveness and a spell isolation control.
Native evidence here belongs to original head `378ffde`; supported-skill composition needs a new native check.
`attack_damage_+%` scales the added range for every attack.
`melee_damage_+%` applies only in explicit melee context: the default attack uses its active weapon profile,
and attack gems use `ActiveSkillType Melee` through `SkillCatalog.IsMelee`.
An inactive weapon set cannot change that context.
Spell hits and damage over time are untouched, and saved gem quality/experience is not read.
The node's separate mana-cost drawback (`base_mana_cost_-%`) is a mana-cost change outside this damage
consumption's scope.
Fourteen real-client stat checks used a fresh synthetic Witch and development overrides for these inputs.
With the 6% grant, 56 maximum mana showed 3–3 added lightning and 1000 showed 60–60.
Adding 50% increased lightning showed 90–90; 997 maximum mana showed 59–59 after the panel's existing floor.
With 1000 mana, 50% increased attack damage showed 90–90; another 50% melee increase raised unarmed damage to 120–120.
A wand stayed at 90–90 even with a club in the inactive set; switching to the club gave 120–120 and back gave 90–90.
Clearing the attack increase left the ranged wand at 60–60 despite the melee input.
Clearing the grant removed the lightning rows' damage, and relog restored the baseline maximum mana and damage.
These checks validate the generic damage consumer; ascendancy allocation remains in its separate lifecycle change.

## 3. Damage over time and ailments

DoT is one damage type, has no hit, cannot critically strike and uses that type's resistance. `DamageOverTime` carries
the damage per second, the remaining seconds and an optional ailment. `Tick(seconds, carry)` returns whole damage and
the fraction to carry into the next server tick, so a 0.35 s tick does not lose damage; it never runs past
`RemainingSeconds`. Call `Advance` when applying a duration.

The modelled damaging ailments are:

| Ailment | Damage type | Base | Duration |
|---|---|---|---|
| Ignite | fire | 90% of the hit's fire damage per second | 4 s |
| Bleed | physical | 70% of the hit's physical damage per second (200% while moving is not modelled) | 5 s |
| Poison | chaos | 30% of the hit's physical + chaos damage per second, stacking | 2 s |

`Ailments.Ignite/Bleed/Poison` build the effect from a hit packet. Pass the **converted base packet before hit-only
increased/more modifiers**; the method's `increasedPct`/`more` arguments are the ailment-specific multipliers, so a
caller cannot double-count hit modifiers. For a specific hit pass its rolled packet; for expected damage pass the
average range. The caller applies the target's resistance. Non-damaging ailments (chill, freeze, shock), critical
ailment damage, ailment chance/application and ailment duration stats are not modelled yet. Righteous Fire still uses
its existing per-tick percentage-of-life burn; it is a fire DoT but not an ignite.

## 4. What the server uses now

- The default attack builds a physical packet from Characters.dat (`Unarmed.HitDamage`), applies the player's
  conversion stats, rolls one number per type and applies the critical multiplier to the rolled total.
- Fireball builds its packet from `spell_minimum/maximum_base_<type>_damage` (`SkillCatalog.HitDamage`), applies the
  spell's increased-damage stats per type (`SpellDamage.Packet`), then the player's and the skill's conversion stats,
  rolls it and sends the life change.
- Monster melee is a physical packet (`MonsterAttack.Damage`); the server rolls it when the swing lands, applies each
  type's resistance and then armour to the physical part.
- Player hits on monsters (default attack, gem projectile and area hits) roll each type once and reduce it by the
  monster's base resistance: `MonsterTypes.Resistances` → `MonsterResistances`, the level's
  `DefaultMonsterStats.Difficulty` (1/2/3) picking the Normal/Cruel/Merciless columns (`MonsterResistanceCatalog`,
  `MonsterCatalog.BaseResistance`, the shared pass is `HitMitigation`). Without the extracted tables every type reads
  0 for the base input.
  Spawned-monster resistance modifiers add before the cap; physical reduction and damage taken apply after mitigation.
  Ordinary bows snapshot player/skill penetration at release and resolve target resistance at contact.
  Main has no connection-aware resistance or presence engine; those archival #206 systems are outside this stack.
  Automated mixed fractional-hit, target-modifier, bow RNG and same-key player/skill cases cover the resolved paths.
  Native penetration checks belong to original head `0d800d1`; recheck the target-modifier and bow composition natively.
- The hit's penetration (`Penetration.cs`) subtracts from the capped resistance with no second clamp: the general
  per-element and elemental stats on every hit, the attack-only variants on attacks (§2), the
  `cannot_penetrate_or_ignore_elemental_resistances` flag suppressing the elemental ones. Damage over time and the
  physical part take no penetration.
- Critical elemental hits with `critical_strikes_ignore_elemental_resistances` use effective resistance zero,
  including when the target's resistance or maximum is negative; ignore overrides penetration.
  `attack_critical_strikes_ignore_elemental_resistances` covers attacks only.
  Non-critical elemental hits add `non_critical_strikes_penetrate_elemental_resistances_%` to general penetration.
  Player and skill contributions to the same key sum; the prohibition suppresses all elemental penetration and ignore.
  Physical, chaos and DoT keep their separate rules.
  Damage types roll once, followed by the original single critical draw; deterministic mitigation uses that result.
  Target physical reduction and damage taken follow mitigation, preserving fractional components until the final total.
  Ordinary bows retain offensive resistance rules in their release snapshot and resolve the target at contact.
  Native checks on original head `13c41e5` verified the basic ignore and non-critical rules.
  Ten composed native groups pass at source `1366f48`, with Gateway/Core/Host embedded Git versions matching it.
  Critical panels show 657/505, then 657/755 after a spell-only increase; active Sea Legs gives 1204 and empty-set zero.
  Fixed mana lightning produces actual Life losses 90/120/60 for ranged/melee/ranged-only contexts.
  Against a level-68 fire skeleton, fixed 100 fire + 100 cold deals 160, or 220 after 40/20 penetration.
  A 150% critical multiplier then gives 300 with ignore, 330 without it and 240 with the prohibition.
  These values are read from the native Life component after the authoritative hit.
  The run uses a fresh unascended Witch and exercises default attacks, not the ascendancy chooser or whole classes.
  Supported-gem, modifier/bow and Purity-replacement combinations retain their automated integration coverage.
  Earlier native evidence remains attributed to its original heads; a stale #225 DLL did not test #226.
  Rebuild after the final checkout and verify embedded Git versions, hashes and the running DLL before `-NoBuild`.
  See [native build provenance](../dev/client-control.md#build-provenance-for-native-verification).
- `DamageModel.Mitigate(DamagePacket, ...)` applies each type's own capped resistance. The existing single-value
  `Mitigate` overloads are used by `/hit` and Righteous Fire; physical `/hit`, `/spellhit` and `/blockhit` use
  `MitigatePhysical`.
- `/hit` and `/spellhit` still use a single type; conversion is not applied to those debug commands.

## 5. Continuous resource updates (2026-10-24)

The client Life handler for S→C 0x1b1 stores the current whole pool value; merely sending the
regeneration stat does not make the visible life value advance between these updates. The former
250 ms player tick therefore made /god recovery jump by roughly one quarter of maximum life.

`RunResourceRecovery` now combines life/mana regeneration, flask recovery and the player's nonlethal
Righteous Fire burn in one 16 ms loop. Each step integrates the actual elapsed `ClientState.Clock`
time, including scheduler delays; the clock stops during pause. `RecoveryPool` retains fractional
points, discards overflow at a full pool and negative carry at RF's one-life floor, and never revives
dead life. Only changed whole values generate 0x1b1. The original per-minute rate formulas and
unreserved mana cap are unchanged. Stat aggregation is refreshed at the old 250 ms cadence, with
an additional refresh when RF changes, instead of rebuilding the panel about 60 times a second.

`RunPlayerTick` keeps the 250 ms cadence for RF on monsters, suppression conditions, waypoints,
auto-loot and quests; its RF duration also uses elapsed game time. Instant damage and instant
healing remain immediate. No client executable or packet layout changes are needed. Live visual
verification: enable /god, damage life with `/blockhit attack physical 10000`, then observe recovery;
also check pause, flasks and RF. The new visual behavior awaits that real-client check.

## 6. Not modelled yet

- Energy shield, exposure, shock and other damage-taken modifiers; monster armour (the monsters' base resistances are
  in, §4, with unconditional penetration).
- Added flat damage besides the maximum-mana granted lightning stat (supports/items, the other added-damage
  stats), increased/more modifiers on the base damage, and skill-specific added-damage scaling.
- Ailment application, duration and effect; critical ailments; bleed movement bonus; poison stacking bookkeeping.
- Per-monster damage conversion and monster-specific mitigation.
- Damage reflection, leech, life/mana gain on hit, and recoup.
- Party/minion damage and the full support-gem effect set.

The next support-gem work (#64) can add its effects as packet operations: `Add` for added damage, `Scale` for
increased/more, and `AddGain`/`Convert` for conversion, without touching the life-update code.

## Player energy shield (2026-10-25)

`EnergyShield.cs` owns the ordinary ES pool, fractional recovery, damage interruption, and recharge phase. Equipment
reads ArmourTypes ES bounds (the same minimum-base percentile currently serialized on items), local flat/increased ES,
quality, intelligence, passive ES and global defence increases. Offhand shields count. Non-chaos damage after mitigation
spends ES before life; chaos bypasses it. A positive damaging hit interrupts recharge; a zero/blocked hit does not.
Death empties ES; existing heal/resurrection/entry refills include it. Righteous Fire uses its life and ES burn inputs
and absorbs its non-chaos self-damage through ES before life. Special keystones and conditional mastery effects are
not included (CI, Eldritch Battery, Zealot's Oath, Wicked Ward, leech and related substitutions).

Character.ot supplies 2000% of maximum per minute: base recharge is one third per second, after a two-second delay.
Faster start divides the delay by `1 + energy_shield_delay_-%/100`; recharge/regen recovery increases scale their rates.
Elapsed game time integrates only the part of a tick after the delay boundary, retaining fractions. Base constants
also match [Path of Building's data](https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/dev/src/Modules/Data.lua)
and [defence calculation](https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/dev/src/Modules/CalcDefence.lua).

Pool 2 of 0x1b1 synchronizes current ES. Updating the virtual `energy_shield_recharge_active` stat through 0x1b3 did
**not** start the client's recharge predictor in the live probe: it predicted only baseline regeneration and jumped
on the next pool correction. During the server recharge phase, publish regeneration plus recharge through the verified
native `energy_shield_recovery_per_minute` input (key 0x590), retaining the separate recharge-rate panel stat. The virtual
recharge-active stat stays zero to avoid double prediction. This is a compatibility bridge; the native server event
that starts the client's distinct recharge phase has not been identified.

Live OfflineWitch had 185 ES: `/hit physical 100` left life at 3250 and ES at 85, then recovered to 185. Chaos reduced
life while leaving ES intact. With the prediction bridge, client values advance between pool packets during recharge.
Measurements used the local client Life component, not orb pixels or an official server trace. ES uses upstream RunResourceRecovery with its 16 ms target and elapsed time/fractional carries; foreground verification saw at most 2-point positive ES updates
instead of the previous 22-point steps. OfflineWitch's selected Energy Shield mastery grants 2% ES regeneration per
second (3.7 at 185 ES); that regeneration correctly continues while recharge is interrupted. A temporary zero-regen
control recovered nothing during the recharge delay. See [recovery timing](../re/recovery-timing.md) for evidence and
measurement limits. Recovery-cadence PR #159 is merged; this change extends its existing recovery loop.
