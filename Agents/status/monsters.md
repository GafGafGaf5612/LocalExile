# Status: Monsters

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/monsters.md](../roadmap/monsters.md).

## Works (verified with the real client)

- Monsters belong to the area instance: re-entering it finds the living ones where they were, with their damage, and
  the dead stay dead; a new instance gets fresh packs.
  Code: `InstanceStore.cs`, `PopulateArea`, Gateway.
  Docs: [14](../docs/protocol/14-monster-packs.md) §2.
- Monsters: `/spawn_mob <row, Id or name>[@level] [friendly] [rarity] [name]`, `/mobs`, `/despawn`, `/mob_walk`,
  `/ai`: any of 10 345 of the 10 691 MonsterVarieties at a level and rarity, with full life for its level, hostile
  (red) or friendly, that chases the player when near (Move action 0x1ab, client pathing).
  Code: `MonsterObject`, `MonsterCatalog.cs`, Gateway.
  Docs: [10](../docs/protocol/10-chat-and-commands.md) §5.
- Monster packs in areas: on entry the area fills with its MonsterPacks at the area level (The Coast: crab and Drowned
  packs, some magic, at least 2 with a rare; cannibal packs at the bonfires) and the unique boss at the camp's
  `mapboss` point with its boss pack (Fire Fury beside the campfire, verified live 2026-10-18);
  room points are found by fitting rooms to the doodads `-Walkmaps` captures.
  Code: `MonsterPacks.cs`, Gateway (`PopulateArea`), `scripts/walkmaps`, `scripts/re/atlas room-points`.
  Docs: [14](../docs/protocol/14-monster-packs.md).
- Monster hit reaction: a hit that leaves a monster alive and takes at least a tenth of its max life plays one of its
  rig's TakeHit clips with the clip's own sound (S→C 0x1b2 kind 0;
  a pool picks one at random), and not again until that clip has played (at least 0.4 s);
  a killing blow gets no flinch.
  Verified live 2026-10-25 on the Sanctum zombie and the Goatman (melee and Holy Strike);
  the damage and clip-length gates are not yet checked live.
  The weapon impact sound and the target's blood come from S→C 0x1af "action hit", sent on melee and attack-gem hits
  and on monster hits on the player (2026-10-25; not yet checked live after the timing cap).
  Code: `HitReaction.cs`, `MonsterCatalog.TakeHit`, Gateway (`SendFlinch`), `scripts/re/monster_takehit.py` →
  `data/monster-takehit.json`.
  Docs: [11](../docs/protocol/11-skills.md).
- Magic and rare monsters roll their mods from Mods.dat (monster domain, spawn weights by the monster's tags, level,
  one per family): magic one affix shared by the monsters of one name in an instance, rare one or two in the campaign
  and two to four from area level 68, with a rare name from the Words.dat monster lists.
  The client shows them on hover (verified live 2026-10-25: "Gorewalker" / "Extra Life", "Quick Hungry Corpse" /
  "Hasted").
  Their life, hit, attack time, walk (sent as movement_velocity_+%, the client walks at the same speed), resistances,
  physical reduction, block, evasion, accuracy, critical strikes and regeneration act on the server; the instance keeps
  them.
  Only the 38 mods the server can honour roll; the other 73 are listed in [14](../docs/protocol/14-monster-packs.md)
  §5.
  Code: `MonsterMods.cs`, `Program.MonsterMods.cs`, `MagicProperties.cs`.
  Docs: [14](../docs/protocol/14-monster-packs.md) §5.
- Monsters fight back: their first granted effect as a non-pathing skill with MonsterVarieties attack time, reach and
  level damage; they swing (animated) when in reach and the player's life goes down after evasion, armour and block;
  walking away dodges; death plays and blocks moving; the death screen (S→C 0x55) shows "Resurrect in Town" /
  "Resurrect at Checkpoint" (hardcore: "Exit"), and both buttons work (C→S 0x54; in town: the last town); `/heal`.
  Code: `MonsterAttack`, `Resurrect`, Gateway.
  Docs: [11](../docs/protocol/11-skills.md) §6–7.

## Partly working

Nothing yet.

## Not working yet

- Area monsters: pack counts, magic/rare chances and rare mod counts are our own numbers;
  73 monster mods need systems the server lacks (auras on allies, monster skills, ailments on the player …) and do
  not roll;
  Drowned ignore their ground types; emerge packs (Gravel Eaters) are skipped;
  the boss life bar and boss stages exist for Hillock only (`BossFights.cs`;
  other bosses with `BossHealthBar` get the bar too); monsters respawn on every entry;
  a new (area, seed) needs a `-Walkmaps` visit for its unique boss ([14](../docs/protocol/14-monster-packs.md) §4).
