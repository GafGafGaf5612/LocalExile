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
The shipped alternate graph contains older ordinary-tree topology.
A separately agreed client-compatible graph is required before Phrecia can be enabled in an actual game connection.
The command rejects a Phrecia choice before saving it, and login refuses an existing Phrecia save before inventory
loading or passive normalization, retaining its data.
There is no automatic graph installation, recovery, archive rewrite, or memory-hook workaround in this slice.
Ordinary startup and unascended/ordinary characters do not require an alternate-graph match.
The integration tests use isolated Phrecia lifecycle fixtures; that is not a claim of play on a pristine client.
The ordinary-client checks below cover the command selection lifecycle; the Phrecia play gate remains in place.

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

### Forest Tracking attack damage

Daughter of Oshabi node 6281, Forest Tracking, grants `attack_damage_+%_per_450_evasion` = 3 (client key 2434).
The common final-stat calculation adds three increased attack-damage points for each complete 450 final evasion.
449 gives zero, 450/899 gives three, and 900 gives six.
The source value is a signed coefficient, not a boolean.
It uses the resolved rating after defence modifiers, Iron Reflexes and explicit final-rating replacements.
An Iron Reflexes character with zero final evasion receives no Forest Tracking contribution.

`ArmourAttackDamage.Total` preserves the existing Steel Willed entry point and adds optional evasion inputs.
Both defence contributions and raw increased attack damage sum in wide arithmetic before one final clamp.
This preserves signed cancellation and avoids turning the new bonus into a final damage multiplier.
It applies once to weapon and added attack damage through the existing ordinary/supported/attack-gem paths.
Spells receive no attack-only increase; conversion, support effectiveness and final Rage scaling keep their order.
Repeated builds and source removal operate on fresh raw inputs without cached increases.
As with Steel Willed, an explicit attack-damage input remains additive with the derived contribution.

Automated checks cover the real node, complete-group boundaries, signed overflow/cancellation, final defence conversion,
ordinary and supported damage, spell isolation, allocation, paid refund, reload and reallocation.
The lifecycle fixture does not open a Phrecia connection.
Full Daughter of Oshabi gameplay and Phrecia client admission remain separate work.

## Validation and limits

Automated tests cover catalog ownership, family separation, native class packing and graph stat, implicit starts,
separate budgets, exclusive choices, ordinary/ascendancy connectivity, paid refunds, old/unknown saves, rollback,
stale connections, regret items and the production passive-message handlers.
Run the Core, Data and Gateway suites from the repository root with the extracted public data available.
The shared Gateway `TestEnvironment` sets the repository working directory once.

The main-integrated source `fd125e7` passed Core 495, Data 18 and Gateway 2375 tests and a warning-free solution build.
Independent review found no remaining substantial issues in the corrected XP and extra-point grant paths.
The merge review preserved both main's locked passive snapshot and the selected ascendancy's stat contribution.
Fourteen deterministic regressions cover delayed XP, overlapping area connections, paid refunds and failed saves.
The complete suites ran in a clean public-data checkout with freshly extracted collision and hideout catalogs.
That checkout has no recorded walk maps; the optional real-capture quest check is outside this verification.

The real 3.29.3b client passed eleven smoke checks on a new synthetic Duelist, choosing ordinary Slayer.
The read-only `scripts/re/frida/trace-ascendancies.js` observed the client's applied tree and counters.
The ordinary graph stayed `Default`, with class byte 4 before selection and 68 afterward.
Ordinary node 39725 survived selection; ascendancy node 61393 used one of two separately earned points.
Repeating the normal-tier award retained two earned points.
An ascendancy refund spent five refund points; an ordinary refund spent one and retained the ascendancy node.
Two extra-point grants produced two ordinary extra points.
Choice, nodes and budgets survived relog and a Gateway process restart, with Auth and Patch left running.
A Phrecia selection request was refused and the synthetic character's saved JSON remained byte-for-byte unchanged.
Admission of an existing saved Phrecia choice is covered by automated tests, not this native smoke.

Local evidence is in `logs/phrecia-completion/choice274-20261026-204858/` in the verification workspace.
The tested Gateway SHA-256 was
`d08482d2c90ed3ac42ab8f4e1ed37619cfb511be50b8442e55f9ce8b24e7b93d`.
Gateway, Core and Host embedded versions matched the source; the live Gateway path and working directory were checked.
The optional merged-graph analysis PSG was absent locally; the JSON graph catalog supplied the ordinary lifecycle.
The client archive was unchanged, and the accepted normal Gateway was restored after the smoke.

To repeat, start the single client through `scripts/dev/live.ps1` and use the ordinary commands above.
Compare the trace's `ascendancyPassives` and `ascendancyGraph` observations with the Gateway log and saved progress.
The smoke does not test the native Altar chooser, every ordinary class, or special ascendancy combat mechanics.
A simultaneous ordinary/Phrecia native-play claim requires separate client agreement and cannot be made by this slice.

Protocol fields and native evidence: [progression](../protocol/15-progression.md) and
[3.29.3b identity/graph research](../re/ascendancy-client-3.29.3b.md).
