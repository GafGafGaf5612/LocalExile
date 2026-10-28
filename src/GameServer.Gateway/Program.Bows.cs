using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

partial class Program
{
    // The original action guards the release only. Once its object exists, the arrow owns the area's flight lifetime.
    internal static bool OrdinaryBowReleaseCurrent(ClientState conn, int action, int epoch)
    {
        lock (conn.Monsters) return conn.ProjectileActive(epoch) && conn.PlayerLife > 0
            && conn.ActionCount == action && !conn.HitRecovery.Active(conn.Clock.Elapsed.TotalSeconds);
    }

    // A pause can win the send gate after an action was accepted. Keep its echo pending rather than releasing an
    // unconfirmed arrow after resume; a later action, death or transfer still cancels it with its original token.
    internal static async Task<bool> SendOrdinaryBowAction(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, byte[] bytes, string? log, int action, int epoch)
    {
        bool sent = false;
        while (!sent && OrdinaryBowReleaseCurrent(conn, action, epoch))
        {
            await Send(remote, stream, ciphers, bytes, log, still: () =>
            {
                lock (conn.Monsters) return sent = OrdinaryBowReleaseCurrent(conn, action, epoch) && !conn.Paused;
            });
            if (!sent) await Task.Delay(16);
        }
        return sent;
    }

    static async Task ConfirmAndLaunchOrdinaryBow(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, byte[] actionBytes, string actionLog, (int X, int Y) from, (int X, int Y) aim,
        double attackSeconds, int action, int epoch, SkillCriticalRoll criticalRoll)
    {
        if (await SendOrdinaryBowAction(remote, stream, ciphers, conn, actionBytes, actionLog, action, epoch))
            await LaunchOrdinaryBow(remote, stream, ciphers, conn, from, aim, attackSeconds, action, epoch, criticalRoll);
    }

    static async Task LaunchOrdinaryBow(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        (int X, int Y) from, (int X, int Y) aim, double attackSeconds, int action, int epoch, SkillCriticalRoll criticalRoll)
    {
        // Both ground and object-targeted arrows release at attack1_bow contact, without the melee walk-in offset.
        double contact = AttackContactDelay(conn, attackSeconds, approach: false);
        if (!await WaitProjectileTime(conn, conn.Clock.Elapsed.TotalSeconds + Math.Max(0.001, contact), epoch)) return;
        OrdinaryBowShot shot;
        (string Id, float CellsPerSecond) projectile;
        lock (conn.Monsters)
        {
            if (!OrdinaryBowReleaseCurrent(conn, action, epoch) || !WeaponProfileFor(conn, activeSet: true).Bow
                || Skills.Projectile("Metadata/Projectiles/DefaultArrow") is not { } arrow) return;
            var character = CharacterPlayerStats(conn);
            from = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            int effect = SkillEffectId(conn, SkillCatalog.FirstSkillId) is { } id ? Skills.EffectRow(id) : -1;
            // Preserve the existing release snapshot for damage/chance/multiplier. Critical luck belongs to the
            // admitted use; prohibition, target evasion and existing impales still resolve at actual contact.
            shot = OrdinaryBowShot.Capture(WeaponFor(conn), character, Skills.EffectStats(effect, 1), Stats,
                conn.Pools.Accuracy, conn.Pools.AttackCriticalStrikeChance, conn.Pools.CriticalStrikeMultiplier)
                with { CriticalRoll = criticalRoll };
            int Get(string name) => Stats.Key(name) is uint key ? character.GetValueOrDefault(key) : 0;
            projectile = (arrow.Id, ProjectileSpeed.CellsPerSecond(arrow.Speed, Get, _ => 0,
                Environment.GetEnvironmentVariable("Game_PROJECTILE_SPEED")));
        }
        await FlyProjectile(remote, stream, ciphers, conn, projectile, from, aim,
            damageTo: null, epoch: epoch, impact: "none",
            actionToken: action, ordinaryShot: shot);
    }
}
