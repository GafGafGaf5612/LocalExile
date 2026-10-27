# Projectile milestone one: framework and upstream behavior migration

The user replaces the earlier two-layer delivery with three stacked layers. Milestone one contains layers 1 and 2
only, for local client validation. Previously researched and playtested skill additions remain preserved in layer 3.
This request authorizes implementation and local integration; the user operates the client.

## Delivery boundary

1. `feat/projectile-framework`: one production simulator and Gateway lifecycle, replacing upstream callback flight.
   Generic profiles, collision, counts, continuations, hit admission, damage/reward commits and presentation live here.
   No named skill recipe or legacy second engine. Generic stage options may express existing upstream policies.
2. `feat/projectile-upstream-coverage`, based on framework: migrate actual special behavior present in upstream
   `79cc0337b331b69e49b3c35ea3e34c67e7d547ce`, including Fireball and all three Kinetic Blast effect IDs.
   Other catalog-known skills keep basic flight and resolved generic stats. Migrate existing named return restrictions.
   This layer must not acquire the researched KB cluster pattern, Clustering count conversion, new skill forms,
   pulses, child emissions, bottle payment or skill-panel/variant changes from the extended delivery.
3. `feat/projectile-skill-coverage`: freeze the complete tested delivery at `f41993dc626193eb4bd0dc5a9f4cc8d535b4f5d2`.
   Preserve all previous recipes, tests and user-feedback fixes. Porting these onto layer 2 is future work,
   not part of this milestone. The branch is deferred and disabled in CI.

Core stage primitives may remain reusable without activating their researched skill recipes. The same engine serves
all three layers. Layer 2 owns the registration entry and an optional extension registration hook; the future layer 3 port will supply
only that hook, so it can replace recipes without copying the registration entry or adding a runtime.

## Upstream migration inventory

Use executable upstream code as the baseline, not comments claiming behavior that the code does not implement.

- Fireball: enemy/terrain contact explosion, existing area scaling and skill-art variation, no double hit of the
  collided target. No ordinary maximum-range explosion. Preserve contact effects when piercing and return policy.
  Returning delays ordinary contact art only (pierced-contact art is immediate): outbound contact area still commits,
  return contacts deal direct damage without area,
  and delayed explosion art appears after the return finishes.
- KineticBlast, KineticBlastAltX and KineticBlastAltY: the same upstream direct-contact hit and one centered area
  excluding the collided target; terrain and empty-flight termination also produce the existing area/art.
  Do not turn this into researched multiple offset explosions or convert extra projectiles into explosions.
  Return contacts retain the same one-area/excluded-contact-victim policy.
- Named cannot-return and collision-only-return policies already in upstream belong in this layer, independently
  of whether a skill's other special behavior has been implemented. Missing catalog metadata does not enable it.
- Existing generic projectile count, speed, pierce and return inputs remain framework responsibilities.
  New support for upstream policies must be source-neutral and configurable, with compatibility tests.

The migration wrapper preserves upstream's capped 20-projectile fixed-gap fan and additive pierce inputs.
The extended layer retains its researched count/aim policies. Generic controls must also express the existing
partial return roll, return speed, finite/unlimited return contact budget, turnaround-target exclusion,
wall-free return path and 150-cell overshoot; named exceptions remain in layer 2.
Outbound piercing preserves the upstream attempt budget, subsequent endpoint travel and suppression of an extra
terminal area after contact. KB uses independent outbound direct quotas and shared volley quota on return.
Default values preserve the current extended recipes unless a layer explicitly requests the upstream policy.

This milestone establishes a migration baseline for local verification, not official-game parity. Height/surface
geometry and previously documented visual uncertainty remain open. Any intentional migration difference is recorded
in the guide and acceptance checklist; never silently claim exact upstream or official equivalence.

## Integration and preservation

Preserve `feat/projectile-runtime`, the old split prep and full coverage heads. Do not reset/rewrite CI or import CI
history into clean feature branches. Existing CI already contains extended coverage, so prepare a normal migration
branch joining that history to the exact milestone-one tree. Verify tree identity and preflight it against current CI;
the transition must remove deferred production adapters while preserving unrelated enabled features.
Update the excluded local handbook to enable only framework and upstream coverage and retain layer 3 as deferred.

## Implementation plan

- [x] Audit upstream named behavior and document migration assertions, including direct/excluded-target damage.
- [x] Add narrowly required generic recipe controls and meaningful synthetic tests; run RED before implementation.
- [x] Build and test standalone framework; commit it without any named-skill provider.
- [x] Create minimal upstream coverage, registration extension hook, recipe/real-input/wire tests and a native checklist.
      Verify Fireball/KB, all KB variants, multiple projectiles, pierce/return, held output and absence of extended recipes.
- [x] Verify the complete full-coverage ref is unchanged; record the future extension-hook migration instead of doing it now.
- [x] Validate framework and the minimal upstream stack with Release solution builds and Core/Data/Gateway suites,
      documentation and diff checks.
- [ ] Create and validate the ordinary milestone transition, then ask the authorized CI chat to integrate layers 1+2,
      check all enabled features/data, update services with `live.ps1 -NoClient`, and report the deployed revision.

Review focus: one simulator; no deferred adapters in milestone one; actual upstream KB semantics; quotas and
continuation/return trigger ordering; source-neutral controls; XP-before-Monsters; no output waits in combat locks;
preserved full-coverage branch and safe removal from existing CI.
