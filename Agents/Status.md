# Status

Where the project stands. Last updated 2026-10-18, against client **3.29.3b**. Update this file in the same pull request
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
| Latency acks (0x1b/0x1c) and the HNC side channel, visibility (wake zone), click-to-move | Gateway, `Hnc.cs` | [07](docs/protocol/07-latency-visibility-movement.md) |
| Area transitions in every area, with their destinations, models (the arch highlighted on hover), rotation and height: fixed layouts from the atlas, generated layouts from the walk-map recorder's capture; verified town ↔ The Coast ↔ The Tidal Island and The Coast ↔ The Mud Flats; area change through them; Ctrl+click opens the Instance Manager (list, join, create); `/goto <area>` | Gateway (`AreaTransitionCatalog.cs`), `scripts/walkmaps`, `scripts/re/atlas`, Auth's instance registry | [08](docs/protocol/08-area-transitions.md), [09](docs/protocol/09-area-change.md) |
| Instances: one per town; other areas get private instances that live 6 minutes once empty | `InstanceRegistry.cs` | [09](docs/protocol/09-area-change.md) |
| Walk maps and area transitions captured from the client on first visit; the Gateway uses them for position estimates, spawns and area transitions | `scripts/walkmaps`, `live.ps1 -Walkmaps` | [GettingStarted](GettingStarted.md) step 8, [08](docs/protocol/08-area-transitions.md) §7 |
| Chat commands `/help`, `/raw <hex>`, `/set_stat`, `/set_speed` (replies in a message box) | `ChatCommands.cs`, Gateway | [10](docs/protocol/10-chat-and-commands.md) |
| Monsters: `/spawn_mob <row, Id or name>[@level] [friendly] [rarity] [name]`, `/mobs`, `/despawn`, `/mob_walk`, `/ai`: any of 10 345 of the 10 691 MonsterVarieties at a level and rarity, with full life for its level, hostile (red) or friendly, that chases the player when near (Move action 0x1ab, client pathing) | `MonsterObject`, `MonsterCatalog.cs`, Gateway | [10](docs/protocol/10-chat-and-commands.md) §5 |
| Skills and combat: the player has the default attack (PlayerMelee, Characters.dat StartingMeleeSkill) in the Actor skill list; it goes on the skill bar, walks into range, swings at the unarmed speed (757 ms with dual wielding's 10%) or faster with `/set_stat attack_speed_+%` (server-computed final stats, 0x2ba skill update, panel's Attacks per Second), and the server lowers the monster's life 2–5 at the contact point; moving cancels; the dead monster's life bar hides | `GrantedSkill`, `SkillCatalog.cs`, Gateway | [11](docs/protocol/11-skills.md) |
| Monsters fight back: their first granted effect as a non-pathing skill with MonsterVarieties attack time, reach and level damage; they swing (animated) when in reach and the player's life goes down; walking away dodges; death plays and blocks moving; the death screen (S→C 0x55) shows "Resurrect in Town" / "Resurrect at Checkpoint" (hardcore: "Exit"), and both buttons work (C→S 0x54); `/heal` | `MonsterAttack`, `Resurrect`, Gateway | [11](docs/protocol/11-skills.md) §6–7 |
| Items: the start kit (Characters.dat weapon with two linked sockets, its skill and support gems, life and mana flasks) in the inventories; pick up (0x28), put down, swap and equip (0x2a, slot classes and attribute requirements checked by the client), one-hander off hand → main hand, socket and unsocket gems (0x30, 0x2f), drop to the ground outside town (0x29 → WorldItem) and pick up again (to the cursor with the inventory open, at the client's 22-cell range), Destroy in town (0x16 /destroy); the equipped weapon on the model; active gems in equipment grant their skill; class attributes and the class on the character panel; `/item <row, Id or name> [count]` | `InstanceItems.cs`, Gateway (`ItemCatalog.cs`, `PlayerInventories.cs`) | [13](docs/protocol/13-items.md) |
| Gem skills: Fireball from the socketed gem with its level-1 stats (9–14 fire, 750 ms cast via `virtual_base_spell_cast_time_ms`, 5 mana), cast at the ground or at a monster; the server creates the `Metadata/Projectiles/Fireball` object, flies it to the target at terrain height + `Game_PROJECTILE_HEIGHT` where the area has a height grid (Lioneye's Watch, The Coast seed 1; live 2026-10-18: above the ground at every level, at belly height), else on the terrain and hits the monster on arrival | `SkillCatalog.cs`, Gateway (`CastOn`, `LaunchProjectile`), `ProjectileObject` | [11](docs/protocol/11-skills.md) §9–10 |
| Saving characters: items, equipment, sockets and the skill bar per character in `data/saves/`; the bar is restored with S→C 0x53 after login and area changes (binds are C→S 0x4e) | `CharacterSaves.cs`, `PlayerInventories.cs`, Gateway | [11](docs/protocol/11-skills.md) §11, [13](docs/protocol/13-items.md) §7 |
| Dev account service: register, log in, create characters (JSON-lines over TCP) | `GameServer.Host`, `GameServer.DevClient` | [dev-server.md](docs/dev-server.md) |
| Leagues and character creation: Standard and Allflame with their Hardcore / SSF / Ruthless variants on the create screen (S→C 0x1b), 24 character slots, any class in any league (C→S 0x12 → 0x15, 0x13), straight into the game; the league under the area name in game (0x1d); the hardcore death screen from the character's league; `Game_LEAGUES` | `LeagueCatalog.cs`, `LoginServerSession.cs`, Auth, Gateway | [01](docs/protocol/01-login.md) §7 |
| Character deletion on the character screen (C→S 0x0c → new list 0x15 + result 0x0d); the save is set aside in `data/saves/deleted/` | `LoginServerSession`, Auth | [01](docs/protocol/01-login.md) §5 |
| Every class in game: the Characters.dat row's object type and model, class byte, attributes, unarmed attack and start kit (weapon, gems, sockets in the gems' colours); verified with a Templar | Gateway (`SkillCatalog.cs`, `PlayerInventories.cs`) | [01](docs/protocol/01-login.md) §7, [04](docs/protocol/04-player-entity.md) |
| Exit to character selection: Esc → "Exit to Character Selection" and the hardcore death screen's Exit (C→S 0x153); the Gateway closes the instance connection and the client returns to the character list on the same login connection; Play enters again | Gateway | [09](docs/protocol/09-area-change.md) §6 |

## Partly working

- **Positions are estimated, not simulated.** The Gateway estimates where the player is from clicks, walk speed and the
  walk map; there is no server-side pathing or collision.
- **Stats.** The client's stat key is the Stats.dat row + 1 with the vendored schema ([11](docs/protocol/11-skills.md)
  §8); `/set_stat` takes stat names or keys. The player sends only its unarmed attack stats; life, mana and
  attributes come from the client's defaults.
