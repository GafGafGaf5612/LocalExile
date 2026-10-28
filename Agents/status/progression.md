# Status: Experience and the passive tree

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/progression.md](../roadmap/progression.md).

## Works (verified with the real client)

- Experience and levels: kills within 150 cells give DefaultMonsterStats experience × the variety's multiplier ×
  rarity, with the level penalty (S→C 0x2ba Player experience; the client derives the level);
  a new level sends its stats, full life and mana, the passive points and the account's level;
  experience is saved per character; `/add_xp`.
  Code: `Progression.cs`, Gateway (`GainExperience`, `ApplyLevel`).
  Docs: [15](../docs/protocol/15-progression.md) §1.
- Passive Skill Tree: S→C 0x33 (level, class, allocated nodes, passive and refund points;
  the client counts level − 1 points), C→S 0x34 allocates a node, checked, applied to the stats (attributes, life,
  mana, resistances and more) and saved; C→S 0x36 with flag 0 refunds a node for a saved refund point (checked,
  mastery effects removed); using an Orb of Regret (right-click, C→S 0x58) takes one item and adds one point;
  a Book of Skill (right-click, C→S 0x58; every quest book of that name) is used up and gives one more passive skill
  point, both saved together; a Book of Regrets gives two refund points the same way;
  `/passives`, `/add_refund_points`, `/reset_passives`.
  Code: `PassiveTree.cs`, `CharacterSaves.cs`, Gateway (`ChangePassives`, `UseItem`).
  Docs: [15](../docs/protocol/15-progression.md) §2.
- Passive masteries: S→C 0x33 carries selected effects (`node | effect << 16`) and C→S 0x35 selects the effect HASH16;
  a mastery node can be allocated normally and its effect selected/changed without spending another point.
  Block Mastery's four effects are implemented: max attack block +2, max spell block +2, 20 life + 20 mana on block,
  and +1% spell block per 5% attack block; the effects are saved with the character.
  Code: `PassiveTree.cs`, `CharacterSaves.cs`, Gateway (`ChangePassives`, `PassivesMessage`).
  Docs: [15](../docs/protocol/15-progression.md) §2.

- Command-based ordinary ascendancy selection, separate point awards, connected allocations, paid refunds and saves.
  A native Duelist/Slayer smoke checked ordinary nodes, ascendancy allocation/refund and both point budgets.
  Choice and progress survived relog and a Gateway process restart; a Phrecia request left the saved JSON unchanged.
  The read-only client trace observed class bytes, the Default graph, allocated nodes and separate point counters.
  Automated domain, packet and production-handler tests cover ownership, separate budgets, graph connectivity,
  multiple-choice effects, atomic refunds, stale connections and old/unknown saves.
  Area admission validates saved progress before inventory changes, then validates items before gold migration.
  Level changes recheck concurrent passive mutations before applying; queued tree packets publish current state.
  Old XP events cannot undo later gains or explicit level resets; Auth follows current XP.
  Old-area gains cannot suppress the live connection's pending level publication.
  XP level reconciliation adopts completed saved trims/refunds atomically before updating runtime state.
  Extra-point grants commit before runtime publication, including concurrent XP updates and failed-write checks.
  Code: `AscendancyCatalog`, `AscendancyProgress`, `OrdinaryPassiveRules`, `CharacterSaves`, Gateway progression.
  Docs: [scope, evidence and beginner steps](../docs/game/ascendancies.md).

## Partly working

- Gambler has consumers for every node in its actual graph, including both Block Luck choices and negative effects.
  Legal eight-point builds, exclusive choices, paid refunds, failed-save rollback and reload have Gateway coverage.
  Incoming ordinary monster ailments give Risk Aversion an actual avoidance consumer, alongside bounded stun recovery.
  Native class acceptance and related-PR integration remain pending; no whole-class readiness is claimed yet.
  Default Phrecia admission is gated; the explicit Gambler opt-in verifies installed graph bytes read-only.
  Docs: [Gambler checklist, scope and verification](../docs/game/gambler.md).
- Phrecia identities, graph ownership and the point/save lifecycle have automated coverage.
  Phrecia game connections/selection remain explicitly gated pending an agreed client-compatible graph.
  Normal startup does not install or recover a graph and ordinary/unascended play has no alternate-graph preflight.
  The native Labyrinth/Altar flow is separate (#151); special class gameplay is outside this slice.
  Native ordinary command checks do not establish an expanded chooser or special class gameplay.
  Docs: [scope and compatibility gate](../docs/game/ascendancies.md).

## Not working yet

- Saving quest progress.
  Quest passive/refund rewards and native Labyrinth/Altar acquisition (#151) remain missing.
  Special ascendancy gameplay and Phrecia client compatibility are separate work.
