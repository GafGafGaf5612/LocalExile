# 23 — Microtransactions: owned lists, the Cosmetics tab, cosmetics in the world

Every account owns every microtransaction. The Gateway serves the lists the client's Microtransactions panel (N), the
store's ALREADY OWNED marks and the Cosmetics tab (K) read, takes the Cosmetics tab's picks and shows what is worn on
the character. No web service is involved: everything below goes over the instance connection. The store itself
(products, prices, buying) is out of scope.

Session offsets are the instance session object's. In the dispatcher `0x14214a190`, r13 is the session + 8, so its
`[r13+0xcd34]` is session `+0xcd3c` (see 13-items.md §6).

## 1. The lists and their "loaded" flags

The client keeps five lists. Each has a *requested* and a *loaded* flag; a panel shows "Loading…" until its list's
loaded flag is set, and asks for a list once per session.

| List | Bit in 0x1f2 | Requested | Loaded | Storage | Filled by |
|---|---|---|---|---|---|
| owned records (N panel, Cosmetics tab entries) | 0 | +0xcd3d | +0xcd3c | +0xd600 (400-byte records) | 0x1f4 flags 0 |
| the store's owned list (ALREADY OWNED) | 1 | +0xcd41 | +0xcd40 | +0xd618 | 0x1f4 flags 1 |
| unlocked hideouts | 2 | +0xcd45 | +0xcd44 | +0xd660 | 0x1f5 flags 0 |
| the guild's store list | 3 | +0xcd3f | +0xcd3e | +0xd630 | 0x1f4 flags 3 |
| guild hideouts | 4 | +0xcd43 | +0xcd42 | +0xd648 | 0x1f5 flags 1 |

**C→S 0x1f2** (3 bytes, vtable `0x1435c0000`, write `0x141f9bd10`): u16 id, u8 bits. Senders: the Microtransactions
panel on open (`0x14216d470`, bit 0), the store (`0x14216d4d0`, bits 1/3), the hideout picker (`0x140bba230`, bits
2/4), the panel controller `0x140a23a40`. Seen live: `01F201` (N), `01F206` (store owned + hideouts). Before
`KnownClientLength` knew it, the read loop stopped at it as unknown and lost the rest of the buffer.

**S→C 0x1f4** (dispatcher case 0x93 at `0x142151a1c`, read `0x1421aac00`): u16 id, u32 BE count, records, u8 flags.
The case tests bit 0 first, then bit 1: 0 → records, 1 → store owned, 3 → guild store. It **replaces** that list, sets
its loaded flag and fires the list's event.

**S→C 0x1f5** (dispatcher case 0x94 at `0x142151c26`, read `0x1421aaee0`): u16 id, u8 flags (bit 0 guild, bit 1
read, not modelled), compact count, one byte per hideout: a `Hideouts.dat` row index. The Gateway sends both empty.

### Records

A record in wire order (`0x141f8bac0`): u8 (+0x08), u32 BE MtxTypes HASH32 (+0x04), u32 BE id (+0x00), raw u64
(+0x10), wstr name, wstr description, u8 (+0x09), u8 0 (no variation list), u8 0 (no variant). The id is the key the
model upserts by. The panels resolve the hash in **`MTXTypeGameSpecific.dat`** (`0x1408e4fb0` → `0x142222170`): a hash
that is not there leaves a null and the row build crashes the client (live 2026-10-23: all 8411 `MtxTypes` rows
crashed it; 690 of them have no `MtxTypeGameSpecific` row). `MtxCatalog` sends the 7721 that have one, with id = the
`MtxTypes` row + 1 and empty names (the client names them from its catalogue). 7721 records are about 190 KB; the N
panel takes a moment to build its tiles the first time.

## 2. The Cosmetics tab (K)

