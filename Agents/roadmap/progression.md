# Roadmap: Experience and the passive tree

Part of the [Roadmap](../Roadmap.md); what already works in this area is in
[status/progression.md](../status/progression.md).
Items keep the Roadmap's priority groups, and are in rough priority order within each group.

## 3. Gameplay

### Character progression: the rest of the Passive Skill Tree

- **Known:** experience, levels, S→C 0x33 and C→S 0x34 (allocate) work ([15](../docs/protocol/15-progression.md)).
- **Known:** gold respec at Bestel (C→S 0x36 / 0x37) and masteries (0x35) work.
- **Known:** refunds with refund points (0x36 flag 0; 0x33's first u16) work;
  Orbs of Regret (right-click, 0x58) add them.
- **Accuracy Mastery:** all five effects currently offered by the target client's Accuracy group are modelled
  for the existing default-attack accuracy roll ([Status](../status/skills-and-combat.md),
  [11](../docs/protocol/11-skills.md) §4–5): Accuracy = Intelligence, +3 per Dexterity instead of +2, +500 / -2 per
  level, 40% more accuracy against Unique Enemies and 50% more against Enemies in Close Range (both at the attack's
  accuracy roll; close range is the constant 20-unit radius).
  Minions' Accuracy equals yours is not offered by that target group and remains unsupported with minion accuracy.
  Gem attacks still need their own accuracy rolls before these target conditions can affect them.
- **Known:** pinned ordinary/Phrecia topology is available as
  [read-only analysis artifacts](../docs/dev/passive-graph-data.md), without installing a client graph.
- **Known:** command selection, saved identities, separate point awards, paid refunds and ordinary/ascendancy
  graph connectivity now have automated coverage; see [scope and limitations](../docs/game/ascendancies.md).
  Native verification of this extracted slice remains with the parent.
- **Start:** quest passive/refund rewards and the native Labyrinth/Altar flow (#151).
  Phrecia play stays explicitly gated until a client-compatible graph is separately agreed.
  Do not add automatic archive writes or require the alternate graph for ordinary characters.
- **Done when:** quest rewards persist and the native Altar can choose an ordinary ascendancy, with real-client checks.
  Implementing all 19 Phrecia classes' special gameplay is separate from this selection lifecycle.
