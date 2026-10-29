# Tattoos

**Status: not implemented (documentation complete, catalogue 100/100).** The server has no tattoo item source,
apply/remove flow or passive-replacement state, so every row below is `not implemented`. This page records the
3.29.3b tattoo catalogue, the client mechanics and the target/limit rules so the feature can be implemented and
verified. The `Status` column deliberately says `not implemented` for every row; after the flow exists, replace it with
`works`, `partial` or `inert` per row and keep the reason. The reason names the extra consumer or special
mechanic beyond the missing tattoo flow itself.

This page is based on the 3.29.3b client tables `BaseItemTypes`, `CurrencyItems`, `PassiveSkillTattoos` and
`PassiveSkillTattooTargetSets`, cross-checked with the poewiki [Tattoo](https://www.poewiki.net/wiki/Tattoo) and
[List of tattoos](https://www.poewiki.net/wiki/List_of_tattoos) pages (2026-10-29) and Path of Building's
`src/Data/TattooPassives.lua`. Path of Building is a read-only third-party reference under `Tools/PathOfBuilding`
(not committed).

## Status summary

| Group | Items | Works | Partial | Inert | Not implemented |
|---|---:|---:|---:|---:|---:|
| Current (drop-enabled) | 53 | 0 | 0 | 0 | 53 |
| Drop-disabled | 40 | 0 | 0 | 0 | 40 |
| Event-only | 7 | 0 | 0 | 0 | 7 |
| **Total** | **100** | **0** | **0** | **0** | **100** |

| Status | Meaning |
|---|---|
| `works` | Implemented and verified with the real client. |
| `partial` | The item/apply flow works, but part of the effect or its condition is not modelled. |
| `inert` | The item/apply flow works, but the effect has no gameplay consumer. |
| `not implemented` | No item source, apply/remove flow or passive-replacement state. |

`stat-only` in the reason column means the effect is a plain stat with no condition; it does **not** claim that the
stat already has a gameplay consumer. The first implementation pass must audit each replacement passive's stat keys.

## What a tattoo is

A tattoo is a currency item (1x1, stack size 10) that replaces the effect of one allocated passive skill. The item
is consumed on use; the passive node keeps its position, connections and allocation cost, but its name, icon and
stats become the tattoo's replacement passive. Tattoos are not jewels: they do not use a socket and they are not
limited by the passive tree's jewel sockets.

The ordinary tattoo items' client directions are: "Right click this item then left click an allocated Passive Skill.
Maximum 50 Tattoos." The 93 non-Forbidden tattoos use `Action = apply_passive_tattoo` and `CurrencyUseType = 7`.
The seven Forbidden ascendancy tattoos have no `apply_passive_tattoo` action (`CurrencyUseType = 3`); their
application is a separate ascendancy-tattoo flow that still needs a client trace.

## Mechanics

### Apply, replace and remove

| Action | Client behaviour |
|---|---|
| Apply | Right-click the tattoo, then left-click an **allocated** passive that matches its target set. The item is consumed and the passive's effect becomes the tattoo's replacement. |
| Replace | Applying another tattoo to the same passive replaces the previous tattoo; the old tattoo is lost. |
| Remove | Use an Orb of Scouring on the tattooed passive. The tattoo is removed and the original passive is restored; the passive stays allocated. |
| Unallocate | Unallocating the passive does **not** remove the tattoo. The tattooed state persists on the node and returns when the passive is allocated again. |
| Limit | A character can have at most **50 applied tattoos**. The state is per character, not per item. |
| Vendor | Three tattoo items from the same tribe can be vendored for one random tattoo of the same tribe. |

### Targets and limits

`PassiveSkillTattoos.Set` selects the target set. The row index maps to `PassiveSkillTattooTargetSets`:

| `PassiveSkillTattoos.Set` | Target set row | Meaning | Items |
|---:|---|---|---:|
| 0 | `SmallStrength` | Small Strength passive | 28 |
| 1 | `SmallDexterity` | Small Dexterity passive | 20 |
| 2 | `SmallIntelligence` | Small Intelligence passive | 22 |
| 3 | `SmallAnyAttribute` | Any small attribute passive | 10 |
| 4 | `BigStrength` | Notable with +30 to Strength | 4 |
| 5 | `BigDexterity` | Notable with +30 to Dexterity | 4 |
| 6 | `BigIntelligence` | Notable with +30 to Intelligence | 4 |
| 7 | `Keystone` | Keystone | 1 |
| 8 | `Mastery` | Mastery | 0 |
| 9 | `Ascendancy` | Ascendancy notable | 7 |

The `Set` field inside `PassiveSkillTattooTargetSets` is a different field (for example `SmallStrength` has
`Set: [1]`); the mapping above uses the **row index** of that table, which is what `PassiveSkillTattoos.Set` stores.
The mapping was checked against Path of Building's `targetType`/`targetValue` for all 92 items that have a
`TattooPassives.lua` entry; there are no mismatches. The remaining eight are Journey Makanui and the seven Forbidden
tattoos, whose target types are read directly from the client table.

Special limits and conditions:

- **Makanga**: four tattoos require **at most 1 adjacent allocated passive** (Arohongui, Ngamahu, Rongokurai,
  Valako); six require **at least 7 adjacent allocated passives** (Hinekora, Kitava, Ramako, Tasalio, Tawhoa,
  Tukohama). The item text says "adjacent"; the wiki mechanics summary describes the same family as 7+ or 8+
  connected, so verify the exact client check when implementing.
- **Loyalty**: limited to 1 Loyalty Tattoo per character.
- **Honoured attribute notables**: the nine Honoured tattoos that replace a +30 attribute notable share a limit of
  1 Attribute Notable Tattoo. The small-attribute Honoured tattoos are not covered by that limit.
- **Ancestral Tattoo of Bloodlines**: limited to 1 Ancestral Tattoo.
- **Journey Tattoo of Makanui**: replaces a keystone and is limited to 1 Keystone Tattoo; it grants a random keystone
  on application.
- **Forbidden**: replaces a non-multiple-choice ascendancy notable and is limited to 1 Ascendancy Tattoo; it grants a
  random non-multiple-choice ascendancy notable from the class's base ascendancies.

### Acquisition

- **Kingsmarch shipments** to the Karui ports: Ngakanu (Strength tattoos), Te Onui (Dexterity tattoos) and Moti Aro
  (Intelligence tattoos). The port is shown per row in the catalogue.
- **Lost Karui Goods** chests, with the Lost in Transit Atlas passive.
- **The Karui Fortress** in Act 6: the pack on the small island across the bridge drops one random tattoo once per
  character.
- Historical: Trial of the Ancestors rewards (Ancestor league), Allflame embers (Necropolis league), and the 3.28
  Return of the Ancestors event. The event-only Forbidden tattoos were introduced in 3.28.0j and deleted at the end
  of that event; the 3.29.3b client still contains their item rows.

### Interactions

- **Unnatural Instinct** grants the effects of tattoos on small **unallocated** passives. A tattoo on an unallocated
  small passive is therefore not inert; the Unnatural Instinct pass must read the tattooed stats.
- **Might of the Meek** and **Warrior's Tale** increase the effect of tattoo stats. The radius scaling must include
  the tattoo replacement's stats, not the original passive's stats.
- **Lethal Pride**, **Brutal Restraint**, **Militant Faith** (non-transformed passives) and Chieftain's Ngamahu,
  Flame's Advance grant their stats in addition to the tattoo stats.
- **Transformed passives** (Glorious Vanity, Elegant Hubris, Militant Faith transformed notables, attribute
  conversion jewels, etc.) prevent or disable tattoos. A timeless-conquered node must not apply its tattoo.
- **Anointed notables** reflect the tattoo's stats, not the original notable's stats.
- **Impossible Escape** and **Thread of Hope** can allocate passives without a connection; this is useful for the
  Makanga adjacency condition. Bypass-allocated passives must count as allocated for the adjacency check.

## Data model

- **100 distinct tattoo names** in `BaseItemTypes` (the 100 rows whose name contains "Tattoo"). Of these, 93 use the
  `apply_passive_tattoo` action; the seven Forbidden ascendancy tattoos use the separate flow described above. The
  client also has 36 Runegraft rows using `apply_passive_tattoo` (including four `[DNT - UNUSED]` rows), so the 129
  `CurrencyItems` rows with that action are 93 tattoos + 36 Runegrafts. Do not treat every `apply_passive_tattoo` item
  as a tattoo.
- **53 drop-enabled**, **40 drop-disabled** and **7 event-only** items in 3.29.3b. The drop-disabled group contains
  the ten Makanga, ten Loyalty, eighteen Honoured, Ancestral Bloodlines and Journey Makanui; the event-only group is
  the seven Forbidden tattoos.
- `PassiveSkillTattoos` has 136 rows: the 100 tattoos (including the seven Forbidden) plus 36 Runegrafts. Each tattoo
  row stores the item row (`Tattoo`), the target-set index (`Set`), the tribe (`Tribe`) and an internal
  `Override`/`OverrideType`.
- The currency item carries **no effect mods**: the replacement passive's stats are the effect. Path of Building's
  `TattooPassives.lua` gives the effect text and stat keys for 92 items; the eight special items (Journey Makanui and
  the seven Forbidden tattoos) need random generation or a special replacement.
- The client's `Override` index needs decoding to the replacement passive before the server can apply the stats. The
  target set and tribe are already available; the replacement stats for the 92 ordinary tattoos are in PoB.

## Implementation plan

1. **Trace the client flow.** Find the C->S packet sent by right-click tattoo + left-click passive, and the state the
   client expects back in 0x33. The current server handles item use and passive allocation separately; tattoos need a
   target passive id and a new save field. Do not guess the packet.
2. **Catalogue and dev source.** Add a `TattooCatalogue` generated from `BaseItemTypes`, `PassiveSkillTattoos`,
   `PassiveSkillTattooTargetSets` and PoB's effect text. Add `/tattoo <name>` (and a list) to create the 100 items
   with the correct stack size, icon and metadata; the seven Forbidden items need their separate currency-use path.
3. **Save shape.** Store a per-character map from passive graph id (or expansion id for cluster/ascendancy nodes, if
   tattoos ever target them) to tattoo item id. The map is independent of allocation, so refunding and reallocating
   the passive preserves the tattoo. Migrate old saves with an empty map.
4. **Apply/remove.** Validate the target set, the item limit, the 50-tattoo cap, the Makanga adjacency condition and
   the special limits; consume the item; install the replacement stats; recompute the stats. Orb of Scouring removes
   the tattoo and restores the original passive without refunding the point. Another tattoo overwrites the old one.
5. **Stat replacement.** Apply the replacement passive's stats in the same pass that builds allocated passive stats.
   The node's allocation, connections and point cost stay unchanged. The replacement must be visible to all
   consumers: panels, damage, defences, flasks, minions, auras, charges and the character sheet.
6. **Interactions.** Keep the timeless-conquered suppression, the Unnatural Instinct unallocated-small-passive rule,
   the Might of the Meek/Warrior's Tale scaling and the anointment replacement rule in the same pass. A tattooed node
   must not double-apply its original stats.
7. **Random specials.** Journey Makanui chooses a random keystone and saves it; the seven Forbidden tattoos choose a
   random non-multiple-choice ascendancy notable from the class's base ascendancies and save it. Use the server RNG
   and save the result, so relog does not reroll.
8. **Vendor recipe.** Three same-tribe tattoo items -> one random same-tribe tattoo, using the existing vendor recipe
   path. Event-only and drop-disabled items need an explicit availability rule.
9. **Tests.** Unit tests for the target-set map, limits, adjacency, save/load and replacement stats; live client checks
   for apply, replace, remove, unallocate/reallocate, relog, Orb of Scouring, 50 cap, Makanga adjacency, Journey
   Makanui and one Forbidden tattoo. Add the real-client evidence to this page when it passes.

## Full catalogue

The effect text is the poewiki item text; the target is the client target set. `Status` is `not implemented` for
every row until the flow exists. `Reason` names the extra consumer or special mechanic beyond the missing tattoo flow.
`Source` is the Kingsmarch port for drop-enabled rows; the drop-disabled and event-only groups are not normally
obtainable in 3.29.

### Journey (3)

These three drop-enabled Journey tattoos count themselves for their own per-allocated-tattoo effect.

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Journey Tattoo of the Body** | +25 to maximum Life per Allocated Journey Tattoo of the Body | Notable (+30 Str) | Ngakanu | not implemented | needs per-allocated-Journey-Tattoo counting |
| **Journey Tattoo of the Mind** | +30 to maximum Mana per Allocated Journey Tattoo of the Mind | Notable (+30 Dex) | Te Onui | not implemented | needs per-allocated-Journey-Tattoo counting |
| **Journey Tattoo of the Soul** | +40 to maximum Energy Shield per Allocated Journey Tattoo of the Soul | Notable (+30 Int) | Moti Aro | not implemented | needs per-allocated-Journey-Tattoo counting |

### Arohongui (5)

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Tattoo of the Arohongui Moonwarden** | +6% to Cold Resistance | Small Dexterity | Te Onui | not implemented | stat-only |
| **Tattoo of the Arohongui Scout** | 10% chance to Avoid being Chilled<br>10% chance to Avoid being Frozen | Small Dexterity | Te Onui | not implemented | needs chill/freeze avoidance rolls |
| **Tattoo of the Arohongui Shaman** | 5% increased Effect of Herald Buffs on you | Small Intelligence | Moti Aro | not implemented | needs Herald buff effect |
| **Tattoo of the Arohongui Warmonger** | 5% chance to Freeze | Small Intelligence | Moti Aro | not implemented | needs freeze chance |
| **Tattoo of the Arohongui Warrior** | 5% increased Cold Damage | Small Dexterity | Te Onui | not implemented | stat-only |

### Hinekora (5)

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Tattoo of the Hinekora Deathwarden** | 6% reduced Effect of Curses on you | Small Intelligence | Moti Aro | not implemented | stat-only |
| **Tattoo of the Hinekora Shaman** | 8% increased Mana Regeneration Rate | Small Intelligence | Moti Aro | not implemented | stat-only |
| **Tattoo of the Hinekora Storyteller** | +3% to Chaos Resistance | Small Intelligence | Moti Aro | not implemented | stat-only |
| **Tattoo of the Hinekora Warmonger** | Minions have 5% increased maximum Life | Small Intelligence | Moti Aro | not implemented | needs minion life |
| **Tattoo of the Hinekora Warrior** | 3% increased maximum Energy Shield | Small Intelligence | Moti Aro | not implemented | stat-only |

### Kitava (5)

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Tattoo of the Kitava Blood Drinker** | 0.5% of Attack Damage Leeched as Life | Small Strength | Ngakanu | not implemented | needs attack leech |
| **Tattoo of the Kitava Heart Eater** | Killing Blows have 4% chance to Consume corpses to recover 10% of Maximum Life | Small Intelligence | Moti Aro | not implemented | needs on-kill corpse consumption |
| **Tattoo of the Kitava Rebel** | 10% chance to Avoid Bleeding | Small Strength | Ngakanu | not implemented | needs bleed avoidance |
| **Tattoo of the Kitava Shaman** | Attacks have 5% chance to cause Bleeding | Small Strength | Ngakanu | not implemented | needs bleed chance |
| **Tattoo of the Kitava Warrior** | 5% increased Global Physical Damage | Small Strength | Ngakanu | not implemented | stat-only |

### Ngamahu (5)

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Tattoo of the Ngamahu Firewalker** | +6% to Fire Resistance | Small Strength | Ngakanu | not implemented | stat-only |
| **Tattoo of the Ngamahu Shaman** | 10% reduced Ignite Duration on you | Small Strength | Ngakanu | not implemented | stat-only |
| **Tattoo of the Ngamahu Warmonger** | 5% chance to Ignite | Small Strength | Ngakanu | not implemented | needs ignite chance |
| **Tattoo of the Ngamahu Warrior** | 5% increased Fire Damage | Small Strength | Ngakanu | not implemented | stat-only |
| **Tattoo of the Ngamahu Woodcarver** | 5% increased Totem Life | Small Strength | Ngakanu | not implemented | needs totem life |

### Ramako (5)

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Tattoo of the Ramako Archer** | 5% increased Global Accuracy Rating | Small Dexterity | Te Onui | not implemented | stat-only |
| **Tattoo of the Ramako Fleetfoot** | 2% increased Movement Speed | Small Dexterity | Te Onui | not implemented | stat-only |
| **Tattoo of the Ramako Scout** | 6% increased Evasion Rating | Small Dexterity | Te Onui | not implemented | stat-only |
| **Tattoo of the Ramako Shaman** | +2% chance to Suppress Spell Damage | Small Dexterity | Te Onui | not implemented | stat-only |
| **Tattoo of the Ramako Sniper** | 5% increased Projectile Speed | Small Dexterity | Te Onui | not implemented | stat-only |

### Rongokurai (5)

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Tattoo of the Rongokurai Brute** | 10% increased Stun Threshold | Small Strength | Ngakanu | not implemented | stat-only |
| **Tattoo of the Rongokurai Goliath** | 10% increased Stun Duration on Enemies | Small Strength | Ngakanu | not implemented | stat-only |
| **Tattoo of the Rongokurai Guard** | Guard Skills have 6% increased Duration | Small Strength | Ngakanu | not implemented | needs guard-skill duration |
| **Tattoo of the Rongokurai Turtle** | You take 5% reduced Extra Damage from Critical Strikes | Small Intelligence | Moti Aro | not implemented | stat-only |
| **Tattoo of the Rongokurai Warrior** | 6% increased Armour | Small Strength | Ngakanu | not implemented | stat-only |

### Tasalio (5)

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Tattoo of the Tasalio Bladedancer** | Attacks have 5% chance to Maim on Hit | Small Strength | Ngakanu | not implemented | needs maim on hit |
| **Tattoo of the Tasalio Scout** | 4% increased Effect of your Marks | Small Dexterity | Te Onui | not implemented | needs mark effect |
| **Tattoo of the Tasalio Shaman** | 5% chance to Hinder Enemies on Hit with Spells | Small Intelligence | Moti Aro | not implemented | needs hinder on spell hit |
| **Tattoo of the Tasalio Tideshifter** | 10% chance to Avoid being Stunned | Small Dexterity | Te Onui | not implemented | needs stun avoidance |
| **Tattoo of the Tasalio Warrior** | 5% chance to Blind Enemies on Hit with Attacks | Small Dexterity | Te Onui | not implemented | needs blind on attack hit |

### Tawhoa (5)

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Tattoo of the Tawhoa Herbalist** | 4% increased Flask Effect Duration | Small Dexterity | Te Onui | not implemented | needs flask effect duration |
| **Tattoo of the Tawhoa Naturalist** | 8% increased Life Recovery from Flasks | Small Dexterity | Te Onui | not implemented | needs flask life recovery |
| **Tattoo of the Tawhoa Scout** | 10% chance to Avoid being Poisoned | Small Dexterity | Te Onui | not implemented | needs poison avoidance |
| **Tattoo of the Tawhoa Shaman** | 5% chance to Poison on Hit | Small Intelligence | Moti Aro | not implemented | needs poison chance |
| **Tattoo of the Tawhoa Warrior** | 5% increased Chaos Damage | Small Intelligence | Moti Aro | not implemented | stat-only |

### Tukohama (5)

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Tattoo of the Tukohama Brawler** | 10% chance to Knock Enemies Back on hit | Small Strength | Ngakanu | not implemented | needs knockback on hit |
| **Tattoo of the Tukohama Shaman** | Regenerate 0.3% of Life per second | Small Strength | Ngakanu | not implemented | stat-only |
| **Tattoo of the Tukohama Warcaller** | 8% increased Warcry Cooldown Recovery Rate | Small Strength | Ngakanu | not implemented | needs warcry cooldown recovery |
| **Tattoo of the Tukohama Warmonger** | 5% increased Melee Damage | Small Strength | Ngakanu | not implemented | stat-only |
| **Tattoo of the Tukohama Warrior** | Melee Hits which Stun have 5% chance to Fortify | Small Strength | Ngakanu | not implemented | needs fortify on stun |

### Valako (5)

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Tattoo of the Valako Scout** | 10% reduced Effect of Shock on you | Small Intelligence | Moti Aro | not implemented | stat-only |
| **Tattoo of the Valako Shaman** | 5% chance to Shock | Small Intelligence | Moti Aro | not implemented | needs shock chance |
| **Tattoo of the Valako Shieldbearer** | +1% Chance to Block Attack Damage | Small Strength | Ngakanu | not implemented | stat-only |
| **Tattoo of the Valako Stormrider** | +6% to Lightning Resistance | Small Intelligence | Moti Aro | not implemented | stat-only |
| **Tattoo of the Valako Warrior** | 5% increased Lightning Damage | Small Intelligence | Moti Aro | not implemented | stat-only |

### Ancestral (1)

Limited to 1 Ancestral Tattoo.

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Ancestral Tattoo of Bloodlines** | 2% increased Recovery Rate of Life, Mana and Energy Shield per Tribe for which you have an allocated Tattoo | Small Attribute | drop-disabled | not implemented | needs per-tribe tattoo counting; limit 1 Ancestral Tattoo |

### Honoured (18)

The nine +30-attribute notable tattoos share the limit of 1 Attribute Notable Tattoo; the nine small-attribute
tattoos do not. All are drop-disabled in 3.29.

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Honoured Tattoo of the Barbarian** | +1% to maximum Cold Resistance<br>+1 to Maximum Frenzy Charges | Notable (+30 Dex) | drop-disabled | not implemented | needs max cold res + frenzy charge cap |
| **Honoured Tattoo of the Berserker** | You gain Onslaught for 4 seconds on Hit | Notable (+30 Dex) | drop-disabled | not implemented | needs Onslaught-on-hit buff |
| **Honoured Tattoo of the Dove** | +4% to Chaos Resistance | Small Attribute | drop-disabled | not implemented | stat-only |
| **Honoured Tattoo of the Flock** | 1% increased effect of Non-Curse Auras from your Skills | Small Attribute | drop-disabled | not implemented | needs non-curse aura effect |
| **Honoured Tattoo of the Flood** | Gain Arcane Surge on Hit with Spells | Notable (+30 Int) | drop-disabled | not implemented | needs Arcane Surge on spell hit |
| **Honoured Tattoo of the Hatungo** | 1% increased Reservation Efficiency of Skills | Small Attribute | drop-disabled | not implemented | needs reservation efficiency |
| **Honoured Tattoo of the Hunter** | +1 to Level of all Dexterity Skill Gems | Notable (+30 Dex) | drop-disabled | not implemented | needs Dexterity gem levels |
| **Honoured Tattoo of the Makanga** | +2% to all maximum Resistances | Small Attribute | drop-disabled | not implemented | stat-only |
| **Honoured Tattoo of the Mountain** | +1% to maximum Fire Resistance<br>+1 to Maximum Endurance Charges | Notable (+30 Str) | drop-disabled | not implemented | needs max fire res + endurance charge cap |
| **Honoured Tattoo of the Oak** | 2% increased maximum Life | Small Attribute | drop-disabled | not implemented | stat-only |
| **Honoured Tattoo of the Pa** | Melee Hits Fortify | Notable (+30 Str) | drop-disabled | not implemented | needs melee-hit fortify |
| **Honoured Tattoo of the Pillager** | 3% increased Rarity of Items found | Small Attribute | drop-disabled | not implemented | stat-only |
| **Honoured Tattoo of the Sky** | +3% to all Elemental Resistances | Small Attribute | drop-disabled | not implemented | stat-only |
| **Honoured Tattoo of the Storm** | +1% to maximum Lightning Resistance<br>+1 to Maximum Power Charges | Notable (+30 Int) | drop-disabled | not implemented | needs max lightning res + power charge cap |
| **Honoured Tattoo of the Tuatara** | +4 to all Attributes | Small Attribute | drop-disabled | not implemented | stat-only |
| **Honoured Tattoo of the Turtle** | 3% increased Global Defences | Small Attribute | drop-disabled | not implemented | stat-only |
| **Honoured Tattoo of the Warlord** | +1 to Level of all Strength Skill Gems | Notable (+30 Str) | drop-disabled | not implemented | needs Strength gem levels |
| **Honoured Tattoo of the Wise** | +1 to Level of all Intelligence Skill Gems | Notable (+30 Int) | drop-disabled | not implemented | needs Intelligence gem levels |

### Journey Makanui (1)

Replaces a keystone, is limited to 1 Keystone Tattoo, and grants a random keystone on application.

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Journey Tattoo of Makanui** | Grants a random Keystone | Keystone | drop-disabled | not implemented | needs random keystone generation; limit 1 Keystone Tattoo |

### Loyalty (10)

Each is limited to 1 Loyalty Tattoo and triggers a Summon Spirit skill; most triggers require a Unique Enemy in your
Presence. All are drop-disabled in 3.29.

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Loyalty Tattoo of Ahuana** | Trigger level 1 Summon Spirit of Ahuana Skill when you Suppress Spell Damage from a Unique Enemy | Small Dexterity | drop-disabled | not implemented | needs Summon Spirit + suppress trigger; limit 1 Loyalty Tattoo |
| **Loyalty Tattoo of Akoya** | Trigger level 1 Summon Spirit of Akoya Skill when you reach Maximum Rage while a Unique Enemy is in your Presence | Small Strength | drop-disabled | not implemented | needs Summon Spirit + max-rage trigger; limit 1 Loyalty Tattoo |
| **Loyalty Tattoo of Ikiaho** | Trigger level 1 Summon Spirit of Ikiaho Skill when you use a Travel Skill while a Unique Enemy is in your Presence | Small Dexterity | drop-disabled | not implemented | needs Summon Spirit + travel-skill trigger; limit 1 Loyalty Tattoo |
| **Loyalty Tattoo of Kahuturoa** | Trigger level 1 Summon Spirit of Kahuturoa Skill when you take a Critical Strike from a Unique Enemy | Small Strength | drop-disabled | not implemented | needs Summon Spirit + crit-taken trigger; limit 1 Loyalty Tattoo |
| **Loyalty Tattoo of Kaom** | Trigger level 1 Summon Spirit of Kaom Skill when a Totem dies while a Unique Enemy is in your Presence | Small Strength | drop-disabled | not implemented | needs Summon Spirit + totem-death trigger; limit 1 Loyalty Tattoo |
| **Loyalty Tattoo of Kiloava** | Trigger level 1 Summon Spirit of Kiloava Skill when you Block Damage from a Unique Enemy | Small Intelligence | drop-disabled | not implemented | needs Summon Spirit + block trigger; limit 1 Loyalty Tattoo |
| **Loyalty Tattoo of Maata** | Trigger level 1 Summon Spirit of Maata Skill when you reach Low Life while a Unique Enemy is in your Presence | Small Intelligence | drop-disabled | not implemented | needs Summon Spirit + low-life trigger; limit 1 Loyalty Tattoo |
| **Loyalty Tattoo of Rakiata** | Trigger level 1 Summon Spirit of Rakiata Skill on Critical Strike against Marked Unique Enemy | Small Dexterity | drop-disabled | not implemented | needs Summon Spirit + marked-unique crit trigger; limit 1 Loyalty Tattoo |
| **Loyalty Tattoo of Tawhanuku** | Trigger level 1 Summon Spirit of Tawhanuku Skill when Energy Shield Recharge starts while a Unique Enemy is in your Presence | Small Intelligence | drop-disabled | not implemented | needs Summon Spirit + ES-recharge trigger; limit 1 Loyalty Tattoo |
| **Loyalty Tattoo of Utula** | Trigger level 1 Summon Spirit of Utula Skill on taking a Savage Hit from a Unique Enemy | Small Strength | drop-disabled | not implemented | needs Summon Spirit + savage-hit trigger; limit 1 Loyalty Tattoo |

### Makanga (10)

Four require at most 1 adjacent allocated passive (Arohongui, Ngamahu, Rongokurai, Valako); six require at least
7 adjacent allocated passives (Hinekora, Kitava, Ramako, Tasalio, Tawhoa, Tukohama). All are drop-disabled in 3.29.

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Tattoo of the Arohongui Makanga** | +1% to maximum Cold Resistance | Small Dexterity | drop-disabled | not implemented | adjacency max 1; stat-only |
| **Tattoo of the Hinekora Makanga** | 8% increased Effect of your Curses | Small Intelligence | drop-disabled | not implemented | adjacency min 7; needs curse effect |
| **Tattoo of the Kitava Makanga** | 80% increased Maximum total Life Recovery per second from Leech | Small Strength | drop-disabled | not implemented | adjacency min 7; needs leech recovery cap |
| **Tattoo of the Ngamahu Makanga** | +1% to maximum Fire Resistance | Small Strength | drop-disabled | not implemented | adjacency max 1; stat-only |
| **Tattoo of the Ramako Makanga** | Skills fire an additional Projectile | Small Dexterity | drop-disabled | not implemented | adjacency min 7; needs additional projectile |
| **Tattoo of the Rongokurai Makanga** | 8% chance to Defend with 200% of Armour | Small Strength | drop-disabled | not implemented | adjacency max 1; needs armour-defend roll |
| **Tattoo of the Tasalio Makanga** | 8% increased Cooldown Recovery Rate | Small Intelligence | drop-disabled | not implemented | adjacency min 7; needs cooldown recovery |
| **Tattoo of the Tawhoa Makanga** | Flasks applied to you have 8% increased Effect | Small Dexterity | drop-disabled | not implemented | adjacency min 7; needs flask effect |
| **Tattoo of the Tukohama Makanga** | 16% increased Life Regeneration rate | Small Strength | drop-disabled | not implemented | adjacency min 7; stat-only |
| **Tattoo of the Valako Makanga** | +1% to maximum Lightning Resistance | Small Intelligence | drop-disabled | not implemented | adjacency max 1; stat-only |

### Forbidden (7)

Each replaces a non-multiple-choice ascendancy notable, is limited to 1 Ascendancy Tattoo, and grants a random
non-multiple-choice notable from the class's base ascendancies. Event-only; the items are deleted at the end of the
Return of the Ancestors event.

| Tattoo | Effect | Replaces | Source | Status | Reason |
|---|---|---|---|---|---|
| **Forbidden Tattoo of the Duelist** | Grants a random Duelist Ascendancy Notable | Ascendancy Notable | event only | not implemented | needs random Duelist ascendancy notable; limit 1 Ascendancy Tattoo |
| **Forbidden Tattoo of the Marauder** | Grants a random Marauder Ascendancy Notable | Ascendancy Notable | event only | not implemented | needs random Marauder ascendancy notable; limit 1 Ascendancy Tattoo |
| **Forbidden Tattoo of the Ranger** | Grants a random Ranger Ascendancy Notable | Ascendancy Notable | event only | not implemented | needs random Ranger ascendancy notable; limit 1 Ascendancy Tattoo |
| **Forbidden Tattoo of the Scion** | Grants a random Scion Ascendancy Notable | Ascendancy Notable | event only | not implemented | needs random Scion ascendancy notable; limit 1 Ascendancy Tattoo |
| **Forbidden Tattoo of the Shadow** | Grants a random Shadow Ascendancy Notable | Ascendancy Notable | event only | not implemented | needs random Shadow ascendancy notable; limit 1 Ascendancy Tattoo |
| **Forbidden Tattoo of the Templar** | Grants a random Templar Ascendancy Notable | Ascendancy Notable | event only | not implemented | needs random Templar ascendancy notable; limit 1 Ascendancy Tattoo |
| **Forbidden Tattoo of the Witch** | Grants a random Witch Ascendancy Notable | Ascendancy Notable | event only | not implemented | needs random Witch ascendancy notable; limit 1 Ascendancy Tattoo |

## Verification log

No live checks yet: the server cannot create or apply a tattoo. The first implementation should add the client trace
and the apply/remove evidence here, then update each row's `Status` and `Reason`.

## See also

- [Unique jewels](unique-jewels.md), [radius jewels](radius-jewels.md) and [cluster jewels](cluster-jewels.md) for
  the other passive-tree item families.
- [Warrior's Tale](radius-jewels.md) is the radius jewel that scales tattoo effects; it is currently inactive for the
  same reason (no tattoo model).
- [Protocol 15: progression](../protocol/15-progression.md) for the passive tree packets and the save path.
