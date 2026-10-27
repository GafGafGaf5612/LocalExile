# Projectile Framework and Coverage Split Implementation Plan

The [three-layer milestone plan](projectile-milestone-one.md) supersedes this earlier delivery plan.
Existing upstream return policies are now migration requirements; full researched coverage remains deferred.

> Execute continuously with `superpowers:executing-plans`; code and stacked branch separation are user-authorized.

**Goal:** Deliver a standalone generic framework and a dependent skill-coverage branch without losing existing fixes.

**Architecture:** Keep Core source-neutral, extract generic Gateway hosting contracts, and register skill-specific
release/presentation behavior through an optional partial-class provider. Framework owns production skill routing.
The [single-runtime correction](projectile-single-runtime-plan.md) supersedes the earlier dormant-host steps below.

**Tech stack:** C# / .NET 10, existing partial classes, typed records/delegates, xUnit and local Git branches.

**Spec:** [Split design](projectile-framework-coverage-split.md).

## Global constraints

- Baseline: `9fe3262668b339589bb334d8b9dd4218b11ab421`; initial upstream base: `20b02f85a9ab33f846dd937a28f1d71c30c4d9cc`.
- Integration base advanced to `79cc0337b331b69e49b3c35ea3e34c67e7d547ce` during CI handoff.
  That synchronization and the one-time equal-tree bridge are complete; corrections use normal stack merges.
- Existing isolated worktree; no client operation, remote push, CI reset or unrelated feature import.
- Serialize builds; dot-source the main checkout's `.env.dev.ps1` before each dotnet command.
- Keep all current mechanics/presentation tests and uncertain-parity qualifications.
- Preserve XP-before-Monsters locking, ordered output, resource timing and per-projectile payload identity.

## Review focus

1. Framework builds without the skill provider, coverage inventory, Concoction helpers or exact-skill tests.
2. Full registration preserves every currently supported effect and rejects the same unsupported routes.
3. Presentation hooks preserve default-versus-MTX metadata/animation selection and event end-reason filters.
4. Count conversions and release specialization execute once, including partial Concoction volleys.
5. The initial split preserved the validated prep tree; later corrections retain normal CI ancestry and feature history.

## Initial split tasks (completed; framework routing revised below)

### 1. Record and refactor the full tree

- [x] Create `feat/projectile-runtime-split-prep` from the preserved baseline; keep original feature unchanged.
- [x] Add framework registration, release-context and presentation contracts under Gateway `Projectiles/`.
  An optional `RegisterProjectileSkillDefinitions` partial method lets the host compile without coverage.
- [x] Extract `GatewayProjectileModifiers.Resolve(profile, get, attack, chainInputConsumed, intrinsicSplitCount)`.
  Named skills and adopted constants remain in `Projectiles/Skills/GatewayProjectileProfiles.cs`.
- [x] Replace host named-skill branches with definition release hooks and presentation payload callbacks.
  Keep the existing profile facade usable by actual-skill regressions.
- [x] Move Concoction and the concrete skill provider beneath `Projectiles/Skills/`.
- [x] Add boundary/contract tests and preserve native presentation, stat/count and resource regressions.
- [x] Run targeted checks, full build/suites and independent review; commit the complete refactor as prep `P`.

### 2. Construct and verify the standalone framework

- [x] Create `feat/projectile-framework` from the pinned upstream base.
- [x] Populate Core/runtime primitives, generic host/contracts and matching standalone tests from `P`.
  Do not import skill registrations or their catalog; the correction makes this host the sole production runtime.
- [x] Keep common combat lifecycle/reaction changes and route production gem/bow uses through the host.
  Framework tests exercise real input and synthetic profiles; remove legacy flight and test-only collision engines.
- [x] Include generic native object/height support and the minimal shared stat projection/lifecycle changes required.
- [x] Add a standalone guide/status entry and verify all local documentation links.
- [x] Run full build/Core/Data/Gateway suites and review its diff against upstream; commit framework `F`.

### 3. Create coverage and preserve local integration ancestry

- [x] Create `feat/projectile-skill-coverage` from `F` and restore the complete tracked tree from prep `P`.
  Commit the coverage-only difference as `C` and verify `P^{tree} == C^{tree}`.
- [x] Run full build/suites in coverage and review its diff against the framework.
- [x] Merge `C` normally into prep `P` to make bridge `B`, preserving the identical full tree and both ancestries.
  Never merge CI's unrelated feature history into either clean delivery branch.
- [x] Preflight the bridge merge with current `dev/ci`; resolve source compatibility before handing off.
- [x] Notify CI to merge `B` first, then verify that both clean branches and all other enabled features are contained.
- [x] After that transition, enable framework/coverage in the excluded local handbook and retire the monolithic
  runtime branch from active development. Preserve original/prep refs for recovery.

## Decisions and progress

Superseded ruling: the standalone Gateway host originally remained dormant until coverage activation.
The user's correction rejects the duplicate-engine outcome; framework now owns the sole production runtime,
including generic catalog recipes and ordinary bows. Coverage supplies recipes and admission policy only.

Ruling: use an equal-tree local ancestry bridge for the one-time CI transition. The clean stack stays upstream-based;
CI already contains the old feature, so direct independent re-addition would risk avoidable add/add conflicts.

Builder review found no unresolved user decision; remaining C# contract details are implementation choices.

Upstream compatibility preserves instance-aware gem requirements and own-aura reconciliation while retaining
projectile simulation time and owner-retirement guards. Coverage flask movement must refresh Mageblood state
at its existing synchronous inventory commit and publish the refresh after the inventory batch.

Validation against `79cc033`: standalone framework build has zero warnings/errors and Core/Data/Gateway
pass 555/18/3106 tests; stacked coverage build has zero warnings/errors and passes 563/18/3243.
The 118-case combined admission/aura/Concoction/Mageblood check passes. Independent compatibility review
found no important remaining issue. Native visual parity remains subject to the existing user retest matrix.
The excluded local handbook switches enabled branches only after CI successfully consumes the refreshed bridge.