Opening a slot's flyout sends nothing: it lists the records from §1. Picking sends **C→S 0x1f9** (8 bytes, vtable
`0x1435c0690`, write `0x141f9be90`, senders `0x140cdc2b0` and, with record 0 to empty a slot, `0x140cdef80`): u16 id,
u8 `MicrotransactionSlot` Id (20 = Portal), u32 BE record id, u8 option (0 in every pick seen). Live 2026-10-22, a
click on Arctic Portal Effect: `01F9 14 0000000B 00`.

The client wears nothing on its own. Its worn table is 50 entries of 24 bytes at session **+0xd118**, indexed by the
`MicrotransactionSlot` Id (1–49): u32 record id, u32 MtxTypeGameSpecific HASH32, u64, u8. Only **S→C 0x1f3** writes it
(dispatcher case 0x92 at `0x142150ef2`, read `0x1421aa830`): u16 id, u8 count + records (appended to the owned list),
**u8 count + worn entries** (u8 slot, u32 BE id, u32 BE hash, raw u64, u8), u8 count + list +0x40 (u32 +
`0x1424aca00`), u8 count + list +0x58 (u32 + `0x1424ac160`), u8 flags (bit 0 → +0x70 … bit 2 → +0x72; 1 by default).
The case resets all 50 slots before filling them, so every 0x1f3 carries the whole worn table.

The Gateway saves picks per character (`Cosmetics` in the save: slot → record and option) and sends 0x1f3 with no
records and the worn table on every area entry and after every pick. A Portal pick also sets the portals' own
variation (21-hideouts.md §5).

## 3. Cosmetics in the world

The worn table alone draws what the client handles by itself: cursors, portals, flask skins. The rest comes from the
character's own data. `ItemisedVisualEffect.dat` has a row per cosmetic (`Mtx`: its MtxTypeGameSpecific row) that says
what it needs:

| Column | Meaning | Where it goes |
|---|---|---|
| `VisualIdentity` | a skin, attachment or character effect (ItemVisualIdentity) | the Inventories component entry's first reference |
| `VisualEffect` | a weapon effect (ItemVisualEffect) | the entry's second reference |
| `Stats` + `__col7` | stats and their values (footprints `footstep_effect_variation`, apparitions `imp_variation`, skills `skill_art_variation` …) | the player's stats |
| `__col8` | the slot groups it fits (`MicrotransactionSlot.__col9`) | |

**The model.** An Inventories component entry (13-items.md §6) has two cosmetic references before its own
ItemVisualIdentity: (u16 ItemVisualIdentity HASH16, u32 MtxTypeGameSpecific HASH32, u8) read by `0x1424f3fc0`, and
(u16 ItemVisualEffect HASH16, u32 hash, u8) read by `0x1424f4250`. `MicrotransactionSlot.__col7` is the model slot a
Cosmetics tab slot dresses: the item slots (Weapon1 0, Offhand1 1, BodyArmour1 2, Helm1 4, Gloves1 5, Boots1 6, Ring1
8, Ring2 9, Belt1 10, Amulet1 11) and slots only cosmetics use (armour attachment 3, character effect 12, footprints
13, helmet attachments 14 and 15, hip 16); 17 is none. The Gateway puts a skin in the first reference of the item's
entry, a weapon effect in the second, and sends the cosmetic-only slots as entries of their own (empty when nothing is
worn there). A skin needs an item in its slot.

**Stats.** The worn cosmetics' stats join the player's (0x1b3 and the player object). Pets have no
ItemisedVisualEffect row: the client spawns the pet itself from the player's `pet_slot1` / `pet_slot2` stat
(`0x1401093a0`), a `Pet.dat` row + 1 (`0x14010f530`); each Pet row names its MtxTypeGameSpecific row.

Verified live 2026-10-23: skins, attachments, character effects, footprints, portals, cursors and flask skins.

## 4. Open

- Pets did not appear with `pet_slot1` set (live 2026-10-23).
- The portrait (MicrotransactionSlot 16, `MicrotransactionCharacterPortraitVariations`) shows neither on the character
  panel nor on the character selection screen.
- Skill effects: `skill_art_variation` belongs to one skill's stats, not the character's; the Cosmetics tab shows no
  skill slots yet.
