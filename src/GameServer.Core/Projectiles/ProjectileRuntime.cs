namespace GameServer.Core.Projectiles;

/// <summary>A deterministic connection/instance-owned combat simulator. The caller serializes all access.
/// Simulation never awaits presentation. Global earliest contacts run before later contacts in other flights.</summary>
public sealed partial class ProjectileRuntime(IProjectileWorld world)
{
    public const long QuantaPerSecond = 120000, TickQuanta = 4000;
    readonly SortedDictionary<long, ProjectileEntity> projectiles = [];
    readonly PriorityQueue<Action, (long At, long Order)> scheduled = new();
    readonly ProjectileHitLedger ledger = new();
    long sequence, commandSequence;
    public long Now { get; private set; }
    public IReadOnlyCollection<ProjectileEntity> Projectiles => projectiles.Values;
    public static long Time(double seconds) => checked((long)Math.Round(seconds * QuantaPerSecond));

    public void Schedule(long at, Action command)
    {
        if (at < Now) throw new ArgumentOutOfRangeException(nameof(at), "Cannot insert a command in committed history.");
        scheduled.Enqueue(command, (at, ++commandSequence));
    }

    public long Release(ProjectileUse use)
    {
        Validate(use.Profile);
        if (use.ProjectilePayloads is { } payloads && payloads.Count != use.Profile.Count)
            throw new ArgumentException("Payload count must match initial projectile count.", nameof(use));
        long useId = ++sequence, emission = ++sequence;
        var random = use.Profile.RandomSpread ? new Random(unchecked(use.Seed * 397 ^ (int)useId)) : null;
        int count = use.Profile.Count;
        double spread = use.Profile.SpreadRadians;
        if (spread is > 0 and < Math.PI && use.Profile.AimSpreadDistance > 0)
            spread = 2 * Math.Atan(Math.Tan(spread / 2) * use.Profile.AimSpreadDistance
                / Math.Max(.001, (use.Aim - use.Origin).Length));
        for (int i = 0; i < count; i++)
        {
            double angle = random is not null ? spread * (random.NextDouble() - .5) : count <= 1 ? 0 : spread *
                (i / (double)(spread >= Math.Tau ? count : count - 1) - .5);
            var direction = (use.Aim - use.Origin).Unit.Rotate(angle);
            long group = use.Profile.SequentialDelay > 0 && i > 0 ? ++sequence : emission;
            var projectileUse = use.ProjectilePayloads is { } snapshots
                ? use with { Payload = snapshots[i], ProjectilePayloads = null } : use;
            void Emit() => Spawn(projectileUse, useId, group, 0, use.Origin, direction);
            if (use.Profile.SequentialDelay > 0 && i > 0) Schedule(Now + i * use.Profile.SequentialDelay, Emit);
            else Emit();
        }
        return useId;
    }

    static void Validate(ProjectileProfile p)
    {
        if (p.Count is < 1 or > 256 || !double.IsFinite(p.Speed) || p.Speed <= 0
            || !double.IsFinite(p.Range) || p.Range <= 0 || !double.IsFinite(p.Radius) || p.Radius < 0
            || p.Lifetime <= 0 || p.SequentialDelay < 0)
            throw new ArgumentOutOfRangeException(nameof(p));
    }

    ProjectileEntity Spawn(ProjectileUse use, long useId, long emission, long parent,
        ProjectileVector position, ProjectileVector direction, ProjectileEntity? continuing = null, bool split = false)
    {
        var p = new ProjectileEntity(++sequence, useId, emission, parent, use, position, direction, Now);
        projectiles.Add(p.Id, p);
        if (p.Profile.Lobbed) p.Remaining = Math.Max(.001, Math.Min(p.Remaining, (use.Aim - position).Length));
        if (continuing is { } previous)
        {
            p.Remaining = previous.Remaining; p.Distance = previous.Distance; p.ExpiresAt = previous.ExpiresAt;
            p.Speed = previous.Speed; p.Form = previous.Form; p.DamageMultiplier = previous.DamageMultiplier;
            p.Pierce = previous.Pierce; p.Fork = previous.Fork; p.Chain = previous.Chain; p.Split = 0;
            p.Contacts.UnionWith(previous.Contacts); p.Interactions.UnionWith(previous.Interactions);
        }
        if (split && p.Profile.SplitForm is { } splitForm)
        {
            p.Form = 1; p.Speed *= splitForm.SpeedMultiplier; p.Pierce = splitForm.Pierce;
        }
        Publish(ProjectileEventKind.Spawn, p);
        StartStages(p);
        return p;
    }

