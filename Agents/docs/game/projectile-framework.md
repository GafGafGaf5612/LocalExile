# Projectile framework boundary

The generic implementation is in `src/GameServer.Core/Projectiles/` and
`src/GameServer.Gateway/Projectiles/`. Concrete registrations live in the optional
`Projectiles/Skills/` provider, delivered by the dependent skill-coverage branch.

`feat/projectile-framework` is based on upstream main. It supplies integer-time simulation,
30 Hz hosting, continuation and hit-admission primitives, resolved-stat binding, synchronous
combat/reward mutations and ordered native presentation. Without the provider the registry is
empty and upstream skill execution remains active. Synthetic tests drive the host directly.

`feat/projectile-skill-coverage` is stacked on that framework. It registers exact effects and
activates adopted gem and ordinary-bow routes, with the current rejection policy for unadopted
gem projectiles. Skill recipes, numeric calibration, native art and resource rules belong there.
The split preserves the previous behavior; it does not establish official visual parity.

## Extension contract

- `ProjectileSkillDefinition` supplies metadata, profile resolution, optional terrain admission,
  optional release specialization and optional presentation selection.
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
Core actor/team contracts are source-neutral. The current Gateway damage adapter targets monsters;
live hostile projectile adapters are still future work.

## Local branch transition

The old `feat/projectile-runtime` ref stays available. Existing CI already includes that history.
A one-time equal-tree ancestry bridge joins the refactored old branch to the clean stack, so CI
can merge only the structural delta while retaining unrelated enabled features. Merge that bridge
before subsequent framework/coverage work; do not reset CI or merge CI history into the clean stack.
