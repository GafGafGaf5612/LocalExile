# Ascendancy selection, points and saves

An ascendancy is a second class chosen for an existing character.
This change supplies the server's choice and passive-point lifecycle through chat commands.
It does not implement the native Labyrinth/Altar chooser (#151), or every special effect of the 19 Phrecia classes.

## What can be used

Ordinary ascendancies keep their normal choices, including the Scion alternatives.
The server can list the 21 ordinary and 19 Phrecia definitions from the exact 3.29.3b tables and graph export.
Each choice is identified by family, stable table Id and revision; two families can share a class number and start node.
Only one main ascendancy is selected at a time, and it must belong to the character's base class.

**Phrecia play is explicitly gated.**
The installed alternate graph contains older ordinary-tree topology.
A separately agreed client-compatible graph is required before Phrecia can be enabled in an actual game connection.
The command rejects a Phrecia choice before saving it, and login refuses an existing Phrecia save before inventory
loading or passive normalization, retaining its data.
There is no automatic graph installation, recovery, archive rewrite, or memory-hook workaround in this slice.
Ordinary startup and unascended/ordinary characters do not require an alternate-graph match.
The integration tests use isolated Phrecia lifecycle fixtures; that is not a claim of play on a pristine client.
Keep the selection PR in draft until the parent has completed the required ordinary-client checks.

## Try ordinary selection

From the repository root, extract the data with `scripts/dev/extract-data.ps1` and start through `scripts/dev/live.ps1`.
The read-only `passive-graphs` extraction dependency supplies `data/passive-skill-graphs.json`.
In chat, on a Duelist for example:

```text
/ascendancy
/ascendancy normal Slayer
/ascendancy_points normal
```

The first command lists choices for this character.
The second selects Slayer and reloads the area; the character keeps its account, league and stash.
The third is a development shortcut that grants two points once for that Labyrinth tier.
It does not complete the Labyrinth or unlock an Altar UI.
Use `cruel`, `merciless`, `eternal`, or `all` to grant the other tiers, each at most once, up to eight earned points.

Open the normal passive tree, allocate a connected ascendancy node and apply it.
`/passives` lists ordinary points; `/ascendancy` lists the selected class and its separate earned/spent points.
An ascendancy node that grants ordinary skill points adds them to the ordinary budget.
It does not add to the character's level or quest-point field.
The class start is implicit and never charged or saved as an allocated node.

## Refunds and changing class

Use the native Refund Passives operation, or `/reset_ascendancy` to refund the whole selected ascendancy.
An ordinary passive costs one refund point; an ascendancy node or selected choice effect costs five.
A multiple-choice parent costs one ascendancy allocation point but zero refund points; its selected effect costs
zero extra allocation points but five refund points to remove.
Gold respec and `/reset_passives` affect ordinary nodes only.
The server refuses any refund that would disconnect a branch or remove ordinary points that are still spent.
Refund the dependent ordinary nodes first.
Once all ascendancy nodes are refunded, `/ascendancy normal <name>` can choose another ordinary class.

## Saves and validation

Identity, ascendancy nodes, tier awards, ordinary nodes/masteries and refund payments save atomically before success
is published to the connection.
An old area cannot overwrite a newer connection's allocation or refund payment.
Unknown fields are retained, and missing graph data or unknown saved identities/nodes refuse loading without dropping
that progress.
Old characters with no ascendancy field remain unascended.

Ordinary allocations must connect to the base class's implicit start in the chosen graph.
An allocated Ascendant Path node can supply another class start; refunding it cannot orphan that ordinary branch.
Masteries cannot act as bridges, and proxy/cluster expansion nodes remain unsupported.
A lower development level removes safely removable ordinary leaves only; it retains ascendancy points and nodes.

The existing ordinary passive stat aggregation also sees allocated ascendancy stats.
No Ward, Rage, charge, minion, granted-skill, passive-buff, presence or other new gameplay engine is imported here.
Choosing a class is not evidence that its signature gameplay works.

## Validation and remaining checks

Automated tests cover catalog ownership, family separation, native class packing and graph stat, implicit starts,
separate budgets, exclusive choices, ordinary/ascendancy connectivity, paid refunds, old/unknown saves, rollback,
stale connections, regret items and the production passive-message handlers.
Run the Core, Data and Gateway suites from the repository root with the extracted public data available.
The shared Gateway `TestEnvironment` sets the repository working directory once.

The parent owns native verification; this isolated implementation work does not launch or mutate the client.
On an ordinary character, verify command selection, native allocation and five-point refund, points after relog and
full restart, preserved ordinary allocations, and an unascended character's unchanged startup.
Check the Gateway logs and the actual native tree counters together.
Confirm that a Phrecia request reports the compatibility gate and leaves the save unchanged.
A simultaneous ordinary/Phrecia native-play claim requires separate client agreement and cannot be made by this slice.

Protocol fields and native evidence: [progression](../protocol/15-progression.md) and
[3.29.3b identity/graph research](../re/ascendancy-client-3.29.3b.md).
