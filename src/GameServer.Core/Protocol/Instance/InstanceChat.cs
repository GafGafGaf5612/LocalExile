using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// Chat on the instance connection (Agents/docs/protocol/10-chat-and-commands.md).
/// </summary>
public static class InstanceChat
{
    public const ushort ChatId = 0x15;

    /// <summary>
    /// C→S 0x15: what the player typed in the chat box, slash commands included. Compact wide string (the format of
    /// <see cref="LoginWriter.WStr"/>) then a u8, 0 in every capture. Plain text arrives with the channel prefix the
    /// client added ("#hello" for global), commands as typed ("/spawn_mob 42"). Captured live 2026-10-16.
    /// </summary>
    public sealed record ChatInput(string Text, byte U8)
    {
        public bool IsCommand => Text.StartsWith('/');
    }

    /// <summary>Bytes of the 0x15 at the start of <paramref name="m"/>, or null if it is not complete yet.</summary>
    public static int? Length(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m);
        r.U16();
        r.WStr();
        r.U8();
        return r.Short ? null : r.Position;
    }

    public const ushort MessageBoxId = 0x0d;

    /// <summary>S→C 0x0d (class 0x1435bf650, the one-wide-string class also used by login 0x24–0x29; dispatcher case 0 →
    /// listeners at +0xa1f0): a message box with the text and an OK button. Live 2026-10-16.</summary>
    public static byte[] MessageBox(string text) => new LoginWriter().U16(MessageBoxId).WStr(text).ToArray();

    public const ushort ChatLineId = 0x17;

    /// <summary>S→C 0x17 (class 0x1435dcec0, read 0x140e0c710; dispatcher case 2 → listeners at instance +0xb78 → chat
    /// panel 0x140179f20 / 0x14017a300): one line in the chat panel. wstr sender (its first character picks the channel),
    /// wstr text, wstr guild tag, u16 flags (bit 8 sends it to +0xd78 instead, bit 3 adds a field), u8, u8, 8 bytes, u8,
    /// u8 n + n linked items. An empty sender and zeros print the text alone. Live 2026-10-20.</summary>
    public static byte[] ChatLine(string text, string sender = "") =>
        new LoginWriter().U16(ChatLineId).WStr(sender).WStr(text).WStr("").U16(0).U8(0).U8(0).RawU64(0).U8(0).U8(0).ToArray();

    public static ChatInput ReadChat(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        return new ChatInput(r.WStr(), r.U8());
    }
}
