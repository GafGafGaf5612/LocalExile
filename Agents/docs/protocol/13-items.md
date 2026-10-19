# 13 — Items: serialization, inventories, moving, sockets, stacks, the ground

Code: `src/GameServer.Core/Protocol/Instance/InstanceItems.cs` (`Item`, `VisibleItem`, `WorldItemObject`,
`InstanceItems`), `InstanceInventories.AddInventory` (`InstanceEntities.cs`); in the Gateway `ItemCatalog.cs`,
`PlayerInventories.cs` and the item handlers in `Program.cs`. Tests: `InstanceItemsTests`. Live trace:
`scripts/re/frida/common.js` + `scripts/re/frida/trace-items.js`. Verified with the real client on 2026-10-18.

## 1. Item types

An item's type is a **BaseItemTypes row**. The client's `Metadata/ItemTypeRegister` files each row under its `HASH32`
column, which is MurmurHash2 of the row's Id: the same hash as object types (`InstanceEntities.ObjectTypeHash`;
`Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1` → 3652280879). Lookup: `0x1421ec390` → `0x141fa5290` (sorted array of
0x30-byte entries keyed by the u32).

Components come from the `.it` files, merged like `.ot` files ([04](04-player-entity.md), `scripts/re/ot_components.py`)
with two differences: nothing is prepended (no Positioned), and most leaf rows have no `.it` of their own, so their
chain is the row's `InheritsFrom`. `client { }` blocks (RenderItem) are never read. Generate every row's list with:

```bash
python -I scripts/re/ot_components.py --items --json data/item-types.json
```

| Type | Components the reader reaches |
|---|---|
| Wand1 (any weapon) | Base, LocalStats, Mods, AttributeRequirements, Weapon, Quality, Sockets |
| FlaskLife1 / FlaskMana1 | Base, Quality, LocalStats, Charges, Mods, Usable, Flask |
| Fireball / Arcane Surge (gems) | Base, SkillGem, Quality |
| Glyph1 (quest item) | Base, Quest |
| CurrencyIdentification | Base, Usable, Stack |

## 2. The item data

Write `0x1421ec1d0` → `0x1421ec010`, read `0x1421ec2c0` → `0x1421ec390`. Wrapped in a **u16 big-endian length**; inside,
everything is little-endian (memcpy):

```
u32  type hash (BaseItemTypes HASH32)
u16  flags            bits 0-8 → item +0x28..+0x2f (bit 4 also to the ctor 0x1421ebf10); bit 9 → u64 follows
...  each component's data, in the type's order: vtable slot 5 (+0x28) reads, slot 4 (+0x20) writes
u16  only for the four type hashes in 0x1421ebe90 (0x192c7aab, 0x365f206a, 0x503da850, 0x9aff2df6)
```

Item component vtables (slot 4 write / slot 5 read). The ones with a bare `ret` (`0x1400f0f00`) carry nothing:

| Component | Vtable | Read | Data |
|---|---|---|---|
| Base | `0x1435e0168` | `0x1421e69c0` | u16 ItemVisualIdentity **HASH16** (unknown → "Attempt to set visual identity to NULL"), u8 +0xc0, u8 +0xc7 flags, u16 flags (low bit0 → u8, bit1 flavour text u16, bit2 wide string, bit3 u32, bit4 wide string, bit5 u8 n × (u32, i32), bit6 nested item, bit7 u8 n × (u32 item type, compact); high bits 0-2 → +0xc4, bit4 → 2 × u32), u8 +0xc6 |
| Mods | `0x1435df838` | `0x1421df6e0` | u8 +0x2b8 (item level), u8 +0x2bc, u16 flags (bit6 incubator u16 u16 u8, bit4 → u8, bit15 → 0x1c8 record `0x1424b70c0`), base magic properties `0x142201d70` (as ObjectMagicProperties: u16 flags, rarity bits 0-2, identified bit 3; u8), u8, u8 |
| Quality | `0x14363acd8` | `0x1424aec50` | u8 |
| Charges | `0x143639a48` | `0x14249d4b0` | signed compact (`0x141e490e0`) |
| Stack | `0x143634b10` | `0x1423f4b30` | u16 stack size (§4a) |
| SkillGem | `0x143637298` | `0x14246eb00` | i32 experience (-1: none), u32 +0x3c, u8 +0x10, u16 +0x40 |
| Sockets | `0x1435df9a0` | `0x1421e2e20` | u8 = bit0/bit1 (special: nothing more) \| socket count << 2 \| link group count << 5; per socket u8 colour << 1 \| has gem, then the gem's item data **without** the length prefix; then a u8 per link group (its socket count). Colours: 1 red, 2 green, 3 blue (seen), 4 white (assumed) |
| Armour | `0x14363ad40` | `0x1424affc0` | u8 0..0x20 (stored at +0x28; 0x20 also sets +0x2a) |
| LocalStats, AttributeRequirements, Weapon, Usable, Flask, Quest, Shield, RenderItem | `0x14363ab88`, `0x1435dfb80` … | `ret` | nothing |

