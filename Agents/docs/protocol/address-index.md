# Address index (3.29.3b `Game.exe`, image base 0x140000000)

Every address used in the protocol docs, grouped by subsystem. Fn = function, Vt = vtable, Cls = message class vtable,
G = global / static data.

## Network core
| Address | Kind | What |
|---|---|---|
| `0x140dfad60` | Fn | stream (socket) ctor, login |
| `0x140dfaf70` | Fn | stream ctor from a connected socket (instance) |
| `0x140dfb630` | Fn | stream read (sets `+0x252` when short) |
| `0x140dfb760` | Fn | stream write |
| `0x140dfb8c0` | Fn | flush / send (cipher slot 1) |
| `0x140dfb3d0` | Fn | receive (cipher slot 2) |
| `0x140dfbe30` | Fn | install cipher pair (`+0x1b8`), decrypt buffered bytes |
| `0x140e02170` | Fn | crypto object ctor (DH groups, "Rhoa") |
| `0x140e03a30` | Fn | Salsa20 pair (km, isClient) |
| `0x140e04840` | Fn | Salsa20 object |
| `0x140e0c9b0` / `0x140e0cbe0` | Fn | wstr write / read |
| `0x141fa4610` / `0x141fa44f0` | Fn | blob16 read / write |
| `0x141fa3c50` / `0x141e61010` | Fn | compact int write / read |
| `0x14089e520` / `0x14089cad0` | Fn | compact int write / read (stream) |
| `0x14299e440` | Fn | message pump: read id, offer to groups |
| `0x14299e3b0` | Fn | register handler group |
| `0x14299e300` | Fn | generic group handle slot (factory → read → owner) |

## Login
| Address | Kind | What |
|---|---|---|
| `0x14213e610` | Fn | login connect, sends 0x0002 |
| `0x14213f190` | Fn | login S→C dispatch (switch on id) |
| `0x14219c350` | Fn | login S→C factory |
| `0x140e03630` / `0x140e03460` | Fn | verify server key / DH2 agree + SHA-512 |
| `0x140d05f70` | Fn | account login sender (0x03, password digest) |
| `0x140d13060` | Fn | character screen → 0x18 |
| `0x1421406f0` | Fn | Play → 0x0e |
| `0x14213e5a0` | Fn | login connection dropped: login `+0xde8` (0 none, 2 handshake, 3 logged in) back to 0 |
| `0x1400f2600` | Fn | state stack singleton; states by id byte (`+0x209`) at `+0x48 + id×0x10` ([09](09-area-change.md) §6) |
| `0x140104510` | Fn | builds every state (login id 7 `0x140d0c8c0`, character select id 9 `0x140d12140`, in game id 4 `0x140d55be0`) |
| `0x140d0d190` | Fn | login state enter: character select if login `+0xde8` = 3 |
| `0x140d574e0` | Fn | in-game frame update → `0x140d57cd0` (instance down: login state, login object kept) or `0x140d57730` |
| `0x140cf1ab0` | Fn | Esc → "Exit to Character Selection": C→S 0x153 |
| `0x140cfc2d0` | Fn | gateway_list.txt reader |
| `0x142fc0150` / `0x142fc04a0` / `0x142fc0620` | G | DSA key / client static private / server static public |

## Instance client
| Address | Kind | What |
|---|---|---|
| `0x1415a4fe0` | Fn | creates the instance client (0xe208 bytes) |
| `0x142144b50` | Fn | instance client ctor |
| `0x14345e810` / `0x14345e8b8` | Vt | instance client / its `+8` sub-object |
| `0x142148880` | Fn | tick (connect job, 0x7d, pump, 0x1b, 0x31, 0xc7) |
| `0x142506420` → `0x1425064d0` → `0x1425065b0` → `0x1425066e0` | Fn | connect job → thread → happy-eyeballs connect |
| `0x142148500` | Fn | send opening 0x0F + 0x0E, state 2 |
| `0x14216e940` | Fn | send message (vtable slot 1 on the stream) |
| `0x141fe1570` | Fn | digest used by 0x0E |
| `0x142fbfbf8` | G | 8-byte constant in the 0x0E digest |
| `0x14214a190` | Fn | instance S→C dispatcher |
| `0x142154e14` | G | dispatcher byte table (`id − 0x0d` → case) |
| `0x1421b5c40` | Fn | instance S→C factory |
| `0x1435d9d98` | Vt | instance handler group |
| `0x142173610` / `0x14299dca0` → `0x14299db60` | Fn | 0x11: main cipher / side-channel cipher |
| `0x142fe0da0` | G | 64-byte XOR mask for the side-channel key |
| `0x142149ca0` | Fn | disconnect (state 1) |
| `0x142162830`, `0x142162630` | Fn | other state writers |

