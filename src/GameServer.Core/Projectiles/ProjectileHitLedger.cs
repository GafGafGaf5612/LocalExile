namespace GameServer.Core.Projectiles;

/// <summary>Damage admission, independent of collision suppression. Keys include actor generations.</summary>
public sealed class ProjectileHitLedger
{
    readonly Dictionary<(long Group, int Phase, string Stage, ProjectileActorKey Target), long> hits = [];
    public bool TryHit(long group, int phase, string stage, ProjectileActorKey target, long at, long origin,
        ProjectileHitRule rule, Func<bool>? commit = null)
    {
        var key = (group, phase, stage, target);
        if (hits.TryGetValue(key, out long previous))
        {
            if (rule.Repeat == ProjectileRepeat.Once) return false;
            if (rule.Period <= 0) throw new ArgumentOutOfRangeException(nameof(rule));
            if (rule.Repeat == ProjectileRepeat.Interval && at - previous < rule.Period) return false;
            if (rule.Repeat == ProjectileRepeat.Window && (at - origin) / rule.Period == (previous - origin) / rule.Period)
                return false;
        }
        if (commit is not null && !commit()) return false;
        hits[key] = at;
        return true;
    }
    public void Clear() => hits.Clear();
    public void RetainGroups(IReadOnlySet<long> active)
    {
        foreach (var key in hits.Keys.Where(key => !active.Contains(key.Group)).ToArray()) hits.Remove(key);
    }
}