Minimal item the server sends: flags 0, Base with only the visual identity, Mods identified and normal with no mods.

## 3. Inventories with items (S→C 0xc0) and changes (S→C 0xbf)

0xc0 ([05](05-inventories-and-ui.md) §2) carries the items after the inventory header: compact count, then per item
(`0x1421f5680`) **u32 id, u8 x, u8 y, item**. Ids identify items within an inventory (the client keeps a map and drops
duplicates); the Gateway gives every item a character-wide id and keeps it when the item moves.

The 0xc0 **flags** byte: bit 0 (+0x148) = Inventories.dat column 3, a single-item slot drawn as one box (equipment, the
cursor; without it an equipment slot is a grid of cells); bit 1 (+0x149) = column 4, the character's own slots
(equipment, flask bar). With bit 1 the client checks what may go in: the slot's item classes (a wand is red over the body
armour slot) and the attribute requirements (§6).

**S→C 0xbf** changes one inventory (dispatcher case 0x34, class `0x1435dbd28`, write `0x1421a37f0` → `0x1425079a0`,
handler `0x141d62550` → `0x1421f5160`):

```
u8   manager (0 = the player's)
u32  inventory id (unknown → fatal "Got an inventory set change for an inventory that isn't in the set")
u32  revision (→ inventory +0x1e8)
u32  removed count (0xffffffff = remove everything) + that many u32 item ids
u32  added count + (u32 id, u8 x, u8 y, item)
u8   1 → 4 × u32 follow (the 0xc0 flag-bit-4 block)
```

Removal runs before addition, so "remove id N, add id N" replaces an item in place (sockets). The other inventory
messages: 0xc1 removes an inventory ("…inventory set remove…", class `0x1435c0898`: u8 manager, u32 id), 0xc2 sets
+0x160/+0x161 ("…permissions change…", class `0x1435dc660`: u8, u32, u8, u8).

## 4. Moving items (C→S)

All senders sit together at `0x14215f300`… and queue through `0x14216e940`.