    public void AdvanceTo(long until)
    {
        if (until < Now) throw new ArgumentOutOfRangeException(nameof(until));
        while (true)
        {
            while (scheduled.TryPeek(out _, out var due) && due.At <= Now) scheduled.Dequeue()();
            if (projectiles.Count == 0)
            {
                if (Now == until) break;
                Now = scheduled.TryPeek(out _, out var idleNext) ? Math.Min(until, idleNext.At) : until;
                continue;
            }
            long horizon = Math.Min(until, (Now / TickQuanta + 1) * TickQuanta);
            if (scheduled.TryPeek(out _, out var next)) horizon = Math.Min(horizon, next.At);
            var actors = world.Actors(Now, horizon);
            Contact? first = null;
            foreach (var p in projectiles.Values)
            {
                var candidate = NextContact(p, horizon, actors);
                if (candidate is { } c && (first is null || c.CompareTo(first.Value) < 0)) first = c;
            }
            // The upper bound is inclusive: another flight may have a contact at the same instant.
            if (first is null && Now == until) break;
            long at = first?.At ?? horizon;
            double seconds = (at - Now) / (double)QuantaPerSecond;
            foreach (var p in projectiles.Values)
            {
                double distance = Math.Min(p.Remaining, seconds * p.Speed);
                p.Position += p.Direction * distance;
                p.Remaining -= distance;
                p.Distance += distance;
            }
            Now = at;
            if (first is { } contact) Resolve(contact, actors);
        }
        // Area events are synchronous. Retain only groups that can still emit/return during later advances.
        ledger.RetainGroups(projectiles.Values.SelectMany(p => new[] { p.Id, p.EmissionId, p.ChildEmission }).ToHashSet());
    }

    readonly record struct Contact(long At, ProjectileEntity Projectile, ProjectileActor? Target,
        ProjectileEndReason? End, ProjectileVector Normal, ProjectileInteraction? Interaction = null,
        ProjectileVector? TerrainPoint = null) : IComparable<Contact>
    {
        public int CompareTo(Contact other)
        {
            int c = At.CompareTo(other.At);
            if (c != 0) return c;
            c = Projectile.Id.CompareTo(other.Projectile.Id);
            return c != 0 ? c : (Target?.Key.Id ?? 0).CompareTo(other.Target?.Key.Id ?? 0);
        }
    }

    Contact? NextContact(ProjectileEntity p, long horizon, IReadOnlyList<ProjectileActor> actors)
    {
        long end = Math.Min(horizon, p.ExpiresAt);
        long rangeEnd = Now + (long)Math.Ceiling(Math.Max(0, p.Remaining) / p.Speed * QuantaPerSecond);
        end = Math.Min(end, rangeEnd);
        ProjectileVector to = p.Position + p.Direction * Math.Min(p.Remaining, p.Speed * (end - Now) / QuantaPerSecond);
        Contact? best = end == p.ExpiresAt ? new(end, p, null, ProjectileEndReason.Lifetime, default)
            : end == rangeEnd ? new(end, p, null, p.Profile.Lobbed ? ProjectileEndReason.Landing
                : p.Phase == 0 ? ProjectileEndReason.Distance : ProjectileEndReason.Returned, default) : null;
        if (world.TraceTerrain(p.Position, to) is { } wall)
        {
            double fraction = Math.Clamp(wall.Fraction, 0, 1);
            best = new(Now + (long)Math.Ceiling((end - Now) * fraction), p, null,
                ProjectileEndReason.Terrain, wall.Normal, TerrainPoint: p.Position + (to - p.Position) * fraction);
        }
        foreach (var interaction in Interactions.OrderBy(w => w.Id))
        {
            if (interaction.Capacity <= 0 || p.Interactions.Contains(interaction.Id)
                || (interaction.Team is int team && team != p.Use.Team)) continue;
            if (Crossing(p.Position, to, interaction) is not double fraction) continue;
            long at = Now + (long)Math.Ceiling((end - Now) * fraction);
            if (best is null || at < best.Value.At) best = new(at, p, null, null, default, interaction);
        }
        if (!p.Profile.DirectDamage || p.Profile.Lobbed) return best;
        foreach (var actor in actors.OrderBy(a => a.Key.Id).ThenBy(a => a.Key.Generation))
        {
            if (actor.Team == p.Use.Team || actor.Key == p.Use.Owner) continue;
            if (p.Contacts.Contains(actor.Key) && p.Profile.HitRule.Repeat != ProjectileRepeat.Once
                && (p.Position - actor.Position(Now)).Length > p.Profile.Radius + actor.Radius + .001)
                p.Contacts.Remove(actor.Key);
            if (p.Contacts.Contains(actor.Key)) continue;
            double? t = ProjectileGeometry.Contact(p.Position - actor.Position(Now), to - actor.Position(end),
                p.Profile.Radius + actor.Radius);
            if (t is not double f) continue;
            long at = Now + (long)Math.Ceiling((end - Now) * f);
            // Terrain wins coincident contact. An actor at the range endpoint still receives its contact.
            if (best is null || at < best.Value.At || (at == best.Value.At && best.Value.End != ProjectileEndReason.Terrain
                && best.Value.Target is null)) best = new(at, p, actor, null, default);
        }
        return best;
    }