## Area and world
| Address | Kind | What |
|---|---|---|
| `0x1435da180` (ctor `0x14219d940`, write `0x1421415c0`, read `0x142141ef0`) | Cls | 0x1d area |
| `0x142155310`, `0x140e31310` | Fn | queue area generation |
| `0x1435bf8a8` | Cls | C→S 0x7d (tile hash, doodad hash) |
| `0x1435c0358` | Cls | C→S 0x1b tick |
| `0x14316c420` | Cls | u32 message (login 0x1f/0x20, instance 0x0F, 0x22) |
| `0x14316c930` | Cls | empty message (login 0x01, instance 0x11) |
| `0x142186fa0` | Fn | set local player id (`world+0x980`) |
| `0x1435dcb80` (ctor `0x1421a2d40`, write `0x1421a2da0`, read `0x1421a2fe0`) | Cls | 0x21 enter area |
| `0x141d4d020` | Fn | 0x21 Record68 writer |
| `0x142156d80` | Fn | enter area: activate world, bind player, state 3 |
| `0x1400f24b0` / `0x14013f1e0` | Fn | world's first local entity / its "Player" component |
| `0x1435da300` | Vt | world handler group |
| `0x1421c6150` | Fn | world S→C factory |
| `0x142187480` | Fn | world message handler (0x2b9 create, …) |
| `0x142188cc0` | Fn | registers the world group on the instance pump |
| `0x142183670` | Fn | world activation (world vtable +0x1c8): replay queue |
| `0x141d83fb0` → `0x141d7c4c0` | Fn | spatial index insert (23×23 buckets, no bounds check) |

## Objects and components
| Address | Kind | What |
|---|---|---|
| `0x141ee54b0`, `0x141ee3f70` | Fn | GameObjectTypeRegister factory / resource cache |
| `0x141ec18c0` | Fn | build the register from dat tables |
| `0x1424f7520` / `0x1424f76c0` / `0x1416719b0` | Fn | register add / insert / lookup |
| `0x1424748a0` → `0x140df8570` | Fn | narrow path → MurmurHash2 |
| `0x1435dd8f8` (write `0x141fe04d0`, read `0x141fe0520`) | Cls | 0x2b9 create object |
| `0x141f91f60` / `0x141f92000` | Fn | entity header write / read |
| `0x141ebe190` | Fn | create object from blob |
| `0x141dea920` / `0x141ee00f0` | Fn | blob base data / tasks |
| `0x142427510` | Fn | build components from the type |
| `0x142475bd0`, `0x142476300`, `0x142476580` | Fn | `.ot` parser, extends, sections |
| `0x140e28a30` | Fn | blob reader out-of-range (throws `0x143a50ab0`) |
| `0x1435a0f18` / `0x141e2f0b0` | Vt / Fn | Positioned / deserializer |
| `0x1435dd4e8` / `0x1421c2f40` | Vt / Fn | comp1 (stats-like) |
| `0x14359f5f8` / `0x141e114d0` | Vt / Fn | comp3 |
| `0x1435a7340` / `0x141e6c2f0` (pool `0x141e6be70`, flags `0x141e6c270`) | Vt / Fn | Life |
| `0x1435a5030` / `0x141e56a80` | Vt / Fn | Animated |
| `0x143466450` / `0x141e7dc10` | Vt / Fn | Player (xp setter `0x141e7f6b0`) |
| `0x14359e078` / `0x1415d8cb0` → `0x141e1ee30` | Vt / Fn | Inventories component |
| `0x14346e558` / `0x142192a80`, slot 20 `0x1421932b0` | Vt / Fn | comp9 |
| `0x1435a6ba0`, `0x1435a0078`, `0x143468920`, `0x14346c238` | Vt | Pathfinding, comp7, comp10, comp11 |
| `0x1400f0f00` | Fn | empty `ret` (no-op slot) |
| `0x142f496a0` | G | static initializer table entry (Positioned) |
| `0x1445bb240`, `0x1445bb6f0`, `0x1445bbe50`, `0x1445bbd60`, `0x1445bb760`, `0x1445bbd10`, `0x1445ba850`, `0x1445bc610` | G | component pools (Positioned, Stats, Pathfinding, Life, Animated, Player, Inventories, Actor) |

