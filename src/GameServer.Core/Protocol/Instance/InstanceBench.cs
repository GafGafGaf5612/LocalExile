using System.Buffers.Binary;

namespace GameServer.Core.Protocol.Instance;

public static class InstanceBench
{
    // 0x140c40720 / 0x141f9b2e0: the high bit confirms replacement of an existing craft.
    public const ushort CraftId = 0x15d;
    public const int CraftLength = 4;
    // Currency-count inventory: the bench's native filter (0x140c3b490) accepts kind 0x12,
    // but excludes ordinary stash kind 0x11. Row 26 is deliberately absent from fixed-slot lookup.
    public const uint BalanceInventoryId = 0xfffffe00;
    public static readonly InstanceInventories.Slot BalanceSlot = new(26, 0x12, 255, 255);
    public static (int Recipe, bool Replace) ReadCraft(ReadOnlySpan<byte> message)
    {
        ushort value = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
        return (value & 0x7fff, (value & 0x8000) != 0);
    }
}