    void Resolve(Contact c, IReadOnlyList<ProjectileActor> actors)
    {
        var p = c.Projectile;
        if (c.TerrainPoint is { } point)
        {
            // Contact time is quantized upward, but geometry must stay on the surface. Otherwise
            // fast reflected/split flights can start inside the blocked cell and die immediately.
            double excess = (p.Position - point).Dot(p.Direction);
            p.Distance -= excess; p.Remaining += excess; p.Position = point;
        }
        if (c.Interaction is { } interaction) { Interact(p, interaction, actors); return; }
        if (c.Target is { } target)
        {
            p.Contacts.Add(target.Key);
            if (p.Profile.EndArea is not null && !CanContinue(p, actors))
            {
                End(p, ProjectileEndReason.Enemy, victim: target.Key);
                return;
            }
            var result = Damage(p, target, "direct");
            if (result == ProjectileHitResult.Miss) return;
            if (p.Profile.ContactArea is { } area) Area(p, area);
            if (result == ProjectileHitResult.Hit && p.Profile.ContactTargets is { } strikes)
                TargetedHits(p, target, strikes);
            if (Continue(p, target, world.Actors(Now, Now))) return;
            End(p, ProjectileEndReason.Enemy);
        }
        else if (c.End == ProjectileEndReason.Terrain)
        {
            if (p.Profile.ContactArea is { } area) Area(p, area);
            if (p.Profile.SplitOnTerrain && !p.Profile.CannotSplit && p.Split > 0)
            {
                p.Position += c.Normal * .001; // Children start on the incident side of the blocking surface.
                if (Split(p, actors, c.Normal)) return;
            }
            if (p.TerrainBounces > 0)
            {
                p.TerrainBounces--;
                p.Direction = (p.Direction - c.Normal * (2 * p.Direction.Dot(c.Normal))).Unit;
                p.Position += c.Normal * .001;
                Publish(ProjectileEventKind.Redirect, p);
            }
            else End(p, c.End.Value);
        }
        else End(p, c.End!.Value);
    }

    bool CanContinue(ProjectileEntity p, IReadOnlyList<ProjectileActor> actors) => p.Phase > 0
        || (!p.Profile.CannotSplit && p.Split > 0 && (p.Profile.SplitWithoutTargets || Nearby(p, actors).Any()))
        || (!p.Profile.CannotPierce && p.Pierce != 0) || (!p.Profile.CannotFork && p.Fork > 0)
        || (!p.Profile.CannotChain && p.Chain > 0 && Nearby(p, actors).Any());

    ProjectileHitResult Damage(ProjectileEntity p, ProjectileActor target, string stage,
        long? group = null, ProjectileHitRule? rule = null, double multiplier = 1, string? admissionStage = null)
    {
        rule ??= p.Profile.HitRule;
        long g = group ?? (rule.Scope == ProjectileHitScope.Projectile ? p.Id : p.EmissionId);
        var result = ProjectileHitResult.Blocked;
        ledger.TryHit(g, p.Phase, admissionStage ?? stage, target.Key, Now, p.CreatedAt, rule, () =>
        {
            result = world.Hit(new(p, target.Key, Now, stage, p.Position, multiplier * p.DamageMultiplier));
            return result != ProjectileHitResult.Miss;
        });
        if (result == ProjectileHitResult.Hit)
            world.Publish(new(ProjectileEventKind.Hit, p, Now, target.Position(Now), Stage: stage));
        return result;
    }

    bool Continue(ProjectileEntity p, ProjectileActor target, IReadOnlyList<ProjectileActor> actors)
    {
        var profile = p.Profile;
        if (p.Phase > 0) return true;
        if (p.Split > 0 && !profile.CannotSplit && Split(p, actors)) return true;
        if (!profile.CannotPierce && p.Pierce != 0) { if (p.Pierce > 0) p.Pierce--; return true; }
        if (!profile.CannotFork && p.Fork > 0)
        {
            p.Fork--;
            ContinueChild(p, p.Direction.Rotate(Math.PI / 6));
            ContinueChild(p, p.Direction.Rotate(-Math.PI / 6));
            End(p, ProjectileEndReason.Fork, allowReturn: false);
            return true;
        }
        if (!profile.CannotChain && p.Chain > 0 && Nearby(p, actors).FirstOrDefault() is { } next)
        {
            p.Chain--; p.Direction = (next.Position(Now) - p.Position).Unit;
            Publish(ProjectileEventKind.Redirect, p);
            return true;
        }
        return false;
    }

