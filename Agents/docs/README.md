# Documentation index

Private server for the Path of Exile 3.29.3b client. Where things stand (2026-10-16): the patched client logs in to our
servers and reaches the in-game HUD in Lioneye's Watch. Start with **[protocol/00-overview.md](protocol/00-overview.md)**.

## Protocol (consolidated, current)
| Doc | Content |
|---|---|
| [protocol/00-overview.md](protocol/00-overview.md) | processes, ports, the full message sequence from launch to the HUD, conventions |
| [protocol/01-login.md](protocol/01-login.md) | login connection: key blobs, handshake, framing and encodings, messages, our login server |
| [protocol/02-instance-connection.md](protocol/02-instance-connection.md) | instance client, connect path, opening, 0x11 encryption, pump, handler groups, dispatcher, tick |
| [protocol/03-area-and-world.md](protocol/03-area-and-world.md) | 0x1d, 0x7d, 0x22, world queueing and activation, 0x21, spatial index |
| [protocol/04-player-entity.md](protocol/04-player-entity.md) | object type register (MurmurHash2), 0x2b9, the component blob and every component |
| [protocol/05-inventories-and-ui.md](protocol/05-inventories-and-ui.md) | inventory manager, 0xc0, Inventories.dat, 0x3d passive slots |
| [protocol/07-latency-visibility-movement.md](protocol/07-latency-visibility-movement.md) | tick/ack and HNC, wake zone (visibility), movement |
| [protocol/08-area-transitions.md](protocol/08-area-transitions.md) | area exits: AreaTransition object, blob, positions from DGR/.arm named points (The Coast in Lioneye's Watch) |
| [protocol/06-debugging-log.md](protocol/06-debugging-log.md) | every live run, crash and fix, and lessons |
| [protocol/address-index.md](protocol/address-index.md) | all client addresses by subsystem |

## How-to
| Doc | Content |
|---|---|
| [dev/live-testing.md](dev/live-testing.md) | running the real client safely, expected output, troubleshooting, env vars |
| [re/tooling.md](re/tooling.md) | Ghidra headless scripts, Python helpers, GGPK dump tool, Frida through REA, recipes |
| [dev-server.md](dev-server.md) | the JSON-lines dev server, `GameServer.Auth` and `GameServer.Patch` usage |

## Working notes (history)
- `re/007`–`010`: the notes these documents were consolidated from (login TCP, handshake, message tables, live instance
  notes). `re/009` still has the most complete login message tables.
- `re/001`–`006`: early notes with known errors (for example "login is HTTPS"). Verify before use.
- `superpowers/`: the original design spec and milestone plan.