| Id | Bytes | Class / write | Meaning | Server answer |
|---|---|---|---|---|
| 0x28 | 13 | `0x1435bf5f0` / `0x141fa0d00` | click on an item: pick it up. u32 inventory id **with HNC side-channel bits 21-29** (mask `0xc01fffff`, reader `0x141fa0ea0`), u32 item id, u8 (3), u8, u8 | 0xbf remove from the inventory, 0xbf add to the cursor (row 12) at (0, 0) |
| 0x2a | 18 | `0x1435bf710` / `0x141f8feb0` | put the held item down: u32 inventory id, u32 x, u32 y (0, 0 for equipment), u8 Shift (1: one of a held stack, §4a), u8 (3), u8, u8. Sent for a click while holding on a cell; a click on an item sends 0x4a | 0xbf target (remove the displaced item, add the held one), 0xbf cursor (remove the held one, add the displaced one) |
| 0x30 | 16 | `0x1435c0838` / `0x141f90650` | put the held gem into a socket: u32 inventory id, u32 item id, u32 socket index, u8, u8 | 0xbf cursor, 0xbf replacing the item |
| 0x2f | 17 | — | take a socketed gem onto the empty cursor: u32 inventory id, u32 item id, u32 socket index, 3 bytes | 0xbf replacing the item, 0xbf cursor |
| 0x29 | 2 | `0x14316c930` (id only), sender `0x140117b80` | drop the held item into the world | 0xbf cursor, then a WorldItem (§5) |
| 0x16 | 5 + n | `0x1435bff00` / `0x141f91480` | a Commands.dat command: u8 command (row + 1), u16 byte count + UTF-8 argument. The town popup's Destroy sends **9** (row 8 DestroyCursorItem, `/destroy`) | 0xbf cursor (the item is gone) |
| 0x2d | 8 | `0x1435c02b8` / `0x141f90340`, senders `0x14215f520` / `0x14215f570` | inventory request, not mapped: u8, u32, u8 flag (0 / 1 by sender) | not handled |
| 0x58 | 10 | `0x1435bf8a8` / `0x141f926a0` | use an item that needs no target (right-click): u32 inventory id, u32 item id, both big-endian | an Orb of Regret loses one item and adds one refund point (0xbf the stack, 0x33 the tree) |
| 0x4a | 14 | `0x1435bf6d0` / `0x141fa08d0`, sender `0x14215f5c0` | the held item clicked **on an item** (§4a): u32 inventory id with HNC bits from bit 20 (mask `0xc00fffff`), u32 the clicked item's id, u8 flags (bit 2 = Shift; HNC bits 3-7), u8 (3), u16 (low byte first, differs every click, unknown) | a merge (0xbf the stack, 0xbf cursor), else a swap as 0x2a |
| 0x7c | 17 | `0x1435bfdd8` / `0x141f90870`, sender `0x142165640` | split a stack (§4a): u32 inventory id, u32 item id, u32 amount, u8 (3), u16 (unknown); no HNC bits | 0xbf the stack (same id, smaller), 0xbf cursor (a new item with the amount) |

Dropping: `0x140117b80` sends 0x29 at once outside town. In town (and for quest items) it opens the Keep / Destroy popup
instead and sends nothing unless Destroy is chosen (0x16 command 9).

Rules the Gateway applies (`PlayerInventories.Place`): grids need the item inside the grid. When exactly one item is in
the way it is swapped onto the cursor; two or more refuse. Single-item slots take the item at (0, 0) when its class fits
(`Accepts`: the ItemClasses Id per equipment row; flasks only in the flask bar). A one-handed weapon put into an empty
main hand's off hand goes to the main hand. Taking the main hand off moves the off hand's one-hander there.

## 4a. Stacks

