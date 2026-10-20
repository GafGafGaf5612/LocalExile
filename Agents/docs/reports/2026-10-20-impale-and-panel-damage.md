# 2026-10-20 — Impale, and the Offence panel's damage rows

Issue #75 asks for the character panel's 387 rows. Two of them were reachable without new game data, and one of them
turned out to need a mechanic rather than a number; this is what was built for both.

## The problem

The panel's **Chance to Impale** row is a sum of stat forms (`PanelStats`, [11](../protocol/11-skills.md) §14): set
`attacks_impale_on_hit_%_chance` and the row moved. Nothing else did. `grep -i impale` across `src/` found the stat only
in `PanelStats.cs`, so the number had no consumer — the same shape as power charges: a menu value with no mechanic
behind it. The Offence tab's per-hand damage rows were blank for the opposite reason: nothing sent
`main_hand_minimum/maximum_*_damage`, and nothing sent `skill_uses_main_hand`, the flag the client gates those rows on.

## Impale

The game's model, in `ImpaleState` (`src/GameServer.Gateway/Impale.cs`):

- A hit that rolls impale stores **10% of its physical damage** on the target, up to **five stacks**.
- Every later physical hit on that target takes the stored damage again as extra physical, and each stack lasts
  **five hits**, so five impales add half the impaling hit's damage to each swing — the game's "10% more damage per
  impale" while they last.
- Only the physical part is stored; impale carries no elemental or chaos damage.
- The roll reads the same `impale_on_hit_%_chance` stat the panel row shows, so `/set_stat` moves both and there is no
  second number to keep in sync.

In the attack path (`Program.cs`, `Attack`), order matters and is the reason for the comments there: the target's
existing stack total is added to the packet **before** the damage roll, so the bonus goes through the same conversion,
armour and mitigation as the hit it rides on; `AfterHit()` spends one hit of every stack **before** `Apply()`, or a new
stack would lose one of its five hits on the swing that created it; and the stack count and stored total are read
**before** the hit is applied, because a killing blow clears them (`Clear()`), which is what made the first version log
`impaled (0 stack(s))` on a kill.

The stored amount is 10% of the swing's physical **maximum**, not of the damage the roll produced, so it is steady for a
given weapon — the damage numbers in the log already include the bonus.

## The damage rows

`PanelStats.HandDamage` turns the hit the server actually swings with — `Skills.UnarmedFor(ClassRow).HitDamage(...)`,
converted — into `main_hand_minimum_total_damage`, `main_hand_maximum_total_damage` and the per-type
`main_hand_minimum/maximum_{physical,fire,cold,lightning,chaos}_damage`, rounding down because the client shows whole
numbers. An empty packet adds no rows, which leaves them blank as the game does for an attack that cannot happen.
`CharacterPlayerStats` also sends `skill_uses_main_hand = 1`; the numbers alone leave the rows blank. Only the main hand
is filled, since the server attacks with it.

`PanelStats` gains one row: `impale_on_hit_%_chance` from `attacks_impale_on_hit_%_chance`, which is what lets the
attack's `Stats.Key("impale_on_hit_%_chance")` see a value a player set.

## Also in this change

`/spawn_mob` read a bare trailing number as a rarity, so `/spawn_mob <monster> 100` tried to parse `100` as
`normal|magic|rare|unique`. A trailing number between 1 and 100 is now the level, which is what the syntax's `@level`
means anyway.

Upstream's 0f6dd54 committed `Agents/Status.md` with unresolved conflict markers; this change resolves them (keeping the
armour/evasion row and the newer monster and item rows) and adds the impale and damage-row entries.

## Verification

Ten new tests (4 in `PanelStatsTests.cs`, 6 in `ImpaleTests.cs`): the stored share, the five-hit stack lifetime, the
five-stack cap and its refusal, no-physical refusal, clearing, the damage rows per type and in total, the empty packet,
and the Chance to Impale sum. Suite: **481 pass, 0 fail** (Data 15, Core 300, Gateway 166), including the pre-existing
guard that every stat name in `PanelStats` exists in `Stats.dat`.

Live, in the client, on the 23:24 build — `/set_stat attacks_impale_on_hit_%_chance 100`, then five swings at one
monster:

```
0x304c hit for 5, impaled (1 stack(s), 0.5 stored),  life 23/28
0x304c hit for 5, impale +0.5 (2 stack(s)), impaled (2 stack(s), 1 stored),   life 18/28
0x304c hit for 3, impale +1   (3 stack(s)), impaled (3 stack(s), 1.5 stored), life 15/28
0x304c hit for 6, impale +1.5 (4 stack(s)), impaled (4 stack(s), 2 stored),   life 9/28
0x304c hit for 9 (critical), impale +2 (5 stack(s)), impaled (5 stack(s), 2.6 stored), life 0/28 (dies)
```

A fresh target reopens at one stack (`0x3023`, `0x301f` in the same log), and the killing blow reports the stack total
before it is cleared.

## Limits and follow-ups

- `/set_stat` values live on the connection (`conn.PlayerStats`) and do not survive a gateway restart, so the live proof
  has to be re-established after every rebuild. Persisting them with the character save would remove that step.
- Impale stacks are per monster and per connection; the game's "impale effect" and "lasts an extra hit" modifiers are not
  modelled.
- The hand rows fill the main hand only; the off hand needs the second weapon set and its own hit.
- Fire, cold, lightning and chaos rows appear only once the weapon's added damage gives them a non-zero range — the rows
  exist, the data does not yet.
- The panel's remaining rows are still issue #75.
