using GameServer.Core.World;

/// <summary>The bounded ground-target instant path: ordinary Frostblink's distance calculation on a captured grid.
/// Terrain endpoint searches, other movement skills and Frostblink's damage/chilled ground are not modelled here.</summary>
static class InstantLocationSkill
{
    public static bool Supported(SkillCatalog skills, int effect) =>
        skills.EffectId(effect) == "Frostblink" && skills.IsInstant(effect) && skills.IsMovement(effect);

    public static bool ObjectTargetSupported(SkillCatalog skills, int effect) =>
        skills.IsInstant(effect) && !skills.IsMovement(effect);

    // 0x141bfe240: max distance truncates 42 * (100 + skill_maximum_travel_distance_+%) / 100;
    // min distance is float 32 at 0x1436a3888. 0x140671d00 rounds each normalized component with floor(x + .5).
    public static (int X, int Y)? Endpoint((int X, int Y) from, (uint X, uint Y) aim, int travelDistancePct)
    {
        if (aim.X > int.MaxValue || aim.Y > int.MaxValue) return null;
        long numerator = 42L * (100L + travelDistancePct);
        if (numerator < 0 || numerator > int.MaxValue) return null; // Native signed multiplication must not wrap.
        long maximum = numerator / 100;
        // Do not guess the native min/max interaction for unsupported extreme distance reductions.
        if (maximum < 32 || maximum > int.MaxValue) return null;
        float dx = (long)aim.X - from.X, dy = (long)aim.Y - from.Y;
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        if (!(distance > 0) || !float.IsFinite(distance)) return null;
        if (distance >= 32 && distance <= maximum) return ((int)aim.X, (int)aim.Y);
        float scale = (distance < 32 ? 32 : (float)maximum) / distance;
        double x = from.X + (double)MathF.Floor(dx * scale + .5f);
        double y = from.Y + (double)MathF.Floor(dy * scale + .5f);
        if (x < 0 || y < 0 || x > int.MaxValue || y > int.MaxValue) return null;
        return ((int)x, (int)y);
    }

    // Called again under the send gate: an old request cannot move the player after transfer/death/pause.
    // A fully walkable straight route is deliberately stricter than the client's blink terrain endpoint search.
    public static bool TryResolve(ClientState conn, int epoch, (uint X, uint Y) aim,
        bool inTown, Func<string, int> stat, out (int X, int Y) from, out (int X, int Y) to)
    {
        lock (conn.Monsters)
        {
            from = to = default;
            if (!conn.ProjectileActive(epoch) || !conn.PlayerSent || conn.PlayerLife == 0 || conn.Paused
                || conn.PendingTransfer is not null || inTown || stat("your_movement_skills_are_disabled") != 0
                || conn.Layout.Walkable is not { } walkable || conn.Layout.GridW <= 0 || conn.Layout.GridH <= 0)
                return false;
            if (aim.X >= conn.Layout.GridW || aim.Y >= conn.Layout.GridH) return false;
            from = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            if (Endpoint(from, aim, stat("skill_maximum_travel_distance_+%")) is not { } endpoint) return false;
            bool Allowed(int x, int y) => x >= 0 && y >= 0 && x < conn.Layout.GridW && y < conn.Layout.GridH
                && walkable(x, y) && !conn.Barrier.Contains((x, y));
            if (!Allowed(from.X, from.Y) || !Allowed(endpoint.X, endpoint.Y)) return false;
            if (conn.Blockers.Any(b => Math.Max(Math.Abs((long)endpoint.X - b.X), Math.Abs((long)endpoint.Y - b.Y))
                - b.Size < 2)) return false; // Player Positioned size 1: retain its ordinary placement clearance.
            var previous = from;
            foreach (var cell in ClientPathfinder.Line(from, endpoint))
            {
                if (!Allowed(cell.X, cell.Y)) return false;
                if (cell.X != previous.X && cell.Y != previous.Y
                    && (!Allowed(previous.X, cell.Y) || !Allowed(cell.X, previous.Y))) return false;
                previous = cell;
            }
            to = endpoint;
            return true;
        }
    }

    // Native ground 0x1ce keeps the current/predicted action at entry, then Frostblink's reposition ends its old walk.
    // Mirror that position change without incrementing ActionCount or cancelling a cast/channel's pending release.
    public static bool TryCommit(ClientState conn, int epoch, (uint X, uint Y) aim, bool inTown,
        Func<string, int> stat, out (int X, int Y) to)
    {
        lock (conn.Monsters)
        {
            if (!TryResolve(conn, epoch, aim, inTown, stat, out _, out to)) return false;
            conn.Walk.Teleport(to.X, to.Y, conn.Clock.Elapsed.TotalSeconds);
            return true;
        }
    }
}
