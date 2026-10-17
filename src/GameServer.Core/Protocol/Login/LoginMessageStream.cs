using System.Collections;
using System.Text;

namespace GameServer.Core.Protocol.Login;

/// <summary>
/// Splits one direction of decrypted login traffic into messages. Bytes can arrive in any chunking. After an unknown
/// id or malformed field the stream cannot be resynchronized (no length prefix): <see cref="Desynced"/> is set and
/// later bytes collect in <see cref="Undecoded"/> without being parsed.
/// </summary>
public sealed class LoginMessageStream(LoginDirection incoming)
{
    private readonly List<byte> _pending = [];

    public bool Desynced { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Bytes not yet decoded: an incomplete message, or everything after a desync.</summary>
    public IReadOnlyList<byte> Undecoded => _pending;

    public List<ILoginMessage> Push(ReadOnlySpan<byte> plaintext)
    {
        foreach (byte b in plaintext) _pending.Add(b);
        var result = new List<ILoginMessage>();
        if (Desynced) return result;
        byte[] buf = [.. _pending];
        int pos = 0;
        try
        {
            while (LoginCodec.TryDecode(incoming, buf.AsSpan(pos), out var m, out int used))
            {
                result.Add(m!);
                pos += used;
            }
        }
        catch (InvalidDataException ex)
        {
            Desynced = true;
            Error = ex.Message;
        }
        _pending.RemoveRange(0, pos);
        return result;
    }
}

public static class LoginMessageFormat
{
    /// <summary>One-line description of a message: name, id, fields; byte arrays as hex (long ones truncated).</summary>
    public static string Describe(ILoginMessage m)
    {
        var sb = new StringBuilder($"0x{m.MessageId:x2} {m.GetType().Name} {{ ");
        bool first = true;
        foreach (var p in m.GetType().GetProperties())
        {
            if (p.Name == nameof(ILoginMessage.MessageId) || p.GetIndexParameters().Length > 0) continue;
            if (!first) sb.Append(", ");
            first = false;
            sb.Append(p.Name).Append(" = ");
            Value(sb, p.GetValue(m));
        }
        return sb.Append(" }").ToString();
    }

    private static void Value(StringBuilder sb, object? v)
    {
        switch (v)
        {
            case null: sb.Append("null"); break;
            case string s: sb.Append('"').Append(s).Append('"'); break;
            case byte[] b:
                sb.Append(Convert.ToHexString(b, 0, Math.Min(b.Length, 64)).ToLowerInvariant());
                if (b.Length > 64) sb.Append($"…({b.Length} bytes)");
                break;
            case IEnumerable e:
                sb.Append('[');
                bool first = true;
                foreach (var x in e) { if (!first) sb.Append(", "); first = false; Value(sb, x); }
                sb.Append(']');
                break;
            case byte or ushort or uint or ulong: sb.Append($"0x{v:x}"); break;
            default: sb.Append(v); break;
        }
    }
}
