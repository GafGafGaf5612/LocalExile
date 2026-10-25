/// <summary>A straight projectile swept against observed monster movement, using elapsed game time.
/// Caller serializes observations with monster movement orders. Positions are the server's integer walk estimates.</summary>
sealed class ProjectileFlight
{
    public readonly record struct Target(uint Id, (int X, int Y) Position, object? Motion = null);
    public readonly record struct Step((double X, double Y) Position, double At, bool Finished, uint TargetId = 0, bool Wall = false);
    readonly (int X, int Y) start, end;
    readonly double launched, duration, radius;
    readonly bool wall;
    Dictionary<uint, Target> previous;
    double sampledAt;
    Step step;

    public ProjectileFlight((int X, int Y) start, (int X, int Y) end, double speed, double launched,
        double radius, bool wall, IEnumerable<Target> targets)
    {
        if (!double.IsFinite(speed) || speed <= 0) throw new ArgumentOutOfRangeException(nameof(speed));
        (this.start, this.end, this.launched, this.radius, this.wall) = (start, end, launched, radius, wall);
        duration = Math.Sqrt(Math.Pow((double)end.X - start.X, 2) + Math.Pow((double)end.Y - start.Y, 2)) / speed;
        sampledAt = launched;
        previous = targets.ToDictionary(t => t.Id);
        step = new(start, launched, false);
    }

    (double X, double Y) Position(double at)
    {
        double f = duration == 0 ? 1 : Math.Clamp((at - launched) / duration, 0, 1);
        return (start.X + ((double)end.X - start.X) * f, start.Y + ((double)end.Y - start.Y) * f);
    }

    public Step Advance(double now, IReadOnlyList<Target> targets)
    {
        if (step.Finished || now <= sampledAt) return step; // A stopped game clock cannot hit or expire anything.
        if (duration == 0 && wall) return step = new(end, launched, true, Wall: true);
        double until = Math.Min(now, launched + duration);
        var from = Position(sampledAt);
        var to = Position(until);
        double fraction = 1;
        uint hit = 0;
        bool atEnd = until >= launched + duration;
        foreach (var target in targets)
        {
            double? contact;
            if (previous.TryGetValue(target.Id, out var observed) && ReferenceEquals(observed.Motion, target.Motion))
            {
                var before = observed.Position;
                // If scheduling passed the end of the flight, only sweep the monster up to that same time.
                double f = Math.Clamp((until - sampledAt) / (now - sampledAt), 0, 1);
                var after = (X: before.X + ((double)target.Position.X - before.X) * f,
                    Y: before.Y + ((double)target.Position.Y - before.Y) * f);
                contact = Contact((from.X - before.X, from.Y - before.Y),
                    (to.X - after.X, to.Y - after.Y), radius);
            }
            else
            {
                // A newly observed/spawned object has no earlier trajectory: it cannot intercept a past segment.
                double x = to.X - target.Position.X, y = to.Y - target.Position.Y;
                contact = until == now && x * x + y * y <= radius * radius ? 1 : null;
            }
            if (contact is double c && c <= fraction && !(wall && atEnd && c == 1)
                && (c < fraction || hit == 0 || target.Id < hit))
                (fraction, hit) = (c, target.Id);
        }
        double at = sampledAt + (until - sampledAt) * fraction;
        step = new(Position(at), at, hit != 0 || atEnd, hit, hit == 0 && atEnd && wall);
        sampledAt = until;
        previous = targets.ToDictionary(t => t.Id);
        return step;
    }

    // Earliest entry of relative projectile/monster movement into a circle, including tangencies and initial overlap.
    static double? Contact((double X, double Y) from, (double X, double Y) to, double radius)
    {
        double c = from.X * from.X + from.Y * from.Y - radius * radius;
        if (c <= 0) return 0;
        double x = to.X - from.X, y = to.Y - from.Y, a = x * x + y * y;
        if (a == 0) return null;
        double b = from.X * x + from.Y * y, discriminant = b * b - a * c;
        if (b >= 0 || discriminant < 0) return null;
        double t = (-b - Math.Sqrt(discriminant)) / a;
        return t is >= 0 and <= 1 ? t : null;
    }

    /// <summary>Clip the static route before the first blocked grid cell. Supercover traversal includes diagonal corners.</summary>
    public static ((int X, int Y) End, bool Wall) ClipWall((int X, int Y) from, (int X, int Y) to,
        Func<int, int, bool> blocked)
    {
        bool wall = false;
        while (true)
        {
            var clipped = TraceWall(from, to, blocked);
            wall |= clipped.Wall;
            if (!clipped.Wall || clipped.End == from) return (clipped.End, wall);
            // The wire endpoint is an integer cell. Rounding can change the line enough to meet an earlier
            // neighboring blocker; trace that actual glide until the retained segment is clear.
            to = clipped.End;
        }
    }

    static ((int X, int Y) End, bool Wall) TraceWall((int X, int Y) from, (int X, int Y) to,
        Func<int, int, bool> blocked)
    {
        int x = from.X, y = from.Y;
        if (blocked(x, y)) return (from, true);
        double dx = (double)to.X - from.X, dy = (double)to.Y - from.Y;
        int sx = Math.Sign(dx), sy = Math.Sign(dy);
        double tx = sx == 0 ? double.PositiveInfinity : 0.5 / Math.Abs(dx);
        double ty = sy == 0 ? double.PositiveInfinity : 0.5 / Math.Abs(dy);
        double stepX = sx == 0 ? double.PositiveInfinity : 1 / Math.Abs(dx);
        double stepY = sy == 0 ? double.PositiveInfinity : 1 / Math.Abs(dy);
        while (Math.Min(tx, ty) <= 1)
        {
            double t = Math.Min(tx, ty);
            bool crossX = tx <= ty, crossY = ty <= tx;
            if ((crossX && blocked(x + sx, y)) || (crossY && blocked(x, y + sy))
                || (crossX && crossY && blocked(x + sx, y + sy)))
            {
                double before = Math.Max(0, t - 0.000001 / Math.Max(1, Math.Sqrt(dx * dx + dy * dy)));
                return (((int)Math.Round(from.X + dx * before), (int)Math.Round(from.Y + dy * before)), true);
            }
            if (crossX) { x += sx; tx += stepX; }
            if (crossY) { y += sy; ty += stepY; }
        }
        return (to, false);
    }
}
