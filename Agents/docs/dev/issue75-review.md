# Character-stat correctness review and completion work

The target is all 357 virtual stats consumed by the 387 character-panel rows in issue #75.
The rows also reference 134 nonvirtual stats, for 491 distinct dependencies including visibility gates.
A row is not complete merely because a value is emitted, a source name exists, or a test passes.
Each needs the applicable source composition, formula, units, conditions, lifecycle and native presentation checked.
Where a value represents a gameplay mechanic, its authoritative consumer must agree with the display.

## Starting revisions and evidence

PR #318 and local `issue-75-character-stats` both started at `a7f93e566082a6dccecd349658374a2334e50ab6`.
Fetched upstream `main` was `939b44fec76ba46df762fcd9cdf616fa3d2f8739`, already in the branch's history.
The initial worktree was clean.
Only local changes and commits are authorized; do not push or publish the review or its changes.

The fresh baseline solution build passed with zero warnings/errors.
Core passed 555, Data 18 and Gateway 4,066 tests, with no reported skips.
The imported historical native evidence retains the limitations in [the port record](issue75-port.md).
The target checkout has extracted tables but no `Client/` installation at this review checkpoint.
No native run of these new changes has been performed.

## Repeatable inventory

Run from the repository root in PowerShell:

```powershell
python -X utf8 scripts/dev/audit-character-panel.py
```

The local report is `artifacts/issue75-review/panel-inventory.json`.
It records the Git revision, working-tree status, table hashes, all 491 dependencies and each panel row using them.
Filter `stats` by `virtual: true` for the exact 357-stat target.
The initial table inventory reproduces 387 rows, 491 dependencies and 357 virtual stats.
Every tracked Markdown file is inventoried with its content hash, headings and panel-related references.
The initial scan covers 81 Markdown files; the new review document adds another on subsequent runs.
This scan supports document navigation; it is not a claim that every statement has been verified against code.

Exact C# literal matches are navigation candidates only.
Interpolated names and numeric keys must be traced manually; absence of a literal does not establish missing behavior.
All generated review states remain `unreviewed`, deliberately avoiding automatic certification.
The concrete findings below and the protocol/status documents provide the reviewed evidence.
Generated reports contain extracted metadata and remain ignored; do not commit the tables or client files.

## Confirmed defects addressed

| Finding | Trigger and previous behavior | Change and evidence | Remaining scope |
|---|---|---|---|
| Selected spell penetration omitted supports | Actor penetration 10 plus real level-one Fire/Cold/Lightning Penetration support 20 displayed 10 while the hit consumer used 30 | Recompute three selected aliases from raw actor and own inputs; six before-fix regressions reproduced 30 expected / 10 actual; nine new Gateway cases cover composition, requirements, overrides, unlink/removal and isolation | Native formatting and conditional sources |
| Attack rows omitted attack-only penetration | General 10 + elemental 5 + typed attack 7 + elemental attack 3 displayed 15 instead of 25 | Add attack-only typed/elemental sources to both hand aliases, including the separate chaos source; four before-fix pure regressions reproduced omissions | Off-hand execution and remaining conditions |
| Selected attack rows omitted supports | Heavy Strike with actor 10 plus linked support 20 displayed 10 | Recompute all eight hand aliases while preserving raw local snapshots; six before-fix Gateway regressions reproduced omissions; twelve added pure/Gateway cases cover sources, removal, hand isolation and inactive gear | Native checks and broader selected aliases |
| Combat ignored non-base/general and weapon-local penetration | Elemental Penetration Support and local weapon penetration appeared in panel sums but had no hit effect | Consume raw general elemental/chaos inputs plus the attacking weapon's local snapshot; real support, parsed contact, inactive-weapon and shot snapshot checks | Native verification, off-hand attack selection and conditional sources |
| Elemental prohibition left positive display values | Combat suppressed penetration while the panel retained positive elemental sums | Emit explicit zero for all nine elemental aliases while preserving chaos; check player/own flags, real support composition and restoration | Native presentation; explicit developer overrides intentionally keep their final priority |
| Selected ailment coefficients omitted supports | Actor duration/effect 10 plus real Unbound Ailments 35 still displayed 10 | Recompute seven duration and five effect aliases from raw actor/own inputs; all 24 new lifecycle/override cases reproduced 45 expected/10 actual | Native presentation and full ailment mechanics remain open |

