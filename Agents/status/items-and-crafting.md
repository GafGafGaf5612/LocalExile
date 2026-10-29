# Status: Items, currency and crafting

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/items-and-crafting.md](../roadmap/items-and-crafting.md).

## Works (verified with the real client)

- Ground items belong to the area instance: dropped items and loot stay where they lay across logouts, disconnects and
  client crashes until the instance expires; ids from 0x100000; one pickup per item.
  Code: `InstanceStore.cs`, Gateway.
  Docs: [13](../docs/protocol/13-items.md) §5.
- Life and mana flasks: a flask key (C→S 0x70) spends the flask's charges and recovers Flasks.dat's life or mana over
  its time in the shared 16 ms recovery loop, the orb rising by +1, with the flask_effect buff.
  Quality, the flask's prefixes and suffixes and the character's flask stats count (amount, rate, instant and partial
  instant, low life, other-pool costs, mana at the end, not removed at full, extra life), as Path of Building does.
  Several recovery flasks queue, the highest rate first (poewiki).
  Checked live: the base recovery and the extra-life suffix; the other mods and queueing by tests.
  A successful use also sends S→C `0x263`; the native UI then looks the base type up and plays its use sound,
  `Audio/Sound Effects/ItemSounds/flaskdrink.ogg` (traced on the official 3.29.3b client and captured from it).
  Full pools refuse the flask before any charge or buff, with silent `0x263` result `0x4e2` and the authoritative pool;
  mana fullness uses the unreserved cap (full-pool refusals verified live locally).
  The client also pays the attack skill's mana for attack-moves the server only walks; the Gateway answers with the
  authoritative mana so the orb and the flasks agree (probe 2026-10-26).
  Code: `Flasks.cs`, `FlaskRecovery.cs`, Gateway (`UseFlask`, `RunResourceRecovery`).
  Docs: [13](../docs/protocol/13-items.md) §8.
- Flask charges: kills give 1 / 3.5 / 6 / 11 charges by rarity (Mods.dat monster rarity mods, poewiki), increased
  flask charges gained and charge recovery multiply, partial charges are kept.
  Charges on dealing a critical strike (one 0.132 s cooldown), when hit and every 3 seconds; charges used and maximum
  charges with mods.
  Ordinary-mode towns, hideouts and resurrection fill every flask; a flask put on the bar in a hostile area starts empty.
  `Game_FLASK_CHARGES=full` brings back "every kill fills every flask".
  Not yet: fewer charges in acts 6 to 10 and maps; charges on taking a critical strike (monsters deal none) and at
  maximum frenzy charges (none here).
  Checked live: kill and crit charges and the hostile-area rule; charges when hit and every 3 seconds by tests.
  Code: `FlaskCharges.cs`, Gateway (`Program.FlaskCharges.cs`).
  Docs: [13](../docs/protocol/13-items.md) §8b.
