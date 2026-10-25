# Roadmap: NPCs, shops and chat

Part of the [Roadmap](../Roadmap.md); what already works in this area is in
[status/npcs-and-chat.md](../status/npcs-and-chat.md).
Items keep the Roadmap's priority groups, and are in rough priority order within each group.

## 2. The world

### NPCs: dialogue options, shops and the other towns

- **Known:** NPC objects, Lioneye's Watch placements and the dialogue window (S→C 0x84, C→S 0x85) work
  ([17](../docs/protocol/17-npcs.md)).
  The client picks the NPCTalk row itself (`0x141e76d40`).
  Dialogue quest flags and the class gem choices in reward offers 0 and 8 are implemented and live-checked.
- **Start:** the remaining quest reward offers and offer 8's magic-armour choices.
  NPCDialogueWindow's option handler is `0x140723d60` / `0x14071f6d0`.
  Shops from NPCShop / ShopSets.
  Placements for the other towns (`NpcCatalog.Towns`, with `/pos`).
- **Done when:** talk options play their text, Nessa and Tarkleigh trade, and every town has its NPCs.
