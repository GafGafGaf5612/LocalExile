# Projectile framework boundary

The generic implementation is in `src/GameServer.Core/Projectiles/` and
`src/GameServer.Gateway/Projectiles/`. Concrete registrations live in the optional
`Projectiles/Skills/` provider, delivered by the dependent skill-coverage branch.

`feat/projectile-framework` is based on upstream main. It supplies integer-time simulation,
30 Hz hosting, continuation and hit-admission primitives, resolved-stat binding, synchronous
combat/reward mutations and ordered native presentation. It owns the production gem/bow entry points.
Without the provider, catalog-recognized projectile gems use a basic straight-flight recipe with
resolved counts and continuations; ordinary bows use the same runtime. This approximation does not
implement a special skill's explosions, pulses, children, forms or resource rules. Missing metadata
or an unsupported damage plan is refused before costs, without falling through to an area hit.

`feat/projectile-skill-coverage` is stacked on that framework. It registers exact effects and
replaces basic recipes for adopted gems and requires explicit recipes for gem projectiles.
Skill recipes, numeric calibration, native art and resource rules belong there. It does not
replace the production entrance or install a flight simulator.
The split preserves the previous behavior; it does not establish official visual parity.

## Extension contract

- `ProjectileSkillDefinition` supplies metadata, profile resolution, optional terrain admission,
  optional windup cancellation, release specialization and presentation selection.
  Paid attacks normally survive Move; a recipe with release-time resources can require the original action.
  Spells, cooldown tickets, death, recovery and epoch restrictions retain their common admission checks.
- The common release captures position, damage, stats, critical inputs and speed, then invokes
  specialization once inside the existing simulation commit. Specialization can supply frozen
  per-initial-projectile payloads. Continuation children inherit those snapshots.
- `GatewayProjectileModifiers` binds generic counts, continuation grants and prohibitions.
  Recipes explicitly declare consumed chain or intrinsic split inputs; count conversion runs once.
- `ProjectilePresentation` chooses flight mesh/clip, second-form clip and event effects.
  Synthetic or future non-gem sources may supply it directly through `ProjectileOffense`.
  The host renders native object lifecycle, terrain height, arcs, redirects and effect cleanup.
- Gameplay commits under XP-before-Monsters locking. Presentation queues never await network
  output under that lock; pause, transfer, retirement and output overflow retain existing guards.

This is an in-process typed boundary, with no dynamic plugin loader or new assembly.
`ProjectileRuntime` is the only flight/collision engine. The old callback flight, collision
estimator and spread/return policy engines have been removed; tests use the same Core runtime
and Gateway session as production.
Core actor/team contracts are source-neutral. The current Gateway damage adapter targets monsters;
live hostile projectile adapters are still future work.

## Local branch transition

The old `feat/projectile-runtime` ref stays available. Existing CI already includes that history.
A one-time equal-tree ancestry bridge already joined the refactored old branch to the clean stack.
Subsequent corrections use normal merges and retain unrelated enabled features; do not reset CI
or merge CI history into the clean stack.
