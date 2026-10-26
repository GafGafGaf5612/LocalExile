using System.Collections.Concurrent;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using WalkEstimator = GameServer.Core.World.WalkEstimator;
using Progression = GameServer.Core.World.Progression;
using DamageModel = GameServer.Core.World.DamageModel;
using TargetAccuracy = GameServer.Core.World.TargetAccuracy;
using DamageType = GameServer.Core.World.DamageType;
using DamagePacket = GameServer.Core.World.DamagePacket;
using DamageRange = GameServer.Core.World.DamageRange;
using HitResistances = GameServer.Core.World.HitResistances;

partial class Program
{
    // Sample both moving actors at the hit time, after the approach and contact delay.
    internal static (int Accuracy, bool Unique, bool CloseRange) AttackAccuracyAt(
        ClientState conn, SpawnedMonster target, double at)
    {
        bool unique = target.Rarity == MonsterRarity.Unique;
        bool close = TargetAccuracy.InCloseRange(Dist(conn.Walk.Position(at), target.Walk.Position(at)));
        return (TargetAccuracy.AgainstTarget(conn.Pools.Accuracy, conn.Pools.AccuracyMoreVsUniquePct,
            conn.Pools.AccuracyMoreInCloseRangePct, unique, close), unique, close);
    }

    // A ground bow shot reserves pacing, its action token and stationary walk together with the input-time gate.
    internal static bool TryBeginBowAttack(ClientState conn, double requestedAt, double duration,
        ushort skill, ushort sequence, out (int Action, int X, int Y) started)
    {
        started = default;
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            if (conn.Closed || conn.Paused || conn.PlayerLife == 0 || conn.HitRecovery.BlocksInput(requestedAt, now)
                || !AttackTiming.Ready(now, conn.NextAttackAt, duration)) return false;
            conn.NextAttackAt = AttackTiming.After(now, conn.NextAttackAt, duration);
            int action = Interlocked.Increment(ref conn.ActionCount);
            var (x, y) = conn.Walk.Position(requestedAt);
            conn.Walk.Order(x, y, requestedAt);
            conn.ChannelSkill = skill;
            conn.ChannelSkillSequence = sequence;
            started = (action, x, y);
            return true;
        }
    }

    // Whether an attack may start now: the shared attack timer (the default attack and the attack gem skills) is over, or
    // the use is no more than a quarter of an attack early (the client's jitter). Does not reserve the timer: an attack used
    // on a monster waits for the walk into reach, and CastGemSkill reserves it when the swing starts.
    static double SkillAttackSeconds(ClientState conn, int effect, int level) =>
        SkillAttackTiming.ForSkill(Skills, effect, level, WeaponFor(conn), CharacterPlayerStats(conn));

    static bool AttackReady(ClientState conn, int effect, int level, out double seconds)
    {
        seconds = SkillAttackSeconds(conn, effect, level);
        double now = conn.Clock.Elapsed.TotalSeconds;
        lock (conn.Monsters) return AttackTiming.Ready(now, conn.NextAttackAt, seconds);
    }

    // The 0x1af hit angle (0..255 = a full turn), the direction the hit comes from (attacker → target) in the client's
    // facing convention atan2(dx, −dy), like Positioned's rotation.
    static byte HitAngle(int dx, int dy)
    {
        double a = Math.Atan2(dx, -dy);
        if (a < 0) a += 2 * Math.PI;
        return (byte)((int)Math.Round(a * 256.0 / (2 * Math.PI)) & 0xff);
    }

    // A positive offset on the hit's contact timing (seconds): the client starts its swing a beat after the server's 0x1ab
    // echo (the walk estimate leads the client's own walk, so the contact keyframe alone leaves the blood early). A fixed
    // offset is a compromise over the variable walk-sync; Game_HIT_OFFSET_MS overrides it (150 ms here covers most of the
    // walk-into-range error without overshooting a swing). A value that is not a finite number keeps the default.
    static double HitContactOffsetSeconds() =>
        (double.TryParse(Environment.GetEnvironmentVariable("Game_HIT_OFFSET_MS"), System.Globalization.CultureInfo.InvariantCulture, out var hitOff)
            && double.IsFinite(hitOff) ? hitOff : 150) / 1000.0;

    // The attack's contact time in seconds (AttackTiming.ContactDelay): the attack time scaled to the active weapon's attack1
    // contact keyframe, plus the walk-in offset for an attack that approached its target first, and always before the next
    // attack may start (which would cancel the hit).
    static double AttackContactDelay(ClientState conn, double attackSeconds, bool approach)
    {
        string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? Skills.ClassFor(conn.ClassRow).AnimatedObject;
        double contact = Animations.AttackContact(ao, WeaponProfileFor(conn, activeSet: true));
        return AttackTiming.ContactDelay(attackSeconds, contact, approach ? HitContactOffsetSeconds() : 0);
    }

    // The player uses a skill on a monster (C→S 0x24, e.g. the default attack on a left click). The client walks into range
    // first, so the server walks its estimate too (WalkEstimator.Approach, reach = the weapon's MaxAttackDistance in cells,
    // centre to centre) and only then starts the swing: it echoes the action aimed at the monster (0x1ab, flags | 0x80 +
    // target id) and, at the contact point (AttackContactDelay: the attack animation's contact keyframe), lowers the monster's life (0x1b1) by the unarmed damage
    // (Characters.dat MinDamage..MaxDamage raised by the increased melee physical damage stats). Any later action (a move, another click) cancels both.
    // One attack per attack time (WeaponSpeed ms scaled by the player's attack speed stats, a quarter early allowed for
    // jitter); earlier repeats of 0x24 are ignored. At 0 the client's Life component runs its death; the corpse is
    // removed 10 s later.
    internal static async Task Attack(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        InstanceTransfer.UseSkillOnObject use, SpawnedMonster m, double requestedAt)
    {
        int projectileEpoch;
        lock (conn.Monsters) projectileEpoch = conn.ProjectileEpoch;
        var weapon = WeaponFor(conn);
        bool bow = WeaponProfileFor(conn, activeSet: true).Bow;
        double now = conn.Clock.Elapsed.TotalSeconds, duration;
        lock (conn.Monsters) duration = weapon.AttackSeconds(CharacterPlayerStats(conn));
        // The client sends one 0x24 per swing while the button is held; jitter makes the next one arrive a little before a
        // full attack time, so accept it up to a quarter of an attack early.
        lock (conn.Monsters)
            if ((bow && conn.Paused) || !AttackTiming.Ready(now, conn.NextAttackAt, duration))
            {
                Console.WriteLine($"[gateway] {remote} <= 0x0024 skill 0x{use.SkillId:x4} seq 0x{use.Sequence:x4} ignored: {conn.NextAttackAt - now:0.000} s before the next attack");
                return;
            }
        int action;
        (int X, int Y) target, here, stop;
        double arrive;
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea || conn.RageRetired || conn.PlayerLife == 0
                || conn.HitRecovery.BlocksInput(requestedAt, conn.Clock.Elapsed.TotalSeconds)) return;
            action = Interlocked.Increment(ref conn.ActionCount);
            target = m.Walk.Position(now);
            (here, stop, arrive) = conn.Walk.Approach(target.X, target.Y, weapon.Range, now, tolerance: m.StopRange - 2);
        }
        Console.WriteLine($"[gateway] {remote} <= 0x0024 skill 0x{use.SkillId:x4} seq 0x{use.Sequence:x4} on 0x{use.TargetId:x}: player ({here.X}, {here.Y}), " +
            $"monster ({target.X}, {target.Y}), distance {Dist(here, target):0.0}; swing from ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now)
        {
            if (bow) { if (!await WaitProjectileTime(conn, arrive, projectileEpoch)) return; }
            else await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        }
        if (conn.ActionCount != action || (bow && !OrdinaryBowReleaseCurrent(conn, action, projectileEpoch))) { Console.WriteLine($"[gateway] {remote} attack on 0x{use.TargetId:x} cancelled before the swing"); return; }
        now = conn.Clock.Elapsed.TotalSeconds;
        lock (conn.Monsters)
        {
            target = m.Walk.Position(now);
            conn.NextAttackAt = AttackTiming.After(now, conn.NextAttackAt, duration);
        }
        if (Dist(stop, target) > weapon.Range + 2)
        {
            Console.WriteLine($"[gateway] {remote} attack on 0x{use.TargetId:x} out of reach: {Dist(stop, target):0.0} > {weapon.Range}");
            return;
        }
        var actionBytes = InstanceProtocol.ActionStarted(PlayerId, stop.X, stop.Y, target.X, target.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId);
        string actionLog = $"0x01ab player skill 0x{use.SkillId:x4} on monster 0x{use.TargetId:x} ({m.Label}, life {m.Life}/{m.MaxLife}), attack time {duration:0.000} s";
        if (bow)
        {
            if (!await SendOrdinaryBowAction(remote, stream, ciphers, conn, actionBytes, actionLog, action, projectileEpoch)) return;
        }
        else await Send(remote, stream, ciphers, actionBytes, actionLog, still: () => PlayerActionCurrent(conn, action));
        if (m.Life == 0) return;
        // A bow's target is an aim, not a reserved hit: its released arrow owns collision and damage.
        if (bow)
        {
            await LaunchOrdinaryBow(remote, stream, ciphers, conn, stop, target, duration, action, projectileEpoch);
            return;
        }
        // The hit lands at the attack animation's contact keyframe (data/animation-contacts.json), not a fixed half: the
        // client stretches the clip to the attack time, so the blood and the weapon impact sound line up with the swing.
        await Task.Delay(TimeSpan.FromSeconds(AttackContactDelay(conn, duration, approach: true)));
        await WhilePaused(conn);
        uint life;
        bool stageStarts = false;
        int accuracy, evasion;
        lock (conn.Monsters)
        {
            if (m.Life == 0) return;
            if (!PlayerActionCurrent(conn, action))
            {
                Console.WriteLine($"[gateway] {remote} attack on 0x{use.TargetId:x} cancelled by a later action");
                return;
            }
            // The Accuracy Mastery's target conditions at the roll: the target's own rarity and its distance at the hit
            // resolution time (TargetAccuracy). Spells do not roll, so spells never see these.
            var targetAccuracy = AttackAccuracyAt(conn, m, conn.Clock.Elapsed.TotalSeconds);
            bool unique = targetAccuracy.Unique, closeRange = targetAccuracy.CloseRange;
            accuracy = targetAccuracy.Accuracy;
            if (accuracy != conn.Pools.Accuracy)
                Console.WriteLine($"[gateway] {remote} attack on 0x{use.TargetId:x} ({m.Label}, {m.Rarity}): accuracy {conn.Pools.Accuracy} -> {accuracy} " +
                    $"for the target ({(unique ? "unique target, " : "")}{(closeRange ? "in close range (20 units) " : "")}accuracy mastery more)");
            evasion = m.Mods.Effects.Evasion(Monsters.BaseEvasion(m.Level) ?? 0);
        }
        int hitChance = DamageModel.AccuracyHitChance(conn.FlaskGrounds.Evasion(use.TargetId, evasion), accuracy);   // blinded in smoke
        if (!DamageModel.Hits(hitChance, Random.Shared.Next(100)))
        {
            Console.WriteLine($"[gateway] {remote} attack on 0x{use.TargetId:x} missed: accuracy {accuracy} vs evasion {evasion}, " +
                $"{hitChance}% chance");
            return;
        }
        if (MonsterBlocks(m, Random.Shared, spell: false))   // Stalwart: nothing of the hit happens
        {
            Console.WriteLine($"[gateway] {remote} attack on 0x{use.TargetId:x} ({m.Label}) blocked");
            return;
        }
        int attackEffect = SkillEffectId(conn, SkillCatalog.FirstSkillId) is { } attackId ? Skills.EffectRow(attackId) : -1;
        var ownPenetration = attackEffect >= 0
            ? Skills.EffectStats(attackEffect, 1).ToDictionary(kv => kv.Item1, kv => kv.Item2) : null;
        // Impale: the stacks already on the target add their stored damage to this hit, this hit uses one of their five
        // hits, and the hit's own physical damage may store a new stack for the hits after it.
        double stored, impaleStored;
        int impaleStacks, damage;
        bool impaled, critical;
        string? resistance;
        lock (conn.Monsters)
        {
            if (m.Life == 0) return;
            if (!PlayerActionCurrent(conn, action) || conn.Paused)
            {
                Console.WriteLine($"[gateway] {remote} attack on 0x{use.TargetId:x} cancelled by a later action");
                return;
            }
            // Settle Rage at accepted contact before this hit's gain. It is a final multiplier on the attack packet,
            // never on an existing impale bonus or on generic damage/DoT.
            var player = CharacterPlayerStats(conn);
            var pen = Penetration.Of(player, Stats, attack: true, ownPenetration);
            var resistanceMods = HitResistMods(player, ownPenetration);
            var hit = AttackHitConverted(weapon, player, WeaponProfileFor(conn, activeSet: true).Melee,
                ConversionFor(conn, attackEffect, 1, attack: true));
            double physical = hit[DamageType.Physical].Max;
            stored = m.Impale.Bonus;
            m.Impale.AfterHit();
            int impaleChance = Stats.Key("impale_on_hit_%_chance") is uint impaleKey ? player.GetValueOrDefault(impaleKey) : 0;
            impaled = impaleChance > 0 && Random.Shared.Next(100) < impaleChance && m.Impale.Apply(physical);
            // Read before the hit is applied: a killing blow clears the target's impales.
            impaleStacks = m.Impale.Stacks;
            impaleStored = m.Impale.Bonus;
            var ownRolled = hit.Roll(Random.Shared);
            // Impale is stored reflected damage, not part of the direct roll which can generate Fortification.
            var rolled = stored > 0 ? ownRolled.Add(DamageType.Physical,
                new DamageRange(Math.Floor(stored), Math.Floor(stored))) : ownRolled;
            critical = RollCriticalStrike(conn, conn.Pools.AttackCriticalStrikeChance, ownPenetration);
            int criticalMultiplier = CriticalMultiplierOn(m, AttackCriticalMultiplier(conn, player));
            (damage, resistance) = MonsterDamage(m, rolled, critical, attack: true, resistanceMods, pen);
            if (critical) damage = DamageModel.CriticalDamage(damage, criticalMultiplier);
            FortificationOnHitLocked(conn, m, RawFortificationDamage(ownRolled, critical, criticalMultiplier),
                WeaponProfileFor(conn, activeSet: true).Melee, player, ownPenetration);
            (m.Life, stageStarts) = TakeDamageLocked(m, damage);
            life = m.Life;
            conn.LastEnemyHitAt = conn.Clock.Elapsed.TotalSeconds;
            if (life == 0) { m.Aggro = false; m.Dead = true; m.Impale.Clear(); }
            RageOnHitLocked(conn, attack: true, melee: WeaponProfileFor(conn, activeSet: true).Melee, critical);
        }
        ChargesOnHit(conn, m, critical, spell: false);
        if (critical) _ = GainFlaskChargesOnCrit(remote, stream, ciphers, conn);   // the hit landed
        // At 0 the client's Life tick (0x141e6a190) stops notifying its listeners, so the mini life bar would keep the last
        // value: hide it with the stat its listener watches (hide_mini_life_bar).
        if (life == 0)
            await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(use.TargetId, [(StatKeys.HideMiniLifeBar, 1)]),
                $"0x01b3 monster 0x{use.TargetId:x} hide_mini_life_bar = 1");
        // The hit reaction: one of the rig's TakeHit clips and its SoundEvent (0x1b2).
        await SendFlinch(remote, stream, ciphers, conn, use.TargetId, m, damage, log: true);
        // The hit result for the client's damage/effect pipeline (0x1af): the client applies the hit locally (the target's
        // blood, the weapon impact sound "bonk") and 0x1b1 stays authoritative on life. flags 0x4201 is the real server's
        // player-melee hit (bit 0 hit, 0x200 damage, 0x4000 attack), kind 0x03 = action hit with hit data, the angle drives
        // the directional blood.
        await Send(remote, stream, ciphers,
            InstanceProtocol.ActionHit(PlayerId, use.Sequence, use.TargetId, flags: 0x4201, kind: 0x03,
                angle: HitAngle(target.X - stop.X, target.Y - stop.Y)),
            $"0x01af player hit on monster 0x{use.TargetId:x} (sequence 0x{use.Sequence:x4})");
        await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(use.TargetId, life),
            $"0x01b1 monster 0x{use.TargetId:x} hit for {damage}{(critical ? " (critical)" : "")}" +
            (resistance is { } note ? $", {note}" : "") +
            (stored > 0 ? $", impale +{stored:0.#} ({impaleStacks} stack(s))" : "") +
            (impaled ? $", impaled ({impaleStacks} stack(s), {impaleStored:0.#} stored)" : "") +
            $", life {life}/{m.MaxLife}{(life == 0 ? " (dies)" : "")}");
        if (life == 0) await MonsterKilled(remote, stream, ciphers, conn, use.TargetId, m);
        if (stageStarts) _ = RunBossStage(remote, stream, ciphers, conn, use.TargetId, m);
    }

    // A gem skill used on a monster (C→S 0x24 with a skill id other than the default attack's). A spell stops and casts from
    // where the player stands: the server echoes the action from there, aimed at the monster, so the client plays the cast.
    // An attack gem (Holy Strike, Ground Slam …) is used like the default attack (see Attack): the client walks into reach
    // first, so the server walks its estimate too and only echoes the swing — and pays the skill's cost — on arrival. Before
    // 2026-10-25 the attack cast at the click from out of reach: the mana was gone at once and the hit landed around the
    // player, hitting nothing. CastGemSkill pays the cost and runs the skill's effect (projectile, area hit, aura, self-cast).
    static async Task CastOn(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        InstanceTransfer.UseSkillOnObject use, SpawnedMonster m, double requestedAt)
    {
        var gem = GemSkillEffects(conn.Character).FirstOrDefault(g => g.Id == use.SkillId);
        // A movement attack (Flicker Strike, Leap Slam) closes the distance itself, and an attack used with "always attack
        // without moving" (Flags bit 0) swings from where the player stands: both keep the immediate cast.
        if (gem.Name is not null && Skills.IsAttack(gem.Effect) && !Skills.IsMovement(gem.Effect) && (use.Flags & 0x1) == 0)
        {
            if (!AttackReady(conn, gem.Effect, gem.Level, out double attackSeconds))
            {
                Console.WriteLine($"[gateway] {remote} skill 0x{use.SkillId:x4} {gem.Name} on 0x{use.TargetId:x}: ignored, the previous attack is still running (attack time {attackSeconds:0.00} s) (t={conn.Clock.Elapsed.TotalSeconds:0.000})");
                return;
            }
            var weapon = WeaponFor(conn);
            double now = conn.Clock.Elapsed.TotalSeconds;
            int action = 0;
            (int X, int Y) target = default, here = default, stop = default;
            double arrive = now;
            if (!TryBeginCooldownUse(conn, gem.Effect, CooldownFor(conn, gem.Effect, gem.Level, gem.Quality), false,
                () =>
                {
                    action = Interlocked.Increment(ref conn.ActionCount);
                    target = m.Walk.Position(now);
                    (here, stop, arrive) = conn.Walk.Approach(target.X, target.Y, weapon.Range, now, tolerance: m.StopRange - 2);
                }, out var approachTicket, reserve: false, requestedAt: requestedAt)) return;
            Console.WriteLine($"[gateway] {remote} <= 0x0024 skill 0x{use.SkillId:x4} {gem.Name} on 0x{use.TargetId:x}: player ({here.X}, {here.Y}), " +
                $"monster ({target.X}, {target.Y}), distance {Dist(here, target):0.0}; swing from ({stop.X}, {stop.Y}) in {arrive - now:0.00} s (t={now:0.000})");
            if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
            if (conn.ActionCount != action || approachTicket?.Current(conn) == false)
            {
                Console.WriteLine($"[gateway] {remote} skill 0x{use.SkillId:x4} {gem.Name} on 0x{use.TargetId:x} cancelled before the swing");
                return;
            }
            now = conn.Clock.Elapsed.TotalSeconds;
            lock (conn.Monsters) target = m.Walk.Position(now);
            if (Dist(stop, target) > weapon.Range + 2)
            {
                Console.WriteLine($"[gateway] {remote} skill 0x{use.SkillId:x4} {gem.Name} on 0x{use.TargetId:x} out of reach: {Dist(stop, target):0.0} > {weapon.Range}");
                return;
            }
            if (!TryBeginCooldownUse(conn, gem.Effect, CooldownFor(conn, gem.Effect, gem.Level, gem.Quality), false,
                () => { }, out var cooldownUse, actionToken: action)) return;
            await Send(remote, stream, ciphers,
                InstanceProtocol.ActionStarted(PlayerId, stop.X, stop.Y, target.X, target.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
                $"0x01ab player skill 0x{use.SkillId:x4} {gem.Name} on monster 0x{use.TargetId:x} ({m.Label}) from ({stop.X}, {stop.Y}) to ({target.X}, {target.Y}), flags 0x{use.Flags:x4} (t={conn.Clock.Elapsed.TotalSeconds:0.000})",
                still: () => cooldownUse?.CommitNativeUse(conn, use.SkillId) ?? PlayerActionCurrent(conn, action));
            _ = CastGemSkill(remote, stream, ciphers, conn, use.SkillId, stop, target, use.TargetId, sequence: use.Sequence,
                actionToken: action, cooldownUse: cooldownUse);
            return;
        }
        double castAt = conn.Clock.Elapsed.TotalSeconds;
        int castAction = 0;
        (int X, int Y) aim = default, stand = default;
        if (!TryBeginCooldownUse(conn, gem.Effect, gem.Name is null ? null : CooldownFor(conn, gem.Effect, gem.Level, gem.Quality), false,
            () =>
            {
                castAction = Interlocked.Increment(ref conn.ActionCount);
                aim = m.Walk.Position(castAt);
                stand = conn.Walk.Position(castAt);
                conn.Walk.Order(stand.X, stand.Y, castAt);
            }, out var spellUse, requestedAt: requestedAt)) return;
        await Send(remote, stream, ciphers,
            InstanceProtocol.ActionStarted(PlayerId, stand.X, stand.Y, aim.X, aim.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
            $"0x01ab player skill 0x{use.SkillId:x4} cast on monster 0x{use.TargetId:x} ({m.Label}) from ({stand.X}, {stand.Y}) to ({aim.X}, {aim.Y}), flags 0x{use.Flags:x4}",
            still: () => spellUse?.CommitNativeUse(conn, use.SkillId) ?? PlayerActionCurrent(conn, castAction));
        _ = CastGemSkill(remote, stream, ciphers, conn, use.SkillId, stand, aim, use.TargetId, sequence: use.Sequence,
            actionToken: spellUse?.ActionToken ?? castAction, cooldownUse: spellUse);
    }

    // A gem skill's cast: Righteous Fire puts its buff on, any other skill launches its projectile (if it has one). False when
    // it was refused (cooldown, mana), so its use is not confirmed to the client.
    static async Task<bool> CastGemSkill(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, (int X, int Y) from, (int X, int Y) to, uint targetId = 0, ushort sequence = 0,
        int? actionToken = null, CooldownUseTicket? cooldownUse = null, bool locationInstant = false)
    {
        int action = actionToken ?? conn.ActionCount;
        lock (conn.Monsters)
            if (actionToken is not null && (conn.ActionCount != action || conn.HitRecovery.Active(conn.Clock.Elapsed.TotalSeconds))) return false;
        if (InTown(conn) || conn.PlayerLife == 0) return false;
        var gem = GemSkillEffects(conn.Character).FirstOrDefault(g => g.Id == skillId);
        bool Current() => cooldownUse?.Current(conn) ?? (actionToken is null || PlayerActionCurrent(conn, action));
        if (!Current()) return false;
        if (locationInstant && (gem.Name is null || !InstantLocationSkill.Supported(Skills, gem.Effect))) return false;
        if (gem.Name is not null && Skills.IsSpell(gem.Effect) && !Skills.IsAttack(gem.Effect)
            && CastMs(conn, gem.Effect) is null)
        {
            Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: invalid cast duration (no effect or mana cost)");
            await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana {conn.PlayerMana} (invalid cast duration, cost back)");
            return false;
        }
        // Shared backstop for actor restrictions and gem requirements, before cooldown/payment/effect admission.
        if (gem.Name is not null && GemDisabled(conn, gem.Effect) is { } disabled)
        {
            Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: {disabled} (disabled)");
            await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana {conn.PlayerMana} (disabled skill, cost back)");
            return false;
        }
        // Vaal souls are not gathered yet (Roadmap "Gems later"), so a Vaal skill never has the souls it needs.
        if (gem.Name is not null && Skills.VaalSouls(gem.Effect, gem.Level) is int souls and > 0)
        {
            Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: needs {souls} Vaal souls, has 0");
            return false;
        }
        // Eligible uses arrive with their one original preflight reservation. Never reserve again after an await.
        if (cooldownUse is not null)
        {
            if (!cooldownUse.TryExecute(conn, gem.Effect)) return false;
            if (!cooldownUse.Instant) await SendNativeCooldownRefresh(remote, stream, ciphers, conn);
            if (!Current()) return false;
        }
        else if (gem.Name is not null && CooldownFor(conn, gem.Effect, gem.Level, gem.Quality) is not null)
            return false;
        // Multi-use and unsupported cooldown mechanics retain their existing raw-deadline behavior.
        else if (gem.Name is not null && Skills.CooldownMs(gem.Effect, gem.Level) is int cooldown and > 0)
        {
            double now = conn.Clock.Elapsed.TotalSeconds, readyAt;
            lock (conn.Monsters)
            {
                readyAt = conn.SkillReadyAt.GetValueOrDefault(gem.Effect);
                if (now >= readyAt)
                {
                    conn.SkillReadyAt[gem.Effect] = now + cooldown / 1000.0;
                    conn.SingleUseCooldowns.Remove(gem.Effect);
                }
            }
            if (now < readyAt)
            {
                Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: on cooldown for {(readyAt - now) * 1000:0} ms more");
                return false;
            }
        }
        // An attack gem swings with the weapon's attack time, like the default attack (see Attack): while the button is held
        // the client keeps aiming it, and every use used to pay its cost and start a swing (live 2026-10-25: Holy Strike on
        // the right mouse button, twelve swings a second at 6 mana each). The timer is shared with the default attack, so
        // two attacks cannot start at once either.
        if (gem.Name is not null && Skills.IsAttack(gem.Effect))
        {
            double duration = SupportedAttackSeconds(conn, skillId);
            double now = conn.Clock.Elapsed.TotalSeconds, next;
            bool ready;
            lock (conn.Monsters)
            {
                next = conn.NextAttackAt;
                ready = AttackTiming.Ready(now, next, duration);
                if (ready) conn.NextAttackAt = AttackTiming.After(now, next, duration);
            }
            if (!ready)
            {
                Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: ignored, {next - now:0.000} s before the next attack (t={now:0.000})");
                return false;
            }
        }
        if (gem.Name is not null && Supports.Of(gem.Effect)?.Types.Contains(47) == true)
            conn.NextChannelHitAt = conn.Clock.Elapsed.TotalSeconds + SupportedAttackSeconds(conn, skillId);
        if (gem.Name is not null && SupportedReservation(conn, skillId) is { } reservation)
        {
            if (!Current()) return false;
            await ToggleAura(remote, stream, ciphers, conn, skillId, gem.Effect, gem.Name, reservation, SupportedSkill(conn, skillId).Level, SupportedSkill(conn, skillId).Quality);
            return true;
        }
        if (gem.Name is not null)
        {
            uint cost;
            bool paid;
            lock (conn.Monsters)
            {
                if (!Current()) return false;
                paid = PayMana(conn, skillId, out cost);
            }
            if (!paid)
            {
                Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: not enough mana ({conn.PlayerMana} < {cost})");
                return false;
            }
            if (cost > 0)
                await SendPlayerMana(remote, stream, ciphers, conn,
                    $"0x01b1 player mana {conn.PlayerMana} (-{cost} for {gem.Name}) (t={conn.Clock.Elapsed.TotalSeconds:0.000})");
        }
        if (!Current()) return false;
        // Location instant acceptance handles only Frostblink's repositioning, without inventing its departure/arrival
        // hits or chilled ground. In particular do not route it through the generic single-area hit approximation.
        if (locationInstant) return true;
        // The Portal skill: a town portal, as the Portal Scroll's, once its cast (2.5 s) is over.
        if (actionToken is not null && !PlayerActionCurrent(conn, action)) return false;
        if (gem.Name is not null && Skills.EffectId(gem.Effect) == "Portal")
        {
            if (!PortalAllowed(conn)) { Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} Portal: not here"); return false; }
            if (CastMs(conn, gem.Effect) is not int castMs) return false;
            _ = Task.Delay(castMs).ContinueWith(async _ =>
            {
                if (!conn.Closed && PlayerActionCurrent(conn, action) && Current())
                    await OpenTownPortal(remote, stream, ciphers, conn, from.X, from.Y, "Portal skill");
            });
            return true;
        }
        if (gem.Name is not null && UseChargeSkill(conn, gem.Effect, gem.Level, gem.Quality, from)) return true;
        if (gem.Name is not null && IsRighteousFire(gem.Effect))
            await ActivateRighteousFire(remote, stream, ciphers, conn, skillId, gem.Effect, gem.Level, gem.Quality, gem.Gem);
        else
            await LaunchProjectile(remote, stream, ciphers, conn, skillId, from, to, targetId, sequence: sequence,
                actionToken: actionToken, cooldownUse: cooldownUse);
        return true;
    }

    // An instant skill (C→S 0x191): its effect, then S→C 0x1ce, which has the client run the skill's action without animation
    // and consume a use of it: the skill bar's cooldown starts (the tooltip showed the cooldown, the bar did not run it).
    internal static async Task UseInstantSkill(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, uint targetId, (int X, int Y) at)
    {
        var gem = GemSkillEffects(conn.Character).FirstOrDefault(g => g.Id == skillId);
        if (gem.Name is not null && InstantLocationSkill.Supported(Skills, gem.Effect))
        {
            await UseInstantSkillAtLocation(remote, stream, ciphers, conn, skillId, default, targetId);
            return;
        }
        if (gem.Name is null || !InstantLocationSkill.ObjectTargetSupported(Skills, gem.Effect)) return;
        int epoch;
        lock (conn.Monsters)
        {
            epoch = conn.ProjectileEpoch;
            if (!conn.ProjectileActive(epoch) || conn.Paused || !conn.PlayerSent || conn.PendingTransfer is not null) return;
        }
        if (!TryBeginCooldownUse(conn, gem.Effect, CooldownFor(conn, gem.Effect, gem.Level, gem.Quality),
            true, () => { }, out var cooldownUse)) return;
        if (await CastGemSkill(remote, stream, ciphers, conn, skillId, at, at, targetId, cooldownUse: cooldownUse))
        {
            await Send(remote, stream, ciphers, InstanceProtocol.InstantAction(PlayerId, skillId, at.X, at.Y, targetId),
                $"0x01ce player skill 0x{skillId:x4}: instant action (its use and cooldown)",
                still: () => conn.ProjectileActive(epoch) && conn.PlayerLife > 0 && !conn.Paused
                    && (cooldownUse?.CommitNativeUse(conn, skillId) ?? true));
            await SendNativeCooldownRefresh(remote, stream, ciphers, conn);
        }
    }

    static async Task UseInstantSkillAtLocation(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort skillId, (uint X, uint Y) aim, uint? targetObjectId = null)
    {
        var gem = GemSkillEffects(conn.Character).FirstOrDefault(g => g.Id == skillId);
        if (gem.Name is null || !InstantLocationSkill.Supported(Skills, gem.Effect)) return;
        var own = Skills.EffectStats(gem.Effect, gem.Level, gem.Quality).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        int epoch;
        (int X, int Y) from, to;
        // GemSkills places the skill's own stat over the player's; use the same inputs for native distance resolution.
        Func<string, int> CurrentStats()
        {
            var player = FinalPlayerStats(conn).ToDictionary(kv => kv.Item1, kv => kv.Item2);
            return name => Stats.Key(name) is uint k ? own.TryGetValue(k, out int value) ? value : player.GetValueOrDefault(k) : 0;
        }
        lock (conn.Monsters)
        {
            epoch = conn.ProjectileEpoch;
            if (targetObjectId is uint target)
            {
                if (!InstantLocationSkill.TryResolveObject(conn, epoch, target, InTown(conn), CurrentStats(),
                    out from, out to, out _)) return;
            }
            else if (!InstantLocationSkill.TryResolve(conn, epoch, aim, InTown(conn), CurrentStats(), out from, out to)) return;
        }
        if (!TryBeginCooldownUse(conn, gem.Effect, CooldownFor(conn, gem.Effect, gem.Level, gem.Quality),
            true, () => { }, out var cooldownUse)) return;
        if (!await CastGemSkill(remote, stream, ciphers, conn, skillId, from, to, cooldownUse: cooldownUse, locationInstant: true)) return;
        bool accepted = false;
        // Keep the raw aim: IceDash resolves it from the client's position when 0x1ce runs. Sending an already rounded
        // endpoint can normalize a short diagonal twice and move another cell. Resolve the server copy at this gate too.
        await Send(remote, stream, ciphers, [],
            $"0x01ce player skill 0x{skillId:x4}: instant {(targetObjectId is uint id ? $"on 0x{id:x}" : $"location ({aim.X}, {aim.Y})")}, movement only", atGate: () =>
            {
                lock (conn.Monsters)
                {
                    var current = GemSkillEffects(conn.Character).FirstOrDefault(g => g.Id == skillId);
                    if (current != gem || GemDisabled(conn, gem.Effect) is not null) return null;
                    (int X, int Y) rawAim;
                    if (targetObjectId is uint target)
                    {
                        if (!InstantLocationSkill.TryCommitObject(conn, epoch, target, InTown(conn), CurrentStats(),
                            out rawAim, out _)) return null;
                    }
                    else
                    {
                        if (!InstantLocationSkill.TryCommit(conn, epoch, aim, InTown(conn), CurrentStats(), out _)) return null;
                        rawAim = ((int)aim.X, (int)aim.Y);
                    }
                    if (cooldownUse?.CommitNativeUse(conn, skillId) == false) return null;
                    accepted = true;
                    return InstanceProtocol.InstantAction(PlayerId, skillId, rawAim.X, rawAim.Y, targetObjectId);
                }
            });
        if (accepted)
        {
            await SendNativeCooldownRefresh(remote, stream, ciphers, conn);
            await FollowRighteousFire(remote, stream, ciphers, conn);
        }
    }

    // Aura skills toggle: the first cast reserves their mana and puts their buff on the player, the next cast turns them
    // off. The reserved mana is sent in the Life pool and in every later 0x1b1 mana update; the client does not derive it.
    static async Task ToggleAura(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, int effect, string name, (int Flat, int Percent) reservation, int level, int quality)
    {
        string? effectId = Skills.EffectId(effect);
        if (effectId is null || !AuraCatalog.Supported(effectId))
        {
            Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {name}: reservation is known but this aura's effect is not implemented yet");
            return;
        }
        AuraCatalog.Aura? ended = null;
        AuraCatalog.Aura? started = null;
        lock (conn.Monsters)
        {
            if (conn.Auras.Remove(skillId, out var old))
                ended = old;
            else if (conn.Auras.Values.FirstOrDefault(a => a.EffectId == effectId) is { } same)
            {
                // One aura of a kind at a time: a second gem of the same aura can't start it.
                Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {name}: refused, {same.Name} already runs (skill 0x{same.SkillId:x4})");
                return;
            }
            else
            {
                var aura = new AuraCatalog.Aura(skillId, effect, effectId, name, conn.NextBuff++,
                    AuraCatalog.BuffRow(effectId), reservation.Flat, reservation.Percent, level, quality, SourceBuff: conn.NextBuff++);
                conn.Auras[skillId] = aura;
                int reserved = AuraCatalog.RequiredMana(conn.Pools, conn.Auras.Values);
                if (reserved > conn.Pools.Mana)
                {
                    conn.Auras.Remove(skillId);
                    Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {name}: needs {reserved} mana reserved, " +
                        $"maximum is {conn.Pools.Mana}");
                    return;
                }
                started = aura;
                conn.PlayerMana = (uint)Math.Min(conn.PlayerMana, (uint)(conn.Pools.Mana - reserved));
            }
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
        }

        CharacterSaves.SetAuraActive(CharacterName(conn), effectId, started is not null);
        if (ended is not null)
        {
            foreach (var packet in AuraCatalog.EndPackets(ended, PlayerId))
                await Send(remote, stream, ciphers, packet,
                    $"0x01c2 player aura {ended.Name} ends");
        }
        if (started is not null)
        {
            foreach (var packet in AuraBuffs(started))
                await Send(remote, stream, ciphers, packet,
                    $"0x01c1 player aura {started.Name} (BuffDefinitions row {started.BuffRow})");
        }
        await SendCharacterStats(remote, stream, ciphers, conn, started is not null ? $"aura {name} on" : $"aura {ended!.Name} off");
        await SendPlayerMana(remote, stream, ciphers, conn,
            $"0x01b1 player mana {conn.PlayerMana}/{conn.Pools.Mana}, {ReservedMana(conn)} reserved");
    }

    static IEnumerable<byte[]> AuraBuffs(AuraCatalog.Aura aura) =>
        AuraCatalog.StartPackets(aura, PlayerId, Skills.ActionHash(aura.Effect));

    // Experimental: a projectile gem skill's projectile. After the cast time the server creates the Projectiles.dat object
    // named after the effect (Fireball → Metadata/Projectiles/Fireball) just ahead of the caster, wakes it and moves it to the
    // aimed point and on to its range with 0x1ba at the row's ProjectileSpeed (world units per second, 250/23 per grid
    // cell), stopping at the first wall or monster on its path; a monster takes the skill's damage packet
    // (SkillCatalog.HitDamage, after conversion). It explodes there; then the object is removed.
    static async Task LaunchProjectile(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, (int X, int Y) from, (int X, int Y) to, uint targetId = 0, ushort sequence = 0,
        int? actionToken = null, CooldownUseTicket? cooldownUse = null)
    {
        int action = actionToken ?? cooldownUse?.ActionToken ?? conn.ActionCount;
        var gem = GemSkillEffects(conn.Character).FirstOrDefault(g => g.Id == skillId);
        if (gem.Name is null) { Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4}: no gem skill"); return; }
        // An attack gem keeps its swing through a later move order: the client attack-moves while the mouse button is
        // held (C→S 0x25 with bit 0 clear), sending a new Move that used to cancel the release — the mana was paid and
        // no projectile appeared (live 2026-10-26, Kinetic Blast while walking). Spells still stop on a new action.
        bool attackGem = Skills.IsAttack(gem.Effect);
        if (!attackGem && cooldownUse?.Instant != true && !PlayerActionCurrent(conn, action)) return;
        if (Skills.ProjectileFor(gem.Effect) is not { } projectile)
        {
            // Not a projectile skill (strikes, slams, areas): its hit lands on the monsters around the aimed point.
            await AreaSkillHit(remote, stream, ciphers, conn, skillId, gem, from, to, sequence: sequence,
                actionToken: actionToken, cooldownUse: cooldownUse);
            return;
        }
        var projectileHitPlan = SkillHitPlan.ForSkill(Skills, gem.Effect, gem.Level, gem.Quality);
        if (!projectileHitPlan.CanHit) return;
        int epoch;
        lock (conn.Monsters) epoch = cooldownUse?.ProjectileEpoch ?? conn.ProjectileEpoch;
        // Released at the action's contact keyframe. An attack gem (Burning Arrow, Caustic Arrow …) plays the weapon's
        // attack1 animation, which the client stretches to the weapon's attack time: its contact is attack1_bow's 399/1066
        // (0.374 for the Ranger) of that time. A spell plays its spell_projectile clip instead (Dex: 699/1133 = 0.617 of
        // the cast). Using the spell fraction for a bow sent the arrow 0.617 of the action after the swing had already
        // lowered the bow (live 2026-10-25: the client's create arrived ~600 ms after 0x1ab, while attack1_bow's contact is
        // ~0.37 of it).
        var cls = Skills.ClassFor(conn.ClassRow);
        bool attack = attackGem;
        int? castDuration = attack ? 0 : CastMs(conn, gem.Effect, skillId);
        if (castDuration is null) return;
        double releaseDelay = attack
            ? AttackContactDelay(conn, SupportedAttackSeconds(conn, skillId), approach: false)
            : castDuration.Value / 1000.0 * Animations.SpellRelease(cls.AnimatedObject);
        if (!await WaitProjectileTime(conn, conn.Clock.Elapsed.TotalSeconds + Math.Max(0.001, releaseDelay), epoch)
            || (!attack && cooldownUse?.Instant != true && !PlayerActionCurrent(conn, action)) || cooldownUse?.Current(conn) == false)
        { Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} cancelled before release"); return; }
        // A worn skill effect (its skill_art_variation) flies its own projectile and explodes in its own art (SkillArt).
        int variation = SkillArtVariation(conn, gem.Gem);
        projectile = (Art.Projectile(projectile.Id, variation), projectile.Speed);
        // The fireball explosion is only right for the Fireball spell; a bow's arrow (Burning Arrow, Caustic Arrow …)
        // sticks and disappears on hit, so it gets no impact. Its burning/debuff is the monster's, not the arrow's.
        // Kinetic Blast is the exception among the non-Fireball skills: its wand projectile bursts in the cluster_burst
        // explosion where it lands, which is what the player sees.
        string? effectId = Skills.EffectId(gem.Effect);
        string impact = effectId is "KineticBlast" or "KineticBlastAltX" or "KineticBlastAltY"
            ? KineticBlastImpactAo
            : projectile.Id.Contains("Fireball")
                ? Art.AnimatedInPlaceOf(DefaultProjectileImpactAo, variation)
                : "none";
        // Keep the player's final speed separate from the skill's raw stats: the panel already includes the player's
        // base speed, while a gem's own base speed (including quality) adds to that total instead of replacing it.
        var playerSpeedStats = CharacterPlayerStats(conn);
        var skillSpeedStats = SupportedSkill(conn, skillId).Stats;
        int PlayerSpeedStat(string name) => Stats.Key(name) is uint key ? playerSpeedStats.GetValueOrDefault(key) : 0;
        int SkillSpeedStat(string name) => Stats.Key(name) is uint key ? skillSpeedStats.GetValueOrDefault(key) : 0;
        float speed = ProjectileSpeed.CellsPerSecond(projectile.Speed, PlayerSpeedStat, SkillSpeedStat,
            Environment.GetEnvironmentVariable("Game_PROJECTILE_SPEED"));
        // Kinetic Blast bursts where its wand projectile lands: the projectile itself carries no single-target hit, and
        // the explosion hits every monster in the skill's radius around that cell (live check pending). Its action is not
        // required to be current any more: the shot was paid for when it was released.
        bool kineticBlast = effectId is "KineticBlast" or "KineticBlastAltX" or "KineticBlastAltY";
        Func<SpawnedMonster, (int Damage, bool Critical)>? damageTo = m =>
        {
            // The player's stats (passives, items, auras) under the skill's own, as GemSkills sends them. Keep the hit's
            // types separate so conversion and per-type mitigation apply. An attack gem (Burning Arrow, Caustic Arrow …) has
            // no spell damage of its own: its hit is the weapon's, scaled by the skill's damage effectiveness, with the
            // attack's crit — the same split AreaSkillHit makes for the non-projectile gems.
            var stats = FinalPlayerStats(conn).ToDictionary(kv => kv.Item1, kv => kv.Item2);
            var own = SupportedSkill(conn, skillId).Stats;
            int chance = SupportedCriticalChance(conn, skillId);
            var pen = Penetration.Of(stats, Stats, attack: Skills.IsAttack(gem.Effect), own);
            var resistanceMods = HitResistMods(stats, own);
            foreach (var (k, v) in own) stats[k] = v;
            bool spell = projectileHitPlan.Kind == SkillHitKind.Spell;
            if (MonsterBlocks(m, Random.Shared, spell))
            {
                Console.WriteLine($"[gateway] {remote} {gem.Name} on {m.Label} blocked");
                return (Blocked, false);
            }
            var hit = SupportedHit(conn, skillId);
            var rolled = hit.Roll(Random.Shared);
            bool critical = RollCriticalStrike(conn, chance, own, attack: !spell);
            var (damage, resistance) = MonsterDamage(m, rolled, critical, attack: !spell, resistanceMods, pen);
            if (critical)
                damage = DamageModel.CriticalDamage(damage,
                    CriticalMultiplierOn(m, SupportedCriticalMultiplier(conn, skillId)));
            ChargesOnHit(conn, m, critical, spell, gem.Effect, gem.Level, gem.Quality, melee: false);
            if (resistance is not null) Console.WriteLine($"[gateway] {remote} {gem.Name} resistance: {resistance}");
            return (damage, critical);
        }, epoch: epoch, acceptedHit: (_, critical) =>
            RageOnHitLocked(conn, attack, melee: false, critical, SupportedSkill(conn, skillId).Stats));
        Func<(int X, int Y), uint, Task>? explosion = kineticBlast ? (at, exclude) => AreaSkillHit(remote, stream, ciphers, conn, skillId, gem, at, at,
            immediate: true, melee: false, excludeMonster: exclude) : null;
        string? impactObject = kineticBlast ? KineticBlastImpactAo : null;
        // "While returning": Returning Projectiles Support's less Damage and the game's returning-projectile finals.
        double returnMore = ProjectileReturn.DamageMore(SumStat);
        bool returnPierce = SumStat("returning_projectiles_always_pierce") != 0;
        Func<SpawnedMonster, (int Damage, bool Critical)>? returnDamageTo = damageTo is null ? null : m =>
        {
            var (damage, critical) = damageTo(m);
            return ((int)Math.Floor(damage * returnMore), critical);
        };
        Func<(int X, int Y), uint, Task>? returnExplosion = kineticBlast ? (at, exclude) => AreaSkillHit(remote, stream, ciphers, conn, skillId, gem, at, at,
            immediate: true, melee: false, damageMore: returnMore, excludeMonster: exclude) : null;
        var returnShotgunned = new System.Collections.Concurrent.ConcurrentDictionary<uint, byte>();
        // The player's extra projectiles (passives, wand mastery, supports) and the skill's: one object each, spread
        // around the aim. The client fires none of its own (every projectile is a server object), so without this only
        // the centre one existed.
        int SumStat(string name) => Stats.Key(name) is uint key
            ? playerSpeedStats.GetValueOrDefault(key) + skillSpeedStats.GetValueOrDefault(key) : 0;
        // Returning projectiles: the art that would replace the projectile's Animated component moves to a standalone
        // effect object, so the projectile itself stays whole for the flight back through the caster.
        bool returning = ProjectileReturn.ShouldReturn(effectId, attack, SumStat, Random.Shared.Next(100),
            Environment.GetEnvironmentVariable("Game_PROJECTILE_RETURN"));
        if (returning && impactObject is null && impact != "none")
        {
            impactObject = impact;
            impact = "none";
        }
        bool delayedImpact = returning && projectile.Id.Contains("Fireball");
        if (returning) Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: projectile returns");
        // Pierce Support: projectile_base_number_of_targets_to_pierce (2 at level 1, 4 at 20) plus the other pierce
        // stats; always_pierce (Freezing Pulse) is unlimited. A projectile with N hits pierces N-1 targets.
        int pierceCount = Math.Max(0,
            SumStat("projectile_number_of_targets_to_pierce") + SumStat("projectile_base_number_of_targets_to_pierce") +
            SumStat("arrow_number_of_targets_to_pierce") + SumStat("arrow_base_number_of_targets_to_pierce"));
        bool alwaysPierce = SumStat("always_pierce") != 0 || SumStat("arrows_always_pierce") != 0;
        int pierceHits = alwaysPierce ? 1000 : 1 + pierceCount;
        if (pierceHits > 1)
            Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: pierces, {pierceHits} hit(s)");
        int extra = SumStat("number_of_additional_projectiles");
        int count = Math.Clamp(1 + extra, 1, 20);
        double gap = SumStat("projectile_spread_radius") is int spread and > 0 and <= 90
            ? spread : ProjectileSpread.DefaultGapDegrees;
        if (double.TryParse(Environment.GetEnvironmentVariable("Game_PROJECTILE_SPREAD"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double devSpread))
            gap = Math.Clamp(devSpread, 0, 90);
        IReadOnlyList<double> angles = ProjectileSpread.Angles(count, gap);
        if (count > 1)
            Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: {count} projectiles, {gap:0.#}° apart " +
                $"({extra} additional)");
        await Task.WhenAll(angles.Select(angle => FlyProjectile(remote, stream, ciphers, conn, (projectile.Id, speed), from,
            ProjectileSpread.Rotate(from, to, angle), impact: impact,
            actionToken: cooldownUse is null || cooldownUse.Instant ? null : action, damageTo: damageTo,
            epoch: epoch, onImpact: explosion, impactObjectAo: impactObject, keepThroughActions: attack, returning: returning,
            impactObjectSeconds: kineticBlast ? 0.35 : 1.5,
            returnSpeedPercent: SumStat("returned_projectile_speed_+%"),
            returnOnLanding: !ProjectileReturn.CannotReturnFromDistance(effectId),
            delayedImpact: delayedImpact,
            shotgunned: kineticBlast ? null : new System.Collections.Concurrent.ConcurrentDictionary<uint, byte>(),
            returnDamageTo: returnDamageTo, returnExplosion: returnExplosion, returnPierce: returnPierce,
            returnShotgunned: returnShotgunned, pierceHits: pierceHits)));
    }

    // The radius in cells of a gem skill's hit around its aimed point (AreaSkillHit): its own
    // active_skill_base_area_of_effect_radius, else Cyclone's 16 or the generic 12, grown by the player's area stats
    // (SkillArea). The radius stat is in cells (Righteous Fire's 18 is its 1.8 m, verified against the real game 2026-10-23).
    static int SkillRadius(ClientState conn, IReadOnlyDictionary<uint, int> skillStats, bool spell, int effect)
    {
        var areaStats = MergeSkillStats(AreaPlayerStats(conn), skillStats);
        int Get(string name) => Stats.Key(name) is uint k ? areaStats.GetValueOrDefault(k) : 0;
        return SkillArea.Radius(SkillArea.BaseRadius(Skills.EffectId(effect), Get), Get, spell);
    }

    // Whether a living monster stands within a gem attack's hit radius of a ground point. The client sends a point with a
    // monster on it as C→S 0x25 (not C→S 0x24 on the object), so an attack-move at such a point has to become a swing.
    static bool MonsterAtAim(ClientState conn, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        int x, int y, double at)
    {
        var stats = SupportedSkill(conn, gem.Id).Stats;
        int radius = SkillRadius(conn, stats, spell: false, gem.Effect);
        lock (conn.Monsters)
            return conn.Monsters.Any(m => !m.Value.Dead && m.Value.Life > 0
                && Dist(m.Value.Walk.Position(at), (x, y)) <= radius);
    }

    // A gem skill without a projectile (strikes, slams, area spells): a first, simple hit (2026-10-23). It lands once, after
    // half the attack time (attacks) or the cast (spells), on every living monster within the skill's area radius
    // (active_skill_base_area_of_effect_radius, else AreaSkillRadius cells, grown by area of effect: SkillArea) of the aimed point, or of the player for a
    // point too far for a melee skill. Attacks deal the weapon's hit as the default attack does, spells their own damage
    // (SkillCatalog.HitDamage) with the player's stats; both convert, crit and roll per monster. An attack's hit carries
    // the skill's damage effectiveness (SkillCatalog.DamageEffectiveness). Not modelled yet: the skills' own shapes (waves,
    // cones, chains), stun, and the attack's accuracy roll.
    static async Task AreaSkillHit(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) from, (int X, int Y) to,
        bool immediate = false, ushort sequence = 0, int? actionToken = null, CooldownUseTicket? cooldownUse = null, bool? melee = null,
        double damageMore = 1, uint excludeMonster = 0)
    {
        bool Current() => cooldownUse?.Current(conn) ?? (actionToken is null || conn.ActionCount == actionToken);
        if (!Current()) return;
        var skillStats = SupportedSkill(conn, skillId).Stats;
        // An attack gem's share of the weapon's damage ("Deals 77.3% of base Attack Damage"): the hit below uses it.
        int effectiveness = Skills.DamageEffectiveness(gem.Effect, gem.Level);
        int Stat(string name) => Stats.Key(name) is uint k ? skillStats.GetValueOrDefault(k) : 0;
        if (Stat("base_deal_no_damage") != 0) return;
        var hitPlan = SkillHitPlan.ForSkill(Skills, gem.Effect, gem.Level, gem.Quality);
        if (!hitPlan.CanHit) return;
        bool spell = hitPlan.Kind == SkillHitKind.Spell;
        var weapon = WeaponFor(conn);
        int? castDuration = spell ? CastMs(conn, gem.Effect, skillId) : 0;
        if (castDuration is null) return;
        double delay;
        // An attack lands at its contact keyframe; the player swings where it stands, so without the walk-in offset.
        lock (conn.Monsters) delay = spell ? castDuration.Value / 1000.0
            : AttackContactDelay(conn, SupportedAttackSeconds(conn, skillId), approach: false);
        int radius = SkillRadius(conn, skillStats, spell, gem.Effect);
        // A melee skill aimed far away still lands in front of the player.
        var centre = !spell && Dist(from, to) > radius + weapon.Range ? from : to;
        int action = actionToken ?? cooldownUse?.ActionToken ?? conn.ActionCount;
        if (cooldownUse?.Instant != true && conn.ActionCount != action) return;
        // A channel's hits (Cyclone, C→S 0x197) land at once, where the player is.
        if (!immediate)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.05, delay)));
            await WhilePaused(conn);
            if ((cooldownUse?.Instant != true && conn.ActionCount != action) || conn.Closed || !Current()) return;
        }
        double now = conn.Clock.Elapsed.TotalSeconds;
        List<(uint Id, SpawnedMonster M)> targets;
        lock (conn.Monsters)
            targets = [.. conn.Monsters.Where(e => e.Key != excludeMonster && !e.Value.Dead && e.Value.Life > 0
                && Dist(e.Value.Walk.Position(now), centre) <= radius).Select(e => (e.Key, e.Value))];
        var player = CharacterPlayerStats(conn);
        var stats = FinalPlayerStats(conn).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        int criticalChance = SupportedCriticalChance(conn, skillId);
        var pen = Penetration.Of(stats, Stats, attack: !spell, skillStats);
        var mods = HitResistMods(stats, skillStats);
        foreach (var (k, v) in skillStats) stats[k] = v;
        bool fortificationGranted = false; // One simultaneous sweep, independent of notification latency.
        // These contacts belong to one resolved sweep. Notification awaits may outlast the shared hit interval,
        // but cannot create a second positive Rage gain from the same sweep. Rejected gains do not take this latch.
        bool rageGranted = false;
        foreach (var (id, m) in targets)
        {
            if (MonsterBlocks(m, Random.Shared, spell))
            {
                Console.WriteLine($"[gateway] {remote} {gem.Name} on 0x{id:x} ({m.Label}) blocked");
                continue;
            }
            DamagePacket hit = SupportedHit(conn, skillId);
            var rolled = hit.Roll(Random.Shared);
            bool critical = RollCriticalStrike(conn, criticalChance, skillStats, attack: !spell);
            int criticalMultiplier = CriticalMultiplierOn(m, SupportedCriticalMultiplier(conn, skillId));
            var (damage, resistance) = MonsterDamage(m, rolled, critical, attack: !spell, mods, pen);
            if (critical) damage = DamageModel.CriticalDamage(damage, criticalMultiplier);
            if (damageMore != 1) damage = (int)Math.Floor(damage * Math.Max(0, damageMore));
            if (critical && m.Life > 0) _ = GainFlaskChargesOnCrit(remote, stream, ciphers, conn);
            if (await HitMonster(remote, stream, ciphers, conn, id, m, damage,
                    $"{gem.Name}{(critical ? " (critical)" : "")}{(resistance is null ? "" : $", {resistance}")}",
                    melee: melee ?? !spell, sequence: sequence, actionToken: action, cooldownUse: cooldownUse,
                    accepted: () =>
                    {
                        if (!fortificationGranted)
                            fortificationGranted = FortificationOnHitLocked(conn, m,
                                RawFortificationDamage(rolled, critical, criticalMultiplier),
                                !spell && Skills.IsMelee(gem.Effect), player, skillStats);
                        if (!rageGranted) rageGranted = RageOnHitLocked(conn, attack: !spell,
                            melee: !spell && Skills.IsMelee(gem.Effect), critical, skillStats);
                    }))
                ChargesOnHit(conn, m, critical, spell, gem.Effect, gem.Level, gem.Quality, primary: id == targets[0].Id);
        }
        Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: {(spell ? "spell" : "attack")} hit {targets.Count} monster(s) within {radius} cells of ({centre.X}, {centre.Y}) (t={conn.Clock.Elapsed.TotalSeconds:0.000})");
    }

    // A projectile's flight (LaunchProjectile): created just ahead of from, aimed at to, it flies on
    // to its range and stops at the first wall or monster on its path; a monster hit takes damage(monster).
    internal static async Task FlyProjectile(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        (string Id, float CellsPerSecond) projectile, (int X, int Y) from, (int X, int Y) to, Func<SpawnedMonster, (int Damage, bool Critical)>? damageTo,
        int epoch, bool log = true, string? impact = null, int? actionToken = null, OrdinaryBowShot? ordinaryShot = null,
        Action<SpawnedMonster, bool>? acceptedHit = null,
        Func<(int X, int Y), uint, Task>? onImpact = null, string? impactObjectAo = null, bool keepThroughActions = false,
        bool returning = false, double impactObjectSeconds = 0.35, double returnSpeedPercent = 0, bool returnOnLanding = true,
        bool delayedImpact = false, ConcurrentDictionary<uint, byte>? shotgunned = null,
        Func<SpawnedMonster, (int Damage, bool Critical)>? returnDamageTo = null,
        Func<(int X, int Y), uint, Task>? returnExplosion = null, bool returnPierce = false,
        ConcurrentDictionary<uint, byte>? returnShotgunned = null, int pierceHits = 1)
    {
        uint id;
        lock (conn.Monsters) id = conn.NextObjectId++;
        bool Active() => conn.ProjectileActive(epoch);
        if (!Active()) return;
        async Task<bool> SendVisual(byte[] bytes, string? line, Action? commit = null)
        {
            bool sent = false;
            while (!sent && Active())
            {
                await Send(remote, stream, ciphers, bytes, line,
                    still: () => sent = conn.TryCommitProjectileVisual(epoch, commit));
                if (!sent) await Task.Delay(16);
            }
            return sent;
        }
        double dx = to.X - from.X, dy = to.Y - from.Y, aim = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        // One launch-time speed drives collision sweeps, the client's glide duration and the server's flight clock.
        float cellsPerSecond = projectile.CellsPerSecond;
        if (!float.IsFinite(cellsPerSecond) || cellsPerSecond < 0) return;
        // It leaves a little ahead of the caster (Game_PROJECTILE_AHEAD cells, default 1.5) and flies toward the clicked
        // point or monster and on to Game_PROJECTILE_RANGE cells (default 120, ~1.2 s at the real speed), clamped to the
        // area. The Positioned x/y are whole grid cells, so an offset under one cell rounds back onto the player's own
        // cell: the arrow then spawns inside the character and only becomes visible after it has flown out of the body.
        double ahead = double.TryParse(Environment.GetEnvironmentVariable("Game_PROJECTILE_AHEAD"), inv, out var pa) ? pa : 1.5;
        double range = double.TryParse(Environment.GetEnvironmentVariable("Game_PROJECTILE_RANGE"), inv, out var pr) && pr > 0 ? pr : 120;
        ahead = Math.Min(ahead, aim - 1);
        from = ((int)Math.Round(from.X + dx / aim * ahead), (int)Math.Round(from.Y + dy / aim * ahead));
        double length = Math.Max(range - ahead, 1);
        if (TerrainHeights.Size(conn.Layout.Id, conn.Seed) is (int w, int h))
            foreach (var (d, lo, hi) in new[] { (dx, -from.X, w - 1 - from.X), (dy, -from.Y, h - 1 - from.Y) })
                if (d != 0) length = Math.Min(length, Math.Max(0, (d > 0 ? hi : lo) / (d / aim)));
        cellsPerSecond = ProjectileSpeed.ForFlightDistance(cellsPerSecond, length + 1);
        // Static walls cap the route; monsters are swept from observed positions during flight, not selected at launch.
        double hitRadius = double.TryParse(Environment.GetEnvironmentVariable("Game_PROJECTILE_HIT_RADIUS"), inv, out var phr) && phr > 0 ? phr : 4;
        to = ((int)Math.Round(from.X + dx / aim * length), (int)Math.Round(from.Y + dy / aim * length));
        bool hitWall;
        (to, hitWall) = ProjectileFlight.ClipWall(from, to,
            (x, y) => ProjectileGrid.Blocks(conn.Layout.Id, conn.Seed, x, y) == true);
        dx = to.X - from.X; dy = to.Y - from.Y;
        double cells = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        // Positioned rotation = atan2(dx, -dy) (the player's own moves, traced live 2026-10-18: facing (+15, -18) read 0.668)
        // Height: the captured terrain height plus Game_PROJECTILE_HEIGHT steps of 7.8125 world units (default 5, the
        // height the live server's arrow flew at: i32 -5, f32 +39.0625). The protocol carries no player z (C→S
        // 0x25/0x24 have only x,y, and the player's Positioned follows the terrain), so natural ground is the supported
        // case; without a capture the bare offset is used. A platform/doodad is not in the terrain grid (the hideout's
        // waypoint platform read z -78.37 while the terrain under it was 0..2 steps); that needs a server-side
        // world/surface height model (issue #182).
        int up = int.TryParse(Environment.GetEnvironmentVariable("Game_PROJECTILE_HEIGHT"), out int u) ? u : 5;
        int startHeight = (TerrainHeights.At(conn.Layout.Id, conn.Seed, from.X, from.Y) ?? 0) + up;
        // Positioned's marker and 0x1ba's endpoint are -worldZ / 7.8125, whereas ProjectileObject.Height
        // takes worldZ / 7.8125. A mismatched glide endpoint makes the native interpolation drift vertically.
        var height = new ProjectileHeight(startHeight);
        var obj = new ProjectileObject(projectile.Id, from.X, from.Y, (float)Math.Atan2(dx, -dy), Height: height.ObjectHeight);
        uint type = InstanceEntities.ObjectTypeHash(projectile.Id);
        // All observations below are taken with the monster lock held, including the initial observation when the
        // glide reaches the send gate. The game clock, not time spent waiting for the gate, starts the flight.
        // Filtration is per projectile, not per launch: a target that leaves the route before contact is not hit and
        // one that walks into it later is (the sweep in ProjectileFlight.Advance).
        List<ProjectileFlight.Target> Targets(double at) => [.. conn.Monsters
            .Where(m => !m.Value.Dead && m.Value.Life > 0 && (shotgunned is null || !shotgunned.ContainsKey(m.Key)))
            .Select(m => new ProjectileFlight.Target(m.Key, m.Value.Walk.Position(at), m.Value.Walk))];
        ProjectileFlight? flight = null;
        bool released = false;
        try
        {
            while (!released && Active())
            {
                if (!keepThroughActions && actionToken is int original && !OrdinaryBowReleaseCurrent(conn, original, epoch)) break;
                await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, type, obj.Blob(), headerU32: 0xffffffff), !log ? null :
                    $"0x02b9 create projectile 0x{id:x} {projectile.Id} type=0x{type:x8} at ({from.X}, {from.Y})", still: () =>
                    {
                        lock (conn.Monsters)
                        {
                            if (!Active() || conn.Paused) return false;
                            if (!keepThroughActions && actionToken is int token && !OrdinaryBowReleaseCurrent(conn, token, epoch)) return false;
                            conn.ProjectileObjects.Add(id);
                            released = true;
                            return true;
                        }
                    });
                if (!released) await Task.Delay(16);
            }
            if (!released) return;
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), !log ? null :
                $"0x01a9 projectile 0x{id:x} enters wake zone", still: Active);
            await Send(remote, stream, ciphers, height.MoveTo(id, to.X, to.Y, (float)(cells / cellsPerSecond)), !log ? null :
                $"0x01ba projectile 0x{id:x} to ({to.X}, {to.Y}), {cells:0.0} cells at {cellsPerSecond:0.0} cells/s, height marker {height.Marker} (world z {height.WorldZ})" +
                (hitWall ? ", route ends at a wall" : ""), still: () =>
                {
                    lock (conn.Monsters)
                    {
                        if (!Active()) return false;
                        double at = conn.Clock.Elapsed.TotalSeconds;
                        flight = new(from, to, cellsPerSecond, at, hitRadius, hitWall, Targets(at));
                        return true;
                    }
                });
            if (flight is null) return;
            // Pierce: the flight does not stop at monsters (Targets is empty above); every monster along the line, up
            // to the pierce budget, takes the hit as the projectile passes it. The client keeps gliding to the end,
            // which is what a piercing projectile does.
            int lineHits = 0;
            async Task ImpactEffect((int X, int Y) at)
            {
                if (impactObjectAo is null) return;
                uint effect;
                lock (conn.Monsters) effect = conn.NextObjectId++;
                var art = new EffectObject(at.X, at.Y, impactObjectAo);
                if (!await SendVisual(InstanceEntities.CreateObject(effect, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), art.Blob()),
                    $"0x02b9 create effect 0x{effect:x} {impactObjectAo} at ({at.X}, {at.Y}) (pierced target)")) return;
                if (!await SendVisual(InstanceProtocol.EnterWakeZone(effect), $"0x01a9 effect 0x{effect:x} enters wake zone")) return;
                if (!await WaitProjectileTime(conn, conn.Clock.Elapsed.TotalSeconds + impactObjectSeconds, epoch)) return;
                await SendVisual(InstanceProtocol.RemoveObject(effect), $"0x02bb remove effect 0x{effect:x}");
            }
            async Task ApplyDirect(uint mid, SpawnedMonster monster, string suffix)
            {
                if (damageTo is null) return;
                int hitDamage = 0;
                bool hitCritical = false;
                MonsterHit? struck = null;
                lock (conn.Monsters)
                {
                    if (monster.Dead || monster.Life == 0) return;
                    (hitDamage, hitCritical) = damageTo(monster);
                    struck = HitUnlessBlockedLocked(conn, monster, hitDamage);
                    shotgunned?.TryAdd(mid, 0);
                }
                if (struck is { } applied)
                    await NotifyMonsterHit(remote, stream, ciphers, conn, mid, monster, applied, hitDamage,
                        $"{projectile.Id}{suffix}{(hitCritical ? " (critical)" : "")}", log);
            }
            async Task ApplyReturnDirect(uint mid, SpawnedMonster monster)
            {
                if (returnDamageTo is null) return;
                int hitDamage = 0;
                bool hitCritical = false;
                MonsterHit? struck = null;
                lock (conn.Monsters)
                {
                    if (monster.Dead || monster.Life == 0) return;
                    (hitDamage, hitCritical) = returnDamageTo(monster);
                    struck = HitUnlessBlockedLocked(conn, monster, hitDamage);
                    returnShotgunned?.TryAdd(mid, 0);
                }
                if (struck is { } applied)
                    await NotifyMonsterHit(remote, stream, ciphers, conn, mid, monster, applied, hitDamage,
                        $"{projectile.Id} (returning{(hitCritical ? ", critical" : "")})", log);
            }
            ProjectileFlight.Step step = default;
            if (pierceHits > 1)
            {
                // Pierce: the flight does not stop at monsters; every contact found by the live sweep (not a launch-time
                // snapshot) spends one point of the budget. The client keeps gliding to the end, which is what a
                // piercing projectile does.
                HashSet<uint> pierced = [];
                int budget = pierceHits;
                while (true)
                {
                    await Task.Delay(16);
                    IReadOnlyList<ProjectileFlight.Pierced> contacts = [];
                    lock (conn.Monsters)
                    {
                        if (!Active()) return;
                        if (conn.Paused) continue;
                        double now = conn.Clock.Elapsed.TotalSeconds;
                        if (budget > 0)
                            (step, contacts) = flight.AdvancePiercing(now, Targets(now), pierced);
                        else
                            step = flight.Advance(now, []);
                    }
                    foreach (var entry in contacts)
                    {
                        if (budget <= 0) break;
                        if (!Active()) return;
                        if (!conn.Monsters.TryGetValue(entry.Id, out var monster) || monster.Dead || monster.Life == 0)
                            continue;
                        pierced.Add(entry.Id);
                        budget--;
                        await ApplyDirect(entry.Id, monster, " (pierced)");
                        if (onImpact is not null)
                        {
                            var at = ((int)Math.Round(entry.Position.X), (int)Math.Round(entry.Position.Y));
                            // Kinetic Blast: the collision's explosions never hit the target they were created from
                            // (poewiki "Kinetic Blast": a typical-sized target is not hit by its own collision's explosions).
                            await onImpact(at, entry.Id);
                            _ = ImpactEffect(at);
                        }
                        lineHits++;
                    }
                    if (step.Finished) break;
                }
            }
            bool committed = false;
            SpawnedMonster? target = null;
            MonsterHit? hit = null;
            int damage = 0;
            bool critical = false;
            // Piercing flights are resolved by the live sweep above; this terminal sweep only handles the ordinary
            // first-contact flight.
            while (pierceHits <= 1)
            {
                await Task.Delay(16);
                if (ordinaryShot is not null)
                {
                    // Accuracy and the accepted contact's impale/life effects share the actual relocation gate.
                    // A queued sample invalidated by pause/transfer consumes no RNG or target state. A miss can
                    // accept a later contact in this same sweep, without an intermediate packet or melee hit.
                    await Send(remote, stream, ciphers, [], null, atGate: () =>
                    {
                        lock (conn.Monsters)
                        {
                            if (!Active() || conn.Paused) return null;
                            bool Accept(uint targetId) => conn.Monsters.TryGetValue(targetId, out var monster)
                                && !monster.Dead && monster.Life > 0
                                && ordinaryShot.Hits(Monsters.BaseEvasion(monster.Level) ?? 0, Random.Shared);
                            double now = conn.Clock.Elapsed.TotalSeconds;
                            step = flight.Advance(now, Targets(now), Accept);
                            if (!step.Finished || (step.TargetId == 0 && !step.Wall)) return null;
                            if (step.TargetId != 0 && conn.Monsters.TryGetValue(step.TargetId, out target) && !target.Dead && target.Life > 0)
                            {
                                (damage, critical) = ordinaryShot.Roll(target.Impale, Random.Shared,
                                    (rolled, crit) => MonsterDamage(target, rolled, crit, attack: true,
                                        ordinaryShot.ResistanceMods, ordinaryShot.Penetration).Damage,
                                    CriticalMultiplierOn(target, ordinaryShot.CriticalMultiplier),
                                    extraCriticalRolls: ordinaryShot.CriticalChance > 0 ? CriticalExtraRolls(conn) : 0,
                                    criticalRandom: conn.CriticalRandom,
                                    cannotCrit: ordinaryShot.CriticalChance > 0 && CriticalProhibited(conn, attack: true));
                                hit = ApplyMonsterHitLocked(conn, target, damage);
                                if (hit is not null) RageOnHitLocked(conn, attack: true, melee: false, critical);
                                if (target.Life == 0) target.Impale.Clear();
                            }
                            committed = true;
                            int x = (int)Math.Round(step.Position.X), y = (int)Math.Round(step.Position.Y);
                            if (log) Console.WriteLine($"[gateway] {remote} => arrow 0x{id:x} impact at ({x}, {y}) on {(step.Wall ? "wall" : $"monster 0x{step.TargetId:x}")} (t={step.At:0.000})");
                            return height.StopAt(id, x, y);
                        }
                    });
                    if (!Active()) return;
                    if (step.Finished) break;
                    continue;
                }
                lock (conn.Monsters)
                {
                    if (!Active()) return;
                    if (conn.Paused) continue;
                    double now = conn.Clock.Elapsed.TotalSeconds;
                    step = flight.Advance(now, Targets(now));
                }
                if (step.Finished) break;
            }
            int hitX = (int)Math.Round(step.Position.X), hitY = (int)Math.Round(step.Position.Y);
            uint returnedFrom = step.TargetId;
            bool contact = step.TargetId != 0 || step.Wall;
            if (contact)
            {
                // Copy creation's marker/world-Z pair at contact; do not resample terrain at impact.
                while (!committed && Active())
                {
                    // 0x1b7 relocates and cancels the existing glide (0x141e31200 -> 0x141e35250). Commit the hit
                    // under the same gate and short monster lock; a pause/transfer while waiting defers/cancels it.
                    await Send(remote, stream, ciphers, height.StopAt(id, hitX, hitY), !log ? null :
                        $"0x01b7 projectile 0x{id:x} impact at ({hitX}, {hitY}) on {(step.Wall ? "wall" : $"monster 0x{step.TargetId:x}")} (t={step.At:0.000})", still: () =>
                        {
                            lock (conn.Monsters)
                            {
                                if (!Active() || conn.Paused) return false;
                                if (damageTo is not null && step.TargetId != 0 && conn.Monsters.TryGetValue(step.TargetId, out target) && !target.Dead && target.Life > 0)
                                {
                                    (damage, critical) = damageTo(target);
                                    hit = HitUnlessBlockedLocked(conn, target, damage);
                                    if (hit is not null) acceptedHit?.Invoke(target, critical);
                                    shotgunned?.TryAdd(step.TargetId, 0);
                                }
                                committed = true;
                                return true;
                            }
                        });
                    if (!committed) await Task.Delay(16);
                }
                if (!committed) return;
                if (critical && hit is not null) _ = GainFlaskChargesOnCrit(remote, stream, ciphers, conn);
                if (hit is { } applied && target is not null)
                    await NotifyMonsterHit(remote, stream, ciphers, conn, step.TargetId, target, applied, damage,
                        $"{projectile.Id}{(critical ? " (critical)" : "")}", log);
            }
            else if (onImpact is not null)
            {
                Console.WriteLine($"[gateway] {remote} => {projectile.Id} lands at ({hitX}, {hitY}) (t={step.At:0.000})");
                // Stop the glide at the landing cell: the explosion art is attached to this object, so it must stand
                // where it landed rather than keep interpolating to the glide's endpoint.
                await Send(remote, stream, ciphers, height.StopAt(id, hitX, hitY), !log ? null :
                    $"0x01b7 projectile 0x{id:x} stops where it lands at ({hitX}, {hitY}) (t={step.At:0.000})");
            }
            // A skill whose impact is its landing (Kinetic Blast) also bursts at the aimed point when the flight finds
            // nothing to touch, so a ground cast shows the explosion and hits the area; other projectile skills keep
            // their behaviour (their impact art plays only on contact).
            bool projectileRemoved = false;
            Task<bool>? effectLifetime = null;
            uint effectId = 0;
            if (contact || onImpact is not null)
            {
                impact = Environment.GetEnvironmentVariable("Game_PROJECTILE_IMPACT_AO") ?? impact ?? DefaultProjectileImpactAo;
                if (!delayedImpact && impact != "none" && impactObjectAo is null)
                {
                    if (!await SendVisual(InstanceEntities.AnimatedModel(id, ProjectileObject.AnimatedComponentIndex, impact),
                        $"0x02ba projectile 0x{id:x} explodes ({impact})")) return;
                }
                // Some effects are not drawn from a projectile's Animated component (Kinetic Blast's cluster_burst):
                // they are a standalone effect object of their own, as Righteous Fire's ring is (EffectObject).
                if (impactObjectAo is not null && !delayedImpact && !(lineHits > 0 && onImpact is not null))
                {
                    lock (conn.Monsters) effectId = conn.NextObjectId++;
                    var effect = new EffectObject(hitX, hitY, impactObjectAo);
                    if (!await SendVisual(InstanceEntities.CreateObject(effectId, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), effect.Blob()),
                        $"0x02b9 create effect 0x{effectId:x} {impactObjectAo} at ({hitX}, {hitY})")) return;
                    if (!await SendVisual(InstanceProtocol.EnterWakeZone(effectId),
                        $"0x01a9 effect 0x{effectId:x} enters wake zone")) return;
                    // The art runs on its own; its lifetime starts now so the return below is not delayed by it.
                    effectLifetime = WaitProjectileTime(conn, conn.Clock.Elapsed.TotalSeconds + impactObjectSeconds, epoch);
                }
                // The hit lands at the impact, not after the explosion's art: a delayed area hit read as the
                // explosion doing nothing for a second.
                if (onImpact is not null && lineHits == 0)
                {
                    if (!Active()) return;
                    await onImpact((hitX, hitY), step.TargetId);
                }
                if (impactObjectAo is not null && !returning)
                {
                    await SendVisual(InstanceProtocol.RemoveObject(id), $"0x02bb remove projectile 0x{id:x}",
                        () => conn.ProjectileObjects.Remove(id));
                    projectileRemoved = true;
                }
                else if (impactObjectAo is null && impact != "none")
                {
                    if (!await WaitProjectileTime(conn, conn.Clock.Elapsed.TotalSeconds + 1.5, epoch)) return;
                }
            }
            // A returning projectile flies back through the caster right after the impact, on the reverse line: it reaches
            // the player and continues for 15 m past them (the wiki's "typical projectiles"; 1 cell = 0.1 m, so 150 cells),
            // then is removed. Its hits on the way back are not modelled yet (the skill's impact damage is already
            // applied). Freezing Pulse only returns from a collision, not at maximum distance.
            int returnX = hitX, returnY = hitY;
            if (returning && !projectileRemoved && Active() && (returnOnLanding || contact))
            {
                (int X, int Y) player;
                lock (conn.Monsters) player = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
                double rdx = player.X - hitX, rdy = player.Y - hitY;
                double back = Math.Max(1, Math.Sqrt(rdx * rdx + rdy * rdy));
                double pastDistance = double.TryParse(Environment.GetEnvironmentVariable("Game_PROJECTILE_RETURN_DISTANCE"),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double devDistance)
                    ? Math.Clamp(devDistance, 0, 400) : 150;
                double past = back + pastDistance;
                returnX = (int)Math.Round(hitX + rdx / back * past);
                returnY = (int)Math.Round(hitY + rdy / back * past);
                double backSpeed = Math.Max(1, cellsPerSecond * (1 + returnSpeedPercent / 100));
                // Damage while returning: the monsters along the return line, except the one the projectile returned
                // from. Returning Projectiles Support's "pierce all targets while returning" keeps them all, otherwise
                // the first stops the volley's damage (the flight itself still comes back; the wiki's stop is visual).
                if (returnDamageTo is not null || returnExplosion is not null)
                {
                    List<(uint Id, SpawnedMonster Monster, (int X, int Y) At)> along = [];
                    double now = conn.Clock.Elapsed.TotalSeconds;
                    lock (conn.Monsters)
                        foreach (var (mid, monster) in conn.Monsters)
                        {
                            if (monster.Dead || monster.Life == 0 || mid == returnedFrom) continue;
                            if (returnShotgunned?.ContainsKey(mid) == true) continue;
                            var at = monster.Walk.Position(now);
                            if (ProjectileReturn.DistanceToSegment(at, (hitX, hitY), (returnX, returnY)) <= hitRadius)
                                along.Add((mid, monster, at));
                        }
                    along.Sort((a, b) => Dist((hitX, hitY), a.At).CompareTo(Dist((hitX, hitY), b.At)));
                    int returnBudget = returnPierce ? int.MaxValue : pierceHits;
                    if (along.Count > returnBudget) along.RemoveRange(returnBudget, along.Count - returnBudget);
                    // General pierce applies on the way back too: the support's "pierce all while returning" wins,
                    // otherwise the pierce budget (one hit without pierce).
                    foreach (var (mid, monster, at) in along)
                    {
                        if (!Active()) return;
                        await ApplyReturnDirect(mid, monster);
                        if (returnExplosion is not null) await returnExplosion(at, mid);
                    }
                }
                // Two legs, as the wiki describes: the projectile travels to the caster, then continues 15 m past them.
                // One combined glide made the client stop the returning object in the player's model (live 2026-10-26).
                double toCaster = Math.Max(0.05, back / backSpeed);
                double pastCaster = Math.Max(0.05, pastDistance / backSpeed);
                if (!await SendVisual(height.MoveTo(id, player.X, player.Y, (float)toCaster),
                    $"0x01ba projectile 0x{id:x} returns to the caster ({player.X}, {player.Y})")) return;
                if (!await WaitProjectileTime(conn, conn.Clock.Elapsed.TotalSeconds + toCaster, epoch)) return;
                if (!await SendVisual(height.MoveTo(id, returnX, returnY, (float)pastCaster),
                    $"0x01ba projectile 0x{id:x} continues past the caster to ({returnX}, {returnY})")) return;
                if (!await WaitProjectileTime(conn, conn.Clock.Elapsed.TotalSeconds + pastCaster + 0.1, epoch)) return;
            }
            // Fireball (and the other skills that explode only when they cannot move further) bursts after the return,
            // not at the first collision: the wiki's 3.21 return rule.
            if (delayedImpact && impactObjectAo is not null && Active())
            {
                uint delayedId;
                lock (conn.Monsters) delayedId = conn.NextObjectId++;
                var delayed = new EffectObject(returnX, returnY, impactObjectAo);
                if (!await SendVisual(InstanceEntities.CreateObject(delayedId, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), delayed.Blob()),
                    $"0x02b9 create effect 0x{delayedId:x} {impactObjectAo} at ({returnX}, {returnY}) after the return")) return;
                if (!await SendVisual(InstanceProtocol.EnterWakeZone(delayedId),
                    $"0x01a9 effect 0x{delayedId:x} enters wake zone")) return;
                if (!await WaitProjectileTime(conn, conn.Clock.Elapsed.TotalSeconds + impactObjectSeconds, epoch)) return;
                await SendVisual(InstanceProtocol.RemoveObject(delayedId), $"0x02bb remove effect 0x{delayedId:x}");
            }
            // The standalone impact object goes once its art has played, beside the return rather than before it.
            if (effectLifetime is not null)
            {
                if (!await effectLifetime) return;
                if (effectId != 0)
                    await SendVisual(InstanceProtocol.RemoveObject(effectId), $"0x02bb remove effect 0x{effectId:x}");
            }
            if (!projectileRemoved)
                await SendVisual(InstanceProtocol.RemoveObject(id), $"0x02bb remove projectile 0x{id:x}",
                    () => conn.ProjectileObjects.Remove(id));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } // The old area's connection has gone.
    }

    // Release and terminal visual lifetimes share the game clock and area epoch with an active flight.
    static async Task<bool> WaitProjectileTime(ClientState conn, double until, int epoch)
    {
        while (true)
        {
            lock (conn.Monsters)
            {
                if (!conn.ProjectileActive(epoch)) return false;
                if (!conn.Paused && conn.Clock.Elapsed.TotalSeconds >= until) return true;
            }
            await Task.Delay(16);
        }
    }

    // A hit on a living monster: life is mutated once under Monsters, then notifications await outside the lock. False when
    // the monster was dead already or the action was replaced (the hit did not land).
    static async Task<bool> HitMonster(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster m,
        int damage, string by, bool log = true, bool melee = false, ushort sequence = 0, int? actionToken = null,
        CooldownUseTicket? cooldownUse = null, Action? accepted = null)
    {
        MonsterHit? hit;
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea || conn.RageRetired || conn.PlayerLife == 0 || conn.Paused) return false;
            if (cooldownUse is not null ? !cooldownUse.Current(conn) : actionToken is int token && !PlayerActionCurrent(conn, token)) return false;
            hit = ApplyMonsterHitLocked(conn, m, damage);
            if (hit is not null) accepted?.Invoke();
        }
        if (hit is { } applied) await NotifyMonsterHit(remote, stream, ciphers, conn, id, m, applied, damage, by, log, melee, sequence);
        return hit is not null;
    }

    // The damage a player's outgoing hit deals to its monster: one roll per type, then each type's own base resistance
    // with the hit's penetration subtracted from the capped value (HitMitigation), summed to the total the critical
    // multiplier later applies to. Damage over time has neither here and keeps its own path. Internal for the tests.
    internal static int MonsterDamage(SpawnedMonster m, DamagePacket hit, Random random, Penetration.PerType pen) =>
        MonsterTakes(m, hit, random, spell: false, pen);

    // Resolve an already-rolled packet using its single critical draw; target damage taken follows mitigation.
    internal static (int Damage, string? Resistance) MonsterDamage(SpawnedMonster m, DamagePacket rolled,
        bool critical, bool attack, HitResistances.Mods mods, Penetration.PerType pen = default)
    {
        var described = new List<string>();
        var strike = new HitResistances.Strike(Critical: critical, Attack: attack);
        var mitigated = DamageModel.Mitigate(rolled, type =>
        {
            var value = MonsterHitResistance(m, type, pen);
            if (rolled[type].Max > 0 && HitResistances.Describe(type, value, strike, mods) is { } note) described.Add(note);
            return HitResistances.Effective(type, value, strike, mods);
        });
        return (MonsterTakesMitigated(m, mitigated, spell: !attack),
            described.Count > 0 ? string.Join(", ", described) : null);
    }

    internal static HitResistances.Mods HitResistMods(IReadOnlyDictionary<uint, int> player,
        IReadOnlyDictionary<uint, int>? skill = null)
    {
        var a = HitResistances.Mods.From(player, Stats.Key);
        var b = skill is null ? default : HitResistances.Mods.From(skill, Stats.Key);
        return new(a.CriticalIgnoreElemental | b.CriticalIgnoreElemental,
            a.AttackCriticalIgnoreElemental | b.AttackCriticalIgnoreElemental,
            a.NonCriticalPenetration + b.NonCriticalPenetration, a.CannotPenetrateOrIgnore | b.CannotPenetrateOrIgnore);
    }

    // The short state mutation is shared with projectile impact; no awaits under Monsters.
    static MonsterHit? ApplyMonsterHitLocked(ClientState conn, SpawnedMonster m, int damage)
    {
        if (m.Dead || m.Life == 0) return null;
        (m.Life, bool starts) = TakeDamageLocked(m, damage);
        conn.LastEnemyHitAt = conn.Clock.Elapsed.TotalSeconds;
        if (m.Life == 0) { m.Aggro = false; m.Dead = true; }
        return new(m.Life, starts);
    }

    static async Task NotifyMonsterHit(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster m,
        MonsterHit hit, int damage, string by, bool log = true, bool melee = false, ushort sequence = 0)
    {
        var (life, stageStarts) = hit;
        if (life == 0)
            await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(id, [(StatKeys.HideMiniLifeBar, 1)]),
                !log ? null : $"0x01b3 monster 0x{id:x} hide_mini_life_bar = 1");
        // The hit result for the client's damage/effect pipeline (0x1af): a melee hit plays the target's blood and the weapon
        // impact sound "bonk" locally, 0x1b1 stays authoritative on life. Spells and projectiles have their own impact and skip it.
        if (melee)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            var (px, py) = conn.Walk.Position(now);
            var (mx, my) = m.Walk.Position(now);
            await Send(remote, stream, ciphers,
                InstanceProtocol.ActionHit(PlayerId, sequence, id, flags: 0x4201, kind: 0x03, angle: HitAngle(mx - px, my - py)),
                !log ? null : $"0x01af player hit on monster 0x{id:x} (skill, sequence 0x{sequence:x4})");
        }
        // The hit reaction: one of the rig's TakeHit clips and its SoundEvent (0x1b2), as in Attack.
        await SendFlinch(remote, stream, ciphers, conn, id, m, damage, log);
        await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(id, life),
            !log && life > 0 ? null : $"0x01b1 monster 0x{id:x} hit by {by} for {damage}, life {life}/{m.MaxLife}{(life == 0 ? " (dies)" : "")}");
        if (life == 0) await MonsterKilled(remote, stream, ciphers, conn, id, m);
        if (stageStarts) _ = RunBossStage(remote, stream, ciphers, conn, id, m);
    }

    // The hit reaction: one of the rig's TakeHit clips and its SoundEvent, S→C 0x1b2 kind 0 (see Attack and HitMonster).
    // A killing blow gets none: the client's Life component runs the death instead, and the flinch would only be cut off.
    // The gates (HitReaction) keep it a stun-like reaction rather than a loop; see that class. The monster is checked again
    // inside the send gate: a hit that kills it meanwhile marks it Dead before it sends its own 0x1b1, so the flinch either
    // goes out before the death or not at all.
    static async Task SendFlinch(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
        SpawnedMonster m, int damage, bool log)
    {
        HitAnimation? clip;
        lock (conn.Monsters) clip = HitReaction.TryFlinch(m, damage, conn.Clock.Elapsed.TotalSeconds, Random.Shared);
        if (clip is not { } flinch) return;
        await Send(remote, stream, ciphers, InstanceProtocol.AnimationUpdate(id, 0, (uint)flinch.Index, 1f),
            !log ? null : $"0x01b2 monster 0x{id:x} flinch (animation {flinch.Index}, {flinch.Seconds:0.00} s)",
            still: () => { lock (conn.Monsters) return m.Life > 0 && !m.Dead; });
    }

    // Native TakeHit owns the client's predictive cancellation and recovery lockout. Commit the matching server state
    // inside the send gate, so death or another recovery while this packet waits cannot send an obsolete flinch.
    static async Task SendPlayerHitRecovery(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        int damage)
    {
        if (damage <= 0) return;
        var stats = CharacterPlayerStats(conn);
        if (stats.Any(s => s.Value != 0 && PlayerHitRecovery.UnsupportedStat(Stats.Name(s.Key)))) return;
        // Skill immunity can depend on a native action's contact phase, which this server does not track yet.
        // Conservatively skip builds with any equipped skill carrying it, rather than stun an uninterruptible phase.
        if (GemSkillEffects(conn.Character).Any(g => Skills.EffectStats(g.Effect, g.Level, g.Quality)
            .Any(s => s.Item2 != 0 && Stats.Name(s.Item1).StartsWith("skill_cannot_be_stunned", StringComparison.Ordinal)))) return;
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        if (PlayerHasEnergyShieldForRecovery(conn, Get)) return;
        double chance = PlayerHitRecovery.Chance(damage, conn.PlayerMaxLife, Get);
        float seconds = PlayerHitRecovery.Duration(Get);
        if (seconds <= 0 || Random.Shared.NextDouble() * 100 >= chance) return;
        await Send(remote, stream, ciphers,
            InstanceProtocol.ActorAnimation(PlayerId, seconds, PlayerHitRecovery.TakeHitAnimation),
            $"0x01d1 player TakeHit recovery {seconds:0.000} s ({chance:0.#}% chance, {damage} physical damage)",
            still: () =>
            {
                lock (conn.Monsters)
                {
                    if (conn.Closed || conn.PlayerLife == 0 || !conn.HitRecovery.Begin(conn.Clock.Elapsed.TotalSeconds, seconds)) return false;
                    Interlocked.Increment(ref conn.ActionCount);
                    conn.ChannelSkill = 0;
                    double now = conn.Clock.Elapsed.TotalSeconds;
                    conn.NextAttackAt = now;
                    var at = conn.Walk.Position(now);
                    conn.Walk.Teleport(at.X, at.Y, now);
                    return true;
                }
            });
    }

    static bool PlayerActionCurrent(ClientState conn, int action)
    {
        lock (conn.Monsters)
            return !conn.Closed && !conn.LeavingArea && !conn.RageRetired && conn.PlayerLife > 0
                && conn.ActionCount == action && !conn.HitRecovery.Active(conn.Clock.Elapsed.TotalSeconds);
    }

    // A hit's damage off a monster's life (under the Monsters lock): the new life, and whether the hit starts its boss stage
    // (BossFights; the stage is then taken, so it runs once).
    static (uint Life, bool StageStarts) TakeDamageLocked(SpawnedMonster m, int damage)
    {
        // While the stage runs the boss cannot die: its life fills up at the end anyway.
        if (m.StageRunning) return ((uint)Math.Max(1, (long)m.Life - damage), false);
        var (life, starts) = BossFights.Damage(m.Life, m.MaxLife, damage, m.StageTaken ? null : m.Stage);
        if (starts) m.StageTaken = m.StageRunning = true;
        return (life, starts);
    }

    // A boss's stage (BossFights): it stops and uses its stage skill in place (0x1ab, the skill's animation: Hillock pulls
    // out his sword), then its Transitionable steps to the stage's state (0x2ba: the sword in his hand) and its life fills
    // up (0x1b1).
    static async Task RunBossStage(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster m)
    {
        var stage = m.Stage!;
        (int X, int Y) at;
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            at = m.Walk.Position(now);
            m.Walk.Order(at.X, at.Y, now);
            m.LastTarget = null;
            m.BusyUntil = now + stage.CastSeconds;
        }
        await Send(remote, stream, ciphers, InstanceProtocol.ActionStarted(id, at.X, at.Y, at.X, at.Y, StageSkillId, 0xffff, 0x0408),
            $"0x01ab monster 0x{id:x} ({m.Label}) starts its stage at {m.Life}/{m.MaxLife} life: skill 0x{StageSkillId:x4} (GrantedEffects {stage.SkillEffect}), {stage.CastSeconds:0.#} s");
        await Task.Delay(TimeSpan.FromSeconds(stage.CastSeconds));
        await WhilePaused(conn);
        uint life;
        lock (conn.Monsters)
        {
            m.StageRunning = false;
            if (m.Dead || conn.Closed) return;
            m.Life = life = m.MaxLife;
        }
        if (m.Variety?.ComponentsOrBase.ToList().IndexOf("Transitionable") is int t and >= 0)
            await Send(remote, stream, ciphers, InstanceEntities.TransitionableUpdate(id, (byte)t, stage.TransitionTo),
                $"0x02ba monster 0x{id:x} ({m.Label}) Transitionable → {stage.TransitionTo}");
        await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(id, life), $"0x01b1 monster 0x{id:x} ({m.Label}) life refilled to {life}");
        // The stage's action is over: 0x1ac ends it, so the client never keeps the boss in it.
        await Send(remote, stream, ciphers, InstanceProtocol.ActionEnded(id), $"0x01ac monster 0x{id:x} ({m.Label}) stage action ended");
    }

    // A boss whose variety has the big life bar (MonsterVarieties BossHealthBar) shows it at the top of the screen once it
    // aggroes: its StateMachine's boss_life_bar (Metadata/Monsters/BossLifeBar.ot) and aggro (Hillock's aggro effect) go to 1.
    static async Task ShowBossLifeBar(string remote, NetworkStream stream, ConnectionCiphers ciphers, uint id, SpawnedMonster m, ClientState conn)
    {
        if (m.Variety is not { } v || m.States is not { } states) return;
        int index = v.ComponentsOrBase.ToList().IndexOf("StateMachine");
        if (index < 0) return;
        states = states.Set("boss_life_bar", 1).Set("aggro", 1);
        lock (conn.Monsters) m.States = states;
        await Send(remote, stream, ciphers, InstanceEntities.StateMachineUpdate(id, (byte)index, states),
            $"0x02ba monster 0x{id:x} ({m.Label}) StateMachine: boss_life_bar 1, aggro 1");
    }

    // A monster died (its life reached 0 from the player's attack or skill): the player gains its experience when near
    // enough, it drops its items around where it fell (ItemDrops), and its corpse is removed 10 s later.
    static async Task MonsterKilled(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster m)
    {
        double now = conn.Clock.Elapsed.TotalSeconds;
        (int X, int Y) at, player;
        bool alive;
        lock (conn.Monsters) { at = m.Walk.Position(now); player = conn.Walk.Position(now); alive = conn.PlayerLife > 0; }
        _ = Task.Delay(10_000).ContinueWith(async _ =>
        {
            bool removed;
            lock (conn.Monsters) removed = conn.Monsters.Remove(id);
            if (removed && !conn.Closed)
                await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove dead monster 0x{id:x}");
        });
        if (m.Variety is not { } variety || !m.Hostile) return;
        ChargesOnKill(conn, m);
        if (conn.Layout.Id == TutorialObjects.Strand && variety.Id == TutorialObjects.Zombie
            && CharacterSaves.Load(CharacterName(conn))?.Tutorial is { ZombieKilled: false })
        {
            CharacterSaves.ChangeTutorial(CharacterName(conn), t => t with { ZombieKilled = true });
            await DropTutorialGem(remote, stream, ciphers, conn, false, at.X, at.Y);
        }
        await SetQuestFlags(remote, stream, ciphers, conn, Quests.OnMonsterKilled(conn.Layout.Id, variety.Id));
        await DropQuestItems(remote, stream, ciphers, conn, variety.Id, at);   // a1q5: Hailrake's Medicine Chest
        if (alive && Dist(at, player) <= ExperienceRange && Monsters.BaseExperience(variety, m.Level) is int baseXp)
        {
            uint xp = Progression.MonsterExperience(baseXp, variety.ExperienceMultiplier, m.Rarity, conn.Pools.Level, m.Level);
            // The gems gain their share of the kill only (not a quest's level or /add_xp), at the character's cap too.
            await GainGemExperience(remote, stream, ciphers, conn, xp);
            await GainExperience(remote, stream, ciphers, conn, xp, $"{m.Rarity} {m.Label} 0x{id:x}");
        }
        // A quest kill that levels the character up (a1q1: Hillock → at least level 2), wherever the player stands.
        if (Quests.LevelAfterKill(conn.Layout.Id, variety.Id) is int level and > 1 && level <= CharacterStats.LevelStarts.Count)
        {
            uint needed = CharacterStats.LevelStarts[level - 1], have;
            lock (conn.Monsters) have = conn.Experience;
            if (have < needed) await GainExperience(remote, stream, ciphers, conn, needed - have, $"quest: {m.Label} killed, level {level}");
        }
        await GainKillCharges(remote, stream, ciphers, conn, m.Rarity, $"{m.Rarity} {m.Label} 0x{id:x} killed");
        await DropLoot(remote, stream, ciphers, conn, m, at);
    }

    // The monster's items (ItemDrops.Roll) as WorldItem objects scattered around where it died, on walkable cells when the
    // area's walk map is known. Like dropped items they lie in the instance (InstanceStore).
    static async Task DropLoot(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, SpawnedMonster m, (int X, int Y) at)
    {
        if (Environment.GetEnvironmentVariable("Game_DROPS") == "0") return;
        List<ItemDrops.Drop> drops = [.. Drops.Roll(m.Level, m.Rarity, Random.Shared, IncreasedItemRarity(conn))];
        // And a pile of gold (ItemDrops.Gold), picked up into the stash's gold.
        if (Drops.Gold(m.Level, m.Rarity, Random.Shared) is { } gold) drops.Add(gold);
        foreach (var drop in drops)
        {
            // A ring around the corpse, 3 to 6 cells out.
            double angle = Random.Shared.NextDouble() * Math.Tau, r = 3 + Random.Shared.NextDouble() * 3;
            int x = at.X + (int)Math.Round(Math.Cos(angle) * r), y = at.Y + (int)Math.Round(Math.Sin(angle) * r);
            if (conn.Layout.Walkable is { } walks && !walks(x, y)) (x, y) = at;
            var ground = new WorldItemObject(x, y, drop.Item);
            uint id = conn.Instance.Ground.Add(ground);
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), ground.Blob()),
                $"0x02b9 create world item 0x{id:x} {drop.Type.Name} (item level {drop.Item.ItemLevel}) dropped by {m.Label} at ({x}, {y})");
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
        }
    }

    // /god: what it adds on top of the character's stats (GodStats) and pools (PoolsFor): 1000% increased attack and cast
    // speed, 100% of maximum life and 10 000 mana regenerated per second, 200% increased area of effect, +10 000 maximum
    // life and +1000 to each attribute.
}
