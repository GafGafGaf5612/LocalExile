using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// Localhost control channel from GameServer.Gateway to GameServer.Auth (one JSON object per line, one request per TCP
/// connection). Auth owns the login connections and the <see cref="World.InstanceRegistry"/>; the Gateway asks it which
/// area/instance a ticket belongs to, for instance lists, and to move a player (Auth issues the new ticket; the Gateway
/// sends it to the client in 0x27).
/// Port: Game_AUTH_CONTROL_PORT (default 20495), bound to 127.0.0.1 only.
/// </summary>
public static class InstanceControl
{
    public const int DefaultPort = 20495;

    public static int Port => int.TryParse(Environment.GetEnvironmentVariable("Game_AUTH_CONTROL_PORT"), out var p) ? p : DefaultPort;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Sends one request to Auth and returns its answer (Ok = false with Error on any failure).</summary>
    public static async Task<ControlResponse> CallAsync(ControlRequest request, int? port = null, CancellationToken ct = default)
    {
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port ?? Port, ct);
            using var stream = tcp.GetStream();
            byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, Json) + "\n");
            await stream.WriteAsync(line, ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? reply = await reader.ReadLineAsync(ct);
            return reply is null
                ? new ControlResponse { Ok = false, Error = "no reply" }
                : JsonSerializer.Deserialize<ControlResponse>(reply, Json) ?? new ControlResponse { Ok = false, Error = "bad reply" };
        }
        catch (Exception ex) when (ex is IOException or SocketException or JsonException)
        {
            return new ControlResponse { Ok = false, Error = ex.Message };
        }
    }
}

/// <summary>
/// Ops: "resolve" (Ticket) → the ticket's area, seed, instance, character; "list" (Ticket, Area) → live instances of
/// Area; "transfer" (Ticket, Area, Instance = 0 for the default choice, New = true for a fresh instance) → Auth moves
/// the ticket's player and returns the destination's new Ticket; "leave" (Ticket) → the player left its instance (expiry starts).
/// </summary>
public sealed record ControlRequest
{
    public string Op { get; init; } = "";
    public uint Ticket { get; init; }
    public ushort Area { get; init; }
    public ulong Instance { get; init; }
    public bool New { get; init; }
}

public sealed record ControlResponse
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public ushort Area { get; init; }
    public uint Seed { get; init; }
    public ulong Instance { get; init; }
    public uint Ticket { get; init; }
    public bool IsTown { get; init; }
    public string? Character { get; init; }
    public List<ControlInstance>? Instances { get; init; }
}

public sealed record ControlInstance(ulong Id, long CreatedUnix, int SecondsLeft, List<string> Players);
