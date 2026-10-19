# Status

Where the project stands. Last updated 2026-10-19, against client **3.29.3b**. Update this file in the same pull request
as any change that makes something work, or that finds something broken.

What to build next is in [Roadmap.md](Roadmap.md). How the protocol works is in [docs/README.md](docs/README.md).

## Works (verified with the real client)

| Feature | Where | Docs |
|---|---|---|
| Local patching server: the client never contacts the official patch servers | `GameServer.Patch` | [01](docs/protocol/01-login.md) §1, [GettingStarted](GettingStarted.md) step 6 |
| Login: custom TCP, Diffie-Hellman + DSA-signed server key + Salsa20, account login, character list, Play | `GameServer.Auth`, `GameServer.Core/Protocol/Login` | [01](docs/protocol/01-login.md), [re/008](docs/re/008-login-handshake.md), [re/009](docs/re/009-login-messages.md) |
| Instance connection: opening, encryption switch (0x11), framing, dispatcher | `GameServer.Gateway`, `GameServer.Core/Protocol/Instance` | [02](docs/protocol/02-instance-connection.md) |
| Entering Lioneye's Watch: area (0x1d), local player id, world activation, HUD | Gateway | [03](docs/protocol/03-area-and-world.md) |
| Player object (0x2b9) with its component blob; the character's name and class model | `InstanceEntities.cs` | [04](docs/protocol/04-player-entity.md) |
| Inventories and passive slots the UI needs (empty) | Gateway | [05](docs/protocol/05-inventories-and-ui.md) |
| Latency acks (0x1b/0x1c) and the HNC side channel, answered from the read loop ahead of other handlers (no spikes behind slow handlers; ticks or handlers over 50 ms are logged), visibility (wake zone), click-to-move | Gateway, `Hnc.cs`, `ClientInbox.cs` | [07](docs/protocol/07-latency-visibility-movement.md) |
| Area transitions in every area, with their destinations, models (the arch highlighted on hover), rotation and height: fixed layouts from the atlas, generated layouts from the walk-map recorder's capture; verified town ↔ The Coast ↔ The Tidal Island and The Coast ↔ The Mud Flats; area change through them; Ctrl+click opens the Instance Manager (list, join, create); `/goto <area>` | Gateway (`AreaTransitionCatalog.cs`), `scripts/walkmaps`, `scripts/re/atlas`, Auth's instance registry | [08](docs/protocol/08-area-transitions.md), [09](docs/protocol/09-area-change.md) |
| Arrival points: entering through an exit (or `/goto`) puts the player next to the exit back; The Twilight Strand → Lioneye's Watch arrives at the town's `entrance1`; Resurrect at Checkpoint revives there | `AreaLayout.Arrival`, Gateway | [09](docs/protocol/09-area-change.md) §4 |
| Monsters belong to the area instance: re-entering it finds the living ones where they were, with their damage, and the dead stay dead; a new instance gets fresh packs | `InstanceStore.cs`, `PopulateArea`, Gateway | [14](docs/protocol/14-monster-packs.md) §2 |
| Ground items belong to the area instance: dropped items and loot stay where they lay across logouts, disconnects and client crashes until the instance expires; ids from 0x100000; one pickup per item | `InstanceStore.cs`, Gateway | [13](docs/protocol/13-items.md) §5 |
| Quests: flags per character in the save, one script per quest (`Quests.cs`), `/quest_flag`. a1q1: new characters start on The Twilight Strand's beach and log in there until they reach a town; the gate into Lioneye's Watch can be clicked once Hillock is dead (the flag reaches the client's quest flags; the Gateway checks it too); no Resurrect in Town before a town | `Quests.cs`, Gateway | [12](docs/protocol/12-quests.md) §5–6 |
| Instances: one per town (all 12 act towns from WorldAreas `IsTown`); other areas get private instances that live 6 minutes once empty | `InstanceRegistry.cs`, `WorldAreaTowns.cs` | [09](docs/protocol/09-area-change.md) |
| Walk maps and area transitions captured from the client on first visit; the Gateway uses them for position estimates, spawns and area transitions | `scripts/walkmaps`, `live.ps1 -Walkmaps` | [GettingStarted](GettingStarted.md) step 8, [08](docs/protocol/08-area-transitions.md) §7 |
| Chat commands `/help`, `/raw <hex>`, `/set_stat`, `/set_speed`, `/set_level <1 to 100>` (saved to the account, sent in place), `/add_passive_points <n>`, `/add_passive <id>` (replies in a message box) | `Program.cs` (`Commands`), `ChatCommands.cs` | [10](docs/protocol/10-chat-and-commands.md) |
| Monsters: `/spawn_mob <row, Id or name>[@level] [friendly] [rarity] [name]`, `/mobs`, `/despawn`, `/mob_walk`, `/ai`: any of 10 345 of the 10 691 MonsterVarieties at a level and rarity, with full life for its level, hostile (red) or friendly, that chases the player when near (Move action 0x1ab, client pathing) | `MonsterObject`, `MonsterCatalog.cs`, Gateway | [10](docs/protocol/10-chat-and-commands.md) §5 |
| Monster packs in areas: on entry the area fills with its MonsterPacks at the area level (The Coast: crab and Drowned packs, some magic, at least 2 with a rare; cannibal packs at the bonfires) and the unique boss at the camp's `mapboss` point with its boss pack (Fire Fury beside the campfire, verified live 2026-10-18); room points are found by fitting rooms to the doodads `-Walkmaps` captures | `MonsterPacks.cs`, Gateway (`PopulateArea`), `scripts/walkmaps`, `scripts/re/atlas room-points` | [14](docs/protocol/14-monster-packs.md) |
| Skills and combat: the player has the default attack (PlayerMelee, Characters.dat StartingMeleeSkill) in the Actor skill list; it goes on the skill bar, walks into range, swings at the unarmed speed (757 ms with dual wielding's 10%) or faster with `/set_stat attack_speed_+%` (server-computed final stats, 0x2ba skill update, panel's Attacks per Second), and the server lowers the monster's life 2–5 at the contact point; moving cancels; the dead monster's life bar hides | `GrantedSkill`, `SkillCatalog.cs`, Gateway | [11](docs/protocol/11-skills.md) |
| Accuracy: the player's final rating includes flat/increased passives, weapon-specific stats and the unconditional Accuracy Mastery effects; attacks roll PoB's hit chance against the monster's `DefaultMonsterStats.Evasion`, misses send no life update, spells always hit; the Offence panel gets `main_hand_accuracy_rating` / `off_hand_accuracy_rating` and the Chance to Hit rows at the character's level; `/accuracy [level]` | `CharacterStats.cs`, `DamageModel.cs`, `MonsterCatalog.cs`, Gateway | [11](docs/protocol/11-skills.md) §4–5 |
| Monsters fight back: their first granted effect as a non-pathing skill with MonsterVarieties attack time, reach and level damage; they swing (animated) when in reach and the player's life goes down; walking away dodges; death plays and blocks moving; the death screen (S→C 0x55) shows "Resurrect in Town" / "Resurrect at Checkpoint" (hardcore: "Exit"), and both buttons work (C→S 0x54; in town: the last town); `/heal` | `MonsterAttack`, `Resurrect`, Gateway | [11](docs/protocol/11-skills.md) §6–7 |
| Items: the start kit (Characters.dat weapon with two linked sockets, its skill and support gems, life and mana flasks) in the inventories; pick up (0x28), put down, swap and equip (0x2a, slot classes and attribute requirements checked by the client), one-hander off hand → main hand, socket and unsocket gems (0x30, 0x2f), drop to the ground outside town (0x29 → WorldItem) and pick up again (to the cursor with the inventory open, at the client's 22-cell range), Destroy in town (0x16 /destroy); stacks: merge a held stack onto one of its type up to the CurrencyItems StackSize (0x4a), split with Shift + click (0x7c), Shift + click while holding places one, full fragment / shard stacks become their item, pick-ups, `/item` and sale rewards fill existing stacks first; the equipped weapon on the model; active gems in equipment grant their skill; class attributes and the class on the character panel; `/item <row, Id or name> [count]` | `InstanceItems.cs`, Gateway (`ItemCatalog.cs`, `PlayerInventories.cs`) | [13](docs/protocol/13-items.md) §4a |
| Gem skills: Fireball from the socketed gem with its level-1 stats (9–14 fire, 750 ms cast via `virtual_base_spell_cast_time_ms`, 5 mana), cast at the ground or at a monster; the server creates the `Metadata/Projectiles/Fireball` object, flies it to the target at terrain height + `Game_PROJECTILE_HEIGHT` where the area has a height grid (Lioneye's Watch, The Coast seed 1; live 2026-10-18: above the ground at every level, at belly height), else on the terrain and hits the monster on arrival | `SkillCatalog.cs`, Gateway (`CastOn`, `LaunchProjectile`), `ProjectileObject` | [11](docs/protocol/11-skills.md) §9–10 |
| Saving characters: items, equipment, sockets and the skill bar per character in `data/saves/`; the bar is restored with S→C 0x53 after login and area changes (binds are C→S 0x4e) | `CharacterSaves.cs`, `PlayerInventories.cs`, Gateway | [11](docs/protocol/11-skills.md) §11, [13](docs/protocol/13-items.md) §7 |
| Logins start at the character's last town's spawn point, as in the real game (after logging out, Exit to Character Selection, a crash, a restart or a disconnect while dead); Auth records the town when a town ticket resolves (`Character.LastTown` in `data/accounts.json`) | Auth (`InstanceDirectory`), `WorldAreaTowns.cs` | [09](docs/protocol/09-area-change.md) §4 |
| Waypoints: the Waypoint object where the area's layout is captured (Lioneye's Watch built in), activated on entry (towns), within 25 cells or on click (the client plays the activation), saved per character; clicking opens the World panel in travel mode (S→C 0x7b), its pins and connection lines show from the character's visited areas and activated waypoints (S→C 0x4c, quest flags in the Player component via 0x2ba), and picking a pin (C→S 0x4d, Ctrl → instance chooser) takes the player next to the destination's waypoint; `/waypoints [all \| reset]` | `InstanceWaypoints.cs`, `WaypointCatalog.cs`, `AreaVisitFlags.cs`, Gateway | [16](docs/protocol/16-waypoints.md) |
| NPCs in Lioneye's Watch: Tarkleigh, Nessa and Bestel at their real-game cells, with name, minimap icon and idle model; clicking walks to them and opens the dialogue window (S→C 0x84); talk options play (0x82 → 0x84 + index); Continue and Goodbye (C→S 0x85) are answered; Purchase Items opens the shop (S→C 0xc0 manager 1 + 0x8c) and buying (C→S 0x88) costs 1 Scroll of Wisdom; Sell Items: the sell window (S→C 0xb8) takes drops, Ctrl + click (first free cell) and Shift + click, Accept gives 1 Scroll Fragment per item and Cancel returns them, and the player can walk afterwards; Respecialisation opens the character's passive tree (0x7b kind 0x5f), where Apply refunds (C→S 0x36) and Refund All (0x37) refund passives for the client's gold price; the normal tree's Apply refunds (0x36 with flag 0) spend saved refund points; `/npc`, `/npcs`, `/pos` | `InstanceNpcs.cs`, `NpcCatalog.cs`, Gateway (`SpawnTownNpcs`, `TalkToNpc`) | [17](docs/protocol/17-npcs.md) |
| Stash: the Stash in Lioneye's Watch at (319, 272) opens the stash panel (S→C 0x7b kind 1) with the account's 11 tabs (normal, premium, quad, currency, essence, fragment, map, flask, gem) from the tab list in S→C 0x21; each tab's contents come on the client's request (C→S 0xc7 → 0xc0 row 26 + 0xc8); items go into and out of tabs by click (0x28 / 0x2a) and Ctrl + click (C→S 0xe0 in, 0xe3 out); the stash reopens on the account's last tab after a relog (0x7b affinity on the first open per connection) and keeps the panel's own tab while the connection lasts; tabs and items are account-wide (`data/stashes/<account>.json`). Verified live with normal and premium tabs; the special tabs load but their slots are not checked | `AccountStash.cs`, `StashCatalog.cs`, `InstanceStash.cs`, Gateway (`OpenStash`, `SendStashTab`) | [18](docs/protocol/18-stash.md) |
| Gold: account-wide, shared by every character of the account (S→C 0x2a9), saved with the account's stash, sent on area entry; older per-character gold moves into the account at login; `/gold <±amount>` | `InstanceEntities.Gold`, `AccountStash`, Gateway | [04](docs/protocol/04-player-entity.md) §6 |
| Dev account service: register, log in, create characters (JSON-lines over TCP) | `GameServer.Host`, `GameServer.DevClient` | [dev-server.md](docs/dev-server.md) |
| Leagues and character creation: Standard and Allflame with their Hardcore / SSF / Ruthless variants on the create screen (S→C 0x1b), 24 character slots, any class in any league (C→S 0x12 → 0x15, 0x13), straight into the game; the league under the area name in game (0x1d); the hardcore death screen from the character's league; `Game_LEAGUES` | `LeagueCatalog.cs`, `LoginServerSession.cs`, Auth, Gateway | [01](docs/protocol/01-login.md) §7 |
| Character deletion on the character screen (C→S 0x0c → new list 0x15 + result 0x0d); the save is set aside in `data/saves/deleted/` | `LoginServerSession`, Auth | [01](docs/protocol/01-login.md) §5 |
| Every class in game: the Characters.dat row's object type and model, class byte, attributes, unarmed attack and start kit (weapon, gems, sockets in the gems' colours); verified with a Templar | Gateway (`SkillCatalog.cs`, `PlayerInventories.cs`) | [01](docs/protocol/01-login.md) §7, [04](docs/protocol/04-player-entity.md) |
| Exit to character selection: Esc → "Exit to Character Selection" and the hardcore death screen's Exit (C→S 0x153); the Gateway closes the instance connection and the client returns to the character list on the same login connection; Play enters again | Gateway | [09](docs/protocol/09-area-change.md) §6 |
| Experience and levels: kills within 150 cells give DefaultMonsterStats experience × the variety's multiplier × rarity, with the level penalty (S→C 0x2ba Player experience; the client derives the level); a new level sends its stats, full life and mana, the passive points and the account's level; experience is saved per character; `/add_xp` | `Progression.cs`, Gateway (`GainExperience`, `ApplyLevel`) | [15](docs/protocol/15-progression.md) §1 |
| Passive Skill Tree: S→C 0x33 (level, class, allocated nodes, passive and refund points; the client counts level − 1 points), C→S 0x34 allocates a node, checked, applied to the stats (attributes, life, mana, resistances and more) and saved; C→S 0x36 with flag 0 refunds a node for a saved refund point (checked, mastery effects removed); using an Orb of Regret (right-click, C→S 0x58) takes one item and adds one point; `/passives`, `/add_refund_points`, `/reset_passives` | `PassiveTree.cs`, `CharacterSaves.cs`, Gateway (`ChangePassives`, `UseItem`) | [15](docs/protocol/15-progression.md) §2 |
| Resistances: the passive `base_resist_all_elements_%` / per-element `base_*_damage_resistance_%` are summed from the 0% base, the two Kitava quest penalties (-30% each) are mirrored from quest flags, and the final value uses Path of Building's floor -200 / default max 75 / absolute cap 90 (`DamageModel`); used by Righteous Fire's self-burn and `/hit`; `/resistances` shows modifier, penalty and final | `CharacterStats.cs`, `DamageModel.cs`, Gateway | [11](docs/protocol/11-skills.md) §12 |
| Spell Suppression: the passive `base_spell_suppression_chance_%` is capped at 100% and a successful suppress takes 40% less spell damage plus `base_spell_damage_%_suppressed` (PoB `SuppressionChanceCap`/`SuppressionEffect`); `spell_suppression_chance_is_lucky` rolls twice and keeps the better roll (effective `1-(1-p)^2`), with an unlucky `p^2` dev mode; `/suppression`, `/suppression_luck`, `/spellhit`; conditional effect stats and monster spell hits are not implemented | `CharacterStats.cs`, `DamageModel.cs`, Gateway | [11](docs/protocol/11-skills.md) §12 |
| Movement speed: the passive `base_movement_velocity_+%` is summed into `Pools`, sent as the client's `movement_velocity_+%` and used by the server's `WalkEstimator` (37 × (1 + pct / 100) cells/s); applied on login, level, passive and area changes; `/set_speed` adds its percentage on top; only unconditional passive increases are modelled (no action speed, more multipliers, gear/buff movement or the 128 cap yet) | `CharacterStats.cs`, Gateway (`ApplyMovementSpeed`) | [07](docs/protocol/07-latency-visibility-movement.md) §3 |
| Block / Spell Block: passives' `additional_block_%` and `base_spell_block_%` are summed and capped at 75% / 90% (PoB `BlockChanceCap`), max-res passives `maximum_block_%` / `base_maximum_spell_block_%` raise the caps; a successful block prevents the hit, monster melee and `/blockhit` use it; `/block` shows both chances; an equipped shield adds its `ShieldTypes.Block` base and enables the shield-specific passives, staff passives apply with a staff, and dual wielding adds its inherent 20% attack block and dual-wield block/spell-block passives; Versatile Combatant reduces both maxima by 10% and converts overcapped attack block to spell block at 2% per 1%; lucky block uses 1-(1-p)^2 for attack/spell (dev `/block_luck`); the character panel gets `uncapped_attack_block_%` and `display_spell_block_chance` so it shows the uncapped value in parentheses and the spell block row | `CharacterStats.cs`, `DamageModel.cs`, Gateway | [11](docs/protocol/11-skills.md) §12 |
| Passive masteries: S→C 0x33 carries selected effects (`node \| effect << 16`) and C→S 0x35 selects the effect HASH16; a mastery node can be allocated normally and its effect selected/changed without spending another point. Block Mastery's four effects are implemented: max attack block +2, max spell block +2, 20 life + 20 mana on block, and +1% spell block per 5% attack block; the effects are saved with the character | `PassiveTree.cs`, `CharacterSaves.cs`, Gateway (`ChangePassives`, `PassivesMessage`) | [15](docs/protocol/15-progression.md) §2 |
| Loot: dying monsters drop currency, flasks and equipment (our own table) around the corpse | `ItemDrops.cs`, Gateway (`DropLoot`) | [15](docs/protocol/15-progression.md) §3 |
| Buffs (S→C 0x1c1 add, 0x1c2 remove) and Righteous Fire: cast on oneself, its buffs and the big burning circle (an effect object, `Metadata/Effects/PermanentServerEffect` with `rifire.ao`, following the player), 90% of life per second nonlethal burn on the player, 70% of life per second to monsters within 18 cells (they burn to death); server-side mana (cost, regeneration); `/god` | Gateway (`ActivateRighteousFire`, `RunPlayerTick`) | [11](docs/protocol/11-skills.md) §12 |
| Spells are released at the cast animation's contact keyframe (Witch 599 of 1066 ms; every class's from its rig .amd), sped up by cast speed | `AnimationContacts.cs`, `scripts/re/anim_contacts.py` | [11](docs/protocol/11-skills.md) §9 |