    IEnumerable<ProjectileActor> Nearby(ProjectileEntity p, IReadOnlyList<ProjectileActor> actors) => actors
        .Where(a => a.Team != p.Use.Team && !p.Contacts.Contains(a.Key) && a.Key != p.Use.Owner
            && (a.Position(Now) - p.Position).Length <= p.Profile.ChainRange)
        .OrderBy(a => (a.Position(Now) - p.Position).Length).ThenBy(a => a.Key.Id).ThenBy(a => a.Key.Generation);

    bool Split(ProjectileEntity p, IReadOnlyList<ProjectileActor> actors, ProjectileVector? terrainNormal = null)
    {
        var targets = Nearby(p, actors)
            .Where(a => terrainNormal is null || world.TraceTerrain(p.Position, a.Position(Now)) is null)
            .Take(p.Split).ToArray();
        if (targets.Length == 0 && terrainNormal is null && !p.Profile.SplitWithoutTargets) return false;
        foreach (var other in targets)
            Spawn(p.Use, p.UseId, p.EmissionId, p.Id, p.Position, (other.Position(Now) - p.Position).Unit,
                continuing: p, split: true);
        if (terrainNormal is not null || p.Profile.SplitWithoutTargets)
            // LocalExile provisional geometry for unclaimed slots: seeded azimuths after enemy hits,
            // incident hemisphere after terrain hits. The split count does not depend on this geometry.
            for (int i = targets.Length; i < p.Split; i++)
                Spawn(p.Use, p.UseId, p.EmissionId, p.Id, p.Position,
                    terrainNormal is { } normal ? normal.Unit.Rotate((p.Random.NextDouble() - .5) * Math.PI)
                        : p.Direction.Rotate(p.Random.NextDouble() * Math.Tau), continuing: p, split: true);
        End(p, ProjectileEndReason.Split, allowReturn: false);
        return true;
    }

    void ContinueChild(ProjectileEntity p, ProjectileVector direction)
    {
        Spawn(p.Use, p.UseId, p.EmissionId, p.Id, p.Position, direction, continuing: p);
    }

    void End(ProjectileEntity p, ProjectileEndReason reason, bool allowReturn = true, ProjectileActorKey? victim = null)
    {
        if (reason is ProjectileEndReason.Enemy or ProjectileEndReason.Terrain or ProjectileEndReason.Distance
            or ProjectileEndReason.Lifetime or ProjectileEndReason.Landing or ProjectileEndReason.Returned)
        {
            if (p.Profile.EndArea is { } area) Area(p, area, victim);
            if (p.Profile.EndEmission is { } emission) EmitChildren(p, emission);
        }
        if (reason == ProjectileEndReason.Landing && p.GroundChains > 0)
        {
            p.GroundChains--;
            p.GroundBounces++;
            p.Remaining = Math.Max(.001, p.Profile.GroundBounceDistance);
            Publish(ProjectileEventKind.Redirect, p);
            return;
        }
        if (allowReturn && p.Phase == 0 && p.Profile.Returns && !p.Profile.CannotReturn
            && p.Use.ReturnAnchor is { } key && world.Actors(Now, Now).FirstOrDefault(a => a.Key == key) is { } anchor
            && reason is ProjectileEndReason.Enemy or ProjectileEndReason.Distance or ProjectileEndReason.Lifetime or ProjectileEndReason.Terrain)
        {
            p.Phase = 1; p.Contacts.Clear();
            p.ExpiresAt = Now + p.Profile.Lifetime;
            var delta = anchor.Position(Now) - p.Position;
            p.Direction = delta.Unit; p.Remaining = Math.Max(.001, delta.Length);
            Publish(ProjectileEventKind.Redirect, p);
            return;
        }
        projectiles.Remove(p.Id);
        Publish(ProjectileEventKind.End, p, reason);
    }

    void Publish(ProjectileEventKind kind, ProjectileEntity p, ProjectileEndReason? reason = null) =>
        world.Publish(new(kind, p, Now, p.Position, reason));

    public void Clear()
    {
        foreach (var p in projectiles.Values.ToArray()) End(p, ProjectileEndReason.Teardown, allowReturn: false);
        scheduled.Clear(); ledger.Clear(); Interactions.Clear();
    }
}