## Monsters ([10](10-chat-and-commands.md) §5)
| Address | Kind | What |
|---|---|---|
| `0x1435dc320` (read `0x1421a4f30`, write `0x1421a4e70`) | Cls | S→C 0x20 monster types at a level (dispatcher case 9) |
| `0x141ec2540` / `0x141ec28e0` | Fn | register `"<variety Id>@<level>"` / the plain variety Id |
| `0x142288c90` | Fn | MonsterVarieties row by index (0x31a-byte rows) |
| `0x1435a6778` / `0x141e6fd00` | Vt / Fn | Monster component (slot 17 `ret`) / its factory ("Creating monster with no level") |
| `0x1435a5a28`, `0x141e701d0` | Vt, Fn | Monster component type; slot 9 parses the `@level` suffix |
| `0x141e2d860` | Fn | Positioned .ot keys (`serialise_collidable` → +0x1b, …) |
| `0x14161d160` | Fn | Targetable deleter (where a failed object build crashes) |

## Patching
| Address | Kind | What |
|---|---|---|
| `0x1428eb82e` | call site | "No patching required" decision |
| `0x1421ccaf0` | Fn | local GGPK root hash is non-zero |
| `0x1428ead70` | Fn | compare a local directory hash with the welcome |

## Inventories and UI
| Address | Kind | What |
|---|---|---|
| `0x14216aa30` | Fn | inventory manager by selector |
| `0x141d625f0` | Fn | register inventory (row → id, id → inventory) |
| `0x141d621a0` | Fn | inventory lookup by id (409 call sites) |
| `0x1431ab2f0` (ctor `0x140b18270`, write `0x140b182a0`, read `0x140b18360`) | Cls | 0xc0 add inventory |
| `0x1421f4a40` / `0x1421f4cc0` / `0x1421edc10` | Fn | inventory object write / read / ctor |
| `0x1421f4f80`, `0x1421ec1d0` | Fn | inventory items read / item write |
| `0x142173fc0`, `0x142174500` | Fn | 0xc3 sockets/gems, 0xc4 list |
| `0x14029dfc0` → `0x1402bafb0` → `0x1402d0500` | Fn | flask frame UI (needs row 11) |
| `0x14098ab0c` | call site | UI lookup of row 0x54 (expanded main inventory) |
| `0x1435dcd00` (ctor `0x14219eb10`, write `0x14219eb40`) | Cls | 0x3d passive slots |
| `0x142155bf0` | Fn | create passive slot 0/1/2 (`instance+0xc2f8`) |
| `0x140785160` | Fn | UI reading the active passive slot |