Stackable types have a Stack component (its u16 is the stack size) and a CurrencyItems.dat **StackSize** above 1
(`ItemCatalog.BaseType.MaxStack`: Orb of Transmutation 40, Scroll of Wisdom 40 …; `CurrencyTab_StackSize` is the
currency stash tab's limit). The client draws the size and the tooltip's "Stack Size: n/max" from the item and its own
tables; it changes nothing itself, the server's 0xbf does.

- **Merge** (C→S 0x4a, the held stack clicked on a stack; live 2026-10-19 the client sends 0x4a, not 0x2a, for a
  click on an item): a stack of the same type with room fills up to the max and the rest stays held. The answer is a
  0xbf on the target (remove and add the stack's id, larger) and one on the cursor (remove the held id, add it again
  smaller, or not at all when all of it went). A full stack, or another type, is swapped as 0x2a on that item's cell
  (`PlayerInventories.MergeOnto` → `Place`). Stash tabs do the same (`AccountStash.Merge`, [18](18-stash.md)).
- **One of the held stack** (Shift + click while holding): 0x2a with its first flag byte 1 puts one into an empty cell
  as a new item; 0x4a with flag bit 2 adds one to a stack of its type (refused on anything else). The rest stays held.
- **Split** (Shift + click on a stack, then the split window's amount): C→S 0x7c. The stack shrinks in place and the
  split-off part is held as a new item (`PlayerInventories.Split`). Refused with something held, an amount of 0 or the
  whole stack. On a stash tab the tab's stack shrinks (`AccountStash.Shrink`, written first) and the held part gets a
  character item id.
- **Full stacks become their item:** CurrencyItems `FullStack_BaseItemTypesKey` (28 types: 5 Scroll Fragments → a
  Scroll of Wisdom, 20 shards → their orb, 100 splinters → their Breachstone or Emblem …). A stack that reaches its max
  by a merge or an add turns into one of that item in the same cell, with the same id (`PlayerInventories.Completed`).
- **Adding** (`PlayerInventories.AddStacked`): items picked up from the ground into the main inventory, `/item <type>
  <count>` (count = how many, up to 20 full stacks) and the sell window's reward go onto the stacks of their type first
  (fullest first), then as new stacks of at most the max in free cells. A ground stack is picked up only when all of it
  fits (`Fits`).

The 0x4a writer `0x141fa08d0` packs the HNC side-channel bits into the inventory id from bit 20 and into the flags byte
from bit 3, like 0x28. The framing table had 0x4a at 12 bytes (it is 14) and did not know 0x7c; both would have split
the next message. Verified live on 2026-10-19: merge, split (10 off 35), Shift one into a cell and onto a stack, 5
Scroll Fragments → a Scroll of Wisdom.

## 5. Items on the ground

`Metadata/MiscellaneousObjects/WorldItem` (`WorldItemObject`): Positioned, BaseEvents (nothing), Animated (u8 0),
WorldItem, InteractionAction (u8 0). WorldItem `0x141ea10a0`: u32 a (non-zero → one more u32), u8 (+0x50), u32
ItemVisualIdentity HASH16 override (0 = the item's), then the item data without its length prefix. Created with 0x2b9 +
wake zone 0x1a9 at the player's estimated cell, removed with 0x2bb.

Clicking it sends C→S 0x24 on the object (skill 0x0266, Interaction). **Flag 0x0002 = the inventory panel is open**
(0x040a vs 0x0408): the item goes to the cursor instead of the first free cell. The Gateway echoes the interaction at
once (0x1ab aimed at the object, from the player's current cell) and picks the item up when its walk estimate is within
**22 cells**: WorldItem.ot `InteractionAction interaction_distance_override = 20` is in grid cells, plus both radii
(object_size 2). Measured with Frida (Positioned grid position at `+0x294/+0x298`): the client stopped 20.9 cells from
the item, on the first step inside the range.

Dying monsters drop items as WorldItems around the corpse (`DropLoot`, [15](15-progression.md) §3).

**Ground items belong to the instance** (`InstanceStore`, `InstanceGround`), as in the real game. Dropped items and
loot are kept per area instance, by Auth's instance id and its creation time (`Created` in the `resolve` answer: Auth's
ids start at 1 again after a restart, so an old instance's items never reach a new one). Re-entering the instance sends
them where they lay (`SendGroundItems`), after a logout, a disconnect or a client crash. Their object ids come from
their own range, **0x100000** up, the same for every connection; the client accepts them (live 2026-10-18). A pickup
takes the item out of the store first, so two connections can never both get it. The store is in the Gateway's memory:
it is wiped like Auth's instance, once no connection has been in it for `InstanceRegistry.Lifetime` (6 minutes), and
lost when the Gateway restarts.

## 6. Equipment on the character: visuals, attributes, gem skills

**What the model wears** is the player's Inventories component (index 8, reader `0x141e1ee30`, also as a 0x2ba update):
u8 n × (u8 slot, record `0x1424f44e0`), then u8 m × u16. The slot is Inventories.dat column 7 (Weapon1 0, Offhand1 1,
BodyArmour1 2, Helm1 4, Gloves1 5, Boots1 6, Ring1 8, Ring2 9, Belt1 10, Amulet1 11). The record: u8 tag (**0 = empty**,
nothing more), two (u16 ItemVisualIdentity HASH16, u32 TieredMicrotransactions key, u8) references (`0x1424f3fc0`,
`0x1424f4250`), u8 ItemStances row (`0x1424f4b10`; the row's ItemStance, else its class's: Wand = 2), u16
ItemVisualIdentity HASH16 (`0x1424f4730`), u8. The Gateway sends every visible slot, empty ones with tag 0: a slot left
out keeps what the model showed before (the unequipped wand stayed in the hand).

**Attribute requirements** are checked (`0x1402ce5f0`) against `virtual_strength_for_requirements`,
`virtual_dexterity_for_requirements` and `virtual_intelligence_for_requirements` (keys 0x3d1b-0x3d1d), cached on the Player
component by `0x141e80340`; `global_no_attribute_requirements` (0x2ee3) skips the check and
`global_item_attribute_requirements_+%` (0x51a) scales it. The character panel shows `strength` / `dexterity` /
`intelligence` (575 / 581 / 578), which a UI cache (`0x141e802d0`) reads when it is built. With only those three the
panel said 32 Int while "Requires 14 Int" stayed red. The Gateway sends all six from Characters.dat (BaseStrength …), in
the creation blob and again as 0x1b3 after entering.

**The class byte** of the Player component is Characters.dat **IntegerId** (Marauder 1, Witch 3), not the row: with row
1 the panel showed Marauder.

**Gem skills:** each active gem socketed in an equipped item grants its skill (SkillGems → first GemEffects variant →
GrantedEffect at level 1, `ItemCatalog.GemEffect`), sent in the Actor skill list after the default attack. **Support
gems must not be granted:** a support's effect in the list crashed the client in the skill reader (`0x141ee795a`, via
`0x142197b59` → `0x141eea955`). Supports and links do nothing yet. Gem skills carry their level-1 stats and can be cast
([11](11-skills.md) §9–10).

## 7. Start kit, saves and dev command

A new character gets Characters.dat
StartWeapons[0] in Weapon1 with two linked sockets in its colour, StartSkillGem and StartSupportGem in the main
inventory, and a Small Life Flask and Small Mana Flask in the flask bar. `Game_START_ITEMS=0` starts empty.

**Saves.** Every inventory change writes the character's items to `data/saves/<name>.json` (`CharacterSaves`, git-ignored):
per Inventories.dat row the item ids, cells and items by BaseItemTypes HASH32 with level, quality, charges, stack size,
sockets (with their gems), links and gem experience. A saved character loads from it instead of the start kit, so items
and equipment survive Gateway restarts and logins (live 2026-10-18). The file also holds the skill bar
([11](11-skills.md) §11).

Each move is one change and one save, so a crash at any moment leaves an item in exactly one inventory: picking up
(`PlayerInventories.PickUp`, C→S 0x28: off its row and onto the cursor), placing or swapping (`Place`, 0x2a), socketing
and unsocketing (0x30, 0x2f) each change everything under one lock and then write the file once. Items on the ground are
not in the save ("Open"), so dropping one takes it out of the save, and a disconnect loses it (#5). Tests:
`tests/GameServer.Gateway.Tests`.

`/item <BaseItemTypes row, Id or name> [count]` adds items to the main inventory; types with a Sockets component get two
linked sockets in the colour of their highest attribute requirement (ComponentAttributeRequirements).

## Open

- Gem levels and experience, support gems and links.
- Mods (implicit, explicit), rarity, names, quality effects; the meaning of Base +0xc0 / +0xc6 / +0xc7 and Mods +0x2bc.
- C→S 0x2d and 0x12 (5 bytes, seen once); 0x4a's flag bit 1, its u16 and its other callers (`0x140b34140`,
  `0x14031c0a0`: probably currency use on an item).
- Flask use and currency use other than the Orb of Regret. Ctrl + click of a stack between inventories; a ground stack that only partly fits stays
  on the ground whole (the real game takes what fits).
- Another connection in the same instance sees items dropped meanwhile only when it re-enters (no broadcast; the
  Gateway shows each client only its own player).
