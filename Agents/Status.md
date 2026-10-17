# Status

Where the project stands. Last updated 2026-10-17, against client **3.29.3b**. Update this file in the same pull request
as any change that makes something work, or that finds something broken.

What to build next is in [Roadmap.md](Roadmap.md). How the protocol works is in [docs/README.md](docs/README.md).

## Works (verified with the real client)

| Feature | Where | Docs |
|---|---|---|
| Local patching server: the client never contacts the official patch servers | `GameServer.Patch` | [01](docs/protocol/01-login.md) §1, [GettingStarted](GettingStarted.md) step 6 |
| Login: custom TCP, Diffie-Hellman + DSA-signed server key + Salsa20, account login, character list, Play | `GameServer.Auth`, `GameServer.Core/Protocol/Login` | [01](docs/protocol/01-login.md), [re/008](docs/re/008-login-handshake.md), [re/009](docs/re/009-login-messages.md) |
| Instance connection: opening, encryption switch (0x11), framing, dispatcher | `GameServer.Gateway`, `GameServer.Core/Protocol/Instance` | [02](docs/protocol/02-instance-connection.md) |
| Entering Lioneye's Watch: area (0x1d), local player id, world activation, HUD | Gateway | [03](docs/protocol/03-area-and-world.md) |
| Player object (0x2b9) with its component blob; the character's name; Witch model | `InstanceEntities.cs` | [04](docs/protocol/04-player-entity.md) |
| Inventories and passive slots the UI needs (empty) | Gateway | [05](docs/protocol/05-inventories-and-ui.md) |
| Latency acks (0x1b/0x1c) and the HNC side channel, visibility (wake zone), click-to-move | Gateway, `Hnc.cs` | [07](docs/protocol/07-latency-visibility-movement.md) |
| The Coast exit in town; area change through it; Ctrl+click opens the Instance Manager (list, join, create) | Gateway, Auth's instance registry | [08](docs/protocol/08-area-transitions.md), [09](docs/protocol/09-area-change.md) |
| Instances: one per town; other areas get private instances that live 6 minutes once empty | `InstanceRegistry.cs` | [09](docs/protocol/09-area-change.md) |
| Walk maps captured from the client on first visit; the Gateway uses them for position estimates and spawns | `scripts/walkmaps`, `live.ps1 -Walkmaps` | [GettingStarted](GettingStarted.md) step 8 |
| Chat commands `/help`, `/raw <hex>`, `/set_stat`, `/set_speed` (replies in a message box) | `ChatCommands.cs`, Gateway | [10](docs/protocol/10-chat-and-commands.md) |
| Monsters: `/spawn_mob <row, Id or name>[@level] [rarity] [name]`, `/mobs`, `/despawn`, `/mob_walk`, `/ai` put a visible monster next to the player that chases the player when near (Move action 0x1ab, client pathing) (0x20 monster type at a level, 0x2b9, wake zone), for the 10 345 of 10 691 varieties whose components are known | `MonsterObject`, `MonsterCatalog.cs`, Gateway | [10](docs/protocol/10-chat-and-commands.md) §5 |
| Dev account service: register, log in, create characters (JSON-lines over TCP) | `GameServer.Host`, `GameServer.DevClient` | [dev-server.md](docs/dev-server.md) |

## Partly working

- **Positions are estimated, not simulated.** The Gateway estimates where the player is from clicks, walk speed and the
  walk map; there is no server-side pathing or collision.
- **Stats.** `/set_stat` and `/set_speed` work with the client's own stat keys (for example `0xf5` =
  `movement_velocity_+%`). The extracted `Stats.json` row order did not match those keys with the old schema; whether
  the vendored schema fixes this is not checked.
- **Spawn points.** The Coast uses the walkable cell farthest from any edge, not the arrival point next to the exit.
- **Area seeds.** Non-town areas use seed 1 unless `Game_RANDOM_SEEDS=1`; each new seed needs its own walk map.
- **Monsters.** Spawned monsters chase but do not attack, and cannot be hit, and no mods.

## Not working yet

- NPCs, and the 346 monster varieties with NPC, ControlZone, BlightTower … components. "Emerge" monsters (Gravel Eater …)
  are created but stay hidden until an Emerge action, which the server does not send.
- Chat lines: server text only appears as a message box. The chat-line message and the global channel join are unknown
  (the client logs "Timeout while joining global chat channel").
- Esc → "Exit to character selection": the client sends 0x153, gets no answer and hangs. Workaround:
  `.\scripts\dev\live.ps1 -NoClient`.
- Exits beyond town: The Coast's own exits, and every other area.
- Items, skills, combat, levelling, saving the character's position or progress.
- `GameServer.Sim` is an empty placeholder project.

## Tests

`dotnet test tests/GameServer.Core.Tests` (157 tests) and `dotnet test tests/GameServer.Data.Tests` (8 tests) pass.
Tests that need the real client pass quietly without `Client/`.

## Tooling

Everything used to work out the protocol is in the repository: the Ghidra, Frida and Python scripts in `scripts/re/`, our
own tools in `Tools/`, and how to install the third-party ones in [GettingStarted](GettingStarted.md) ("Third-party
tools"). The full reverse-engineering setup has not yet been rebuilt from a fresh clone; if something is missing, fix the
guide.

## Doc caveats

`docs/re/001`–`006` are early notes with known errors (for example "login is HTTPS"; it is custom TCP, see `re/007`).
`docs/superpowers/` and `docs/history/` are the original plans. Treat all of these as history, not reference.
