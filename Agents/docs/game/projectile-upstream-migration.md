# Upstream projectile migration: milestone one

This layer migrates executable upstream `79cc0337b331b69e49b3c35ea3e34c67e7d547ce` behavior onto the
[sole projectile framework](projectile-framework.md). It establishes a refactoring baseline for local validation.
It does not enable the previously researched extended skill recipes or establish official-game parity.
The [three-layer plan](../design/projectile-milestone-one.md) records the delivery boundary.

## Code responsibilities

- Core `Projectiles/` owns time, movement, collision, continuation, phase-specific hit admission and area primitives.
- Gateway `Projectiles/` owns gem/bow release, stat/damage snapshots, synchronous combat and ordered native output.
- Gateway `Projectiles/Upstream/` selects upstream count/pierce/return policies and Fireball/KB areas/art.
  Its registration owns the catalog fallback and an optional extension hook. A future layer 3 port can override
  recipes through that hook; it must not copy the entry points or install another simulator.

Without layer 2, the framework remains executable with basic catalog flight. Milestone one always integrates
framework and upstream migration together, preserving special behaviors already present upstream.

## Migrated policies

| Family | Enemy contact | Terrain | Empty maximum range | Returning |
|---|---|---|---|---|
| Fireball metadata family | Direct hit plus one centered area excluding direct victim; piercing contacts retain area | One area | No explosion | Outbound area commits immediately; ordinary contact art waits until return completion, while pierced contact art is immediate. Return hits are direct only |
| KineticBlast, KineticBlastAltX, KineticBlastAltY | Direct hit plus one centered area excluding direct victim | One area | One area | Return contact retains direct plus excluded-victim area |
| Other catalog projectile gems | Basic flight with resolved generic modifiers | Normal termination | Normal termination | Existing named prohibitions and collision-only exception retained |

KB follows executable upstream code despite the old comment claiming no direct hit. All three KB IDs share this
one centered explosion; no offset cluster pattern or extra-projectile-to-explosion conversion is added here.
Fireball uses existing art variation and 1.5-second effects; KB uses the existing cluster burst and .35-second effects.
Radius uses the existing supported `SkillRadius` calculation, applying player/final skill area inputs once.

The catalog migration selects upstream capped 20-projectile fans with a fixed 10-degree neighbor gap, overridden
by valid `projectile_spread_radius` values. It sums the four upstream pierce inputs. Return selects partial per-use
chance, modified speed with an actual minimum of one cell/second, finite/unlimited contact budget, exclusion of
the turnaround victim, a wall-free path and 150-cell overshoot beyond the live caster anchor. A spent contact
budget suppresses further contacts while completing the flight. Upstream piercing uses this rule outbound too:
a finite piercing shot spends at most `1 + pierce` actor attempts, then travels to its endpoint; after any piercing
actor contact it suppresses the later wall/range area. A no-hit piercing shot retains its usual terminal policy.
The migration removes the generic ten-second lifetime cap so slow, range-bounded flights can finish.
Returned direct-hit quota is shared across the
volley, including KB; all migrated outbound direct quotas are per projectile, including Fireball.
Upstream creates that dictionary inside each `angles.Select` call; it does not enforce batch-wide outbound
shotgun prevention. A shared-return-quota-denied contact is skipped
without consuming budget or generating an explosion.

Cannot-return policies: Spark and its two variants, RainOfArrows, BlastRain, SplittingSteel, VenomGyre, WinterOrb,
ForbiddenRite, Cremation, RollingMagma, ToxicRain and PoisonousConcoction. FreezingPulse returns only from collision.
These restrictions do not claim that the rest of each skill is implemented.

## Intentional boundaries and remaining differences

The framework replaces callback timing with integer simulation time and a 30 Hz host. Collision follows moving
actors, and offense is captured at release rather than re-reading mutable gear at each contact.
Attack gems use the shared adapter's accuracy/evasion check;
the old callback omitted this check for gem attacks. Thus KB can now evade, like ordinary bow attacks.
The shared adapter also consumes/applies Impale on gem attack hits; the old gem callback omitted that path.
Ordered presentation
never blocks combat on the send gate. Generic continuation primitives can express more than the old callback code;
this milestone does not promise new skill-specific behavior from them.
Floating geometry replaces integer-rounded origins and aims, with a different close-aim muzzle rule; the default
120-cell flight starts at the muzzle (about 1.5 cells beyond the old endpoint), and a zero-length aim uses the
common direction fallback. These are explicit migration differences to check locally, not exact upstream claims.
Old development-only overrides for spread, muzzle, range, hit radius, forced return, impact art and return distance
are not acceptance controls; gameplay stat inputs determine those policies. The speed override remains supported.

Height/surface geometry and native visual fidelity remain partly verified. Other skills do not receive the old
extended delivery's forms, pulses, child emissions, bottle costs, panel/variant changes or art specializations.
Ordinary bow attacks use the common basic profile and release path. Monster-specific hostile adapters remain future
work, while the Core owner/team model remains reusable.

## Local acceptance checklist

The user operates the client. Use the migrated stack, not the frozen full coverage branch, for these checks.

- Fireball: shoot a lone enemy, a group, a wall and empty space. Check damage on the direct target and nearby enemies;
  expect contact explosions, no ordinary maximum-range burst, and no direct-plus-area double hit on the victim.
- Fireball with extra projectiles, pierce and return: check the fixed fan, successive contacts and return damage;
  distinguish delayed return art from the already committed outbound explosion damage.
- KB and both variants: repeat enemy/group/wall/empty checks. Expect the upstream single centered burst rather than
  the expanded delivery's researched cluster pattern. Additional projectiles remain additional primary shots.
- Returning KB: check shared direct quota on return and continued flight beyond the caster after budget exhaustion.
- Ordinary bow and basic catalog gem: check attack-move release, hit damage, pause/resume and area transfer retirement.
- Compare logs for unavailable metadata, exceptions and stale objects; report visual height issues separately from
  damage/contact failures. Automated held-output tests verify gameplay commits before native sends drain.

The extended delivery is preserved at `f41993dc626193eb4bd0dc5a9f4cc8d535b4f5d2` on
`feat/projectile-skill-coverage`. Its future port is deliberately deferred. Keep that ref and the old runtime/split
refs; switch local integration with the normal transition merge instead of resetting integration history.