Implementation: `PanelStats.Selected`, `PanelStats.Sums`, and `Program`'s `PanelSourceSnapshot`.
Tests: `SelectedMiscPanelTests`, `SelectedPanelStatCompositionTests`, `HandPenetrationPanelTests` and existing
`PanelStatsTests`/`PenetrationTests`.
The penetration fixes improve eleven display stats and the ailment fix improves twelve coefficients.
They do not certify those stats against every game source or complete their associated gameplay systems.

## Validation of the penetration changes

Local implementation commits are `5052fa3` (selected spell aliases) and `fb80ff9` (attack-hand composition).
The solution build passed with zero warnings and errors.
The final required suites passed Core 555, Data 18 and Gateway 4,087: 4,660 tests with no reported skips.
This is 21 additional Gateway cases relative to the starting revision.
The first full Gateway run after these changes had one timeout in
`ConditionalCriticalGatewayTests.Released_ordinary_bow_reads_life_luck_and_prohibition_at_impact(false)`.
All 28 tests in that class then passed, followed by all 4,087 Gateway tests on the unchanged code.
The timeout remains recorded; this does not prove its cause or establish fault-free background execution.

Reproduce from the repository root:

```powershell
dotnet build GameServer.slnx --artifacts-path artifacts/issue75-review/build
dotnet test tests/GameServer.Core.Tests --no-build --artifacts-path artifacts/issue75-review/build
dotnet test tests/GameServer.Data.Tests --no-build --artifacts-path artifacts/issue75-review/build
dotnet test tests/GameServer.Gateway.Tests --no-build --artifacts-path artifacts/issue75-review/build
```

Local evidence is under `artifacts/issue75-review/results/`.
`selected-penetration-reproduced.trx` and `hand-penetration-reproduced.trx` preserve before-fix failures.
The final successes are `core-penetration-final.trx`, `data-penetration-final.trx` and
`gateway-penetration-recheck.trx`; `gateway-penetration-final.trx` preserves the timeout run.
No native completion claim follows from these tests.

The next penetration batch adds 19 Gateway cases (4,106 total): ten parsed contact cases, four real
Elemental Penetration support/lifecycle cases, two pure source tests, two prohibition projection cases and one
ordinary-shot snapshot case. The focused run passed 58; build passed without warnings/errors; Core 555 and Data 18 passed.
The first full Gateway run passed 4,105 and timed out in
`OrdinaryBowLifecycleTests.Paused_creation_waits_and_a_released_arrow_survives_a_later_player_action_until_one_impact`.
That test polled for a transient projectile and could miss its whole lifetime if its continuation was delayed.
It now holds the actual send gate between packets, witnessing creation before changing the action and releasing
flight. All 15 bow lifecycle/conditional-release checks passed after this test-only correction.
The subsequent full Gateway run passed all 4,106 tests with no reported skips. Combined with Core 555 and Data 18,
the required suites pass 4,679 tests. This validates the current automated behavior; native checks remain pending.
Receipts: `penetration-consumers-final-focused.trx`, `core-penetration-consumers.trx`,
`data-penetration-consumers.trx`, `gateway-penetration-consumers.trx` (failure), `bow-lifecycle-gated.trx`,
and `gateway-penetration-consumers-gated.trx` (full success).
The local commits are `0e91b09` (bow-test synchronization) and `329aa0a` (penetration consumers/prohibitions).

