using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

partial class Program
{
    // The original action guards release only. Once created, an arrow or wand bolt owns the area's flight lifetime.
    internal static bool OrdinaryRangedReleaseCurrent(ClientState conn, int action, int epoch)
    {
        lock (conn.Monsters) return conn.ProjectileActive(epoch) && !conn.RageRetired && conn.PlayerLife > 0
            && conn.ActionCount == action && !conn.HitRecovery.Active(conn.Clock.Elapsed.TotalSeconds);
    }

    // A pause can win the send gate after an action was accepted. Keep its echo pending rather than releasing an
    // unconfirmed arrow after resume; a later action, death or transfer still cancels it with its original token.
    internal static async Task<bool> SendOrdinaryRangedAction(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, byte[] bytes, string? log, int action, int epoch)
    {
        bool sent = false;
        while (!sent && OrdinaryRangedReleaseCurrent(conn, action, epoch))
        {
            await Send(remote, stream, ciphers, bytes, log, still: () =>
            {
                lock (conn.Monsters) return sent = OrdinaryRangedReleaseCurrent(conn, action, epoch) && !conn.Paused;
            });
            if (!sent) await Task.Delay(16);
        }
        return sent;
    }

    static async Task ConfirmAndLaunchOrdinaryRanged(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, byte[] actionBytes, string actionLog, (int X, int Y) from, (int X, int Y) aim,
        double attackSeconds, int action, int epoch, string projectileId, ushort sequence)
    {
        if (await SendOrdinaryRangedAction(remote, stream, ciphers, conn, actionBytes, actionLog, action, epoch))
            await LaunchOrdinaryRanged(remote, stream, ciphers, conn, from, aim, attackSeconds, action, epoch, projectileId, sequence);
    }

    internal static double OrdinaryRangedContactDelay(ClientState conn, double attackSeconds, string projectileId,
        ushort sequence)
    {
        if (projectileId == OrdinaryRangedAttack.Wand)
        {
            string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? Skills.ClassFor(conn.ClassRow).AnimatedObject;
            var profile = WeaponProfileFor(conn, activeSet: true);
            if (Animations.TryDefaultWandContact(ao, profile, sequence, out var selected))
            {
                double delay = Math.Max(0, attackSeconds * selected.ContactFraction);
                Console.WriteLine($"[gateway] wand release sequence 0x{sequence:x4}: {selected.Stance}/{selected.Animation} " +
                    $"(clip {selected.AnimationIndex}, contact {selected.ContactMs}/{selected.LengthMs} ms), " +
                    $"attack {attackSeconds:0.000000} s, release after {delay:0.000000} s");
                return delay;
            }
            Console.WriteLine($"[gateway] WARNING: missing native wand variation metadata for {ao}; using approximate attack1 release");
        }
        return AttackContactDelay(conn, attackSeconds, approach: false);
    }

    static async Task LaunchOrdinaryRanged(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        (int X, int Y) from, (int X, int Y) aim, double attackSeconds, int action, int epoch, string projectileId, ushort sequence)
    {
        // The predicted action's sequence seeds the client's ACT variation choice. Reproduce that choice rather than
        // applying attack1's keyframe to every swing. Ranged release has no walk-in offset or melee early-hit clamp.
        double startedAt = conn.Clock.Elapsed.TotalSeconds;
        double contact = OrdinaryRangedContactDelay(conn, attackSeconds, projectileId, sequence);
        if (!await WaitProjectileTime(conn, startedAt + Math.Max(0.001, contact), epoch)) return;
        OrdinaryRangedShot shot;
        (string Id, float CellsPerSecond) projectile;
        lock (conn.Monsters)
        {
            if (!OrdinaryRangedReleaseCurrent(conn, action, epoch)
                || OrdinaryRangedAttack.ProjectileId(WeaponProfileFor(conn, activeSet: true)) != projectileId
                || Skills.Projectile(projectileId) is not { } ranged) return;
            var character = CharacterPlayerStats(conn);
            from = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            int effect = SkillEffectId(conn, SkillCatalog.FirstSkillId) is { } id ? Skills.EffectRow(id) : -1;
            // Freeze damage, chance and multiplier at release. Conditional critical rules read the live owner
            // at impact; target evasion and existing impales resolve at contact.
            shot = OrdinaryRangedShot.Capture(WeaponFor(conn), character, Skills.EffectStats(effect, 1), Stats,
                conn.Pools.Accuracy, conn.Pools.AttackCriticalStrikeChance, conn.Pools.CriticalStrikeMultiplier);
            int Get(string name) => Stats.Key(name) is uint key ? character.GetValueOrDefault(key) : 0;
            projectile = (ranged.Id, ProjectileSpeed.CellsPerSecond(ranged.Speed, Get, _ => 0,
                Environment.GetEnvironmentVariable("Game_PROJECTILE_SPEED")));
        }
        await FlyProjectile(remote, stream, ciphers, conn, projectile, from, aim,
            damageTo: null, epoch: epoch, impact: "none",
            actionToken: action, ordinaryShot: shot);
    }
}
