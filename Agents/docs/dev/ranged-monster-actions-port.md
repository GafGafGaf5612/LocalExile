# Standalone ranged attacks and monster-action port

This branch ports LocalExile `67deb2a` onto `main` at `939b44f`.
The source checkpoint remains `83ead2e`; other job branches and the combined PoB/stats branch are separate.

## Scope

Ordinary bows and wands share projectile release, live collision and hit handling.
Wand release uses the accepted input sequence and the character ACT's main-hand animation pool.
The projectile plays its native impact animation before removal.
Stationary melee contacts no longer receive a walk-in offset.
Animation-contact extraction includes the native wand pools and bumps the manifest entry to version 2.

Monster movement/attacks use bounded game-time cadence and per-monster action generations.
Native TakeHit recovery cancels stale ordinary movement/attacks, while boss stages keep their own lifecycle.
Queued echoes and damage commit through the actual send gate; pause freezes their game time.
Invalidating incoming hits also retires ordinary monster generations, preventing a failed transfer from reviving them.
Nonflinching hits return without waiting on a paused animation notification.

## Upstream and source integration

The source's later Rage/retired-connection guards were carried into the ranged path.
The renamed OrdinaryRangedShot retains the destination's existing Rage and conditional-critical calculations.
Merged onto main's projectile framework, the legacy flight loop is gone: ordinary bows and wands release through the
shared runtime (`Projectiles/Program.ProjectileRanged.cs`), scheduled at the weapon contact before the action echo, and
the wand impact clip is a runtime presentation whose removal lifetime runs on the same game clock.
Monster contact keeps main's Gambler, ailment, Antiquarian, Hardcore and grace-period rules inside the action gate, and
the AI planner keeps main's surround cells and blocker frames on the 50 ms cadence.
Monster contact retains the current destination's injected incoming RNG, recent-hit armour decisions,
chaos-hit armour, resistance/immunity processing, Fortification, Energy Shield, block gains and Rage gains.
Charge-on-hit accounting remains single rather than duplicating the pre-existing contact update.

Existing Gateway fixtures now call the new action-generation attack interface.
Their original damage/block/ES expectations remain intact.
The older pause fixture now observes the action echo after resume, consistent with the new send-gate model.
A queued-flinch test measures generation changes relative to the transfer's own invalidation.
No private Ruthless catalog, PoB importer, character-stat port or league presentation was folded into this branch.

## Validation and limits

The solution builds with zero warnings/errors.
Before the merge onto main, the standalone port passed Core 525, Data 18 and Gateway 3,368 (429 focused bow/wand,
monster lifecycle, recent-hit and Rage cases; 38 chaos-armour cases after adapting the last reflection-based helper).
After the merge (main's trimmed suites, projectile framework and surround AI): Core 353, Data 34 and Gateway 1,335
pass with serialized collections. Under the repository's unlimited test parallelism two wall-clock-bound tests can
flake and pass again in isolation: `Chill_slows_attack_and_spell_timers_once_in_both_modes` (main's, its chill expires
while a slow connection setup consumes the clock) and
`A_running_enemy_worker_sends_its_first_chase_promptly_after_the_player_enters_aggro_range` (300 ms bound under load).
Four Python animation-extraction tests pass.
The first focused/full attempts are retained in local artifacts alongside their corrections.
The final Gateway runs serialize test collections through a local build-output runner file.

Native screenshots/traces described in the imported protocol/status notes are historical LocalExile evidence.
Their raw artifact paths resolve in the preserved source checkout.
No native client was launched for this standalone port and no existing server runtime was replaced.
Fresh native validation and combinations with other job branches remain separate work.

From the repository root, with extracted catalogs available under `data/`:

```powershell
dotnet build GameServer.slnx
'{"parallelizeTestCollections":false,"maxParallelThreads":1}' |
    Set-Content tests/GameServer.Gateway.Tests/bin/Debug/net10.0/xunit.runner.json
dotnet test tests/GameServer.Core.Tests --no-build
dotnet test tests/GameServer.Data.Tests --no-build
dotnet test tests/GameServer.Gateway.Tests --no-build
python -m unittest discover -s scripts/dev -p test_anim_contacts.py -v
```

The test runner file is local build output, not a repository-wide test configuration change.
Production data stays relative to the checkout's `data/`; the shared test initializer is unchanged.
