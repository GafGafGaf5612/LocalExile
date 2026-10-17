using GameServer.Core.Protocol.Instance;

/// <summary>
/// Slash commands typed in the client's chat box (C→S 0x15, Agents/docs/protocol/10-chat-and-commands.md). Dev tools for now:
/// every command is accepted from every player.
/// </summary>
sealed class ChatCommands
{
    /// <summary>Sends one S→C message (bytes, log line).</summary>
    public required Func<byte[], string, Task> Send { get; init; }

    /// <summary>Tells the player something (log only until the S→C chat line is mapped).</summary>
    public required Func<string, Task> Reply { get; init; }

    private readonly Dictionary<string, (string Usage, Func<string[], Task> Run)> _commands = new(StringComparer.OrdinalIgnoreCase);

    public ChatCommands Add(string name, string usage, Func<string[], Task> run)
    {
        _commands[name] = (usage, run);
        return this;
    }

    /// <summary>Runs a command line ("/name args..."). Returns false if it is not one of ours (unknown commands are
    /// answered with the list).</summary>
    public async Task<bool> RunAsync(string line)
    {
        if (!line.StartsWith('/')) return false;
        string[] parts = line[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        if (parts[0].Equals("help", StringComparison.OrdinalIgnoreCase) || !_commands.TryGetValue(parts[0], out var cmd))
        {
            await Reply("Commands: " + string.Join(", ", _commands.Values.Select(c => c.Usage).Order()));
            return parts[0].Equals("help", StringComparison.OrdinalIgnoreCase);
        }
        try { await cmd.Run(parts[1..]); }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException or IndexOutOfRangeException)
        {
            await Reply($"Usage: {cmd.Usage} ({ex.Message})");
        }
        return true;
    }

    /// <summary>/raw: hex bytes of one S→C message, sent as is (spaces allowed).</summary>
    public static byte[] ParseHex(string[] args) => Convert.FromHexString(string.Concat(args));
}
