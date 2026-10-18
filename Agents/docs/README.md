# Documentation index

Protocol reference and reverse-engineering notes for the Path of Exile 3.29.3b client. What works now is in
[../Status.md](../Status.md), what to build next in [../Roadmap.md](../Roadmap.md). Start with
**[protocol/00-overview.md](protocol/00-overview.md)**.

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
| [protocol/08-area-transitions.md](protocol/08-area-transitions.md) | area transitions: the object and blob, tile objects (model, rotation, height), the atlas, every area's transitions in the Gateway |
| [protocol/09-area-change.md](protocol/09-area-change.md) | changing area through an exit, instance details (0x27), the instance chooser |
| [protocol/10-chat-and-commands.md](protocol/10-chat-and-commands.md) | chat input (0x15), server replies, stats update (0x1b3), dev commands |
| [protocol/11-skills.md](protocol/11-skills.md) | skills: ActiveSkillManager, basic actions from the .ot, the Actor skill list, the default attack, stat keys |
| [protocol/13-items.md](protocol/13-items.md) | items: types and the item data, inventories with items (0xc0, 0xbf), moving, sockets, the ground, equipment on the model, requirements, gem skills |
| [protocol/12-quests.md](protocol/12-quests.md) | quests (research): the data tables, a1q4 "Breaking Some Eggs" and the Submerged Passage blocker |
| [protocol/06-debugging-log.md](protocol/06-debugging-log.md) | every live run, crash and fix, and lessons |
| [protocol/address-index.md](protocol/address-index.md) | all client addresses by subsystem |

## Generated references
| Doc | Content |
|---|---|
| [protocol/schema.md](protocol/schema.md) ([json](protocol/schema.json)) | read sequence of every component deserializer and of 333 instance/world message classes |
| [re/components.md](re/components.md) ([json](re/components.json)) | every component class the client registers: pool, factory, vtables |
| [re/object-types.md](re/object-types.md) | component list of every object type (`Metadata/...`), from the `.ot` files |

How to regenerate them: [re/tooling.md](re/tooling.md) §7.

## How-to
| Doc | Content |
|---|---|
| [../GettingStarted.md](../GettingStarted.md) | setup from a fresh clone, third-party tools, troubleshooting |
| [re/tooling.md](re/tooling.md) | Ghidra headless scripts, Python helpers, GGPK dump tool, Frida through REA, recipes |
| [dev/live-testing.md](dev/live-testing.md) | the older live-testing notes (GettingStarted supersedes them) |
| [dev-server.md](dev-server.md) | the JSON-lines dev server, `GameServer.Auth` and `GameServer.Patch` usage |

## Working notes (history)
- `re/007`–`010`: the notes these documents were consolidated from (login TCP, handshake, message tables, live instance
  notes). `re/009` still has the most complete login message tables.
- `re/001`–`006`: early notes with known errors (for example "login is HTTPS"). Verify before use.
- `superpowers/` and `history/`: the original design spec, milestone plan and agent prompt.