## Items ([13](13-items.md))
| Address | Kind | What |
|---|---|---|
| `0x1421ec1d0` / `0x1421ec010` | Fn | item write (u16 length + data) / data |
| `0x1421ec2c0` / `0x1421ec390` | Fn | item read (length) / data: type hash → ItemTypeRegister, flags, components (slot 5) |
| `0x141fa5290` | Fn | ItemTypeRegister lookup by HASH32 |
| `0x1421ebe90` | Fn | the four type hashes with a trailing u16 |
| `0x1421e69c0`, `0x1421df6e0`, `0x1421e2e20`, `0x1424aec50`, `0x14249d4b0`, `0x1423f4b30`, `0x14246eb00` | Fn | item component reads: Base, Mods, Sockets, Quality, Charges, Stack, SkillGem |
| `0x1421f5680` | Fn | inventory item entry read (u32 id, u8 x, u8 y, item) |
| `0x1435dbd28` (ctor `0x1421a37b0`, write `0x1421a37f0`, read `0x1421a3880`) | Cls | S→C 0xbf inventory change (`0x1425079a0` / `0x142507bf0`) |
| `0x141d62550` → `0x1421f5160` | Fn | 0xbf handler → apply removals, additions, revision |
| `0x1435c0898`, `0x1435dc660` | Cls | S→C 0xc1 remove inventory, 0xc2 inventory permissions |
| `0x1435bf5f0` (write `0x141fa0d00`, sender `0x14215f300`) | Cls | C→S 0x28 pick up item |
| `0x1435bf710` (write `0x141f8feb0`, sender `0x14215f400`) | Cls | C→S 0x2a place held item |
| `0x1435c0838` (write `0x141f90650`) | Cls | C→S 0x30 socket the held gem |
| `0x140117b80` | Fn | drop held item: 0x29 outside town, Keep / Destroy popup in town |
| `0x1435bff00` (write `0x141f91480`, sender `0x14215c3b0`) | Cls | C→S 0x16 Commands.dat command (Destroy = 9) |
| `0x1435c02b8`, `0x1435bf6d0` | Cls | C→S 0x2d, 0x4a (not mapped) |
| `0x141ea10a0` | Fn | WorldItem component read |
| `0x141e1ee30` → `0x1424f44e0` | Fn | player Inventories component: visible equipment entries |
| `0x1402ce5f0` | Fn | item attribute requirement check (virtual_*_for_requirements) |
| `0x141e80340`, `0x141e802d0` | Fn | Player caches: requirement attributes; UI attributes |
| `0x1402f36c0` | Fn | item's AttributeRequirements component getter |
| `0x141e1dce0` | Fn | InteractionAction type parser (`interaction_distance_override` → +0x1bb) |

## Strings worth searching
"Connect time to instance server was", "Client-Safe Instance ID = ", "Generating level ", "Instance sent world area
that does not exist with hash: ", "Area reported by instance does not exist", "Unable to deserialise packet with pid ",
"Unknown object type serialized by server", "Unrecognised task serialised by server; missing registration?",
"Metadata/GameObjectRegister", "Game::GameObjectTypeRegister", "Art/2DArt/UIImages/InGame/Azmeri/SapFlaskFrame".

## Latency, HNC, wake zone, movement ([07](07-latency-visibility-movement.md))

| Address | Kind | What |
|---|---|---|
| `0x142155930` | Fn | 0x1c: drop acknowledged entries from the send-time queue (`+0xc458`) |
| `0x141fa0720` | Fn | 0x1c read (u16, u16, u32 HNC carrier) |
| `0x14299de50` | Fn | HNC byte state machine ("HNC Protocol Violated") |
| `0x14299db60` | Fn | HNC key: km XOR `0x142fe0da0`, byte swaps |
| `0x142fe0da0` | Data | HNC 64-byte mask |
| `0x141ec0aa0` | Fn | ObjectInWakeZone (0x1a9) |
| `0x1421940a0` | Fn | Actor message handler (0x1ab action started, …) |
| `0x141e30c90` | Fn | Positioned message handler (0x1b7 teleport, 0x1ba move, …) |
| `0x142166300` | Fn | C→S 0x25 / 0x24 sender (use skill at location / on object) |
| `0x141fdda70` | Fn | 0x1ab read |
| `0x141fa3c50` / `0x141fa3d60` | Fn | compact int write / read |
| `0x141f91f60` / `0x141f92000` | Fn | world object header write / read |
| `0x1416aa670` | Fn | perf overlay ("network: {} ms") |


## Skills ([11](11-skills.md))