## Partly working

- **Positions are estimated, not simulated.** The Gateway estimates where the player is from clicks, walk speed and the
  walk map; there is no server-side pathing or collision.
- **Stats.** The client's stat key is the Stats.dat row + 1 with the vendored schema ([11](docs/protocol/11-skills.md)
  §8); `/set_stat` takes stat names or keys. The player's life, mana, accuracy and evasion come from its class and level,
  with the attributes' bonuses (energy shield, evasion and melee physical damage increases); accuracy also takes its
  flat/increased passives, weapon-specific stats and Accuracy Mastery, and the final value is sent as
  `main_hand_accuracy_rating` / `off_hand_accuracy_rating` for the Offence panel; life and mana still ignore passives
  and items, and evasion takes its passive increases but not items (attack speed is handled below)
  ([11](docs/protocol/11-skills.md) §4).
- **Attack speed passives.** Generic, melee, one-/two-handed, weapon-family, shield and dual-wield passive attack speed
  is summed into the client's `main_hand_attack_speed_+%`; dual wielding adds PoB's 10% more and the server's unarmed
  attack uses the same final value. The default attack also sends the panel's `hundred_times_damage_per_second` and
  `display_hundred_times_damage_per_skill_use`. Live verified: the dual-wield character's APS follows the passives
  (2.93 → 2.98 after a node). Minion, totem and timed/conditional stats are not modelled; the character panel's APS
  stays cached after a passive allocation (reopening the panel or switching its tab does not refresh it; only a relog
  does). ([11](docs/protocol/11-skills.md) §4)
