namespace GameServer.Core.Projectiles;

public sealed partial class ProjectileRuntime
{
    void StartStages(ProjectileEntity p)
    {
        if (p.Profile.WanderInterval > 0)
        {
            void Wander()
            {
                if (!projectiles.ContainsKey(p.Id)) return;
                p.Direction = p.Direction.Rotate((p.Random.NextDouble() - .5) * Math.PI);
                Publish(ProjectileEventKind.Redirect, p);
                Schedule(Now + p.Profile.WanderInterval, Wander);
            }
            Schedule(Now + p.Profile.WanderInterval, Wander);
        }
        if (p.Form == 0 && p.Profile.FormChange is { } form)
            Schedule(Now + (long)Math.Ceiling(Math.Max(0, form.Distance - p.Distance) / p.Speed * QuantaPerSecond), () =>
            {
                if (!projectiles.ContainsKey(p.Id)) return;
                p.Form = 1; p.Speed *= form.SpeedMultiplier; p.Pierce = form.Pierce;
                p.DamageMultiplier *= form.DamageMultiplier;
                Publish(ProjectileEventKind.Redirect, p);
            });
        if (p.Profile.Pulse is { } pulse)
        {
            if (pulse.Interval <= 0) throw new ArgumentOutOfRangeException(nameof(pulse));
            void Pulse()
            {
                if (!projectiles.ContainsKey(p.Id)) return;
                foreach (var target in world.Actors(Now, Now).OrderBy(a => a.Key.Id))
                    if (target.Team != p.Use.Team && target.Key != p.Use.Owner
                        && (target.Position(Now) - p.Position).Length <= pulse.Radius + target.Radius)
                        Damage(p, target, pulse.Stage);
                Schedule(Now + pulse.Interval, Pulse);
            }
            Pulse();
        }
        if (p.Profile.TravelEmission is { Interval: > 0 } emission)
        {
            void Emit()
            {
                if (!projectiles.ContainsKey(p.Id)) return;
                EmitChildren(p, emission);
                Schedule(Now + emission.Interval, Emit);
            }
            Schedule(Now + emission.Interval, Emit);
        }
    }

    void EmitChildren(ProjectileEntity parent, ProjectileEmissionRecipe recipe)
    {
        var child = recipe.Child;
        Validate(child);
        if (parent.ChildEmission == 0) parent.ChildEmission = ++sequence;
        for (int i = 0; i < child.Count; i++)
        {
            double angle = recipe.RandomDirections ? parent.Random.NextDouble() * Math.Tau
                : recipe.Radial ? Math.Tau * i / child.Count + parent.EmissionNumber * 2.399963229728653
                : child.Count <= 1 ? 0 : child.SpreadRadians * (i / (double)(child.Count - 1) - .5);
            var direction = parent.Direction.Rotate(angle);
            var use = parent.Use with { Profile = child, Origin = parent.Position,
                Aim = parent.Position + direction * child.Range };
            Spawn(use, parent.UseId, parent.ChildEmission, parent.Id, parent.Position, direction);
        }
        parent.EmissionNumber++;
    }

    void TargetedHits(ProjectileEntity p, ProjectileActor primary, ProjectileTargetedRecipe recipe)
    {
        long hitEvent = ++sequence;
        foreach (var target in world.Actors(Now, Now).Where(a => a.Team != p.Use.Team && a.Key != primary.Key
            && a.Key != p.Use.Owner && (a.Position(Now) - primary.Position(Now)).Length <= recipe.Radius)
            .OrderBy(a => (a.Position(Now) - primary.Position(Now)).Length).ThenBy(a => a.Key.Id).Take(recipe.Count))
            Damage(p, target, recipe.Stage, hitEvent, new());
    }

    void Area(ProjectileEntity p, ProjectileAreaRecipe recipe, ProjectileActorKey? victim = null)
    {
        for (int i = 0; i < recipe.Count; i++)
        {
            // Each explosion is its own damage event. A terminal Fireball never also emits direct damage.
            long areaId = ++sequence;
            var centre = p.Position;
            if (recipe.Offset > 0)
                centre += new ProjectileVector(1, 0).Rotate(p.Random.NextDouble() * Math.Tau)
                    * (recipe.Offset * (.5 + .5 * p.Random.NextDouble()));
            world.Publish(new(ProjectileEventKind.Area, p, Now, centre, Radius: recipe.Radius));
            foreach (var target in world.Actors(Now, Now).OrderBy(a => a.Key.Id).ThenBy(a => a.Key.Generation))
                if (target.Team != p.Use.Team && target.Key != p.Use.Owner
                    && (target.Position(Now) - centre).Length <= recipe.Radius + target.Radius)
                    if (target.Key == victim)
                        Damage(p, target, recipe.Stage, multiplier: recipe.Multiplier, admissionStage: "direct");
                    else Damage(p, target, recipe.Stage,
                        recipe.Scope == ProjectileHitScope.Emission ? p.EmissionId : areaId, new(), recipe.Multiplier,
                        admissionStage: recipe.Scope == ProjectileHitScope.Emission
                            ? recipe.Stage + ":" + p.GroundBounces : recipe.Stage);
        }
    }

    public bool TryConsume(long projectileId)
    {
        if (!projectiles.TryGetValue(projectileId, out var p)) return false;
        End(p, ProjectileEndReason.Consumed, allowReturn: false);
        return true;
    }
}
