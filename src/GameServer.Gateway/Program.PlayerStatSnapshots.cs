using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

partial class Program
{
    // Every full player stat/Actor refresh shares this gate, including refreshes caused by other mechanics.
    // Otherwise a packet prepared before expiry could overwrite a newer Fortification expiry update.
    static Task SendPlayerStatSnapshot(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        Func<byte[]> packet, string? label) => Send(remote, stream, ciphers, [], label, atGate: () =>
        {
            lock (conn.Monsters)
            {
                if (conn.Closed || conn.LeavingArea) return null;
                return WithOwnAuraChanges(conn, packet);
            }
        });
}