| Address | Kind | What |
|---|---|---|
| `0x1435db980` | VT | ActiveSkillManager (Actor +0x598; skills at +0x158, sorted by instance +0x40) |
| `0x141eea6a0` | Fn | ActiveSkillManager::SkillInstanceAdded (manager slot 7) |
| `0x141ee97f0` | Fn | create the action for a skill id |
| `0x142192860` | Fn | ClientActor ctor: basic actions from the type's u16 list (`type+0xf8`) |
| `0x142198d40` | Fn | AddSkill(manager, ActionTypes HASH16): basic action |
| `0x1421932b0` | Fn | Actor slot 20 reader (f0 0x40 skill list, 0x20 skill update, …) |
| `0x142197b10` | Fn | Actor f0 0x40: skill list |
| `0x142198800` | Fn | Actor f0 0x20: skill update |
| `0x1421981d0` | Fn | read n skill records into the manager |
| `0x142196ce0` | Fn | skill record reader |
| `0x1424a0dc0` | Fn | GrantedEffectsPerLevel reference reader |
| `0x1424a0990` | Fn | GrantedEffectStatSetsPerLevel reference reader |
| `0x141ee73d0` | Fn | ActiveSkillInstance ctor (action id from GEPL → GrantedEffect → ActiveSkill → ActionType) |
| `0x1435b9728` | VT | ActiveSkillInstance |
| `0x1424cfa40` | Fn | stat getter (stats, key); stored pairs at stats +0xf0 |
| `0x141e6a190` | Fn | Life tick: notifies listeners only while life > 0 |
| `0x140220ab0` | Fn | mini life bar entry: flags from the Life pools (0x20 = dead) |
| `0x140220c30` | Fn | mini life bar entry: stat listener (`hide_mini_life_bar` 0x2a8b, `virtual_is_hidden_monster` 0x59d0) |
| `0x140221fd0` | Fn | mini life bar: visible? |
| `0x141e8cf10` | Fn | Actor: start (queue) an action |
| `0x141e8e060` | Fn | Actor: per-frame runner of the queued action |
| `0x141938830` | Fn | Melee action begin = skill_is_non_pathing |
| `0x14219f820` | Fn | S→C 0x55 death screen read |
| `0x14028fc80` | Fn | `death_screen` panel ctor (setup `0x140290820`, buttons `0x140290bf0` → C→S 0x54 / 0x153) |
| `0x140d86180` | Fn | HUD vtable +0x4d0: show/hide the death screen |
| `0x1435dc4c0` (read `0x14219f600`) | Cls | S→C 0x53 skill bar: 13 × u16 skill ids (LE), u16, u16 |
| `0x1435bfc58` | Cls | C→S 0x4e bind a skill to a skill bar slot (u8 slot, u16 skill id) |
| `0x140aecab0` | Fn | skill bar UI: put a skill in a slot (`0x140ae16b0`) or clear it (`0x140ae1fd0`), then send C→S 0x4e |
| `0x14319d870` | VT | skill bar UI (13 slots; `0x140ae04b0` subscribes it to instance +0xaf78 and +0xad78) |
| `0x140ae0fb0` | Fn | skill bar vtable +0x390: rebuild from 13 skill ids (lookup in ActiveSkillManager +0x158, slot set +0x398, clear +0x3a0) |
| `0x1435a0d10` | VT | Projectile component (0x20 bytes; slot 17 reads nothing, almost every slot a `ret`) |
| `0x141e30c90` | Fn | Positioned message handler: 0x1b7 teleport, 0x1b8, 0x1b9, 0x1ba move (height u32 +0x28, speed f32 +0x2c), 0x1bc |
| `0x141d84b00` | Fn | terrain height of a cell (terrain, int cell[2]) in steps of 7.8125; `0x141e35800` interpolates for a Positioned |
| `0x1415f53c0` | Fn | Render: model transform; tilt = −`0x141e35c00`(Positioned) when the type has `tilt_to_target` |
| `0x141e34400` | Fn | Positioned: start a move (interpolation `0x141e4d180`; height 0x7fffffff = terrain, else z = −height × 7.8125) |