- **Critical strikes.** The server rolls unconditional crit passives: the equipped main hand's `WeaponTypes.dat`
  critical chance (permyriad) raised by generic/attack/melee/weapon-shape increased crit chance for attacks, Fireball's
  5% base for spells, and 150% plus `base_critical_strike_multiplier_+`; critical damage is multiplied and logged.
  Unarmed has 0% base crit. The final main-hand and spell crit chance/multiplier stats go to the client for the
  Offence panel; timed, charge and enemy-state crit stats are not modelled; `/crit` shows both final values
  ([11](docs/protocol/11-skills.md) §5).
- **Spawn points.** Logins, Resurrect in Town and one-way entries other than The Twilight Strand → Lioneye's Watch
  use the area's spawn: the town's fixed point, or the walkable cell farthest from any edge in a generated area.
- **Area seeds.** Non-town areas use seed 1 unless `Game_RANDOM_SEEDS=1`; each new seed needs its own walk map.
- **Projectiles.** Areas without a height grid (`scripts/re/frida/capture-terrain-tiles.js` + `scripts/walkmaps/heights.py`) fly the fireball on the terrain; it hits the first monster or wall on its path (checked at launch; walls from the
  projectile grid, Lioneye's Watch and The Coast seed 1) and explodes there, but doodads with their own colliders do not
  stop it ([11](docs/protocol/11-skills.md) §10).
- **Combat.** The player's default attack kills monsters (server-side damage: the unarmed range raised by strength's melee physical damage,
  no armour or resistances; the contact point is half the attack time, not the animation's). Monsters use only their
  first granted effect; the player's life comes from class, level and strength only. The server has no
  simulation loop: hits use timers, attacks are bounded by a 30/s tick (`TickRate`), the AI runs every 500 ms. Monsters chase but do not attack: they have no granted skills yet. No mods; "emerge"
  monsters stay hidden; 346 varieties with NPC / ControlZone / … components are refused.

## Not working yet

- Area monsters: pack counts and magic/rare chances are our own numbers; rarity adds no mods or life; Drowned ignore their ground types; emerge packs (Gravel Eaters) are skipped; no boss life bar; monsters respawn on every entry; a new (area, seed) needs a `-Walkmaps` visit for its unique boss ([14](docs/protocol/14-monster-packs.md) §4).
- NPC dialogue options, shops, NPCs in other towns and outside towns ([17](docs/protocol/17-npcs.md) "Open"); the 346
  monster varieties with NPC, ControlZone, BlightTower … components. "Emerge" monsters (Gravel Eater …)
  are created but stay hidden until an Emerge action, which the server does not send.
- Chat lines: server text only appears as a message box. The chat-line message and the global channel join are unknown
  (the client logs "Timeout while joining global chat channel").
- Area transitions of a generated area need a `-Walkmaps` capture for its seed; quest-gated ones are created but the
  way stays blocked: The Mud Flats' Submerged Passage has a client-side wall until quest flag `a1q4-PlacedGlyphs`.
  Quest objects with extra components are sent as plain AreaTransition, so The Twilight Strand's gate opens without its
  animation; its force wall and Doorman are not created ([12](docs/protocol/12-quests.md) §6). Only quest a1q1 is
  scripted; no quest log.
- Item mods, rarity, flask and currency use; support gems and links; gem levels
  ([13](docs/protocol/13-items.md) "Open").
- The client's lockstep network mode (also what "auto" picks on localhost): the player is not drawn and cannot move.
  Only Predictive is supported; the Gateway asks for it once per connection ([07](docs/protocol/07-latency-visibility-movement.md) §5).
- Waypoint travel to a generated area without a `-Walkmaps` capture for its seed puts the player at an arbitrary cell
  (The Submerged Passage, seed 1); quest flags are derived from visited areas and waypoints, not from quests
  ([16](docs/protocol/16-waypoints.md) "Open").
- Stash: a switch to an already loaded tab sends nothing, so it is missed if the player logs out without reopening the stash or moving an item; the special tabs' slot rules, tab rename / order / folders, the guild stash, per-league stashes and stashes in other towns ([18](docs/protocol/18-stash.md) "Open").
- Saving quest progress. Quest passive points, quest refund points and ascendancy ([15](docs/protocol/15-progression.md) "Open").
- Effects that should be attached to bones (Righteous Fire's orbs, MiscAnimated RighteousFireOrb) are not sent; an
  effect object follows the player only by 0x1ba glides. Buffs other than Righteous Fire's are not used. Ruthless and SSF change nothing in game; towns are shared across leagues.
- `GameServer.Sim` is an empty placeholder project.

## Tests

`dotnet test tests/GameServer.Core.Tests` (269 tests), `tests/GameServer.Data.Tests` (15) and
`tests/GameServer.Gateway.Tests` (69: inventory moves, stacks, arrival points, instance ground items and monsters, quests,
passives, spell suppression, attack speed, critical strikes, accuracy and the account stash) pass.
Tests that need the real client pass quietly without `Client/`.

## Tooling

Everything used to work out the protocol is in the repository: the Ghidra, Frida and Python scripts in `scripts/re/`, our
own tools in `Tools/`, and how to install the third-party ones in [GettingStarted](GettingStarted.md) ("Third-party
tools"). The full reverse-engineering setup has not yet been rebuilt from a fresh clone; if something is missing, fix the
guide.

## Doc caveats

`docs/re/001`–`006` are early notes with known errors (for example "login is HTTPS"; it is custom TCP, see `re/007`).
`docs/superpowers/` and `docs/history/` are the original plans. Treat all of these as history, not reference.
