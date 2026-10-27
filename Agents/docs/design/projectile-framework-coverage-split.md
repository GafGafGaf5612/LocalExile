# Projectile framework and skill coverage split

## Intent

Split the existing implementation into separately reviewable code responsibilities and stacked development branches.
The user selected both code and branch separation. The preserved behavioral baseline is `9fe3262`.
Existing adopted skills retain their counts, timing, hit policies, damage and visuals.
The user's correction requires the framework alone to own actual production execution and removes the old engine.
Unresolved native height, appearance and official-parity questions remain unresolved.

## Dependency boundary

1. `feat/projectile-framework`, based on the current upstream `main`, supplies the source-neutral Core simulator
   and generic Gateway hosting contracts, lifecycle, combat mutation and presentation machinery.
2. `feat/projectile-skill-coverage`, based on the framework branch, registers exact supported skills, their profiles,
   resource rules, display bindings and presentation choices, with explicit-recipe admission for gem projectiles.

The original `feat/projectile-runtime` and existing `dev/ci` history are preserved.
No force push, branch deletion, CI reset or unrelated feature import is part of the split.
The final migration records which branches are enabled locally; Git ancestry must preserve existing CI integration.
Branch names and stacked delivery are implementation choices following the user's local branch convention.

## Framework responsibilities

- Core integer time, 30 Hz boundaries, subtick geometry, emissions, continuation, hit admission and actor generations.
- Typed stage primitives: areas, intervals/windows, child emission, forms, lobbed legs and interaction surfaces.
- Generic modifier binding/composition, distinct count consumers and prohibitions; no exact skill-ID exceptions.
- Gateway session pump, accepted-action scheduling, release snapshots, synchronous damage/reward reactions,
  pause/epoch teardown, bounded ordered outbox and ordinary native object lifecycle/height/glide/effect operations.
- A narrow skill-definition registration contract and release/presentation hooks.
  Registration must not require the coverage provider to compile or run.
- Generic damage/offense and presentation payloads; the host must not branch on named skills or their art paths.
- Framework tests use synthetic profiles and actor adapters. They do not need the 142-skill inventory or live client.

Framework-only delivery must compile, pass its Core/Data/Gateway suites and execute ordinary bows and catalog-known
projectile gems through the new runtime. A generic catalog recipe provides basic straight flight and resolved modifiers;
it does not establish special-skill parity. Unknown projectile metadata/damage plans are refused before costs.
Coverage replaces recipes and may require explicit registrations, but cannot replace production routing or flight.
There is no dormant-host mode, legacy flight engine or test-only callback collision implementation.

## Coverage responsibilities

- Exact GrantedEffects IDs, metadata, profile defaults and provisional numeric choices.
- Per-skill consumption of count/chain/split inputs, including conversion ownership.
- Specialized release behavior: Tornado Shot endpoint, Ice Spear second-form crit and Concoction flask payment.
- Flight/form animation clips, standalone hit/terminal effects and other skill-specific native presentation.
- Skill panel final counts, Concoction equipment admission, variant wire/name compatibility and related regressions.
- The current 18 partial gem routes plus ordinary bows; the existing coverage denominator and research provenance.
- All existing actual-skill, linked-support, visual and resource tests, including accumulated user-feedback fixes.

No new plugin loader, dynamic assembly system, general skill DSL or public package is required.
Use the repository's current C# partial classes and typed records/delegates; make the dependency explicit.
Monster sources can reuse the generic Core/host contracts, but live hostile adapters remain future work.

## Code separation rules

`Core/Projectiles` keeps general primitives. Skill-specific constants and exact-ID conditionals belong to the
coverage registry/adapters. Non-projectile melee/area behavior remains separate; no old projectile engine remains.

`ProjectileSession` owns simulation/output lifecycle and generic visual rendering. A definition/presentation payload
chooses birth/form clips and event effects, replacing its current Ice Spear/Spark/Frostbolt/LA/TS/EoW/bottle branches.

Release preparation captures common offense/geometry once, then invokes an optional definition hook.
The hook may specialize a profile/payload and pay skill-specific resources at the existing accepted release point.
It must preserve XP-before-Monsters ordering and must not await network output under a combat lock.
The framework supports per-initial-projectile payload snapshots without knowing why a skill needs them.

Generic stat binding is shared by ordinary-arrow and skill profiles. Coverage declares consumed inputs or applies
profile-specific adjustments explicitly; extra count must still be consumed once.
Preserve existing `GatewayProjectileProfiles` test-facing behavior through an adapter if needed.

## Verification and migration

- Preserve original refs before changes; work only in the already isolated worktree and new `feat/**` branches.
- Record the full baseline and exact upstream base in the implementation plan.
- Run meaningful contract/boundary tests before implementation, then preserve all existing mechanics regressions.
- Build the standalone framework and run all three test suites in that checkout.
- Build the stacked coverage result and run all three suites; compare its enabled coverage against the baseline.
- Check that framework production additions do not reference the coverage provider or contain moved named-skill logic.
- Review both diffs independently, including actual input under held output, pause/transfer and resource ordering.
- Update the existing design/runtime/status documentation to describe the dependency and activation boundaries.
- Notify the authorized CI thread only with a concrete validated integration path and exact commits.
  CI continues using its existing deployment until that handoff; the user alone operates the client.

The framework/coverage split does not itself establish new native visual parity or expand supported skills.
