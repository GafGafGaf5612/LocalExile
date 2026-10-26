using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

partial class Program
{
    static async Task RunLivingCollisions(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        int frames = 0;
        try
        {
            while (!conn.Closed && conn.PendingTransfer is null)
            {
                if (!conn.Paused)
                {
                    await CorrectMonsterCollisions(remote, stream, ciphers, conn);
                    await PushOutOfCollision(remote, stream, ciphers, conn, living: true);
                }
                PublishPlayerPose(conn);
                if ((frames++ & 1) == 0) await SyncInstancePlayers(remote, stream, ciphers, conn);
                await Task.Delay(50);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        finally { conn.Instance.RemovePlayer(conn.PeerId); }
    }

    static void PublishPlayerPose(ClientState conn)
    {
        if (conn.PeerTemplate is not { } template || !conn.PlayerSent || conn.Closed) return;
        var p = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        conn.Instance.PublishPlayer(new(conn.PeerId, conn.PeerType, template, p.X, p.Y, conn.PlayerLife));
    }

    static async Task SyncInstancePlayers(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var peers = conn.Instance.PlayersExcept(conn.PeerId);
        var present = peers.Select(p => p.Id).ToHashSet();
        foreach (uint id in conn.SeenPlayers.Keys.Where(id => !present.Contains(id)).ToArray())
        {
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"player 0x{id:x} left instance");
            conn.SeenPlayers.Remove(id);
        }
        foreach (var peer in peers)
        {
            bool exists = conn.SeenPlayers.TryGetValue(peer.Id, out var previous);
            if (!exists || previous!.Life == 0 && peer.Life > 0)
            {
                if (exists) await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(peer.Id), null);
                var player = peer.Template with { GridX = peer.X, GridY = peer.Y, Life = peer.Life, Skills = [], QuestFlags = [], LeagueQuestFlags = [] };
                await Send(remote, stream, ciphers, InstanceEntities.CreateObject(peer.Id, peer.Type, player.Blob()),
                    $"create instance player {player.Name} 0x{peer.Id:x} at ({peer.X},{peer.Y})");
                await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(peer.Id), null);
            }
            else
            {
                if ((previous!.X, previous.Y) != (peer.X, peer.Y))
                    await Send(remote, stream, ciphers,
                        Math.Max(Math.Abs(previous.X - peer.X), Math.Abs(previous.Y - peer.Y)) > 20
                            ? InstanceProtocol.Teleport(peer.Id, peer.X, peer.Y, 0x7fffffff)
                            : InstanceProtocol.MoveTo(peer.Id, peer.X, peer.Y, .1f, 0x7fffffff), null);
                if (previous.Life != peer.Life)
                    await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(peer.Id, peer.Life), null);
            }
            conn.SeenPlayers[peer.Id] = peer;
        }
    }

    static async Task CorrectMonsterCollisions(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var corrections = new List<(uint Id, int X, int Y)>();
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            foreach (var (id, m) in conn.Monsters)
            {
                if (!LivingCollision.Blocks(m)) { m.CollisionPosition = null; continue; }
                var current = m.Walk.Position(now);
                var previous = m.CollisionPosition ?? current;
                var player = conn.Walk.Position(now);
                int size = LivingCollision.Size(m);
                bool playerOverlap = conn.PlayerLife > 0 && !conn.Phasing
                    && Math.Max(Math.Abs(current.X - player.X), Math.Abs(current.Y - player.Y)) < size + 2;
                bool peerOverlap = conn.Instance.PlayersExcept(conn.PeerId).Any(p => p.Life > 0
                    && Math.Max(Math.Abs(current.X - p.X), Math.Abs(current.Y - p.Y)) < size + 2);
                bool bodyOverlap = conn.Monsters.Any(pair => pair.Key < id && LivingCollision.Blocks(pair.Value)
                    && ClientPathfinder.ObjectBlocks([new(pair.Value.Walk.Position(now).X, pair.Value.Walk.Position(now).Y,
                        LivingCollision.Size(pair.Value))], current.X, current.Y, size + 1, size + 4));
                if (current == previous && !playerOverlap && !peerOverlap && !bodyOverlap) continue;
                var blockers = conn.MonsterBlockers(id, includePrevious: true);
                bool Floor(int x, int y) => conn.Layout.WalkableFor(x, y, size) && !conn.Barrier.Contains((x, y));
                var stop = LivingCollision.Sweep(previous, current, size, Floor, blockers);
                if (!CollisionEscape.Clear(stop, conn.Layout.GridW, conn.Layout.GridH, Floor, blockers, size))
                    stop = CollisionEscape.Find(stop, conn.Layout.GridW, conn.Layout.GridH, Floor, blockers, moverSize: size) ?? stop;
                if (stop != current)
                {
                    m.Walk.Teleport(stop.X, stop.Y, now);
                    m.LastTarget = null;
                    corrections.Add((id, stop.X, stop.Y));
                }
                m.CollisionPosition = stop;
            }
        }
        foreach (var p in corrections)
        {
            await Send(remote, stream, ciphers, InstanceProtocol.ActionEnded(p.Id), null);
            await Send(remote, stream, ciphers, InstanceProtocol.Teleport(p.Id, p.X, p.Y, 0x7fffffff),
                $"living collision stop 0x{p.Id:x} at ({p.X},{p.Y})");
        }
    }
}
