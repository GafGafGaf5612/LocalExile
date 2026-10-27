/// <summary>
/// Multiple projectiles: the angles of a volley around the aimed direction, and the aim of each one. PoE fires the
/// skill's own projectile plus one per <c>number_of_additional_projectiles</c>, evenly spread; the stat's
/// <c>projectile_spread_radius</c> (degrees between neighbours) sets the gap, else a default is used. The client only
/// plays the cast: every projectile is a server object of its own (Program.Combat), so the count and the spread are
/// decided here.
/// </summary>
static class ProjectileSpread
{
    /// <summary>The default gap between neighbouring projectiles in degrees (our own number, no client rule recovered).</summary>
    public const double DefaultGapDegrees = 10;

    /// <summary>Angles in degrees, centred on the aim: 1 → [0], 2 → [-g/2, +g/2], 3 → [-g, 0, +g].</summary>
    public static IReadOnlyList<double> Angles(int count, double gapDegrees)
    {
        if (count <= 1) return [0];
        double gap = double.IsFinite(gapDegrees) ? Math.Clamp(gapDegrees, 0, 90) : DefaultGapDegrees;
        return [.. Enumerable.Range(0, count).Select(i => (i - (count - 1) / 2.0) * gap)];
    }

    /// <summary>The aimed cell turned by <paramref name="degrees"/> around <paramref name="from"/> (positive is
    /// clockwise on the grid, whose y grows down).</summary>
    public static (int X, int Y) Rotate((int X, int Y) from, (int X, int Y) to, double degrees)
    {
        if (degrees == 0 || (from.X == to.X && from.Y == to.Y)) return to;
        double radians = degrees * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
        double dx = to.X - from.X, dy = to.Y - from.Y;
        return ((int)Math.Round(from.X + dx * cos - dy * sin), (int)Math.Round(from.Y + dx * sin + dy * cos));
    }
}
