# Character stat publication across area transfers

## Failure and ownership

Character equipment qualification is published in `Program.InactiveItems`, keyed by character name.
An area connection has its own raw developer inputs and stat-source cache.
The native client keeps its old socket open while the destination connection starts.
A retired source connection could still run recovery and panel publishers and replace the destination's active-item set.
An equipped gem's skill could disappear from the current native Actor until another stat rebuild repaired it.

The common fix belongs to the character-panel prerequisite, PR #318, before ordinary Inquisitor.
The #318 author retains ownership of `issue-75-character-stats`.
The contributor proposal updates `candidate/pr318-main361-admission` from exact owner head
`b001bfa3c6fdf23c07affc0da66252f6858a9731`, containing main
`5d5b5150e1b2e287c642edcaf75a49d075b9b4fe`.
Owner follow-up `cb8ff7ee6b416b9b1d4e05acb510c3d57285ffb8` is merged as well.
It changes only the gem-requirement documentation; production code and tests match the tested owner base.
It supersedes the older admission proposal; the owner already performed that merge independently.
The earlier candidate `f651d0e6` is retained locally as `archive/pr318-admission-f651`.
The new diff contains no Inquisitor gameplay, fixtures, data generation or catalog-path changes.

## Publication and recovery contract

`CharacterStatSources` checks Closed, LeavingArea and RageRetired under the connection's combat lock before
publishing its equipment snapshot or its qualification log.
The function can still calculate local sources for transfer capture and read-only callers.
The lifecycle check occurs after calculation, so a calculation queued before retirement cannot publish afterwards.
The existing `CarryRage` successful-transfer path retires the source even when its Rage balance is empty.

The continuous recovery, player, charge, Fortification and flask workers stop on retirement before socket closure.
Temporary LeavingArea suspends work without permanently stopping the connection.
Recovery resets its elapsed boundary and invalidates cached rates while leaving, allowing a failed transfer to resume
without applying cached regeneration across the loading interval.
Queued Energy Shield refreshes check lifecycle inside their send gate before source aggregation.
Critical-context and Rage/charge refreshes also reject old lifecycles before their preparatory stat reads.
Current connections retain normal requirement removal/restoration and panel publication.

## Automated evidence

The code revision is `8b6a9c76eecd99eb9d2507d01332be4bbd8a5d46`.
`AscendancyGatewayIntegrationTests.StatLifecycle.cs` adds 17 generic cases using real extracted items and gems.
It covers three stale lifecycles, a refresh retired while queued at the send gate, active publication, real
successful-transfer retirement, resource rollback/resumption, and seven continuous publishers.
The same-character fixture gives the earlier connection insufficient raw Dexterity and the current connection enough
to qualify a Short Bow containing Fireball.
Assertions inspect the shared snapshot before a current rebuild could hide a stale write.
The four stale-publication/resource-retirement cases fail on unmodified owner code `b001bfa3` and pass with the fix.

Solution build: zero warnings and errors.
Core 389, Data 34 and Gateway 2578 pass, with no failures or skipped tests and serial Gateway collections.
Ignored evidence: `logs/pr318-lifecycle-{build,core,data,gateway,before,focused}.log` and their TRX results.

## Native evidence and boundaries

Twelve native controls pass on the pinned code revision, launched through `scripts/dev/live.ps1`.
Gateway SHA-256: `dae82555202af09f734443d52b050a39fbdc6f4ca77342605406bb0e4becbf29`.
The initial stopped runtime is restored after validation, which used a separate copied SQLite database.
Raw Dexterity removal removes the bow's Fireball from the current native Actor; restoration restores it.
Four alternating town/Coast transfers retain the qualified skill throughout read-only idle observations.
Two actual Fireball uses each reduce native Mana from 492 to 487 with Mana regeneration prohibited.
The ES regeneration panel receives 900/minute from base 600 and 50% recovery increase, with recharge disabled.
Native ES rises from 110 to 122 in 0.801 seconds, matching 15/second.
Removing the source clears the panel and stops actual ES recovery; relog restores the equipped skill after qualification.
The current skill probe reads both stat containers of the owned native Actor's Fireball, not an old skill object.
Evidence: `logs/pr318-lifecycle-native-20261030-032042/{metadata,results,payment-diagnostic}.json`,
the ignored smoke source `logs/pr318-lifecycle-native.py`, and `logs/pr318-lifecycle-native-gateway.log`.
Earlier diagnostic attempts used an incomplete Mana regeneration control and are not counted as acceptance.
These controls do not re-certify all 387 panel rows or deferred skill execution.

The existing issue #75 row ledger remains an audit ledger: its pending entries do not mean absent panel rows.
The owner's earlier native row-batch evidence retains its original code revision.
Related Behemoth, Whisperer and Harbinger changes remain outside this proposal.
Their downstream merge order must be revalidated against accepted heads; the typed-expiry seam remains open.