- **Spawn points.** The Coast uses the walkable cell farthest from any edge, not the arrival point next to the exit.
- **Area seeds.** Non-town areas use seed 1 unless `Game_RANDOM_SEEDS=1`; each new seed needs its own walk map.
- **Projectiles.** Areas without a height grid (`scripts/re/frida/capture-terrain-tiles.js` + `scripts/walkmaps/heights.py`) fly the fireball on the terrain; it hits the first monster or wall on its path (checked at launch; walls from the
  projectile grid, Lioneye's Watch and The Coast seed 1) and explodes there, but doodads with their own colliders do not
  stop it ([11](docs/protocol/11-skills.md) §10).
- **Combat.** The player's default attack kills monsters (server-side damage: the unarmed range, no damage model,
  no armour or resistances; the contact point is half the attack time, not the animation's). Monsters use only their
  first granted effect; the player's life is a fixed 50 for every class (no levels or stats). The server has no
  simulation loop: hits use timers, attacks are bounded by a 30/s tick (`TickRate`), the AI runs every 500 ms. Monsters chase but do not attack: they have no granted skills yet. No mods; "emerge"
  monsters stay hidden; 346 varieties with NPC / ControlZone / … components are refused.

## Not working yet

- NPCs, and the 346 monster varieties with NPC, ControlZone, BlightTower … components. "Emerge" monsters (Gravel Eater …)
  are created but stay hidden until an Emerge action, which the server does not send.
- Chat lines: server text only appears as a message box. The chat-line message and the global channel join are unknown
  (the client logs "Timeout while joining global chat channel").
- Area transitions of a generated area need a `-Walkmaps` capture for its seed; quest-gated ones are created but the
  way stays blocked: The Mud Flats' Submerged Passage has a client-side wall until quest flag `a1q4-PlacedGlyphs`
  ([12](docs/protocol/12-quests.md)). Quest objects with extra components (the cave entrance) are sent as plain
  AreaTransition. Quests are not implemented.
- Item mods, rarity, flask and currency use; support gems and links; gem levels; ground items are per connection
  ([13](docs/protocol/13-items.md) "Open").
- Levelling from experience (monsters give none; only `/set_level` changes the level), saving the character's area, position or progress. Ruthless and SSF change nothing in game; towns are shared across leagues.
- `GameServer.Sim` is an empty placeholder project.

## Tests

`dotnet test tests/GameServer.Core.Tests` (187 tests) and `dotnet test tests/GameServer.Data.Tests` (15 tests) pass.
Tests that need the real client pass quietly without `Client/`.

## Tooling

Everything used to work out the protocol is in the repository: the Ghidra, Frida and Python scripts in `scripts/re/`, our
own tools in `Tools/`, and how to install the third-party ones in [GettingStarted](GettingStarted.md) ("Third-party
tools"). The full reverse-engineering setup has not yet been rebuilt from a fresh clone; if something is missing, fix the
guide.

## Doc caveats

`docs/re/001`–`006` are early notes with known errors (for example "login is HTTPS"; it is custom TCP, see `re/007`).
`docs/superpowers/` and `docs/history/` are the original plans. Treat all of these as history, not reference.
