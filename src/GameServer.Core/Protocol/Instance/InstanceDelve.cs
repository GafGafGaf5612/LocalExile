using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// The Delve chart (Agents/docs/protocol/21-hideouts.md, Niko): the client opens it on S→C 0x7b kind 0x1c, then asks for
/// its data with C→S 0x23f (just the id). S→C 0x240 (class 0x1435dbfa0, read 0x1421b3d00) answers: u32 (+0x10), u16
/// (+0x14), u16 n, then n × (u16, u16, u64, u8, u32). What the fields mean is being worked out live; an empty chart
/// first.
/// </summary>
public static class InstanceDelve
{
    public const ushort ChartRequestId = 0x023f, ChartId = 0x0240;

    /// <summary>One chart entry as the reader takes it: u16, u16, u64, u8, u32 (meaning being worked out).</summary>
    public readonly record struct Cell(ushort A, ushort B, ulong C, byte D, uint E);

    public static byte[] Chart(uint a = 0, ushort b = 0, IReadOnlyList<Cell>? cells = null)
    {
        var w = new LoginWriter().U16(ChartId).U32(a).U16(b).U16((ushort)(cells?.Count ?? 0));
        foreach (var c in cells ?? []) w.U16(c.A).U16(c.B).U32((uint)(c.C >> 32)).U32((uint)c.C).U8(c.D).U32(c.E);
        return w.ToArray();
    }
}
