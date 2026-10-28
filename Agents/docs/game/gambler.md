# Gambler

Gambler is the Duelist's Phrecia Gladiator identity for client 3.29.3b.
All nodes offered by the pinned graph have runtime consumers, including their drawbacks.
This is a bounded implementation of the server's existing combat, not every Path of Exile skill or ailment source.
Native acceptance and composed-PR verification must be recorded before declaring the class ready.

## Node checklist

| Node | Actual gameplay consumer |
|---|---|
| 62445 Risky Exploit | -25% all elemental resistance; one 50% outcome per incoming enemy hit treats all elemental components as exactly 90% resistance, above the normal cap. |
| 25530 Reversed Odds | One 50% outcome per outgoing hit negates capped enemy elemental resistance before penetration; negative resistance can become harmful. Critical resistance ignore takes precedence. |
| 15874 Growing Accumulator | 30% less outgoing hit and damage-over-time damage; one 40% triple-damage outcome for the direct hit. Stored Impale reflection is not tripled. |
| 5523 Spectacular Parlay | Unlucky hit damage rolls; lucky critical chance through the existing `CriticalStrikePolicy`, including conditional luck cancellation. |
| 16638 Overly Confident | Unlucky enemy hit damage rolls per component; never rerolls damage-over-time ticks. |
| 20073 Risk Aversion | Ordinary 66% avoidance per incoming elemental ailment and 66% stun avoidance. It grants neither lucky avoidance nor immunity to the hit. |
| 11167 Block Luck | One paid allocation point; mutually exclusive Heads/Tails effects cost no extra allocation point. |
| 51178 Heads | Lucky spell block, unlucky attack block and +3% maximum spell block. |
| 6481 Tails | Lucky attack block, unlucky spell block and +3% maximum attack block. |
| 17298, 56028, 53426, 12789, 63992, 56067, 54394 | Each grants 3% attack block and 5% increased item rarity to the existing normal/projectile drop consumers. |

The installed graph has seven small nodes.
PassiveSkills row 48297 has matching class ownership but is absent from this graph; it is not offered or allocated.
The start, 32730, is implicit and free.
Refunding a choice effect costs five refund points; refunding its parent after removing the effect is free.
Luck sources sum with their signs, with opposing sources cancelling before the bounded extra roll.

Two legal eight-point builds cover the entire class, swapping the free choice to cover Tails:

```text
56028 62445 17298 25530 63992 15874 12789 5523
53426 16638 54394 11167 51178 56067 20073 63992 15874
```

## Incoming elemental ailments

Supported monster melee contacts now inflict ordinary ailments: any qualifying cold hit chills, and a critical
elemental hit can ignite, freeze or shock with its corresponding component.
Blocked and evaded hits do not apply ailments.
Each eligible ailment independently checks final avoidance once; 65 succeeds and 66 fails against Risk Aversion's 66%.
Damage luck, critical luck and block luck do not make this avoidance lucky.

Ignite uses 90% per second of a separate ordinary roll of the pre-hit fire range, with the target monster's 130% critical ailment multiplier,
for four seconds before self-duration modifiers.
Monster.ot sets `critical_ailment_dot_multiplier_+ = 30`; Character.ot's +50 applies to player ailments.
Enemy hit luck never chooses this ailment roll.
Only the strongest active ignite damages; a weaker longer ignite can resume when the stronger expires.
Live fire resistance and damage immunity apply to ticks, independently of Risky Exploit's hit-only override.
Ticks carry fractional damage, absorb Energy Shield first, interrupt recharge and can kill.
Lethal ticks use the normal death cleanup and an epoch-checked, once-only native death notification.
They do not block, suppress, apply armour, generate hit recovery or record another enemy hit.
The resource writer retains RF's separate nonlethal behavior and Fortification recovery.

Chill and shock use the ordinary maximum-Life threshold: floor(50 × (damage / threshold)^0.4), ignoring effects below 5%.
Chill caps at 30% and shock at 50%, with two-second base durations and supported self-effect/duration modifiers.
Freeze lasts six seconds times the cold damage / threshold ratio, capped at three seconds before self-duration;
durations below 0.3 seconds do not apply.
Chill scales supported attack/cast timing and movement; freeze interrupts movement/channeling and gates actions.
An interrupted projectile windup stays cancelled after freeze expires; an already released flight remains independent.
Shock increases subsequent incoming hit damage, ignite ticks and RF self-burn.
All timers use the pauseable connection clock.
Immunity flasks remove their ordinary ailment during recomputation, and death/connection teardown clears all effects.
Native status stats are projected; dedicated ailment buff icons and their art are not implemented by this path.

This scope does not add hostile projectile/spell adapters, enemy chance-to-ailment mods, special ailment thresholds,
conditional ailment immunities or alternative-ailment sources (Scorch, Brittle, Sap).
Bleed and poison are not elemental ailments and gain no avoidance from Risk Aversion.
The existing stun consumer remains bounded to supported ordinary-Life physical melee hit recovery.
These engine limits must not be described as broader ailment parity.
Formula references are the repository's pinned PoB `CalcOffence`/`CalcDefence` source and Core `Ailments` formulas.

## Admission and reproducible verification

The default Phrecia gate stays closed.
`Game_VERIFIED_GAMBLER=1` opts in only this 3.29.3b identity after read-only checks of the installed ordinary and
combined alternate PSG bytes, SHA-256 `82D82CD3CD2DAD81606AF0DA9D18FCD53B2E92CF006550947D65A4F7E102DC32`
and `F2766B5A4618CDBE7E2993973B4D653F155F71BDFE0E5D063A425196CC93D638` respectively.
Missing or mismatched archive data refuses selection/admission before saving; exported JSON cannot bypass the check.
No startup path installs, patches or recovers an archive.
The shared passive-graphs v4 extractor accepts the pinned original or separately installed combined graph read-only,
retains current ordinary topology and refuses an unexpected derived hash.
The extraction manifest and Gateway data requirements are bumped together.

From the repository root, start only through `scripts/dev/live.ps1` and run:

```text
/ascendancy phrecia Gambler
/ascendancy_points all
/add_passive 56028 62445 17298 25530 63992 15874 12789 5523
/ascendancy
/block
```

The usual point award command is a development shortcut, not Labyrinth acquisition or an expanded native Altar.
Use native passive refunds or `/reset_ascendancy`; allocate/refund/save failure tests exercise the actual Gateway and SQLite.
`AscendancyGatewayIntegrationTests.Gambler`, `GamblerRuntimeTests`, `PlayerElementalAilmentsTests` and the existing
critical/projectile/recovery suites cover node consumers, live removal, multiple choices and normal combat composition.
Local full-suite and native evidence belongs under this worktree's ignored `logs/`; record exact revisions with results.
