# Single projectile runtime correction

The [three-layer milestone plan](projectile-milestone-one.md) supersedes this earlier delivery plan.
Existing upstream return policies are now migration requirements; full researched coverage remains deferred.

> Execute continuously using the existing isolated worktree; the user's split correction authorizes implementation.

## Goal and revised boundary

The previous split left a dormant framework and retained a legacy simulator for production or tests.
The user rejected that boundary because it leaves two projectile implementations. Framework must own the
production gem/bow entrance and the only flight, collision, hit, return and output lifecycle implementation.
Coverage supplies exact skill recipes, admission restrictions, presentation and resource specialization only.

Framework-only uses catalog projectile metadata plus a generic straight-flight profile and resolved count,
speed, pierce/fork/chain/split/return stats. This is explicitly a basic approximation, not complete behavior for
special skills. Missing projectile metadata or a non-hit damage plan is refused before costs; Projectile-tagged
gems must never fall through to generic area damage. Non-projectile area/melee behavior remains its own path.
Coverage retains its current explicit-recipe admission policy and all existing adopted behavior, including
refusing unknown variants rather than silently approximating them. A provider may replace the recipe, never
the simulator. Ordinary bow is generic framework behavior, independent of the named PlayerMelee recipe.

Do not expand gameplay parity in this correction. Partial return chance, return overshoot, official numeric
calibration and native heights retain their documented limitations. Do not retain obsolete return exception
lists, debug-only policy engines or a second spread/collision algorithm merely to keep their old tests.

## Constraints

- Main is `79cc0337b331b69e49b3c35ea3e34c67e7d547ce`; previous framework `4d80081`, coverage `8c7c36a`.
- Preserve all refs and normal merge ancestry; no reset/rewrite, pushes or remote PRs.
- Framework additions must contain no exact skill IDs or art-selection exceptions.
- XP-before-Monsters locking, instance-specific admission, own-aura retirement and chronological simulation
  under blocked output remain mandatory. All network awaits stay outside combat locks.
- No client operation. Dot-source `E:/coding/repos/LocalExile/.env.dev.ps1` before each dotnet command;
  serialize builds. Release solution build and Core/Data/Gateway suites are required for each layer.

## Implementation tasks

1. Prove the framework gap with actual ground-input gem/bow regressions under a held native send gate.
   Transfer common production routing from coverage into framework; provide generic catalog definition resolution
   and generic ordinary-bow scheduling. Remove the provider activation switch as a routing decision; its strict
   policy only controls whether an unregistered skill may use a basic catalog recipe.
2. Remove `FlyProjectile`, `ProjectileFlight`, obsolete spread helpers and unused visual commit guards.
   Retain only the returning-damage multiplier helper. Migrate old behavioral coverage to `ProjectileRuntime`
   and `ProjectileSession`, preserving moving-target, evasion, wall, pierce, return, pause, transfer, impale,
   actor-scale and output assertions. Update old tests whose assumptions tied simulation to socket publication.
3. Validate and commit framework, then merge it normally into coverage. Remove the old coverage-owned ordinary-bow
   adapter and shared production-routing differences. Keep all skill recipes, variant policy and resource behavior.
   Validate coverage; verify the generic code is identical across both heads and no legacy engine symbol remains.
4. Update boundary/runtime/status documentation and get independent review. Preflight the exact integration
   against current dev/ci, then notify the authorized CI chat with concrete heads and merge order. CI updates the
   excluded handbook and services through `live.ps1 -NoClient`; all existing enabled features must survive.

## Review focus

- Catalog metadata is not evidence that a skill's special behavior is implemented; strict coverage policy remains.
- Every production projectile use starts chronological simulation before any held send can stall it.
- A Projectile-tagged gem without metadata cannot spend resources or become an area hit.
- Removing legacy tests must preserve their meaningful assertions on the sole runtime, not hide regressions.
- Framework can actually play basic gem/bow projectiles alone; coverage cannot install another flight loop.

## Decisions

The user correction supersedes the earlier dormant-host ruling in the split design and plan. The implementation
uses the repository's existing typed partial registration boundary; no dynamic plugin loader or separate assembly.
Default basic-profile geometry is the existing staged runtime's adopted defaults, not the old fixed-angle fan.
