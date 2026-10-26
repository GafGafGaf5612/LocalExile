using GameServer.Core.Protocol.Instance;

/// <summary>Immutable published poses; observers never lock another connection's simulation.</summary>
sealed record InstancePlayer(uint Id, uint Type, PlayerObject Template, int X, int Y, uint Life);

sealed partial class InstanceState
{
    readonly System.Collections.Concurrent.ConcurrentDictionary<uint, InstancePlayer> _players = new();
    public void PublishPlayer(InstancePlayer player) => _players[player.Id] = player;
    public void RemovePlayer(uint id) => _players.TryRemove(id, out _);
    public InstancePlayer[] PlayersExcept(uint id) => [.. _players.Values.Where(p => p.Id != id)];
}

sealed partial class ClientState
{
    static int _nextPeerId = 0x20000000;
    public readonly uint PeerId = unchecked((uint)Interlocked.Increment(ref _nextPeerId));
    public PlayerObject? PeerTemplate;
    public uint PeerType;
    public readonly Dictionary<uint, InstancePlayer> SeenPlayers = [];
}