The ailment coefficient batch adds 24 Gateway cases. All 24 failed before the fix with 45 expected / 10 actual,
preserved in `selected-ailment-reproduced.trx`. Afterward, 149 focused checks passed in
`selected-ailment-focused.trx`. The solution build passed without warnings/errors, followed by Core 555,
Data 18 and Gateway 4,130: 4,703 tests with no reported skips. The three final receipts are
`core-selected-ailment.trx`, `data-selected-ailment.trx` and `gateway-selected-ailment.trx`.
The exact inventory confirms that these twelve names are virtual dependencies of the panel, including native
`virtual_frostburn_*` names for the Brittle duration/effect rows. Native checks remain pending.

## Autonomous decisions and shared validation resources

The owner authorized choosing safe reasonable options while unavailable, recording the decision and alternative,
and continuing until an irreversible action is required. Publishing remains prohibited.

- Penetration: supply raw weapon-local sources separately from player/skill inputs. Reusing final hand aliases
  would double-count inputs. Keep existing attack timing while off-hand execution is reviewed separately.
  [Damage module rationale](../protocol/20-damage.md) records this choice and the library's pinned source IDs.
- Prohibition display: send explicit zeros and preserve the existing final developer-override convention.
  Merely hiding rows would retain incorrect selected values; see [skills documentation](../protocol/11-skills.md).
- Bow-test timeout: synchronize on the real send gate instead of increasing timeout limits or accepting a lucky rerun.
  This preserves the test's assertion that a later action occurs before impact; it changes no production flight behavior.
- Ailment coefficients: extend the selected projection only for twelve established duration/effect sums, keeping
  Unbound Ailments' separate more-damage source out. The alternative of extending all ailment rows as sums would
  assume unresolved critical, eligibility and application rules; [the skills module](../protocol/11-skills.md)
  records those limits and the pinned reference evidence. Continue implementing those mechanics separately.
- Native testing: other agents may use the machine's client and fixed server ports. A Gateway from the parent
  checkout's `build/quest-validation/wake-skip-final` was running at this checkpoint. Leave it alone and continue
  independent code/tests while the live slot is occupied. Do not invoke `live.ps1` until the shared resources are
  free: it stops existing dotnet listeners and patched clients. The alternative of restarting those resources
  would interrupt another agent's validation. Recheck immediately before launching, and stop only this task's
  own processes when its test session ends. Preserve logs and existing client/profile data.
  A subsequent check also found an active client under a Claude `custom-assets-spike` worktree; this task
  launched no servers/client and did not stop any of those processes.

## Remaining review and implementation

Continue from the exact inventory, tracing each stat through raw collection, derivation, publication and gameplay.
Prioritize the following confirmed partial systems, then reconcile every remaining inventory entry:

1. Penetration native validation, off-hand attack selection and remaining target/conditional sources.
   Unconditional raw composition and display prohibition now have automated coverage, not native certification.
2. Selected ailment chances, leech and other aliases whose raw support contributions are not recomputed.
   The twelve duration/effect coefficients are corrected; prove other omissions using real sources before expanding
   the selected alias set. In particular, `SupportImpale` grants attack chance 60, but gem projectile/area paths
   currently neither apply new impales nor consume the target's existing ones. Ordinary attacks have a separate
   legacy implementation whose stored-damage roll, critical interaction, expiry and hand/source scope need review.
3. Hit averages/DPS, critical weighting, effective critical chances, off-hand selection and secondary damage.
   `SpellPanelStats` and `AttackPanelStats` still describe pre-critical averages.
4. Damaging-ailment ranges and the corresponding application, duration, critical and stacking mechanics.
   Protocol 20 section 3 documents helpers, but also the missing execution paths.
5. Ward, defence estimates, remaining regeneration/recharge conversions, active leech, special charges,
   minion/brand/trap/mine mechanics and conditional source operators documented in the roadmap.
6. Current-branch native validation of source changes, link/equipment changes, overrides, clearing, transitions and relog.

Do not turn historical client observations, hidden rows, sparse zero packets or source-name matches into completion.
The persistent goal remains active until every required stat is implemented and supported by adequate evidence.