- Flask modifier composition (#75): the validation counts and Standard/Ruthless observations in this entry are
  historical LocalExile evidence. This branch ports ordinary-mode calculations; fresh automated results and
  outstanding native checks are in the [port record](../docs/dev/issue75-port.md).
  Raw character charge-gain sources resolve into the native virtual once,
  then multiply local flask charge recovery with fractional carry. Generic and resource-specific recovery-rate
  factors multiply; hybrid Life/Mana keep independent rates, native buff rows 5/9, queues and lifetimes.
  Global duration now resolves `flask_duration_+%` plus the actual-player-level source once for both panel and use.
  Each use snapshots level/stat inputs before its charge send; a virtual display replacement does not change the
  actual duration. Item-local and global utility-duration factors multiply, followed by quality and local more.
  Recovery periods use the same global factor independently for Life/Mana; only over-time amount scales with it.
  Runtime global effect also resolves raw flat plus actual-player-level inputs once, without replacing raw published
  keys 1784/20244. Local and applicable magic-utility increases add to it; duration, quality, recovery rates, instant/
  low-life stages, flags and independent Onslaught stay separate. Existing active effects keep captured values.
  All 39 new pure/real-handler cases pass within the combined 53/53 focused batch, followed by full Gateway
  3710/3710 with no skips. Prior unchanged Core 648/Data 37 remain applicable (4395 total, not all newly rerun).
  Five actual disposed-stream task faults remain in the full-run diagnostics, with no new-calculation reference.
  No new native proof is claimed. Wide effect arithmetic, individual buff integer saturation and signed strongest
  comparison do not harden every downstream aggregate.
  The preceding panel/duration batch passed 55 new plus 80 existing focused checks and Gateway 3657/3657;
  unchanged prior Core 648/Data 37 runs remain applicable (4342 total). Combined native results cover 40 completed
  controls: 22 complete PASS (12 positive source, six utility and four Life), with 18 complete zero controls UNPROVED;
  panel-field verdicts total 138 VERIFIED/42 UNPROVED. Standard utility durations match 13.8/3/3.9 seconds and
  Ruthless 4.6/2/2.6 for level-10 white, level-100 white and level-100 local-duration uses respectively.
  Life gains are Standard160/34 and Ruthless161/34 at levels10/100 against fixed161/35, within the original
  one-integer publication boundary; this does not prove official rounding. Final recovery3 cleanup is clean,
  all769 frozen inputs held, canonical inventories/original saves were preserved and normal storage was restored.
  Earlier failed observations and all UNPROVED fields remain recorded. They do not validate the new effect resolver.
  Whole-unique native acquisition/tooltip and conditional operators, nonpositive-duration policy and official
  fractional rounding remain open.
  Earlier twenty-eight automated controls cover real handlers and both modes. Twenty-four native controls verify
  installed buffs, separate hybrid expiry, full/end/queue/death, actual recovery, charge gains and overrides.
  Ruthless spent charges persist in town; ordinary Nessa interaction refills them before further controls.
  Original saves and inventories held, apart from flask charges and native move IDs. Conditional acquisition
  and queued native timer/predictor presentation remain separate work.
  Code: `FlaskCharges.cs`, `FlaskRecovery.cs`, `FlaskEffectScaling.cs`, `Flasks.cs`, `UtilityFlasks.cs`,
  Gateway (`UseFlask`, `UseUtilityFlask`, `CharacterPlayerStatsCore`).
  Docs: [13](../docs/protocol/13-items.md) §8.

- Utility flasks: every utility flask spends its charges, shows its buff and the timer bar under it, and gives its
  buff's stats for its duration, all from the tables (Flasks.dat, BuffDefinitions, ComponentCharges).
  The stats count in the pools (the walk estimate, `/defences`, `/resistances`, `/hit`) and go to the client.
  Two flasks of one kind keep their own timers and only the strongest applies (poewiki); different effects stack.
  They end with the duration, at death, with the area and when the flask leaves its cell; a pause holds them.
  Quality, the flask's duration and effect mods, its "during effect" suffixes and the character's flask effect and
  duration count.
  Stibnite blinds monsters in its smoke cloud, Sulphur's consecrated ground regenerates life, Basalt taunts, Quartz
  phases through monsters, Gold raises the rarity of drops, Silver gives Onslaught.
  Checked live: every flask's use, stats on the client, bars, pause, death, area change; not the client walking faster
  or phasing (the control bridge cannot make it walk).
  Iron Flask now contributes to the authoritative Ward pool and restores broken Ward only after paid use.
  A native Antiquarian check spent 40 charges, restored 502 Ward immediately and returned to 22 after expiry.
  See [Ward and native evidence](../docs/game/antiquarian.md).
  Not yet: ailments, stun and curses on the player (Aquamarine, Corundum and the ailment
  suffixes reach the panel only), Aquamarine's chilled ground, the smoke cloud's art, leech suffixes.
  Code: `UtilityFlasks.cs`, `FlaskGrounds.cs`, Gateway (`UseUtilityFlask`, `RunUtilityFlasks`, `RunFlaskGrounds`).
  Docs: [13](../docs/protocol/13-items.md) §8a.
- Items: the start kit (Characters.dat skill and support gems, two life flasks on keys 1–2 and a mana flask on key 5)
  in the inventories, and the class weapon with two linked sockets worn in the main hand (`Game_START_WEAPON=ground`:
  on the ground a few cells from the spawn until it is picked up);
  pick up (0x28), put down, swap and equip (0x2a, slot classes and attribute requirements checked by the client),
  one-hander off hand → main hand, socket and unsocket gems (0x30, 0x2f), drop to the ground outside town (0x29 →
  WorldItem) and pick up again (to the cursor with the inventory open, at the client's 22-cell range), Destroy in town
  (0x16 /destroy); stacks: merge a held stack onto one of its type up to the CurrencyItems StackSize (0x4a), split
  with Shift + click (0x7c), Shift + click while holding places one, full fragment / shard stacks become their item,
  pick-ups, `/item` and sale rewards fill existing stacks first; the equipped weapon on the model;
  active gems in equipment grant their skill; class attributes and the class on the character panel;
  `/item <row, Id or name> [count]`.
  Code: `InstanceItems.cs`, Gateway (`ItemCatalog.cs`, `PlayerInventories.cs`).
  Docs: [13](../docs/protocol/13-items.md) §4a.
- Item mods: rarity (normal, magic, rare, unique), identified, and the implicit, explicit, enchant, scourge and hidden
  mod lists in the Mods component; new items roll their BaseItemTypes implicits (Driftwood Wand 8-12% increased Spell
  Damage); magic names come from the affixes; rarity and mods are saved with the item (characters and stash);
  `/mod`, `/mods`, `/rarity`, `/sockets` (colours and links, e.g. `/sockets RRGBBB`) on the held item;
  `Game_SOCKET_CAPACITY=1..6` (dev) lets `/sockets` give any socketable base that many, e.g. a 6-link wand (its own
  capacity is 3).
  Magic and rare items: affixes rolled from Mods.dat by domain, item level, the item's tags and families, rare names
  from Words.dat; equipment drops magic / rare and unidentified; a Scroll of Wisdom identifies (C→S 0x71);
  `/item <type> magic|rare`, `/roll`.
  Equipped items' mods count on the character: global stats like a passive's (life, resistances, attributes, spell
  damage …), the main hand's local crit, attack speed and accuracy, its local physical damage and quality,
  and its local added elemental/chaos damage (`ItemWeapons`).
  Armour/evasion/ES local flat, increased and hybrid mods and multiplicative quality feed the shared pools and panel
  (`ItemDefences`, [13](../docs/protocol/13-items.md) §2c).
  Armour, evasion and ES share a saved variable base roll, sent in the native Armour component.
  Fresh armour gets an ordinary roll once; legacy items keep their minimum, and ordinary crafting preserves the roll.
  Native checks verified ordinary/special base-roll states, quality composition, equipment refresh and relog.
  Sacred Orb rerolls and supported special ES rules are implemented with automated coverage (see Partly working).
  Other uniques and special mods still depend on their individual mechanics (#60).
  Mageblood's supported subset is below.
  Equipment uses the shared `ItemDefences.Calculate`, including Tabula and explicit no-ES overrides.
  Code: `InstanceItems.cs`, Gateway (`ModCatalog.cs`, `ItemRolls.cs`, `ItemDrops.cs`, `ItemModStats.cs`,
  `ItemWeapons.cs`, `ItemDefences.cs`, `ItemCatalog.cs`, `CharacterSaves.cs`).
  Docs: [13](../docs/protocol/13-items.md) §2a-§2c.
- Sacred Orb: ordinary and Shift currency use reroll the saved shared defence base on eligible armour.
  Only the base roll changes; mods, quality, sockets, gems, links, rarity and identification remain.
  Inventory and stash pairings save the target and one-unit payment atomically, including unchanged roll outcomes.
  Worn-item rerolls refresh the character pools and panel.
  Native use verified raw 32 becoming ordinary, armour/evasion/ES updates, Shift repeat and same-value consumption.
  Corrupted, mirrored, non-armour and fixed-range targets refuse use without payment.
  Ward and the fifth native range count for eligibility; their character-pool mechanics remain separate work.
  Uniform ordinary roll selection is local policy; the official distribution is unmeasured.
  Native fixed/zero-base refusal and Ward-only rerolling matched; inventories and stash survived a Gateway restart.
  See [13](../docs/protocol/13-items.md), "Sacred Orb" for the verified values.
  Code: `CurrencyEffects.cs`, `ItemCatalog.cs`, Gateway (`UseItemOn`, `ItemMoves.UseAcross`).
- Special defence overrides and ES-to-Life conversion are implemented.
  Native full/50% conversion, independent ES, Life scaling, zero-ES controls and shield swaps matched.
  Both test inventories and their final pools survived a Gateway restart.
  Tabula's hidden override and explicit no-ES suppress only their item's ES before local scaling.
  Full and percentage conversion use final local ES per active armour slot, including the eligible active shield.
  Converted Life receives global Life increases; independent global ES stays ES.
  PoB imports restore the hidden override only for four recognised unique names with matching native bases.
  The importer recognises explicit no-ES and full/percentage conversion text and uses the shared local calculator.
  Conditional corrupted-item ES maps to a global ES increase for that corrupted source, preserving local tooltip ES.
  Shield-only increases now affect character defences without changing local item values or converted Life.
  Native application, clearing, weapon swaps and Gateway-restart checks passed with final half-up pool rounding.
  Ward conversions, ring-slot and jewel-radius mechanics remain separate work.
  Code: `ItemDefences.cs`, `Program.Equipment.cs`, `Program.Progression.cs`, `PathOfBuildingItems.cs`.
  Docs: [13](../docs/protocol/13-items.md) §2, [20](../docs/protocol/20-damage.md), "Player energy shield".
- Gold: shared by the account's characters of one league (S→C 0x2a9), saved with that league's stash, sent on area
  entry; every league has its own stash and gold, Standard keeps the account's stash from before (`AccountStash.Key`);
  monsters drop piles of gold (normal 40%, magic 75%, rare and unique always;
  (3 + 1.2 × level) × 1/2/5/15, our own numbers, `ItemDrops.Gold`), and picking one up adds it to the gold;
  gold within 15 cells of the player is picked up by itself (player tick, `GoldAutoLootRange`);
  older per-character gold moves into the account at login; `/gold <±amount>`.
  Code: `InstanceEntities.Gold`, `AccountStash`, Gateway.
  Docs: [04](../docs/protocol/04-player-entity.md) §6.
- Loot: dying monsters drop currency, gems, flasks and equipment (our own table) around the corpse.
  Code: `ItemDrops.cs`, Gateway (`DropLoot`).
  Docs: [15](../docs/protocol/15-progression.md) §3.

## Partly working

- Sacred Orb rerolls: ordinary and Shift-repeat currency routes replace only the shared defence base roll (0..31).
  Requires Armour/Mods components and a known variable native range.
  Corrupted, mirrored and fixed-range targets are refused.
  The same roll can recur and still costs one orb; affixes, quality, sockets, gems and other properties stay intact.
  Automated coverage checks eligibility, persistence, save rollback and worn-item pool refresh.
  Fourteen native captures recorded at `63c1039` verify ordinary/repeat use, refusals, inventory/stash combinations,
  unchanged outcomes and restart persistence; fresh checks of the merged branch remain open.
  Ward ranges count for eligibility, but Ward character calculations and recovery remain unimplemented.
  Code: `CurrencyEffects.Sacred`, Gateway (`Program.Items.cs`).
  Docs: [13](../docs/protocol/13-items.md), "Sacred Orb".
- Special defence rules: local zero-ES modifiers override this item's base, mods and quality only for ES.
  Supported equipment ES-to-Life conversion truncates each item's contribution, removes all equipment ES and adds
  base Life before Life increases; independent global/aura ES remains ES and excluded shields grant no converted Life.
  The corrupted-item conditional ES increase enters the global ES bucket, leaving tooltips and converted Life alone.
  Hidden no-attribute-requirement modifiers retain the level gate.
  Automated coverage passes; historical native conversion and corrupted-item ES controls are recorded at `0bae37a`
  and `c9060e4` (see the protocol reference for values).
  Fresh real-client checks of the merged branch and other unique/conditional mechanics remain open.
  Code: `ItemDefences.cs`, `ItemModStats.cs`, Gateway (`Program.Equipment.cs`).
  Docs: [13](../docs/protocol/13-items.md) §2-§2d.
- **Unique items and Mageblood (verified live 2026-10-26).** The client data links a unique's name (`Words` wordlist 6)
  and artwork (`UniqueStashLayout` → `ItemVisualIdentity`), but not its mods, so `UniqueCatalog` curates each definition.
  `/item Mageblood` creates the Heavy Belt unique with its name, artwork and fixed mods (the Strength is the base
  implicit); the client shows the unique tooltip.
  While Mageblood is worn, the leftmost 2–4 magic utility flasks in the flask bar apply their effects permanently without
  charges or a timer, and using them is refused (client mod, server backstop); the stats refresh when the belt or the
  flask bar changes.
  Not yet: other uniques, unique drops/chancing, the permanent flask buff icons, the "effects cannot be removed"
  backstop, the Foulborn rightmost variant and Enkindling (`UniqueCatalog`, `Program.Mageblood`;
  [13](../docs/protocol/13-items.md) §2e, §8a).

- Ruthless uses native mod/flask alternatives, reduced tunable drops and vendor returns, and refuses bench crafting.
  Entering town or a hideout does not refill its pools/flasks; supported vendors refill them instead.
  Active utility flasks cannot gain charges, and native Ruthless durations/Onslaught values apply.
  Automated pool-restoration and session-ownership checks cover area changes, reconnects and vendor refill persistence.
  Drop, recipe, bench and flask-rule calculations have automated coverage; full retail balance remains partial.
  See [league modes](../docs/game/league-modes.md).
- Essence crafting (2026-10-25, automated verification; real-client use pending): all 105 craftable essences resolve
  their exact class-specific guaranteed mod from Essences.dat, including tier-8 and Desolation.
  Normal/rare eligibility, random-mod level caps, fractures, metamod refusal and repeated consumption from the Essence
  tab are covered.
  Uses the existing currency path for ordinary and Shift application.
  See [13](../docs/protocol/13-items.md).
- Quiver eligibility fix (2026-10-25, awaiting client confirmation): active bows now publish `main_hand_weapon_type =
  9`, required by the client's inventory gate; see [13](../docs/protocol/13-items.md).
- **Jeweller's Orb: verified live (2026-10-21).** Normal use and Shift share the currency handler; rerolls counts
  1/2/3/4/6 and link groups, limited by class, dimensions and item level, and consumes one orb.
  3.29: sockets the reroll keeps retain their colours and only the added sockets are white.
  Socketed gems, unsupported targets and maximum base capacity are refused without spending an orb.
  The live-found refusal of level-1 boots/gloves with two sockets is fixed and verified live.
  Count and link probabilities are approximate; special socket overrides are not modelled.
  See [13](../docs/protocol/13-items.md), "Jeweller's Orb".
- **Socket colours (3.29; verified live 2026-10-26).** Item sockets generate white and may roll a
  colour in proportion to the base's attribute requirements, with the chance rising with item level; a gem in a
  matching red/green/blue socket gets +10% quality (the stat path is in [25](../docs/protocol/25-gem-supports.md)).
  Chromatic Orbs force one socket non-white and roll the rest, with item quality raising their chance; the bench's
  "At Least Two/Three/Four Non-White Sockets" crafts (`NN`/`NNN`/`NNNN`) reroll until they guarantee that many; Vaal
  corruption colours one white socket. `/item`, drops and shops use the same generator, and the start weapon's two
  linked sockets are white.
  The probabilities are an approximation (5% non-white at item level 1, +0.25 points per level up to 30% at 100, and
  +0.25 points per 1% quality for Chromatic/bench); `SocketColours`, [13](../docs/protocol/13-items.md),
  "Socket colours (3.29)".
  Not modelled: Tainted Chromatic/Jeweller's Orbs, Orb of Binding, the Locus of Corruption, the 25-roll bench reward,
  special item overrides.
- **Orb of Alchemy, Chaos Orb, Divine Orb (verified live, 2026-10-21):** Alchemy makes a normal item an identified
  rare with 4-6 random affixes and a rare name; Chaos rerolls all of a rare item's affixes (4-6, new tiers);
  Divine keeps the affixes, tiers and order and rerolls only values.
  Shift works for all three.
  See [13](../docs/protocol/13-items.md), "Orb of Alchemy", "Chaos Orb", "Divine Orb".
- **Orb of Scouring (verified live, 2026-10-21):** a magic / rare / unique item becomes normal, only base mods stay.
  See [13](../docs/protocol/13-items.md), "Orb of Scouring".
- **Orb of Transmutation (verified live, 2026-10-21):** a normal item becomes magic with 1-2 random affixes.
  See [13](../docs/protocol/13-items.md), "Orb of Transmutation".
- **Orb of Alteration (verified live, 2026-10-21):** a magic item gets 1-2 new affixes (one prefix, one suffix at
  most).
  See [13](../docs/protocol/13-items.md), "Orb of Alteration".
- **Orb of Augmentation (verified live, 2026-10-21):** +1 affix on a magic item, up to one prefix and one suffix.
  See [13](../docs/protocol/13-items.md), "Orb of Augmentation".
- **Regal Orb (verified live, 2026-10-21):** magic → rare, affixes kept, +1 affix, rare name.
  See [13](../docs/protocol/13-items.md), "Regal Orb".
- **Exalted Orb (verified live, 2026-10-21):** +1 affix on a rare item, up to 6 (three of each kind).
  See [13](../docs/protocol/13-items.md), "Exalted Orb".
- **Orb of Annulment (verified live, 2026-10-21):** removes one random affix from a magic or rare item.
  See [13](../docs/protocol/13-items.md), "Orb of Annulment".
- **Armourer's Scrap (verified live, 2026-10-21):** +1% quality on armour, up to 20%; Scouring resets it to 0.
  See [13](../docs/protocol/13-items.md), "Armourer's Scrap".
- **Blacksmith's Whetstone (verified live, 2026-10-21):** +1% quality on weapons, up to 20%; Scouring resets it.
  See [13](../docs/protocol/13-items.md), "Blacksmith's Whetstone".
- **Orb of Chance (verified live, 2026-10-21):** a normal weapon / armour piece → rare (30%, 4-6 affixes) or magic
  (2-4 affixes).
  See [13](../docs/protocol/13-items.md), "Orb of Chance".
- **Vaal Orb and corruption (verified live, 2026-10-21):** weapons, armour and jewellery are always corrupted (Base
  +0xc7 bit 0, saved); normal / magic → rare with 4-6 affixes, a rare item gets 1-5 or all affixes rerolled;
  a corrupted item takes no currency but Wisdom.
  See [13](../docs/protocol/13-items.md), "Vaal Orb and corruption".
- **Glassblower's Bauble (verified live, 2026-10-21):** +1% flask quality up to 20%; Scouring resets it.
  See [13](../docs/protocol/13-items.md), "Glassblower's Bauble".
- **Fracturing Orb (verified live, 2026-10-21):** one random mod of a rare item with 4+ mods is fractured and kept by
  every other currency, Scouring included; shown fractured (magic properties u8 +0x7a).
  See [13](../docs/protocol/13-items.md), "Fracturing Orb".
- **Mirror of Kalandra (verified live, 2026-10-21):** a mirrored copy (Mods flag bit 1) of a non-corrupted,
  non-mirrored item goes onto the cursor; mirrored items take no currency.
  See [13](../docs/protocol/13-items.md), "Mirror of Kalandra".
- **Orb of Fusing: verified live (2026-10-21).** Captured 0x71/0x72 packets feed the same handler.
  Only links change; sockets, colours, gems and item properties stay intact.
  Refuses fewer than two sockets and protects fully linked 5/6-socket items; 2-4 sockets remain rerollable.
  Quality favours longer groups without being consumed.
  Link/quality probabilities are approximate, and special overrides/corruption are not modelled.
  See [13](../docs/protocol/13-items.md), "Orb of Fusing".
- Currency metamods: Scouring/Chaos/Divine/Annul respect prefix/suffix locks;
  annul and affix additions respect attack/caster restrictions.
  Scouring retains quality and refuses uniques.
  Real-data regression tests cover both lock directions.

## Not working yet

- Most uniques and special mods (#60), and unique drops/chancing; only Mageblood has a definition so far;
  currency other than Scroll of Wisdom, Chromatic Orb, Jeweller's Orb, Orb of Fusing, Orb of Alchemy, Chaos Orb,
  Divine Orb, Orb of Scouring, Orb of Transmutation, Orb of Alteration, Orb of Augmentation, Regal Orb, Exalted Orb,
  Orb of Annulment, Armourer's Scrap, Blacksmith's Whetstone, Orb of Chance, Vaal Orb, Glassblower's Bauble,
  Fracturing Orb, Mirror of Kalandra, Gemcutter's Prism, Sacred Orb and Orb of Regret;
  support gems and links ([13](../docs/protocol/13-items.md) "Open").
